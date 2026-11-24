use serde_json::{json, Value};
use std::{
    fs::{self, OpenOptions},
    io::{BufRead, BufReader, Read, Write},
    process::{Child, Command, Stdio},
};
use tempfile::TempDir;

struct Session {
    dir: TempDir,
    child: Child,
    out: Option<BufReader<std::process::ChildStdout>>,
    events: Option<std::sync::mpsc::Receiver<String>>,
}
impl Session {
    fn start(bytes: &[u8], live: bool, history: bool) -> Self {
        let mut s = Self::spawn(bytes);
        let path = s.dir.path().join("input.log");
        s.call(
            "open",
            json!({"bundle":concat!(env!("CARGO_MANIFEST_DIR"),"/tests/fixture.js"),
            "path":path,"region":"NA","live":live,"history":history,"code":"test"}),
        );
        s
    }

    fn spawn(bytes: &[u8]) -> Self {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("input.log");
        fs::write(&path, bytes).unwrap();
        let mut child = Command::new(env!("CARGO_BIN_EXE_fflogs-worker"))
            .current_dir(dir.path())
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .unwrap();
        let out = BufReader::new(child.stdout.take().unwrap());
        Self {
            dir,
            child,
            out: Some(out),
            events: None,
        }
    }
    fn watch(&mut self, history: bool, real_time: bool) {
        let out = self.out.take().unwrap();
        let (tx, rx) = std::sync::mpsc::channel();
        std::thread::spawn(move || {
            for line in out.lines() {
                if tx.send(line.unwrap()).is_err() {
                    break;
                }
            }
        });
        self.events = Some(rx);
        self.send("watch", json!({"directory":self.dir.path(),"region":"NA","history":history,
            "realTime":real_time, "bundle":concat!(env!("CARGO_MANIFEST_DIR"),"/tests/fixture.js")}));
    }

    fn next_live(&mut self) -> Value {
        let line = self
            .events
            .as_ref()
            .unwrap()
            .recv_timeout(std::time::Duration::from_secs(15))
            .expect("missing live event");
        serde_json::from_str::<Value>(&line).unwrap()["result"].clone()
    }

    fn send(&mut self, op: &str, arg: Value) {
        writeln!(
            self.child.stdin.as_mut().unwrap(),
            "{}",
            json!({"v":1,"op":op,"arg":arg})
        )
        .unwrap();
    }
    fn call(&mut self, op: &str, arg: Value) -> Value {
        self.send(op, arg);
        let mut line = String::new();
        assert!(
            self.out.as_mut().unwrap().read_line(&mut line).unwrap() > 0,
            "{op} unexpectedly terminated"
        );
        serde_json::from_str::<Value>(&line).unwrap()["result"].clone()
    }
    fn drain(&mut self, final_line: bool) -> Vec<u64> {
        let mut offsets = vec![];
        loop {
            let b = self.call("batch", json!(final_line));
            offsets.push(b["position"].as_u64().unwrap());
            if b["done"] == true {
                return offsets;
            }
        }
    }
    fn append(&self, bytes: &[u8]) {
        OpenOptions::new()
            .append(true)
            .open(self.dir.path().join("input.log"))
            .unwrap()
            .write_all(bytes)
            .unwrap();
    }
    fn unzip(&self, file: &str) -> String {
        let file = fs::File::open(self.dir.path().join(file)).unwrap();
        let mut archive = zip::ZipArchive::new(file).unwrap();
        let mut out = String::new();
        archive
            .by_name("log.txt")
            .unwrap()
            .read_to_string(&mut out)
            .unwrap();
        out
    }
}
impl Drop for Session {
    fn drop(&mut self) {
        self.child.stdin.take();
        self.child.wait().unwrap();
    }
}
#[test]
fn snapshot_replay_preserves_lines_offsets_and_segment_payloads() {
    let lines: Vec<_> = (0..5500)
        .map(|n| format!("00|2026-11-24T00:00:00Z|{n}{}", "😀".repeat(58)))
        .collect();
    let text = lines.join("\r\n");
    let mut s = Session::start(text.as_bytes(), false, false);
    s.append(b"excluded append");
    let positions = s.drain(true);
    assert!(positions.len() > 1);
    assert_eq!(s.call("raids", Value::Null), 1);
    s.call("replay", json!(0));
    assert_eq!(positions, s.drain(true));
    s.call("collect", json!({"final":false}));
    let result = s.call("prepare", json!(0));
    assert_eq!(result, json!({"start":100,"end":102,"count":5500}));
    assert_eq!(
        s.unzip("events.zip"),
        format!("72|1\n5500\n72|1\n2\n1|{}\n2|end\n", lines.join(";"))
    );
    assert!(s
        .unzip("master.zip")
        .contains(&format!("offsets=0,{}", positions[0])));
    assert_eq!(
        fs::read_to_string(s.dir.path().join("players.txt")).unwrap(),
        "玩家😀"
    );
    s.call("ack", Value::Null);
    assert!(!s.dir.path().join("events.zip").exists());
}

#[test]
fn live_holds_partial_utf8_and_crlf_until_complete() {
    for history in [true, false] {
        let mut s = Session::start(b"history\r\n\xf0\x9f", true, history);
        assert_eq!(s.drain(false), [9]);
        s.call("tail", json!(true));
        s.append(b"\x98\x80\r");
        s.call("refresh", Value::Null);
        assert_eq!(s.drain(false), [9]);
        s.append(b"\n33|40000011|end\n");
        s.call("refresh", Value::Null);
        s.drain(false);
        assert_eq!(s.call("director", Value::Null), true);
        s.call("collect", json!({"provisional":true}));
        assert_eq!(s.call("count", Value::Null), if history { 3 } else { 2 });
        s.call("prepare", json!(0));
        let events = s.unzip("events.zip");
        assert!(events.contains("😀;33|40000011|end"));
        assert_eq!(events.contains("history"), history);
    }
}

