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

internal enum AppTheme
{
    Dark,
    Light,

    /// Follows the Windows app mode (light or dark) and switches with it.
    System,
}

/// Colours of the ring arcs and card bars. Every theme keeps the red alert above Palette.AlertPercent.
internal enum RingColorTheme
{
    Classic,
    Mono,
    Ocean,
    Sunset,
    Neon,
}

internal enum UiLanguage
{
    Spanish,
    English,
}

internal sealed record Settings(PanelMode PanelMode, FrozenDictionary<string, ProviderVisibility> Providers,
    UsageView UsageView = UsageView.Session)
{
    public AppTheme Theme { get; init; } = AppTheme.Dark;
    public RingColorTheme ColorTheme { get; init; } = RingColorTheme.Classic;
    public UiLanguage Language { get; init; } = UiLanguage.Spanish;

    /// Fixed panel scale; null means "auto" (derived from the work area of the monitor).
    public double? UiScale { get; init; }

    /// Renew the Claude Code session in the background shortly before the token expires (see App.ArmAutoRenew).
    public bool AutoRenewClaude { get; init; } = true;

    /// Clicking the RAM ring frees memory (MemoryService.CleanAsync). Off: the click only shows the card.
    public bool RamCleanup { get; init; }
    public int UsageRefreshMinutes { get; init; } = 2;
    public bool AutoCheckUpdates { get; init; }
    public DateTimeOffset? LastAutoUpdateCheck { get; init; }

    public static Settings Defaults { get; } = new(PanelMode.Pinned, FrozenDictionary<string, ProviderVisibility>.Empty);
}

/// Preferences in %LOCALAPPDATA%\OpenControlEdge\OpenControlEdge.settings.json:
///
///   {
///     "panelMode": "pinned" | "auto",
///     "usageView": "session" | "total",
///     "theme": "dark" | "light" | "system",
///     "colorTheme": "classic" | "mono" | "ocean" | "sunset" | "neon",
///     "language": "es" | "en",
///     "uiScale": "auto" | number (0.6 – 1.6),
///     "autoRenewClaude": true | false,
///     "ramCleanup": true | false,
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

    public static string FilePath => Path.Combine(DataFolder.Path, FileName);

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
            return new Settings(mode, providers, view)
            {
                Theme = GetString(root, "theme") switch { "light" => AppTheme.Light, "system" => AppTheme.System, _ => AppTheme.Dark },
                ColorTheme = GetString(root, "colorTheme") switch
                {
                    "mono" => RingColorTheme.Mono,
                    "ocean" => RingColorTheme.Ocean,
                    "sunset" => RingColorTheme.Sunset,
                    "neon" => RingColorTheme.Neon,
                    _ => RingColorTheme.Classic,
                },
                Language = GetString(root, "language") == "en" ? UiLanguage.English : UiLanguage.Spanish,
                UiScale = ParseScale(root),
                AutoRenewClaude = GetBool(root, "autoRenewClaude") ?? true,
                RamCleanup = GetBool(root, "ramCleanup") ?? false,
                UsageRefreshMinutes = GetInt(root, "usageRefreshMinutes") is int minutes && minutes is 2 or 5 or 10 or 15 ? minutes : 2,
                AutoCheckUpdates = GetBool(root, "autoCheckUpdates") ?? false,
                LastAutoUpdateCheck = GetDate(root, "lastAutoUpdateCheck"),
            };
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

    /// For the settings window: applies a change to the stored settings and saves them. Returns what was saved,
    /// or null when the file could not be read (then nothing is written, so it is never overwritten with defaults).
    public static Settings? Update(Func<Settings, Settings> change)
    {
        Settings current = Load();
        if (_unreadable) return null;
        Settings updated = change(current);
        Save(updated);
        return updated;
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
                writer.WriteString("theme", settings.Theme.ToString().ToLowerInvariant());
                writer.WriteString("colorTheme", settings.ColorTheme.ToString().ToLowerInvariant());
                writer.WriteString("language", settings.Language == UiLanguage.English ? "en" : "es");
                if (settings.UiScale is double scale) writer.WriteNumber("uiScale", Math.Round(scale, 2));
                else writer.WriteString("uiScale", "auto");
                writer.WriteBoolean("autoRenewClaude", settings.AutoRenewClaude);
                writer.WriteBoolean("ramCleanup", settings.RamCleanup);
                writer.WriteNumber("usageRefreshMinutes", settings.UsageRefreshMinutes);
                writer.WriteBoolean("autoCheckUpdates", settings.AutoCheckUpdates);
                if (settings.LastAutoUpdateCheck is DateTimeOffset checkedAt) writer.WriteString("lastAutoUpdateCheck", checkedAt.ToString("o"));
                if (settings.Providers.Count > 0)
                {
                    writer.WriteStartObject("providers");
                    foreach (KeyValuePair<string, ProviderVisibility> entry in settings.Providers.OrderBy(p => p.Key, StringComparer.Ordinal))
                        writer.WriteString(entry.Key, VisibilityToJson(entry.Value));
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            if (!DataFolder.WriteAtomic(FileName, buffer.ToArray())) return;
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

    public const double MinUiScale = 0.6;
    public const double MaxUiScale = 1.6;

    /// "auto", a missing value or anything out of range means automatic scaling.
    private static double? ParseScale(JsonElement root)
    {
        if (!root.TryGetProperty("uiScale", out JsonElement value) || value.ValueKind != JsonValueKind.Number) return null;
        return value.TryGetDouble(out double scale) && scale >= MinUiScale && scale <= MaxUiScale ? scale : null;
    }

    private static bool? GetBool(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : null;

    private static int? GetInt(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : null;

    private static DateTimeOffset? GetDate(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), out DateTimeOffset date) ? date : null;

    private static string? GetString(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

}
