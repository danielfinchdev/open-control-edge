using System.Diagnostics;
using System.IO;

namespace EdgeWidget.Services;

/// Minimal file log in %LOCALAPPDATA%\EdgeWidget\widget.log. Never throws. Never logs tokens.
internal static class Log
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();
    private static readonly string Directory_ = ResolveDirectory();
    private static readonly string FilePath = Path.Combine(Directory_, "widget.log");
    private static bool _reported;

    /// %LOCALAPPDATA%\EdgeWidget. GetFolderPath answers with an empty string when it cannot resolve the
    /// folder, which would turn the log path into a relative one and scatter it over whatever the working
    /// directory happens to be — so the environment variable, and then the temp folder, stand in for it.
    private static string ResolveDirectory()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root)) root = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? string.Empty;
        if (string.IsNullOrEmpty(root) || !Path.IsPathRooted(root)) root = Path.GetTempPath();
        return Path.Combine(root, "EdgeWidget");
    }

    public static void Info(string area, string message) => Write("INFO", area, message);
    public static void Warn(string area, string message) => Write("WARN", area, message);
    public static void Error(string area, Exception ex) => Write("ERROR", area, ex.ToString());

    /// Interaction tracing; compiled out of Release builds.
    [Conditional("DEBUG")]
    public static void Trace(string area, string message) => Write("TRACE", area, message);

    private static void Write(string level, string area, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Directory_);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes) File.Move(FilePath, FilePath + ".old", overwrite: true);

                // A byte order mark on the first line, so Notepad and PowerShell read the accents as UTF-8
                // instead of guessing the system code page.
                string start = File.Exists(FilePath) ? string.Empty : "﻿";
                File.AppendAllText(FilePath,
                    $"{start}{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {area}: {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex)
        {
            // Logging must never take the app down — but a log that cannot be written must not disappear in
            // silence either, or a session that misbehaved leaves nothing at all to read afterwards.
            ReportOnce(ex);
        }
    }

    /// The first failure, and only the first, written where the log itself could not go.
    private static void ReportOnce(Exception ex)
    {
        lock (Gate)
        {
            if (_reported) return;
            _reported = true;
        }

        try
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "EdgeWidget-log-error.txt"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{FilePath}{Environment.NewLine}{ex}{Environment.NewLine}");
        }
        catch
        {
            // Nothing else to try.
        }
    }
}