#[test]
fn oversized_line_fails_with_byte_offset() {
    let mut data = b"ok\n".to_vec();
    data.resize(3 + 8 * 1024 * 1024 + 1, b'x');
    let mut s = Session::start(&data, false, false);
    s.send("batch", json!(true));
    assert!(!s.child.wait().unwrap().success());
    let mut err = String::new();
    s.child
        .stderr
        .take()
        .unwrap()
        .read_to_string(&mut err)
        .unwrap();
    assert!(err.contains("8 MiB at byte 3"));
}

#[test]
fn bulk_rejects_replacement_and_live_drains_rotated_handle() {
    for live in [false, true] {
        let mut s = Session::start(b"first\nunfinished", live, true);
        let path = s.dir.path().join("input.log");
        s.drain(!live);
        fs::rename(&path, s.dir.path().join("previous.log")).unwrap();
        fs::write(&path, b"next\n").unwrap();
        if !live {
            s.send("verify", Value::Null);
            assert!(!s.child.wait().unwrap().success());
            continue;
        }
        assert_eq!(s.call("refresh", Value::Null)["replaced"], true);
        s.drain(true);
        s.call("switch", json!(path));
        assert_eq!(s.drain(false), [5]);
        fs::write(&path, b"x\n").unwrap();
        assert_eq!(s.call("refresh", Value::Null)["position"], 0);
        s.drain(false);
        s.call("collect", json!({"final":true}));
        s.call("prepare", json!(0));
        assert!(s.unzip("events.zip").contains("first;unfinished;next;x"));
    }
}

#[test]
fn utf16_and_utf32_boms_preserve_cr_lines_and_source_offsets() {
    let text = "first\r玩家😀\r\nlast";
    for little in [true, false] {
        for wide in [true, false] {
            let mut bytes = Vec::new();
            if wide {
                for c in std::iter::once('\u{feff}').chain(text.chars()) {
                    bytes.extend(if little {
                        (c as u32).to_le_bytes()
                    } else {
                        (c as u32).to_be_bytes()
                    });
                }
            } else {
                for c in std::iter::once(0xfeff).chain(text.encode_utf16()) {
                    bytes.extend(if little {
                        c.to_le_bytes()
                    } else {
                        c.to_be_bytes()
                    });
                }
            }
            let mut s = Session::start(&bytes, true, true);
            assert_eq!(s.drain(true), [bytes.len() as u64]);
            s.call("collect", json!({"final":true}));
            s.call("prepare", json!(0));
            assert!(s.unzip("events.zip").contains("first;玩家😀;last"));
        }
    }
}

#[test]
fn watch_finishes_pending_upload_before_stopping() {
    let mut session = Session::spawn(b"old\n");
    session.watch(false, true);
    while session.next_live()["stage"] != "Listening" {}
    session.append(b"new\n");
    loop {
        let message = session.next_live();
        if message["event"] == "segment" {
            assert_eq!(message["id"], 1);
            assert_eq!(message["events"], 1);
            break;
        }
    }
    session.send("stop", Value::Null);
    assert!(session.unzip("events.zip").contains("1|new\n"));
    session.send("ack", json!("report"));
    loop {
        let message = session.next_live();
        if message["event"] == "segment" {
            assert_eq!(message["id"], 1);
            assert_eq!(message["events"], 0);
            session.send("ack", json!("report"));
            break;
        }
    }
    assert_eq!(session.next_live()["event"], "finished");
    assert!(session.child.wait().unwrap().success());
    assert!(!session.dir.path().join("events.zip").exists());
}

#[test]
fn watch_drains_recent_append_and_rotated_file_without_poll_commands() {
    let mut session = Session::spawn(b"ignored\n");
    session.watch(false, false);
    while session.next_live()["stage"] != "Listening" {}
    session.append(b"previous");
    let path = session.dir.path().join("input.log");
    fs::rename(&path, session.dir.path().join("previous.bak")).unwrap();
    fs::write(&path, b"rotated\n").unwrap();
    while session.next_live()["stage"] != "Finishing previous log" {}
    while session.next_live()["stage"] != "Reading appended data" {}
    session.append(b"last\n");
    session.send("stop", Value::Null);
    while session.next_live()["event"] != "segment" {}
    assert!(session
        .unzip("events.zip")
        .contains("1|previous;rotated;last\n"));
    session.send("ack", json!("report"));
    assert_eq!(session.next_live()["event"], "finished");
    assert!(session.child.wait().unwrap().success());
}

#[test]
fn watch_exits_if_uploader_disconnects_during_history_upload() {
    let mut session = Session::spawn(b"history\n");
    session.watch(true, false);
    while session.next_live()["event"] != "segment" {}
    assert!(session.unzip("events.zip").contains("1|history\n"));
    session.child.stdin.take();
    assert!(!session.child.wait().unwrap().success());
}

#[test]
fn director_event_commits_once_without_realtime_uploads() {
    let mut session = Session::spawn(b"");
    session.watch(false, false);
    while session.next_live()["stage"] != "Listening" {}
    session.append(b"battle\n33|40000011|end\n");
    while session.next_live()["event"] != "segment" {}
    assert!(session
        .unzip("events.zip")
        .contains("battle;33|40000011|end"));
    session.send("ack", json!("report"));
    session.send("stop", Value::Null);
    loop {
        let event = session.next_live();
        if event["event"] == "finished" {
            break;
        }
        assert_eq!(event["event"], "progress", "duplicate committed segment");
    }
    assert!(session.child.wait().unwrap().success());
}
