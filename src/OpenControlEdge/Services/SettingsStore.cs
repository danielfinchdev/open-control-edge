using System.Collections.Frozen;
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

internal enum UsageView
{
    /// Rings show the short window: Claude 5 h, Codex its shortest window (default).
    Session,

    /// Rings show the long window: Claude and Codex weekly (or longest), Cursor its monthly billing cycle.
    Total,
}

internal sealed record Settings(PanelMode PanelMode, FrozenDictionary<string, ProviderVisibility> Providers,
    UsageView UsageView = UsageView.Session)
{
    public static Settings Defaults { get; } = new(PanelMode.Pinned, FrozenDictionary<string, ProviderVisibility>.Empty);
}

/// Preferences in %LOCALAPPDATA%\OpenControlEdge\OpenControlEdge.settings.json:
///
///   {
///     "panelMode": "pinned" | "auto",
///     "usageView": "session" | "total",
///     "providers": { "claude": "auto" | "show" | "hide", ... },
///     "grok": { ... } // ignored for compatibility with older settings files
///   }
///
/// Per user and always writable, so the executable itself can live in a folder only administrators can write
/// to. A file left next to the executable by an earlier version is still read, and never written.
/// A missing or unreadable file means defaults. Never throws.
internal static class SettingsStore
{
    private const string FileName = "OpenControlEdge.settings.json";
    private static bool _unreadable;
    private static bool _warnedUnreadable;

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenControlEdge", FileName);

    private static string PreviousFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdgeWidget", "EdgeWidget.settings.json");

    /// Where versions before 1.1 kept it: next to the executable. Read as a fallback, never written.
    private static string LegacyFilePath => Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, FileName);

    public static Settings Load()
    {
        _unreadable = false;
        try
        {
            bool settingsPresent = FilePresence(FilePath);
            if (_unreadable) return Settings.Defaults;
            if (!settingsPresent && FilePresence(PreviousFilePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.Copy(PreviousFilePath, FilePath);
                settingsPresent = true;
            }

            string path = settingsPresent ? FilePath : LegacyFilePath;
            bool legacyPresent = settingsPresent || FilePresence(path);
            if (_unreadable) return Settings.Defaults;
            if (!legacyPresent) return Settings.Defaults;

            using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                MarkUnreadable("settings root is not an object");
                return Settings.Defaults;
            }

            PanelMode mode = GetString(root, "panelMode") == "auto" ? PanelMode.Auto : PanelMode.Pinned;
            FrozenDictionary<string, ProviderVisibility> providers = ParseProviders(root);
            UsageView view = GetString(root, "usageView") == "total" ? UsageView.Total : UsageView.Session;
            return new Settings(mode, providers, view);
        }
        catch (Exception ex)
        {
            MarkUnreadable(ex.Message);
            return Settings.Defaults;
        }
    }

    private static bool FilePresence(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.Directory) == 0;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (Exception ex) { MarkUnreadable(ex.Message); return false; }
    }

    private static void MarkUnreadable(string reason)
    {
        if (!_warnedUnreadable) Log.Warn("Settings", "unreadable, using defaults: " + reason);
        _warnedUnreadable = true;
        _unreadable = true;
    }

    /// Saves only supported settings; legacy properties such as "grok" are ignored.
    public static void SavePanelMode(PanelMode mode)
    {
        Settings current = Load();
        if (_unreadable) return;
        Save(current with { PanelMode = mode });
    }

    /// The "Sesión" / "Total" tab choice; everything else is kept as loaded.
    public static void SaveUsageView(UsageView view)
    {
        Settings current = Load();
        if (_unreadable) return;
        Save(current with { UsageView = view });
    }

    private static void Save(Settings settings)
    {
        try
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("panelMode", settings.PanelMode == PanelMode.Auto ? "auto" : "pinned");
                writer.WriteString("usageView", settings.UsageView == UsageView.Total ? "total" : "session");
                if (settings.Providers.Count > 0)
                {
                    writer.WriteStartObject("providers");
                    foreach (KeyValuePair<string, ProviderVisibility> entry in settings.Providers.OrderBy(p => p.Key, StringComparer.Ordinal))
                        writer.WriteString(entry.Key, VisibilityToJson(entry.Value));
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            // Write to a temp file and swap, so a crash mid-write never leaves a truncated file.
            string temp = FilePath + ".tmp";
            File.WriteAllBytes(temp, buffer.ToArray());
            File.Move(temp, FilePath, overwrite: true);
            Log.Info("Settings", $"panelMode = {settings.PanelMode}, usageView = {settings.UsageView}");
        }
        catch (Exception ex)
        {
            Log.Warn("Settings", "could not save: " + ex.Message);
        }
    }

    private static FrozenDictionary<string, ProviderVisibility> ParseProviders(JsonElement root)
    {
        if (!root.TryGetProperty("providers", out JsonElement providers))
            return FrozenDictionary<string, ProviderVisibility>.Empty;
        if (providers.ValueKind != JsonValueKind.Object)
        {
            MarkUnreadable("providers is not an object");
            return FrozenDictionary<string, ProviderVisibility>.Empty;
        }

        var map = new Dictionary<string, ProviderVisibility>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in providers.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                MarkUnreadable("provider visibility is not a string");
                continue;
            }
            ProviderVisibility? visibility = ParseVisibility(property.Value.GetString());
            if (visibility is ProviderVisibility parsed) map[property.Name] = parsed;
            else MarkUnreadable("unknown provider visibility");
        }

        return map.Count == 0 ? FrozenDictionary<string, ProviderVisibility>.Empty : map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static ProviderVisibility? ParseVisibility(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "auto" => ProviderVisibility.Auto,
        "show" => ProviderVisibility.Show,
        "hide" => ProviderVisibility.Hide,
        _ => null,
    };

    private static string VisibilityToJson(ProviderVisibility visibility) => visibility switch
    {
        ProviderVisibility.Show => "show",
        ProviderVisibility.Hide => "hide",
        _ => "auto",
    };

    private static string? GetString(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

}
