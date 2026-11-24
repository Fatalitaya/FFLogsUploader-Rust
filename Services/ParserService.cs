using System;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FFLogsPlugin.Services;

public sealed partial class ParserService : IDisposable
{
    private const string FFLOGS_URL = "https://www.fflogs.com";

    private const string ParserGameContentDetectionEnabled = "true";
    private const string ParserMetersEnabled = "false";
    private const string ParserLiveFightDataEnabled = "false";
    private const int FallbackParserVersion = 3075;
    private readonly Plugin plugin;
    private string? parserBundlePath;
    private readonly SemaphoreSlim cacheLock = new(1, 1);
    public int ParserVersion { get; private set; } = FallbackParserVersion;
    private HttpClient HttpClient => plugin.FFLogsService.HttpClient;
    public ParserService(Plugin plugin) => this.plugin = plugin;

    private async Task EnsureParserAsync(CancellationToken token)
    {
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XIVLauncher", "pluginConfigs", "FFLogsPlugin"
        );
        Directory.CreateDirectory(cacheDir);
        // Cache key bumped when the upstream parser changes (now parserVersion 3075).
        // Bumping it forces a re-download rather than waiting out the 24h expiry —
        // required here because 3075 introduced the playersString roster that the
        // master-table upload now sends as its "players" field.
        parserBundlePath = Path.Combine(cacheDir, "fflogs_parser_v8_v4.js");

        // Best-effort cleanup of older cache files so they don't accumulate.
        try
        {
            foreach (var stale in new[] { "fflogs_parser_v8.js", "fflogs_parser_v8_v2.js", "fflogs_parser_v8_v3.js", "fflogs_parser_bundle.js" })
            {
                var p = Path.Combine(cacheDir, stale);
                if (File.Exists(p)) File.Delete(p);
            }
        }
        catch { }

        // If cached and fresh (< 24 hours), validate and use it
        if (File.Exists(parserBundlePath) && File.GetLastWriteTimeUtc(parserBundlePath) > DateTime.UtcNow.AddHours(-24))
        {
            var cachedSize = new FileInfo(parserBundlePath).Length;
            if (cachedSize > 10000)
            {
                Plugin.Log.Information($"Using cached parser: {parserBundlePath}");
                DetectParserVersion(await File.ReadAllTextAsync(parserBundlePath, token).ConfigureAwait(false));
                return;
            }

            Plugin.Log.Warning($"Cached parser bundle appears invalid (size={cachedSize}), re-downloading...");
            File.Delete(parserBundlePath);
        }

        Plugin.Log.Information("Downloading FFLogs parser...");
        var tempPath = parserBundlePath + ".tmp";

