using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using FFLogsPlugin.Handlers;
using FFLogsPlugin.Models;

namespace FFLogsPlugin.Services;

public sealed record LiveLogState(bool Active, int FightCount, string? ReportCode, UploadProgress? Progress);

/// <summary>
/// Handles all FFLogs API communication: login, report creation, uploads.
/// </summary>
public class FFLogsService
{
    private const string FFLOGS_URL = "https://www.fflogs.com";
    // Must match a currently-supported Archon App Lite version. FFLogs deprecated the
    // standalone "FFLogsUploader" client and now gates the desktop-client endpoints on
    // the Archon app's version/User-Agent — an out-of-date value is rejected server-side.
    // Values below are taken from Archon App Lite 9.5.0 (Chromium 142 / Electron 39.8.10);
    // re-derive from the app's asar (package.json version + a captured request) on updates.
    private const string CLIENT_VERSION = "9.5.0";
    private const string CHROME_VERSION = "142.0.7444.265";
    private const string ELECTRON_VERSION = "39.8.10";
    // Chromium's brand list changes format between major versions — 142 emits
    // "Not_A Brand";v="99" where 138 emitted "Not)A;Brand";v="8". Copy verbatim.
    private const string SEC_CH_UA = "\"Not_A Brand\";v=\"99\", \"Chromium\";v=\"142\"";
    private const int MaxRetries = 3;

    /// <summary>
    /// create-report "logMode" values, added in Archon 9.5.0.
    /// The server uses these to distinguish how a report was produced.
    /// </summary>
    private const int LogModeLive = 1;      // liveLogOperation, not auto-log
    private const int LogModeBulkFile = 2;  // uploadALogOperation (a log file on disk)
    // 3 = auto-log live logging; the plugin has no auto-log mode.
    
    private readonly Plugin plugin;
    private readonly CookieContainer cookies;

    // Expose HttpClient for other services that need authenticated access
    public HttpClient HttpClient { get; }
    
    public bool IsLoggedIn { get; private set; }
    public string? Username { get; private set; }
    private volatile IReadOnlyList<GuildInfo> guilds = Array.Empty<GuildInfo>();
    public IReadOnlyList<GuildInfo> Guilds => guilds;

    // From the login response's enabledFeatures. When true the account may upload fights
    // while they're still in progress (real-time live logging); otherwise in-progress
    // fights are only tracked for status and uploaded once they commit — matching Archon.
    public bool RealTimeLiveLoggingEnabled { get; private set; }

    // Recently uploaded report codes (for Parse Viewer)
    private readonly List<string> recentReports = new();
    public IReadOnlyList<string> RecentReportCodes
    {
        get { lock (recentReports) return recentReports.ToArray(); }
    }
    private const int MaxRecentReports = 10;

    // Live logging state
    private readonly object liveLock = new();
    private LiveLogState liveState = new(false, 0, null, null);
    public LiveLogState LiveState { get { lock (liveLock) return liveState; } }
    private bool unloading;

    internal void SetLiveProgress(UploadProgress progress)
    {
        lock (liveLock) liveState = liveState with { Progress = progress };
    }

    private CancellationTokenSource? liveLogCts;
    private Task? liveLogTask;
    private readonly CancellationTokenSource shutdown = new();

    internal CancellationToken ShutdownToken => shutdown.Token;

    internal void Shutdown()
    {
        shutdown.Cancel();
        HttpClient.CancelPendingRequests();
    }

