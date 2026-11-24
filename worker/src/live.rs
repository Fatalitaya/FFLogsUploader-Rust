use crate::{Input, Worker};
use anyhow::{bail, ensure, Context, Result};
use serde_json::{json, Value};
use std::{
    fs,
    io::{self, BufRead, Write},
    path::{Path, PathBuf},
    sync::mpsc::{self, Receiver, RecvTimeoutError},
    thread,
    time::{Duration, Instant},
};

enum Command {
    Stop,
    Ack(String),
}

fn commands() -> Receiver<Result<Command>> {
    let (tx, rx) = mpsc::channel();
    thread::spawn(move || {
        for line in io::stdin().lock().lines() {
            let cmd = (|| {
                let msg: Value = serde_json::from_str(&line?)?;
                ensure!(msg["v"] == 1, "Protocol version mismatch");
                match msg["op"].as_str() {
                    Some("stop") => Ok(Command::Stop),
                    Some("ack") => Ok(Command::Ack(
                        msg["arg"].as_str().context("Missing report code")?.into(),
                    )),
                    _ => bail!("Unexpected live command"),
                }
            })();
            if tx.send(cmd).is_err() {
                break;
            }
        }
    });
    rx
}

struct Monitor<'a> {
    worker: &'a mut Worker,
    out: &'a mut dyn Write,
    commands: Receiver<Result<Command>>,
    stopping: bool,
    committed: u64,
    provisional_events: u64,
    report_code_set: bool,
}

impl Monitor<'_> {
    fn emit(&mut self, event: Value) -> Result<()> {
        writeln!(self.out, "{}", json!({"v":1,"result":event}))?;
        self.out.flush()?;
        Ok(())
    }

    fn poll_stop(&mut self, wait: Duration) -> Result<()> {
        match self.commands.recv_timeout(wait) {
            Ok(cmd) => match cmd? {
                Command::Stop => self.stopping = true,
                Command::Ack(_) => bail!("Unexpected segment acknowledgement"),
            },
            Err(RecvTimeoutError::Timeout) => (),
            Err(RecvTimeoutError::Disconnected) => bail!("Uploader disconnected"),
        }
        Ok(())
    }

    fn read(&mut self, stage: &str, final_line: bool, interruptible: bool) -> Result<bool> {
        let mut next = Instant::now();
        loop {
            let batch = self.worker.execute("batch", &json!(final_line))?;
            let done = batch["done"] == true;
            if done || Instant::now() >= next {
                self.emit(json!({"event":"progress","stage":stage,
                    "position":batch["position"],"total":batch["total"]}))?;
                next = Instant::now() + Duration::from_millis(200);
            }
            self.poll_stop(Duration::ZERO)?;
            if done || (interruptible && self.stopping) {
                return Ok(done);
            }
        }
    }

    fn collect(&mut self, final_fight: bool, provisional: bool) -> Result<()> {
        let count = self
            .worker
            .engine
            .control(
                "collect",
                &json!({"final":final_fight,"provisional":provisional}),
            )?
            .as_u64()
            .context("Invalid fight count")?;
        let first = if provisional { 0 } else { self.committed };
        let changed = !provisional
            || count > 0
                && self
                    .worker
                    .engine
                    .control("count", &Value::Null)?
                    .as_u64()
                    .context("Invalid event count")?
                    > self.provisional_events;
        if changed {
            for i in first..count {
                let fight = self.worker.execute("prepare", &json!(i))?;
                if fight.is_null() {
                    continue;
                }
                let events = if provisional {
                    fight["count"].as_u64().context("Missing event count")?
                } else {
                    0
                };
                let id = if provisional {
                    self.committed + 1
                } else {
                    i + 1
                };
                self.emit(json!({"event":"segment","id":id,"start":fight["start"],"end":fight["end"],"events":events}))?;
                loop {
                    match self
                        .commands
                        .recv()
                        .context("Uploader disconnected before acknowledgement")??
                    {
                        Command::Stop => self.stopping = true,
                        Command::Ack(code) => {
                            if !self.report_code_set {
                                self.worker.engine.control("code", &json!(code))?;
                                self.report_code_set = true;
                            }
                            self.worker.execute("ack", &Value::Null)?;
                            break;
                        }
                    }
                }
                if provisional {
                    self.provisional_events = events;
                }
            }
        }
        if !provisional && count > self.committed {
            self.committed = count;
            self.provisional_events = 0;
        }
        self.worker.engine.control("release", &Value::Null)?;
        Ok(())
    }
}

