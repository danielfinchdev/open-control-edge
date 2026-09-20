using System.Globalization;
using System.IO;
using System.Text.Json;

namespace EdgeWidget.Services;

internal enum PanelMode
{
    /// Panel always expanded (default).
    Pinned,

    /// Panel collapses to the edge strip and expands on hover.
    Auto,
}

/// The "grok" block, typed in by hand. Grok Bot has no usage API on a personal plan, so these are the
/// only numbers the widget can show; WeeklyPercent is what the ring draws.
internal sealed record GrokSettings(double WeeklyPercent, DateTimeOffset? WeeklyResetsAt, double? OnDemandPercent);

internal sealed record Settings(PanelMode PanelMode, GrokSettings? Grok)
{
    public static Settings Defaults { get; } = new(PanelMode.Pinned, null);
}

/// Preferences in %LOCALAPPDATA%\EdgeWidget\EdgeWidget.settings.json:
///
///   {
///     "panelMode": "pinned" | "auto",
///     "grok": { "weeklyPercent": 42, "weeklyResetsAt": "2026-09-22T09:00:00+02:00", "onDemandPercent": 12 }
///   }
///
/// Per user and always writable, so the executable itself can live in a folder only administrators can write
/// to. A file left next to the executable by an earlier version is still read, and never written.
/// A missing or unreadable file means defaults. Never throws.
internal static class SettingsStore
{
    private const string FileName = "EdgeWidget.settings.json";

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdgeWidget", FileName);

    /// Where versions before 1.1 kept it: next to the executable. Read as a fallback, never written.
    private static string LegacyFilePath => Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, FileName);

    public static Settings Load()
    {
        try
        {
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
            return new Settings(mode, ReadGrok(root));
        }
        catch (Exception ex)
        {
            Log.Warn("Settings", "unreadable, using defaults: " + ex.Message);
            return Settings.Defaults;
        }
    }

    /// Null when the block is missing or carries no usable weekly percentage: the ring then stays hidden.
    private static GrokSettings? ReadGrok(JsonElement root)
    {
        if (!root.TryGetProperty("grok", out var grok) || grok.ValueKind != JsonValueKind.Object) return null;
        if (GetNumber(grok, "weeklyPercent") is not double weekly) return null;

        return new GrokSettings(
            Math.Clamp(weekly, 0, 100),
            GetDate(grok, "weeklyResetsAt"),
            GetNumber(grok, "onDemandPercent") is double onDemand ? Math.Clamp(onDemand, 0, 100) : null);
    }

    /// Rewrites the file with the new mode, carrying the "grok" block over untouched so a hand-typed
    /// percentage is never lost by toggling "Ocultar" / "Fijar".
    public static void SavePanelMode(PanelMode mode)
    {
        try
        {
            GrokSettings? grok = Load().Grok;

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("panelMode", mode == PanelMode.Auto ? "auto" : "pinned");
                if (grok is not null)
                {
                    writer.WriteStartObject("grok");
                    writer.WriteNumber("weeklyPercent", grok.WeeklyPercent);
                    if (grok.WeeklyResetsAt is DateTimeOffset resetsAt)
                        writer.WriteString("weeklyResetsAt", resetsAt.ToString("o", CultureInfo.InvariantCulture));
                    if (grok.OnDemandPercent is double onDemand)
                        writer.WriteNumber("onDemandPercent", onDemand);
                    writer.WriteEndObject();
                }
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

    private static double? GetNumber(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double d)
            ? d
            : null;

    private static DateTimeOffset? GetDate(JsonElement obj, string key) =>
        GetString(obj, key) is string text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
}
