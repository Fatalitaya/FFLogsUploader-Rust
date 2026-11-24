using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FFLogsPlugin.Services;

internal sealed class ParserWorker : IDisposable
{
    private readonly Process process;
    private readonly Task<string> errors;
    private readonly object inputLock = new();
    private int disposed;
    public string DirectoryPath { get; }
    public ParserWorker(string pluginDirectory)
    {
        var platform = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var rid = platform + "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var executable = Path.Combine(pluginDirectory, "workers", rid,
            OperatingSystem.IsWindows() ? "fflogs-worker.exe" : "fflogs-worker");
        if (!File.Exists(executable)) throw new FileNotFoundException(Loc.Format("Missing parser worker for {0}", rid), executable);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute);
        DirectoryPath = Directory.CreateTempSubdirectory("fflogs-").FullName;
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = DirectoryPath, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        try
        {
            process = Process.Start(info) ?? throw new IOException(Loc.Text("Could not launch parser worker"));
            errors = process.StandardError.ReadToEndAsync();
        }
        catch { Directory.Delete(DirectoryPath); throw; }
    }

    // Commands are sequenced by the session. Only small responses cross this pipe.
    public async Task<JsonElement> Call(string op, object? arg = null)
    {
        Send(op, arg);
        return await ReadResponse(op);
    }

    internal void Send(string op, object? arg = null)
    {
        lock (inputLock)
        {
            process.StandardInput.WriteLine(JsonSerializer.Serialize(new { v = 1, op, arg }));
            process.StandardInput.Flush();
        }
    }

    internal void RequestStop()
    {
        try { Send("stop"); }
        catch (IOException) { } // The reader reports a closed worker and its stderr.
    }

    internal async Task<JsonElement> ReadResponse(string op)
    {
        string? line;
        try { line = await process.StandardOutput.ReadLineAsync(); }
        catch (IOException) when (process.HasExited) { line = null; }
        if (line == null)
        {
            await process.WaitForExitAsync();
            throw new IOException(Loc.Format("Parser worker exited during {0} (code {1}). {2}", op, process.ExitCode, (await errors).Trim()));
        }
        using var response = JsonDocument.Parse(line);
        if (response.RootElement.GetProperty("v").GetInt32() != 1)
            throw new InvalidDataException(Loc.Text("Parser worker protocol mismatch"));
        return response.RootElement.GetProperty("result").Clone();
    }

    public void Abort()
    {
        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try
        {
            try { if (!process.HasExited) process.StandardInput.Close(); }
            catch (IOException) when (process.HasExited) { }
            if (!process.WaitForExit(1000))
            {
                Abort();
                process.WaitForExit();
            }
        }
        finally
        {
            process.Dispose();
            try { Directory.Delete(DirectoryPath, true); }
            catch (IOException ex) { Plugin.Log.Warning($"Parser directory cleanup failed: {ex.Message}"); }
        }
    }
}
