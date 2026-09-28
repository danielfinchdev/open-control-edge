using System.Globalization;
using System.IO;
using System.Text.Json;

namespace OpenControlEdge.Services;

internal enum PanelMode
{
    /// Panel always expanded (default).
    Pinned,

    /// Panel collapses to the edge strip and expands on hover.
    Auto,
}

internal sealed record Settings(PanelMode PanelMode)
{
    public static Settings Defaults { get; } = new(PanelMode.Pinned);
}

/// Preferences in %LOCALAPPDATA%\OpenControlEdge\OpenControlEdge.settings.json:
///
///   {
///     "panelMode": "pinned" | "auto",
///     "grok": { ... } // ignored for compatibility with older settings files
///   }
///
/// Per user and always writable, so the executable itself can live in a folder only administrators can write
/// to. A file left next to the executable by an earlier version is still read, and never written.
/// A missing or unreadable file means defaults. Never throws.
internal static class SettingsStore
{
    private const string FileName = "OpenControlEdge.settings.json";

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenControlEdge", FileName);

    private static string PreviousFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdgeWidget", "EdgeWidget.settings.json");

    /// Where versions before 1.1 kept it: next to the executable. Read as a fallback, never written.
    private static string LegacyFilePath => Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, FileName);

    public static Settings Load()
    {
        try
        {
            if (!File.Exists(FilePath) && File.Exists(PreviousFilePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.Copy(PreviousFilePath, FilePath);
            }

            string path = File.Exists(FilePath) ? FilePath : LegacyFilePath;
            if (!File.Exists(path)) return Settings.Defaults;

            using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Settings.Defaults;

            PanelMode mode = GetString(root, "panelMode") == "auto" ? PanelMode.Auto : PanelMode.Pinned;
            return new Settings(mode);
        }
        catch (Exception ex)
        {
            Log.Warn("Settings", "unreadable, using defaults: " + ex.Message);
            return Settings.Defaults;
        }
    }

    /// Saves only supported settings; legacy properties such as "grok" are ignored.
    public static void SavePanelMode(PanelMode mode)
    {
        try
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("panelMode", mode == PanelMode.Auto ? "auto" : "pinned");
                writer.WriteEndObject();
            }

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            // Write to a temp file and swap, so a crash mid-write never leaves a truncated file.
            string temp = FilePath + ".tmp";
            File.WriteAllBytes(temp, buffer.ToArray());
            File.Move(temp, FilePath, overwrite: true);
            Log.Info("Settings", $"panelMode = {mode}");
        }
        catch (Exception ex)
        {
            Log.Warn("Settings", "could not save: " + ex.Message);
        }
    }

    private static string? GetString(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

}
