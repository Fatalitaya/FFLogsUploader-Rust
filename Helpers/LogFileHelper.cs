using System;
using System.IO;
using System.Linq;

namespace FFLogsPlugin.Helpers;

/// <summary>
/// Utility methods for finding and reading FFXIV log files.
/// Extracted from MainWindow to keep UI code separate from file system logic.
/// </summary>
public static class LogFileHelper
{
    /// <summary>
    /// Attempts to auto-detect the ACT FFXIV log directory at the standard path.
    /// Returns empty string if not found.
    /// </summary>
    public static string AutoDetectLogDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var actLogs = Path.Combine(appData, "Advanced Combat Tracker", "FFXIVLogs");
        
        if (Directory.Exists(actLogs))
            return actLogs;

        return string.Empty;
    }

    /// <summary>
    /// Validates that a path points to (or is inside) an FFXIVLogs directory
    /// that contains at least one .log file.
    /// </summary>
    public static bool IsValidLogPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        var directoryToCheck = path;
        if (File.Exists(path))
            directoryToCheck = Path.GetDirectoryName(path) ?? path;

        if (!directoryToCheck.EndsWith("FFXIVLogs", StringComparison.OrdinalIgnoreCase))
            return false;

        if (Directory.Exists(directoryToCheck))
        {
            try
            {
                return Directory.GetFiles(directoryToCheck, "*.log").Length > 0;
            }
            catch { }
        }

        return false;
    }

    /// <summary>
    /// If the path is a file, returns its parent directory. Otherwise returns the path as-is.
    /// </summary>
    public static string GetDirectoryFromPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        if (File.Exists(path))
            return Path.GetDirectoryName(path) ?? path;

        return path;
    }

    /// <summary>
    /// If the path is a directory, returns the most recently modified .log file in it.
    /// If already a file, returns the path as-is.
    /// </summary>
    public static string GetLatestLogFileFromPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        if (File.Exists(path))
            return path;

        if (Directory.Exists(path))
        {
            try
            {
                var files = Directory.GetFiles(path, "*.log");
                if (files.Length > 0)
                {
                    return files
                        .Select(f => new FileInfo(f))
                        .OrderByDescending(fi => fi.LastWriteTime)
                        .First()
                        .FullName;
                }
            }
            catch { }
        }

        return path;
    }

}
