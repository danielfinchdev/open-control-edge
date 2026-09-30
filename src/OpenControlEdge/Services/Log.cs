using System.Diagnostics;
using System.IO;

namespace OpenControlEdge.Services;

/// Minimal file log: widget.log in the data folder (DataFolder.Path). Never throws. Never logs tokens.
internal static class Log
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();
    private static readonly string Directory_ = DataFolder.Path;
    private static readonly string FilePath = Path.Combine(Directory_, "widget.log");
    private static bool _reported;
    private static bool _suppressed;

    internal static void Suppress() => _suppressed = true;

    public static void Info(string area, string message) => Write("INFO", area, message);
    public static void Warn(string area, string message) => Write("WARN", area, message);
    public static void Error(string area, Exception ex) => Write("ERROR", area, ex.ToString());

    /// Interaction tracing; compiled out of Release builds.
    [Conditional("DEBUG")]
    public static void Trace(string area, string message) => Write("TRACE", area, message);

    private static void Write(string level, string area, string message)
    {
        if (_suppressed) return;
        try
        {
            lock (Gate)
            {
                if (!DataFolder.IsSafeForWrites())
                {
                    ReportOnce(new UnauthorizedAccessException("Data folder is not protected."));
                    return;
                }
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

    /// The first failure, and only the first, sent to the debugger without another file write.
    private static void ReportOnce(Exception ex)
    {
        lock (Gate)
        {
            if (_reported) return;
            _reported = true;
        }

        System.Diagnostics.Trace.WriteLine($"OpenControlEdge log failure: {ex.GetType().Name}: {ex.Message}");
    }
}
