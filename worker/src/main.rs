mod engine;
mod input;
mod live;

use anyhow::{ensure, Context, Result};
use engine::Engine;
use input::Input;
use serde_json::{json, Value};
use std::{
    io::{self, Write},
    path::Path,
};

struct Worker {
    engine: Engine,
    input: Input,
    live: bool,
    pending: bool,
}

impl Worker {
    fn open(arg: &Value) -> Result<Self> {
        let mut engine = Engine::new(Path::new(
            arg["bundle"].as_str().context("Missing parser path")?,
        ))?;
        let mut input = Input::open(Path::new(arg["path"].as_str().context("Missing log path")?))?;
        let mut options = arg.clone();
        options["tail"] = json!(input.tail()?);
        engine.control("begin", &options)?;
        Ok(Self {
            engine,
            input,
            live: arg["live"] == true,
            pending: false,
        })
    }

    fn execute(&mut self, op: &str, arg: &Value) -> Result<Value> {
        match op {
            "batch" => {
                let start = self.input.position;
                let (lines, done) = self.input.batch(self.live, arg == true)?;
                if !lines.is_empty() {
                    self.engine
                        .feed(&lines, start)
                        .with_context(|| format!("parsing byte {start}"))?;
                }
                Ok(json!({"position":self.input.position,"total":self.input.limit,"done":done}))
            }
            "refresh" => {
                let replaced = self.input.refresh()?;
                Ok(
                    json!({"position": self.input.position, "total": self.input.limit, "replaced": replaced}),
                )
            }
            "switch" => {
                self.input = Input::open(Path::new(arg.as_str().context("Missing log path")?))?;
                Ok(json!(self.input.limit))
            }
            "verify" => {
                ensure!(!self.input.changed()?, "Log file changed during upload");
                Ok(Value::Null)
            }
            "replay" => {
                self.input.rewind()?;
                self.engine.control(op, arg)
            }
            "prepare" => {
                ensure!(!self.pending, "Previous segment has not been acknowledged");
                let result = self.engine.control(op, arg)?;
                if !result.is_null() {
                    self.engine.save(Path::new("."))?;
                    self.pending = true;
                }
                Ok(result)
            }
            "ack" => {
                ensure!(self.pending, "No segment awaiting acknowledgement");
                for file in ["master.zip", "events.zip", "players.txt"] {
                    std::fs::remove_file(file)?;
                }
                self.pending = false;
                Ok(Value::Null)
            }
            _ => self.engine.control(op, arg),
        }
    }
}

fn run() -> Result<()> {
    let mut worker = None;
    let stdin = io::stdin();
    let mut out = io::stdout().lock();
    loop {
        let mut line = String::new();
        if stdin.read_line(&mut line)? == 0 {
            break;
        }
        let msg: Value = serde_json::from_str(&line)?;
        ensure!(msg["v"] == 1, "Protocol version mismatch");
        let op = msg["op"].as_str().context("Missing operation")?;
        let arg = &msg["arg"];
        let result = match op {
            "watch" => {
                ensure!(worker.is_none(), "Session already started");
                return live::watch(arg, &mut out);
            }
            "stop" => Value::Null,
            "open" => {
                ensure!(worker.is_none(), "Session already started");
                let session = Worker::open(arg)?;
                let size = session.input.limit;
                worker = Some(session);
                json!(size)
            }
            _ => worker
                .as_mut()
                .context("Session not started")?
                .execute(op, arg)?,
        };
        writeln!(out, "{}", json!({"v":1,"result":result}))?;
        out.flush()?;
        if op == "stop" {
            break;
        }
    }
    Ok(())
}

fn main() {
    if let Err(error) = run() {
        eprintln!("{error:#}");
        std::process::exit(1);
    }
}
