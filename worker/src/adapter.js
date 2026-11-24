// Loaded in a fresh V8 isolate for each upload session.
const session = {
    region: "", raids: [], selected: [], scanning: false, live: false,
    code: undefined, date: undefined, current: null, master: null, parts: [],
    history: false, includeHistory: false, director: false, wipes: false
};

function dispatch(message, fields = {}) {
    let result;
    window._messageListener({
        data: { message, id: 1, ...fields },
        origin: "emulator",
        source: { postMessage(reply) {
            if (reply.message === message + "-completed")
                result = reply.data === undefined ? reply : reply.data;
        }}
    });
    __drainTimeouts();
    if (result?.success === false)
        throw Error(message + " failed" + (result.parsedLineCount ? " at line " + result.parsedLineCount : "") + ": " + (result.exception?.message ?? result.exception ?? "parser rejected request"));
    return result;
}

function setCode(code) {
    session.code = code;
    if (code) dispatch("set-report-code", {reportCode: code});
}

function timestamp(line) {
    const first = line.indexOf("|");
    if (first < 0) return NaN;
    const end = line.indexOf("|", first + 1);
    if (end < 0) return NaN;
    let value = line.slice(first + 1, end);
    if (/^\d{4}-\d\d-\d\dT[\d:.]+$/.test(value)) value += "Z";
    return Date.parse(value);
}

function begin(arg) {
    session.region = arg.region;
    session.live = arg.live;
    session.history = arg.live;
    session.includeHistory = arg.history;
    session.scanning = !arg.live || !arg.history;
    setCode(arg.code || undefined);
    if (arg.live && !arg.history) {
        let anchor;
        for (const line of arg.tail.split("\n").reverse()) {
            const time = timestamp(line);
            if (Number.isFinite(time)) { anchor = time; break; }
        }
        dispatch("set-live-logging-start-time", {startTime: anchor ?? Date.now()});
    }
}

function feed(lines, offset) {
    if (session.date === undefined) {
        for (const line of lines) {
            const date = timestamp(line);
            if (!Number.isFinite(date)) continue;
            session.date = date;
            dispatch("set-start-date", {startDate: date});
            if (session.live && session.includeHistory)
                dispatch("set-live-logging-start-time", {startTime: date});
            break;
        }
    }
    dispatch("parse-lines", {lines, scanning: session.scanning,
        selectedRegion: session.region, raidsToUpload: session.selected, logFilePosition: offset});
    if (session.live && !session.history) {
        for (const line of lines) {
            if (!line.startsWith("33|")) continue;
            if (session.wipes && line.includes("|40000011|")) dispatch("call-wipe");
            if (/\|400000(?:11|02|03|05)\|/.test(line)) session.director = true;
        }
    }
}

function raids() {
    session.raids = dispatch("collect-scanned-raids") ?? [];
    return session.raids.length;
}

function replay(index) {
    dispatch("clear-state");
    setCode(session.code);
    if (session.date !== undefined) dispatch("set-start-date", {startDate: session.date});
    session.selected = [session.raids[index]];
    session.scanning = false;
}

function collect(arg) {
    session.current = arg.provisional ? dispatch("collect-in-progress-fight") :
        dispatch("collect-fights", {pushFightIfNeeded: arg.final, scanningOnly: false});
    session.master = null;
    return session.current?.fights?.length ?? 0;
}

function prepare(index) {
    const data = session.current, fight = data.fights[index];
    const events = fight.eventsString || "";
    let end = events.length;
    while (end && (events[end-1] === "\n" || events[end-1] === "\r")) end--;
    const start = events.lastIndexOf("\n", end-1) + 1;
    const pipe = events.indexOf("|", start);
    const second = events.indexOf("\n", events.indexOf("\n") + 1);
    if (!end || second < 0 || start <= second || pipe < start || pipe >= end) return null;
    const timestamp = events.slice(start, pipe);
    if (!/^-?\d+$/.test(timestamp)) return null;
    const count = fight.eventCount ?? 0;
    if (!session.master) session.master = dispatch("collect-master-info", {reportCode: session.code});
    const m = session.master;
    if (typeof m?.actorsString !== "string") throw Error("Missing master table");
    const version = (data.logVersion ?? 72) + "|" + (data.gameVersion ?? 1);
    const master = [version + "|" + (data.logFileDetails ?? "") + "\n"];
    for (const [id, field] of [["Actor","actors"],["Ability","abilities"],["Tuple","tuples"],["Pet","pets"]]) {
        const part = m[field + "String"] || "";
        master.push((m["lastAssigned" + id + "ID"] ?? 0) + "\n", part);
        if (part && !part.endsWith("\n")) master.push("\n");
    }
    session.parts = [master, [version + "\n" + count + "\n", events], [m.playersString || ""]];
    return {start: data.startTime ?? 0, end: (data.startTime ?? 0) + Number(timestamp), count};
}

// Cut on UTF-16 scalar boundaries before converting each slice to UTF-8.
function piece(group, part, offset) {
    const value = session.parts[group][part];
    if (value === undefined) return null;
    let end = Math.min(offset + 32768, value.length);
    if (end < value.length && /[\uD800-\uDBFF]/.test(value[end-1])) end--;
    return value.slice(offset, end);
}

function control(op, arg) {
    switch (op) {
        case "begin": begin(arg); break;
        case "code": setCode(arg); break;
        case "raids": return raids();
        case "replay": replay(arg); break;
        case "tail": session.history = false; session.scanning = false; session.wipes = arg; break;
        case "director": { const seen = session.director; session.director = false; return seen; }
        case "inactive": return !!dispatch("check-dungeon-inactivity", {clientSideTime: Date.now()})?.justFired;
        case "collect": return collect(arg);
        case "count": return session.current?.fights?.[0]?.eventCount ?? 0;
        case "prepare": return prepare(arg);
        // Three UTF-8 bytes per UTF-16 code unit bounds the uncompressed size.
        case "zip64": return session.parts[arg].reduce((sum, part) => sum + part.length * 3, 0) >= 0xffffffff;
        case "release": session.current = session.master = null; session.parts = []; break;
        default: throw Error("Unknown parser command");
    }
    return null;
}
