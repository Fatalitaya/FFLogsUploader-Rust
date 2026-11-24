let recorded = [], offsets = [];
window.addEventListener("message", event => {
    const cmd = event.data;
    const reply = data => {
        data.message = cmd.message + "-completed";
        event.source.postMessage(data);
    };
    switch (cmd.message) {
        case "parse-lines":
            offsets.push(cmd.logFilePosition);
            if (!cmd.scanning) recorded.push(...cmd.lines);
            reply({success:true}); break;
        case "collect-scanned-raids": reply([{start:1,end:2}]); break;
        case "clear-state": recorded = []; offsets = []; break;
        case "collect-fights":
        case "collect-in-progress-fight":
            reply({startTime:100, fights:[{eventsString:"72|1\n2\n1|" + recorded.join(";") + "\n2|end\n", eventCount:recorded.length}]});
            break;
        case "collect-master-info":
            reply({actorsString:"offsets=" + offsets.join(","), playersString:"玩家😀"});
            break;
        case "check-dungeon-inactivity": reply({justFired:false}); break;
    }
});