    public FFLogsService(Plugin plugin)
    {
        this.plugin = plugin;
        
        cookies = new CookieContainer();
        var innerHandler = new HttpClientHandler
        {
            CookieContainer = cookies,
            UseCookies = true,
            // Archon advertises "gzip, deflate, br, zstd". We advertise everything the
            // Electron client does that .NET can also transparently decode — gzip,
            // deflate, and brotli. zstd is intentionally omitted: HttpClient cannot
            // decompress it, and advertising it would risk the server returning an
            // undecodable body. In practice FFLogs negotiates down to brotli anyway.
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };

        // The Archon uploader authenticates these desktop-client routes purely with the
        // session cookies (XSRF-TOKEN, wcl_session, remember_web) — the CookieContainer
        // sends those automatically. It never adds an X-XSRF-TOKEN *header*, so we don't
        // either; the routes are CSRF-exempt server-side. (The former XsrfDelegatingHandler
        // is left in the project unused in case a future endpoint needs it.)
        HttpClient = new HttpClient(innerHandler)
        {
            BaseAddress = new Uri(FFLOGS_URL)
        };

        // Only the headers Archon sends on *every* request live here. The Accept,
        // Sec-Fetch-Mode/Dest, sec-ch-ua*, Content-Type and Upgrade-Insecure-Requests
        // headers vary by request type (JSON API vs multipart upload vs iframe
        // navigation vs no-cors script) and are applied per request in
        // ApplyArchonHeaders so each call matches the real client byte-for-byte.
        HttpClient.DefaultRequestHeaders.Add("User-Agent", $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) ArchonAppLite/{CLIENT_VERSION} Chrome/{CHROME_VERSION} Electron/{ELECTRON_VERSION} Safari/537.36");
        HttpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US");
        HttpClient.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");
        HttpClient.DefaultRequestHeaders.Add("Sec-Fetch-Storage-Access", "active");
    }

    /// <summary>
    /// The kinds of request the Archon uploader makes, each with its own header
    /// fingerprint (Accept, Sec-Fetch-Mode/Dest, and whether sec-ch-ua* client hints
    /// are present). Derived from Fiddler captures of Archon App Lite 9.5.0.
    /// </summary>
    public enum RequestKind
    {
        /// <summary>POST with a JSON body (log-in, create-report).</summary>
        JsonApi,
        /// <summary>POST with an empty body (token/v2, terminate-report).</summary>
        EmptyPost,
        /// <summary>POST multipart/form-data upload (set-report-master-table, add-report-segment).</summary>
        Multipart,
        /// <summary>GET of the parser HTML page — loaded as an iframe navigation.</summary>
        ParserPage,
        /// <summary>GET of the parser JS from assets.rpglogs.com — a no-cors script fetch.</summary>
        Script,
    }