fn latest(directory: &Path) -> Result<Option<PathBuf>> {
    let mut newest = None;
    for entry in fs::read_dir(directory)? {
        let path = entry?.path();
        if !path
            .extension()
            .and_then(|ext| ext.to_str())
            .is_some_and(|ext| ext.eq_ignore_ascii_case("log"))
        {
            continue;
        }
        let meta = match path.metadata() {
            Err(e) if e.kind() == io::ErrorKind::NotFound => continue,
            other => other?,
        };
        if meta.is_file() {
            newest = newest.max(Some((meta.modified()?, path)));
        }
    }
    Ok(newest.map(|(_, path)| path))
}

pub fn watch(arg: &Value, out: &mut dyn Write) -> Result<()> {
    let directory = Path::new(arg["directory"].as_str().context("Missing log directory")?);
    let mut path = latest(directory)?.context("No log files found")?;
    let mut options = arg.clone();
    options["path"] = json!(path);
    options["live"] = json!(true);
    let mut worker = Worker::open(&options)?;
    let mut m = Monitor {
        worker: &mut worker,
        out,
        commands: commands(),
        stopping: false,
        committed: 0,
        provisional_events: 0,
        report_code_set: false,
    };
    let history_read = m.read("Reading log history", false, true)?;
    if arg["history"] == true {
        m.collect(false, false)?;
    }
    m.worker.engine.control("tail", &json!(false))?;
    let mut rotation = Instant::now() + Duration::from_secs(10);
    let mut fight_end = None;
    let mut last_size = None;
    let mut listening = false;
    while !m.stopping {
        let replaced = m.worker.input.refresh()?;
        let size = m.worker.input.limit;
        if m.worker.input.position < size && last_size != Some(size) {
            m.read("Reading appended data", false, true)?;
            listening = false;
        }
        if last_size.is_none() {
            m.worker.engine.control("tail", &json!(true))?;
        }
        last_size = Some(size);
        let director = m.worker.engine.control("director", &Value::Null)? == true;
        if fight_end.is_none()
            && (director || m.worker.engine.control("inactive", &Value::Null)? == true)
        {
            fight_end = Some(Instant::now() + Duration::from_secs(5));
        }
        if fight_end.is_some_and(|end| Instant::now() >= end) {
            m.collect(false, false)?;
            fight_end = None;
            listening = false;
        }
        if !m.stopping && arg["realTime"] == true {
            let previous = m.provisional_events;
            m.collect(false, true)?;
            if previous != m.provisional_events {
                listening = false;
            }
        }
        if replaced || Instant::now() >= rotation {
            rotation = Instant::now() + Duration::from_secs(10);
            if let Some(next) = latest(directory)? {
                if replaced || next != path {
                    m.worker.input.refresh()?;
                    m.read("Finishing previous log", true, false)?;
                    m.worker.input = Input::open(&next)?;
                    m.worker.engine.control("tail", &json!(false))?;
                    path = next;
                    last_size = None;
                    listening = false;
                }
            }
        }
        if !listening {
            m.emit(json!({"event":"progress","stage":"Listening","position":0,"total":0}))?;
            listening = true;
        }
        if !m.stopping {
            m.poll_stop(Duration::from_millis(500))?;
        }
    }
    if history_read {
        m.worker.input.refresh()?;
        if m.worker.input.position < m.worker.input.limit {
            m.read("Finishing live log", false, false)?;
        }
    }
    m.collect(true, false)?;
    m.emit(json!({"event":"finished"}))
}
