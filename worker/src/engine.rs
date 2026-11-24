use anyhow::{anyhow, Context as _, Result};
use serde_json::Value;
use std::{fs::File, io::Write, path::Path};
use v8::{Local, PinScope};
use zip::{write::SimpleFileOptions, ZipWriter};

pub struct Engine {
    context: v8::Global<v8::Context>,
    isolate: v8::OwnedIsolate,
}

fn string<'s>(s: &mut PinScope<'s, '_>, value: &str) -> Result<Local<'s, v8::String>> {
    v8::String::new(s, value).context("V8 string allocation failed")
}

fn call<'s>(
    s: &mut PinScope<'s, '_>,
    name: &str,
    args: &[Local<v8::Value>],
) -> Result<Local<'s, v8::Value>> {
    let global = s.get_current_context().global(s);
    let key = string(s, name)?;
    let function = global
        .get(s, key.into())
        .and_then(|v| Local::<v8::Function>::try_from(v).ok())
        .context("Missing adapter function")?;
    v8::tc_scope!(let catch, s);
    function.call(catch, global.into(), args).ok_or_else(|| {
        let detail = catch
            .exception()
            .map(|v| v.to_rust_string_lossy(catch))
            .unwrap_or_default();
        anyhow!("{name}: {detail}")
    })
}

impl Engine {
    pub fn new(bundle: &Path) -> Result<Self> {
        v8::icu::set_common_data_78(deno_core_icudata::ICU_DATA)
            .map_err(|code| anyhow!("Loading V8 locale data: ICU error {code}"))?;
        let platform = v8::new_default_platform(0, false).make_shared();
        v8::V8::initialize_platform(platform);
        v8::V8::initialize();
        let mut isolate = v8::Isolate::new(Default::default());
        let context = {
            v8::scope!(let handles, &mut isolate);
            let context = v8::Context::new(handles, Default::default());
            let s = &mut v8::ContextScope::new(handles, context);
            let bundle = std::fs::read_to_string(bundle)?;
            for source in [include_str!("shims.js"), &bundle, include_str!("adapter.js"), "__drainTimeouts(); if (!window._messageListener) throw Error('Missing parser listener');"] {
                v8::tc_scope!(let catch, s);
                let code = string(catch,source)?;
                if v8::Script::compile(catch,code,None).and_then(|script| script.run(catch)).is_none() {
                    let detail = catch.exception().map(|v| v.to_rust_string_lossy(catch)).unwrap_or_default();
                    anyhow::bail!("Parser initialization failed: {detail}");
                }
            }
            v8::Global::new(s, context)
        };
        Ok(Self { context, isolate })
    }

    pub fn control(&mut self, op: &str, arg: &Value) -> Result<Value> {
        v8::scope_with_context!(let scope, &mut self.isolate, &self.context);
        let name = string(scope, op)?;
        let json = string(scope, &arg.to_string())?;
        let input = v8::json::parse(scope, json).context("Invalid control data")?;
        let result = call(scope, "control", &[name.into(), input])?;
        let text = v8::json::stringify(scope, result).context("Invalid control response")?;
        Ok(serde_json::from_str(&text.to_rust_string_lossy(scope))?)
    }

    pub fn feed(&mut self, lines: &[String], offset: u64) -> Result<()> {
        v8::scope_with_context!(let s, &mut self.isolate, &self.context);
        let array = v8::Array::new(s, lines.len() as i32);
        for (idx, line) in lines.iter().enumerate() {
            let value = string(s, line)?;
            array
                .set_index(s, idx as u32, value.into())
                .context("Creating line array")?;
        }
        let offset = v8::Number::new(s, offset as f64);
        call(s, "feed", &[array.into(), offset.into()])?;
        Ok(())
    }

    fn write_group(&mut self, group: i32, output: &mut impl Write) -> Result<()> {
        for part in 0.. {
            let mut offset = 0;
            loop {
                v8::scope_with_context!(let s, &mut self.isolate, &self.context);
                let args = [group, part, offset].map(|n| v8::Integer::new(s, n).into());
                let value = call(s, "piece", &args)?;
                if value.is_null() {
                    return Ok(());
                }
                let value =
                    Local::<v8::String>::try_from(value).context("Invalid segment field")?;
                if value.length() == 0 {
                    break;
                }
                offset += value.length() as i32;
                output.write_all(value.to_rust_string_lossy(s).as_bytes())?;
            }
        }
        Ok(())
    }

    pub fn save(&mut self, dir: &Path) -> Result<()> {
        for (group, name) in ["master.zip", "events.zip"].iter().enumerate() {
            let mut zip = ZipWriter::new(File::create(dir.join(name))?);
            let large = self
                .control("zip64", &Value::from(group))?
                .as_bool()
                .context("Missing ZIP size")?;
            zip.start_file("log.txt", SimpleFileOptions::default().large_file(large))?;
            self.write_group(group as i32, &mut zip)?;
            zip.finish()?;
        }
        self.write_group(2, &mut File::create(dir.join("players.txt"))?)
    }
}