    /// <summary>
    /// Applies the per-request headers Archon sends for the given <paramref name="kind"/>,
    /// so plugin requests match the official uploader's fingerprint. The universal
    /// headers (User-Agent, Accept-Language, Sec-Fetch-Site, Sec-Fetch-Storage-Access)
    /// come from DefaultRequestHeaders; Content-Type is set on the request content.
    /// </summary>
    public static void ApplyArchonHeaders(HttpRequestMessage request, RequestKind kind)
    {
        // Chromium sends sec-ch-ua* client hints on every fetch/XHR/script request,
        // but omits them on top-level/iframe navigations — which is how Archon loads
        // the parser page.
        if (kind != RequestKind.ParserPage)
        {
            request.Headers.TryAddWithoutValidation("sec-ch-ua", SEC_CH_UA);
            request.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
            request.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
        }

        switch (kind)
        {
            case RequestKind.JsonApi:
            case RequestKind.EmptyPost:
                request.Headers.TryAddWithoutValidation("Accept", "*/*");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
                break;
            case RequestKind.Multipart:
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
                break;
            case RequestKind.ParserPage:
                request.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
                request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "iframe");
                break;
            case RequestKind.Script:
                request.Headers.TryAddWithoutValidation("Accept", "*/*");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "no-cors");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "script");
                break;
        }
    }

    /// <summary>
    /// Builds a JSON request body with a bare <c>application/json</c> Content-Type
    /// (no <c>; charset=utf-8</c> suffix) to match the official client exactly, while
    /// still encoding the payload as UTF-8.
    /// </summary>
    private static StringContent JsonBody(string json)
    {
        var content = new StringContent(json, Encoding.UTF8);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return content;
    }

    /// <summary>An empty POST body: Content-Length 0, no Content-Type (matches Archon).</summary>
    private static ByteArrayContent EmptyBody() => new(Array.Empty<byte>());

    public async Task<bool> LoginAsync(string email, string password)
    {
        try
        {
            Plugin.Log.Information("[Login] Starting login via desktop-client API...");
            
            var payload = new Dictionary<string, string>
            {
                ["email"] = email,
                ["password"] = password,
                ["version"] = CLIENT_VERSION,
                ["clientTime"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
            };

            var json = JsonSerializer.Serialize(payload);

            Plugin.Log.Debug("[Login] Posting to /desktop-client/log-in...");
            using var request = new HttpRequestMessage(HttpMethod.Post, "/desktop-client/log-in") { Content = JsonBody(json) };
            ApplyArchonHeaders(request, RequestKind.JsonApi);
            using var response = await HttpClient.SendAsync(request, shutdown.Token);
            
            Plugin.Log.Debug($"[Login] Response status: {response.StatusCode}");
            var responseBody = await response.Content.ReadAsStringAsync();
            Plugin.Log.Debug($"[Login] Response: {responseBody.Substring(0, Math.Min(500, responseBody.Length))}");

            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                Plugin.Log.Error($"[Login] Login failed with status {response.StatusCode}");
                return false;
            }

            // Parse user data from response
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                var user = doc.RootElement.GetProperty("user");
                Username = user.GetProperty("userName").GetString();
                Plugin.Log.Information($"[Login] Logged in as: {Username}");

                // Account feature flags (sibling of "user" in the response).
                RealTimeLiveLoggingEnabled = false;
                if (doc.RootElement.TryGetProperty("enabledFeatures", out var features) &&
                    features.TryGetProperty("realTimeLiveLogging", out var rtll))
                {
                    RealTimeLiveLoggingEnabled = rtll.ValueKind == JsonValueKind.True;
                }
                Plugin.Log.Information($"[Login] realTimeLiveLogging={RealTimeLiveLoggingEnabled}");

                // Parse guilds
                var parsedGuilds = new List<GuildInfo>();
                if (user.TryGetProperty("guilds", out var guildsArray))
                {
                    foreach (var guild in guildsArray.EnumerateArray())
                    {
                        var guildInfo = new GuildInfo
                        {
                            Id = guild.GetProperty("id").GetInt32().ToString(),
                            Name = guild.GetProperty("name").GetString() ?? Loc.Text("Unknown")
                        };
                        parsedGuilds.Add(guildInfo);
                        Plugin.Log.Debug($"[Login] Found guild: {guildInfo.Name}");
                    }
                }
                guilds = parsedGuilds.AsReadOnly();
                Plugin.Log.Information($"[Login] Found {parsedGuilds.Count} guilds");
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"[Login] Could not parse user data: {ex.Message}");
            }

            // Get cookies from response
            var responseCookies = cookies.GetCookies(new Uri(FFLOGS_URL));
            Plugin.Log.Debug($"[Login] Cookies received: {responseCookies.Count}");
            foreach (Cookie c in responseCookies)
            {
                Plugin.Log.Debug($"[Login] Cookie: {c.Name}");
            }

            // Refresh the session token (required after login). The Archon app moved
            // this from /desktop-client/token to /desktop-client/token/v2; the old path
            // is disabled. The v2 response body is a JWT used for websocket features we
            // don't need — we call it only for the XSRF/session cookie refresh side effect.
            Plugin.Log.Debug("[Login] Refreshing session token...");
            using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "/desktop-client/token/v2") { Content = EmptyBody() };
            ApplyArchonHeaders(tokenRequest, RequestKind.EmptyPost);
            var tokenResponse = await HttpClient.SendAsync(tokenRequest);
            Plugin.Log.Debug($"[Login] Token refresh status: {tokenResponse.StatusCode}");
            
            if (tokenResponse.StatusCode != System.Net.HttpStatusCode.OK)
            {
                Plugin.Log.Warning("[Login] Token refresh failed, but login may still work");
            }

            // XSRF header is now handled automatically by XsrfDelegatingHandler

            IsLoggedIn = true;
            Plugin.Log.Information("[Login] Login successful!");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[Login] Login failed with exception");
            return false;
        }
    }

    public void Logout()
    {
        IsLoggedIn = false;
        // No session fields to clear — XSRF is managed by the handler
    }

    public async Task<string> CreateReportAsync(string filename, string description, int visibility, int region, string? guildId = null, int logMode = LogModeBulkFile)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var payload = new Dictionary<string, object?>
        {
            ["clientVersion"] = CLIENT_VERSION,
            // Advertise the version reported by the actual parser bundle (detected on
            // download) rather than a hardcoded constant, so this stays correct across
            // upstream parser bumps without a code change.
            ["parserVersion"] = plugin.ParserService.ParserVersion,
            ["startTime"] = ts,
            ["endTime"] = ts,
            ["guildId"] = string.IsNullOrEmpty(guildId) ? null : int.Parse(guildId),
            ["fileName"] = Path.GetFileName(filename),
            ["serverOrRegion"] = region,
            ["visibility"] = visibility,
            ["reportTagId"] = null,
            ["description"] = description,
            // Added in Archon 9.5.0 — tells the server how this report was produced.
            ["logMode"] = logMode
        };

        var json = JsonSerializer.Serialize(payload);

        Plugin.Log.Debug($"[CreateReport] POST /desktop-client/create-report: {json}");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/desktop-client/create-report") { Content = JsonBody(json) };
        ApplyArchonHeaders(request, RequestKind.JsonApi);
        using var response = await HttpClient.SendAsync(request, shutdown.Token);
        
        var responseBody = await response.Content.ReadAsStringAsync();
        Plugin.Log.Debug($"[CreateReport] Response: {response.StatusCode} - {responseBody}");
        
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
        {
            throw new Exception(Loc.Format("Create report failed: {0} - {1}", response.StatusCode, responseBody));
        }

        string? createdCode;
        // Response may be just the code as a string or JSON with "code" field
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            createdCode = doc.RootElement.GetProperty("code").GetString();
        }
        catch
        {
            createdCode = responseBody.Trim().Trim('"');
        }
        
        Plugin.Log.Information($"[CreateReport] Created report: {createdCode}");
        return createdCode ?? throw new Exception(Loc.Text("No report code in response"));
    }

    public async Task<string> UploadLogAsync(string logPath, int region, int visibility, string? guildId, string description)
    {
        plugin.ParserService.Progress(false, Loc.Text("Preparing parser"));
        // 0. Make sure the parser bundle (and thus its parserVersion) is known before
        //    create-report advertises it. Cheap when the bundle is already cached.
        await plugin.ParserService.EnsureParserVersionAsync();

        plugin.ParserService.Progress(false, Loc.Text("Creating report"));
        // 1. Create report
        var reportCode = await CreateReportAsync(logPath, description, visibility, region, guildId, LogModeBulkFile);

        try
        {
            await Task.Run(() => plugin.ParserService.RunAsync(logPath, region, reportCode, false, true,
                async segment =>
                {
                    await SendSegmentFiles(reportCode, segment, false);
                    return reportCode;
                }), shutdown.Token);
        }
        catch (Exception ex)
        {
            await ClosePartialReport(reportCode);
            throw new IOException(Loc.Format("Report {0}: {1}", reportCode, ex.Message), ex);
        }
        plugin.ParserService.Progress(false, Loc.Text("Finalizing report"));
        await TerminateReportAsync(reportCode);
        plugin.ParserService.Progress(false, Loc.Text("Complete"), 1, 1);

        TrackReportCode(reportCode);
        Plugin.Log.Information($"Upload complete: {reportCode}");
        return reportCode;
    }

    public Task StartLiveLogAsync(string logDirectory, int region, int visibility, string? guildId, string description, bool uploadPreviousFights = true)
    {
        lock (liveLock)
        {
            if (unloading) throw new ObjectDisposedException(nameof(FFLogsService));
            if (liveLogCts != null) return liveLogTask!;
            var session = new CancellationTokenSource();
            liveLogCts = session;
            liveState = new(true, 0, null, new(Loc.Text("Preparing parser")));
            return liveLogTask = Task.Run(() => RunSession(session));
        }

        async Task RunSession(CancellationTokenSource session)
        {
            string? reportCode = null;
            bool finalizing = false;

            try
            {
                await plugin.ParserService.RunAsync(logDirectory, region, null, true, uploadPreviousFights, async segment =>
                {
                    reportCode ??= await CreateReportAsync("live.log", description, visibility, region, guildId, LogModeLive);
                    lock (liveLock) liveState = liveState with { ReportCode = reportCode };
                    await SendSegmentFiles(reportCode, segment, true);
                    if (segment.ProvisionalCount == 0)
                        lock (liveLock) liveState = liveState with { FightCount = liveState.FightCount + 1 };
                    return reportCode;
                }, session.Token);
                if (reportCode != null)
                {
                    finalizing = true;
                    plugin.ParserService.Progress(true, Loc.Text("Finalizing report"));
                    await TerminateReportAsync(reportCode);
                    TrackReportCode(reportCode);
                }
                plugin.ParserService.Progress(true, Loc.Text("Stopped"));
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                plugin.ParserService.Progress(true, Loc.Text("Stopped"));
                Plugin.Log.Information("[LiveLog] Cancelled");
            }
            catch (Exception ex)
            {
                if (reportCode != null && !finalizing) await ClosePartialReport(reportCode);
                plugin.ParserService.Progress(true, Loc.Format("Error: {0}", ex.Message));
                Plugin.Log.Error(ex, "[LiveLog] Error");
            }
            finally
            {
                lock (liveLock)
                {
                    liveState = liveState with { Active = false };
                    liveLogCts = null;
                    session.Dispose();
                }
            }
        }
    }

    internal Task StopLiveLogForUnload()
    {
        lock (liveLock)
        {
            unloading = true;
            liveLogCts?.Cancel();
            return liveLogTask ?? Task.CompletedTask;
        }
    }

    private void TrackReportCode(string code)
    {
        lock (recentReports)
        {
            recentReports.Remove(code);
            recentReports.Insert(0, code);
            if (recentReports.Count > MaxRecentReports) recentReports.RemoveAt(MaxRecentReports);
        }
    }

    public void StopLiveLog()
    {
        lock (liveLock) liveLogCts?.Cancel();
    }

    /// <summary>
    /// Executes an async action with exponential backoff retry.
    /// </summary>
    private async Task WithRetryAsync(Func<Task> action, int maxRetries = MaxRetries)
    {
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            shutdown.Token.ThrowIfCancellationRequested();
            try
            {
                await action();
                return;
            }
            catch (Exception ex) when (attempt < maxRetries && !shutdown.IsCancellationRequested)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 1s, 2s, 4s
                Plugin.Log.Warning($"[Retry] Attempt {attempt + 1}/{maxRetries} failed: {ex.Message}. Retrying in {delay.TotalSeconds}s...");
                await Task.Delay(delay, shutdown.Token);
            }
        }
    }

    private static string GenerateWebKitBoundary()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var random = new Random();
        var suffix = new char[16];
        for (int i = 0; i < suffix.Length; i++)
            suffix[i] = chars[random.Next(chars.Length)];
        return $"----WebKitFormBoundary{new string(suffix)}";
    }

    private static HttpContent CreateStringPart(string name, string value)
    {
        var content = new StringContent(value);
        content.Headers.ContentType = null;
        content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("form-data")
        {
            Name = $"\"{name}\""
        };
        return content;
    }

    private HttpContent CreateFilePart(string path, bool live, string stage)
    {
        var content = new SegmentFileContent(path, (sent, length) => plugin.ParserService.Progress(live, stage, sent, length));
        content.Headers.Clear();
        // Add headers in exact order to match the official client
        content.Headers.TryAddWithoutValidation("Content-Disposition", "form-data; name=\"logfile\"; filename=\"blob\"");
        content.Headers.TryAddWithoutValidation("Content-Type", "application/zip");
        return content;
    }

    private async Task SendSegmentFiles(string reportCode, Segment segment, bool live)
    {
        plugin.ParserService.Progress(live, Loc.Format("Uploading segment {0} master table", segment.Id));
        await WithRetryAsync(() => UploadMasterTableAsync(reportCode, segment, live));
        plugin.ParserService.Progress(live, Loc.Format("Uploading segment {0} events", segment.Id));
        await WithRetryAsync(() => UploadSegmentAsync(reportCode, segment, live));
    }

    private async Task UploadMasterTableAsync(string reportCode, Segment segment, bool live)
    {
        var segmentId = segment.Id;
        var isRealTime = segment.ProvisionalCount > 0;
        var boundary = GenerateWebKitBoundary();
        using var content = new MultipartFormDataContent(boundary);
        // .NET quotes the boundary by default; the FFLogs server expects an
        // unquoted boundary (matching the official Electron uploader).
        content.Headers.Remove("Content-Type");
        content.Headers.TryAddWithoutValidation("Content-Type", $"multipart/form-data; boundary={boundary}");

        // Field order matches Archon 9.5.0: segmentId, isRealTime, logfile, players.
        content.Add(CreateStringPart("segmentId", segmentId.ToString()));
        content.Add(CreateStringPart("isRealTime", isRealTime.ToString().ToLower()));
        content.Add(CreateFilePart(Path.Combine(segment.Folder, "master.zip"), live, Loc.Format("Uploading segment {0} master table", segment.Id)));
        // "players" roster added in Archon 9.5.0 / parser 3075.
        var players = new StreamContent(File.OpenRead(Path.Combine(segment.Folder, "players.txt")));
        players.Headers.TryAddWithoutValidation("Content-Disposition", "form-data; name=\"players\"");
        content.Add(players);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/desktop-client/set-report-master-table/{reportCode}")
        {
            Content = content
        };
        ApplyArchonHeaders(request, RequestKind.Multipart);

        using var response = await HttpClient.SendAsync(request, shutdown.Token);
        response.EnsureSuccessStatusCode();
        
        Plugin.Log.Debug($"Master table uploaded for {reportCode}");
    }

    private async Task UploadSegmentAsync(string reportCode, Segment segment, bool isLive)
    {
        var segmentId = segment.Id;
        var isRealTime = segment.ProvisionalCount > 0;
        var parameters = JsonSerializer.Serialize(new
        {
            startTime = segment.Start,
            endTime = segment.End,
            mythic = 0,
            isLiveLog = isLive,
            isRealTime,
            inProgressEventCount = segment.ProvisionalCount,
            segmentId
        });

        var boundary = GenerateWebKitBoundary();
        using var formContent = new MultipartFormDataContent(boundary);
        // .NET quotes the boundary by default; the FFLogs server expects an
        // unquoted boundary (matching the official Electron uploader).
        formContent.Headers.Remove("Content-Type");
        formContent.Headers.TryAddWithoutValidation("Content-Type", $"multipart/form-data; boundary={boundary}");

        formContent.Add(CreateFilePart(Path.Combine(segment.Folder, "events.zip"), isLive, Loc.Format("Uploading segment {0} events", segment.Id)));
        formContent.Add(CreateStringPart("parameters", parameters));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/desktop-client/add-report-segment/{reportCode}")
        {
            Content = formContent
        };
        ApplyArchonHeaders(request, RequestKind.Multipart);

        using var response = await HttpClient.SendAsync(request, shutdown.Token);
        response.EnsureSuccessStatusCode();
        
        Plugin.Log.Debug($"Segment {segmentId} uploaded for {reportCode} (isLive={isLive})");
    }

    private async Task ClosePartialReport(string reportCode)
    {
        if (shutdown.IsCancellationRequested) return;
        try { await TerminateReportAsync(reportCode); }
        catch (Exception ex) { Plugin.Log.Warning($"Could not finalize partial report {reportCode}: {ex.Message}"); }
        TrackReportCode(reportCode);
    }

    private async Task TerminateReportAsync(string reportCode)
    {
        try
        {
            await WithRetryAsync(async () =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"/desktop-client/terminate-report/{reportCode}") { Content = EmptyBody() };
                ApplyArchonHeaders(request, RequestKind.EmptyPost);
                using var response = await HttpClient.SendAsync(request, shutdown.Token);
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(Loc.Format("Terminate failed with status {0}", response.StatusCode));
                }
                Plugin.Log.Debug($"Report terminated: {reportCode}");
            });
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[TerminateReport] Failed to terminate report {reportCode} after {MaxRetries} retries: {ex.Message}. " +
                               "The report may be left in an incomplete state on FFLogs — check the website.");
            throw;
        }
    }
}

public class GuildInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}
