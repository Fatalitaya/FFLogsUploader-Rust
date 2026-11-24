using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FFLogsPlugin.Models;

namespace FFLogsPlugin.Services;

public sealed record UploadProgress(string Stage, long Position = 0, long Total = 0, bool BytesPending = false);
internal sealed record Segment(string Folder, int Id, long Start, long End, int ProvisionalCount);

public sealed partial class ParserService
{
    private readonly HashSet<ParserWorker> workers = new();
    private bool disposed;
    public volatile UploadProgress? BulkProgress;

    internal void Progress(bool live, string stage, long position = 0, long total = 0, bool bytesPending = false)
    {
        var state = new UploadProgress(stage, position, total, bytesPending);
        if (live) plugin.FFLogsService.SetLiveProgress(state);
        else BulkProgress = state;
    }

    public void Dispose()
    {
        lock (workers)
        {
            disposed = true;
            foreach (var worker in workers) worker.Abort();
        }
    }

    internal async Task RunAsync(string path, int region, string? reportCode, bool live, bool history,
        Func<Segment, Task<string>> upload, CancellationToken stop = default)
    {
        Progress(live, Loc.Text("Preparing parser"));
        if (live || parserBundlePath == null)
        {
            try { await EnsureParserVersionAsync(stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
        }
        if (live && stop.IsCancellationRequested) return;

        ParserWorker worker;
        lock (workers)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            worker = new ParserWorker(Plugin.PluginInterface.AssemblyLocation.DirectoryName!);
            workers.Add(worker);
        }
        long position = 0;
        string stage = Loc.Text("Opening log");
        async Task ReadPass(string label, bool finalLine)
        {
            stage = label;
            long nextUpdate = 0;
            while (true)
            {
                var batch = await worker.Call("batch", finalLine);
                position = batch.GetProperty("position").GetInt64();
                var total = batch.GetProperty("total").GetInt64();
                var done = batch.GetProperty("done").GetBoolean();
                if (done || Environment.TickCount64 >= nextUpdate)
                {
                    Progress(live, label, position, total);
                    nextUpdate = Environment.TickCount64 + 200;
                }
                if (done || stop.IsCancellationRequested) return;
            }
        }

        int bulkSegments = 0;
        async Task Collect()
        {
            stage = Loc.Text("Collecting fights");
            int count = (await worker.Call("collect", new { final = false })).GetInt32();
            for (int i = 0; i < count; i++)
            {
                var fight = await worker.Call("prepare", i);
                if (fight.ValueKind == JsonValueKind.Null) continue;
                stage = Loc.Format("Uploading segment {0}", ++bulkSegments);
                await upload(new Segment(worker.DirectoryPath, bulkSegments,
                    fight.GetProperty("start").GetInt64(), fight.GetProperty("end").GetInt64(), 0));
                await worker.Call("ack");
            }
            await worker.Call("release");
        }

        try
        {
            if (live)
            {
                stage = Loc.Text("Monitoring live log");
                var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(Path.GetFullPath(path))!;
                worker.Send("watch", new { directory = Path.GetFullPath(directory), bundle = parserBundlePath,
                    region = ((FFLogsRegion)region).ToCode(), history, realTime = plugin.FFLogsService.RealTimeLiveLoggingEnabled });
                using var registration = stop.Register(worker.RequestStop);
                while (true)
                {
                    var message = await worker.ReadResponse("watch");
                    switch (message.GetProperty("event").GetString())
                    {
                        case "finished":
                            return;
                        case "progress":
                            var workerStage = message.GetProperty("stage").GetString()!;
                            stage = Loc.Text(workerStage);
                            position = message.GetProperty("position").GetInt64();
                            Progress(true, stage, position, message.GetProperty("total").GetInt64(),
                                workerStage == "Reading appended data");
                            break;
                        case "segment":
                            int id = message.GetProperty("id").GetInt32();
                            stage = Loc.Format("Uploading segment {0}", id);
                            var code = await upload(new Segment(worker.DirectoryPath, id,
                                message.GetProperty("start").GetInt64(), message.GetProperty("end").GetInt64(),
                                message.GetProperty("events").GetInt32()));
                            worker.Send("ack", code);
                            break;
                        default:
                            throw new InvalidDataException(Loc.Text("Unexpected live worker event"));
                    }
                }
            }

            await worker.Call("open", new { path = Path.GetFullPath(path), bundle = parserBundlePath,
                region = ((FFLogsRegion)region).ToCode(), live, history, code = reportCode });
            await ReadPass(Loc.Text("Scanning log"), true);
            await worker.Call("verify");
            int raids = (await worker.Call("raids")).GetInt32();
            for (int i = 0; i < raids; i++)
            {
                await worker.Call("replay", i);
                await ReadPass(Loc.Format("Parsing raid {0}/{1}", i + 1, raids), true);
                await worker.Call("verify");
                await Collect();
            }
            await worker.Call("stop");
        }
        catch (Exception ex)
        {
            throw new IOException(Loc.Format("{0} at byte {1}: {2}", stage, position, ex.Message), ex);
        }
        finally
        {
            lock (workers) workers.Remove(worker);
            worker.Dispose();
        }
    }
}