        try
        {
            // Fetch the parser page
            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var parserPageUrl = $"{FFLOGS_URL}/desktop-client/parser?id=1&ts={ts}&gameContentDetectionEnabled={ParserGameContentDetectionEnabled}&metersEnabled={ParserMetersEnabled}&liveFightDataEnabled={ParserLiveFightDataEnabled}";

            // Archon loads this page as an iframe navigation, not a fetch — match that
            // request fingerprint (navigate/iframe, no sec-ch-ua* client hints).
            string html;
            using (var pageRequest = new HttpRequestMessage(HttpMethod.Get, parserPageUrl))
            {
                FFLogsService.ApplyArchonHeaders(pageRequest, FFLogsService.RequestKind.ParserPage);
                using var pageResponse = await HttpClient.SendAsync(pageRequest, token).ConfigureAwait(false);
                pageResponse.EnsureSuccessStatusCode();
                html = await pageResponse.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            }

            // Extract parser URL from HTML
            var match = Regex.Match(html, @"src=""([^""]+parser-ff[^""]+)""");
            if (!match.Success)
            {
                throw new Exception(Loc.Text("Could not find parser URL in response"));
            }

            var parserUrl = match.Groups[1].Value;
            Plugin.Log.Debug($"Found parser URL: {parserUrl}");

            // Download the parser JS — Archon fetches this from assets.rpglogs.com as a
            // no-cors <script> load. Match that fingerprint (no-cors/script).
            string parserCode;
            using (var scriptRequest = new HttpRequestMessage(HttpMethod.Get, parserUrl))
            {
                FFLogsService.ApplyArchonHeaders(scriptRequest, FFLogsService.RequestKind.Script);
                using var scriptResponse = await HttpClient.SendAsync(scriptRequest, token).ConfigureAwait(false);
                scriptResponse.EnsureSuccessStatusCode();
                parserCode = await scriptResponse.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            }

            // Extract inline glue code
            var scriptBlocks = Regex.Matches(html, @"<script type=""text/javascript"">([\s\S]*?)</script>");
            var glueCode = "";
            foreach (Match m in scriptBlocks)
            {
                if (m.Groups[1].Value.Contains("ipcCollectFights"))
                {
                    glueCode = m.Groups[1].Value;
                    break;
                }
            }

            // Read the parser version out of the (non-minified) glue while we have it.
            DetectParserVersion(glueCode);

            // Bundle parser + glue only (no Node.js wrapper needed)
            var fullBundle = parserCode + "\n\n" + glueCode;

            // Validate
            if (fullBundle.Length < 10000 || !fullBundle.Contains("ipcCollectFights"))
            {
                throw new Exception(Loc.Format("Downloaded parser bundle appears invalid (size={0}, missing expected markers). This may be a partial download or a Cloudflare error page.", fullBundle.Length));
            }

            if (fullBundle.Contains("<script") || fullBundle.Contains("</script>"))
            {
                throw new Exception(Loc.Text("Parser bundle contains raw HTML tags — the FFLogs parser page structure may have changed. Please report this issue."));
            }

            await File.WriteAllTextAsync(tempPath, fullBundle, token).ConfigureAwait(false);

            if (File.Exists(parserBundlePath))
                File.Delete(parserBundlePath);
            File.Move(tempPath, parserBundlePath);

            Plugin.Log.Information($"Parser downloaded and cached ({fullBundle.Length} bytes)");
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            if (ex is OperationCanceledException && token.IsCancellationRequested) throw;
            Plugin.Log.Error(ex, "Failed to download parser");
            throw new Exception(Loc.Format("Failed to download parser: {0}", ex.Message));
        }
    }

    /// <summary>
    /// Ensures the parser bundle is downloaded/cached and returns its parser version.
    /// Cheap after the first call (the bundle is cached for 24h). Call this before
    /// creating a report so create-report advertises the exact parserVersion the
    /// current parser reports, instead of a hardcoded constant.
    /// </summary>
    public async Task<int> EnsureParserVersionAsync(CancellationToken stop = default)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stop, plugin.FFLogsService.ShutdownToken);
        var token = cancel.Token;
        await cacheLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await EnsureParserAsync(token).ConfigureAwait(false);
            return ParserVersion;
        }
        finally { cacheLock.Release(); }
    }

    /// <summary>
    /// Reads the parser version out of a bundle/glue string — the glue defines it as a
    /// non-minified <c>const parserVersion = N;</c>. Updates <see cref="ParserVersion"/>
    /// on success; leaves the previous (fallback) value if the marker isn't found.
    /// </summary>
    private void DetectParserVersion(string bundleOrGlue)
    {
        // The parser JS core and the glue each declare `const parserVersion = N`. When
        // passed the full bundle (parserCode + glue), the glue's value is the last match
        // because the glue is always concatenated last — take that one.
        var matches = Regex.Matches(bundleOrGlue, @"const\s+parserVersion\s*=\s*(\d{3,6})");
        if (matches.Count > 0 &&
            int.TryParse(matches[^1].Groups[1].Value, out var v) && v > 0)
        {
            if (v != ParserVersion)
                Plugin.Log.Information($"[Parser] Detected parserVersion={v} (was {ParserVersion})");
            ParserVersion = v;
        }
        else
        {
            Plugin.Log.Warning($"[Parser] Could not detect parserVersion in bundle; using {ParserVersion}");
        }
    }

}
