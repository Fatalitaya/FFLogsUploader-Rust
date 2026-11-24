using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using FFLogsPlugin.Helpers;
using FFLogsPlugin.Models;
using FFLogsPlugin.Services;

namespace FFLogsPlugin.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    private readonly FileDialogManager fileDialogManager = new();

    private string logPath = string.Empty;
    private int selectedGuildIndex = 0;
    private int selectedRegion = 0;
    private int selectedVisibility = 1;
    private string description = string.Empty;
    private bool uploadPreviousFights = true;

    private volatile string statusMessage = string.Empty;
    private volatile bool isProcessing = false;

    private readonly string[] visibilities = { Loc.Text("Public"), Loc.Text("Private"), Loc.Text("Unlisted") };

    public MainWindow(Plugin plugin)
        : base(Loc.Text("FFLogs Uploader") + "###FFLogsMain", ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(500, 400),
            MaximumSize = new Vector2(800, 700)
        };

        logPath = plugin.Configuration.LogDirectory;
        
        // Auto-detect if empty OR if saved path is invalid
        if (string.IsNullOrEmpty(logPath) || !LogFileHelper.IsValidLogPath(logPath))
        {
            logPath = LogFileHelper.AutoDetectLogDirectory();
            if (!string.IsNullOrEmpty(logPath))
            {
                plugin.Configuration.LogDirectory = logPath;
                plugin.Configuration.Save();
            }
        }

        // Auto-populate with the latest log file for easier manual upload
        if (!string.IsNullOrEmpty(logPath) && System.IO.Directory.Exists(logPath))
        {
            var latestFile = LogFileHelper.GetLatestLogFileFromPath(logPath);
            if (latestFile != logPath)
            {
                logPath = latestFile;
            }
        }

        selectedRegion = FFLogsRegionExtensions.ApiValueToComboIndex(plugin.Configuration.Region);
        selectedVisibility = plugin.Configuration.Visibility;
    }

    public void Dispose() { }

    public override void Draw()
    {
        fileDialogManager.Draw();

        DrawHeader();
        ImGui.Separator();

        if (ImGui.BeginTabBar("FFLogsTabs"))
        {
            if (ImGui.BeginTabItem(Loc.Text("Live Log")))
            {
                DrawLiveLogTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem(Loc.Text("Upload a Log")))
            {
                DrawUploadTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawHeader()
    {
        var displayName = plugin.FFLogsService.Username ?? plugin.Configuration.Email ?? Loc.Text("Unknown");
        ImGui.Text(Loc.Format("Logged in as: {0}", displayName));
        ImGui.SameLine(ImGui.GetWindowWidth() - 80);
        if (ImGui.Button(Loc.Text("Log Out")))
        {
            plugin.FFLogsService.Logout();
            plugin.ShowLoginWindow();
        }
    }

    private void DrawLiveLogTab()
    {
        var live = plugin.FFLogsService.LiveState;
        ImGui.Spacing();
        ImGui.Text(Loc.Text("Choose the directory that the ACT FFXIV plug-in writes logs to:"));
        ImGui.SetNextItemWidth(-80);
        
        ImGui.BeginDisabled(live.Active);
        
        ImGui.InputText("##logdir", ref logPath, 512);
        ImGui.SameLine();
        if (ImGui.Button(Loc.Text("Browse") + "###dir"))
        {
            fileDialogManager.OpenFolderDialog(
                Loc.Text("Select Log Directory"),
                (success, path) =>
                {
                    if (success && !string.IsNullOrEmpty(path))
                        logPath = path;
                },
                GetDialogStartPath());
        }

        ImGui.Spacing();
            
        DrawSharedOptions();
        ImGui.Spacing();

        ImGui.Checkbox(Loc.Text("Include the Entire File in the Report"), ref uploadPreviousFights);
        
        ImGui.EndDisabled();
            
        ImGui.Spacing();

        DrawProgress(live.Progress);
        if (live.Active)
        {
            ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.2f, 1.0f), 
                Loc.Format("● Live logging active - {0} fight(s) uploaded", live.FightCount));
            ImGui.Text(Loc.Format("Report: {0}", live.ReportCode ?? Loc.Text("Unknown")));
            ImGui.Spacing();
            
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.7f, 0.2f, 0.2f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.8f, 0.3f, 0.3f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.9f, 0.1f, 0.1f, 1.0f));
            
            if (ImGui.Button(Loc.Text("Stop Live Logging"), new Vector2(-1, 40)))
            {
                plugin.FFLogsService.StopLiveLog();
            }
            
            ImGui.PopStyleColor(3);
            
            if (live.FightCount > 0 && !string.IsNullOrEmpty(live.ReportCode))
            {
                ImGui.Spacing();
                if (ImGui.Button(Loc.Text("View Parses"), new Vector2(-1, 30)))
                {
                    plugin.ShowParseViewer(live.ReportCode);
                }
            }
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.5f, 0.2f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.3f, 0.6f, 0.3f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.1f, 0.7f, 0.1f, 1.0f));
            
            if (ImGui.Button(Loc.Text("Start Live Logging"), new Vector2(-1, 40)))
            {
                StartLiveLog();
            }
            
            ImGui.PopStyleColor(3);
        }
    }

    private void DrawUploadTab()
    {
        ImGui.Spacing();
        ImGui.Text(Loc.Text("Choose the file you want to upload:"));
        ImGui.SetNextItemWidth(-80);
        ImGui.InputText("##logfile", ref logPath, 512);
        ImGui.SameLine();
        if (ImGui.Button(Loc.Text("Browse") + "###file"))
        {
            fileDialogManager.OpenFileDialog(
                Loc.Text("Select Log File"),
                Loc.Text("Log files") + "{.log}," + Loc.Text("All files") + "{.*}",
                (success, paths) =>
                {
                    if (success && paths.Count > 0)
                        logPath = paths[0];
                },
                1,
                GetDialogStartPath());
        }

        ImGui.Spacing();
        DrawSharedOptions();
        ImGui.Spacing();

        DrawGoButton(Loc.Text("Go!"), StartUpload);

        if (isProcessing) DrawProgress(plugin.ParserService.BulkProgress);
        DrawStatus();
    }

    private void DrawSharedOptions()
    {
        ImGui.Text(Loc.Text("Choose the guild you want to upload to:"));
        
        var guildList = new List<(string? Id, string Name)> { (null, Loc.Text("Personal Logs")) };
        foreach (var guild in plugin.FFLogsService.Guilds)
        {
            guildList.Add((guild.Id, guild.Name));
        }
        
        var guildNames = new string[guildList.Count];
        for (int i = 0; i < guildList.Count; i++)
            guildNames[i] = guildList[i].Name;
        
        if (selectedGuildIndex >= guildList.Count)
            selectedGuildIndex = 0;
        
        ImGui.SetNextItemWidth(150);
        ImGui.Combo("##guild", ref selectedGuildIndex, guildNames, guildNames.Length);
        ImGui.SameLine();

        if (selectedGuildIndex == 0)
        {
            ImGui.SetNextItemWidth(85);
            ImGui.Combo("##region", ref selectedRegion, FFLogsRegionExtensions.DisplayNames, FFLogsRegionExtensions.DisplayNames.Length);
            ImGui.SameLine();
        }

        ImGui.SetNextItemWidth(120);
        ImGui.Combo("##visibility", ref selectedVisibility, visibilities, visibilities.Length);

        ImGui.Spacing();
        ImGui.Text(Loc.Text("Enter a description for the report:"));
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##description", ref description, 256);
    }

    private string? GetSelectedGuildId()
    {
        if (selectedGuildIndex <= 0)
            return null;
        
        var guilds = plugin.FFLogsService.Guilds;
        if (selectedGuildIndex - 1 < guilds.Count)
            return guilds[selectedGuildIndex - 1].Id;
        
        return null;
    }

    /// <summary>
    /// Returns the best starting directory for the file dialog based on the current logPath.
    /// </summary>
    private string GetDialogStartPath()
    {
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            // If logPath is a file, use its directory
            if (System.IO.File.Exists(logPath))
            {
                var dir = System.IO.Path.GetDirectoryName(logPath);
                if (!string.IsNullOrEmpty(dir))
                    return dir;
            }
            // If logPath is a directory, use it directly
            if (System.IO.Directory.Exists(logPath))
                return logPath;
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private void DrawGoButton(string label, Action action)
    {
        var processing = isProcessing;
        ImGui.BeginDisabled(processing);

        ImGui.SetCursorPosX(ImGui.GetWindowWidth() - 115);
        if (ImGui.Button(processing ? Loc.Text("Processing...") : label, new Vector2(100, 30)))
            action();

        ImGui.EndDisabled();
    }

    private static void DrawProgress(FFLogsPlugin.Services.UploadProgress? progress)
    {
        if (progress == null) return;
        ImGui.TextWrapped(progress.Stage);
        if (progress.Total > 0)
        {
            float fraction = Math.Clamp((float)progress.Position / progress.Total, 0, 1);
            var label = progress.BytesPending
                ? Loc.Format("{0:N0} bytes pending", progress.Total - progress.Position)
                : Loc.Format("{0:P0} ({1:N0}/{2:N0})", fraction, progress.Position, progress.Total);
            ImGui.ProgressBar(fraction, new Vector2(-1, 0), label);
        }
    }

    private void DrawStatus()
    {
        if (!string.IsNullOrEmpty(statusMessage))
        {
            ImGui.Separator();
            ImGui.TextWrapped(statusMessage);

            // Show View Parses button when upload is complete
            if (lastUploadedReport != null)
            {
                ImGui.Spacing();
                if (ImGui.Button(Loc.Text("View Parses") + "###status", new Vector2(-1, 30)))
                {
                    plugin.ShowParseViewer(lastUploadedReport);
                }
            }
        }
    }

    private void StartLiveLog()
    {
        lastUploadedReport = null;
        if (string.IsNullOrWhiteSpace(logPath))
        {
            statusMessage = Loc.Text("Please enter a log directory path first.");
            return;
        }

        var effectivePath = LogFileHelper.GetDirectoryFromPath(logPath);
        if (effectivePath != logPath)
        {
            logPath = effectivePath;
        }

        SaveConfig();
        statusMessage = "";
        _ = DoLiveLog();
    }

    private async System.Threading.Tasks.Task DoLiveLog()
    {
        try
        {
            var guildId = GetSelectedGuildId();
            await plugin.FFLogsService.StartLiveLogAsync(logPath, FFLogsRegionExtensions.ComboIndexToApiValue(selectedRegion), selectedVisibility, guildId, description, uploadPreviousFights);
        }
        catch (Exception ex)
        {
            statusMessage = Loc.Format("Error: {0}", ex.Message);
            Plugin.Log.Error(ex, "Live log failed");
        }
    }

    private volatile string? lastUploadedReport;

    private void StartUpload()
    {
        lastUploadedReport = null;
        if (string.IsNullOrWhiteSpace(logPath))
        {
            statusMessage = Loc.Text("Please select a log file first.");
            return;
        }

        var effectivePath = LogFileHelper.GetLatestLogFileFromPath(logPath);
        if (effectivePath != logPath)
        {
            logPath = effectivePath;
        }

        if (!System.IO.File.Exists(logPath))
        {
            statusMessage = Loc.Text("No log files found in the specified directory.");
            return;
        }

        SaveConfig();
        statusMessage = Loc.Text("Uploading...");
        isProcessing = true;
        _ = DoUpload();
    }

    private async System.Threading.Tasks.Task DoUpload()
    {
        try
        {
            var guildId = GetSelectedGuildId();
            var reportCode = await plugin.FFLogsService.UploadLogAsync(logPath, FFLogsRegionExtensions.ComboIndexToApiValue(selectedRegion), selectedVisibility, guildId, description);
            lastUploadedReport = reportCode;
            statusMessage = Loc.Format("Upload complete! Report: {0}", reportCode);
        }
        catch (Exception ex)
        {
            statusMessage = Loc.Format("Error: {0}", ex.Message);
            Plugin.Log.Error(ex, "Upload failed");
        }
        finally
        {
            isProcessing = false;
        }
    }

    private void SaveConfig()
    {
        var directoryToSave = logPath;
        if (System.IO.File.Exists(logPath))
        {
            directoryToSave = System.IO.Path.GetDirectoryName(logPath) ?? logPath;
        }
        
        plugin.Configuration.LogDirectory = directoryToSave;
        plugin.Configuration.Region = FFLogsRegionExtensions.ComboIndexToApiValue(selectedRegion);
        plugin.Configuration.Visibility = selectedVisibility;
        plugin.Configuration.Save();
    }
}
