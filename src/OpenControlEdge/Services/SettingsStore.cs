using System.Collections.Frozen;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Windows.Media;

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

    /// Look for a new version at start-up and every few hours; a new one puts a dot on the settings button.
    public bool AutoCheckUpdates { get; init; } = true;
    public DateTimeOffset? LastAutoUpdateCheck { get; init; }

    /// Where the settings window was last closed; null opens it at its default size, centred on the panel's monitor.
    public WindowBounds? SettingsWindow { get; init; }

    /// The view the panel shows, and the order and visibility of the rings of each view.
    public WidgetView View { get; init; } = WidgetView.Ai;
    public ViewLayout AiLayout { get; init; } = ViewLayout.Default(WidgetView.Ai);
    public ViewLayout PcLayout { get; init; } = ViewLayout.Default(WidgetView.Pc);
    public ViewLayout CustomLayout { get; init; } = ViewLayout.Default(WidgetView.Custom);

    public ViewLayout Layout(WidgetView view) => view switch
    {
        WidgetView.Ai => AiLayout,
        WidgetView.Pc => PcLayout,
        _ => CustomLayout,
    };

    public Settings WithLayout(WidgetView view, ViewLayout layout) => view switch
    {
        WidgetView.Ai => this with { AiLayout = layout },
        WidgetView.Pc => this with { PcLayout = layout },
        _ => this with { CustomLayout = layout },
    };

    /// Background of the panel and the card; null is the theme's own (black on the dark theme).
    public Color? PanelBackground { get; init; }

    public GameModeOptions GameMode { get; init; } = new();

    /// More accounts per provider ("claude", "codex", "cursor"), shown after the default one, and which one each
    /// ring shows (0 = the default account).
    public FrozenDictionary<string, ImmutableArray<ExtraAccount>> Accounts { get; init; } =
        FrozenDictionary<string, ImmutableArray<ExtraAccount>>.Empty;
    public FrozenDictionary<string, int> SelectedAccounts { get; init; } = FrozenDictionary<string, int>.Empty;

    public ImmutableArray<ExtraAccount> AccountsOf(string provider) =>
        Accounts.TryGetValue(provider, out ImmutableArray<ExtraAccount> list) ? list : ImmutableArray<ExtraAccount>.Empty;

    /// The account a ring shows: 0 is the default one, 1… the extra ones; an index that no longer exists is 0.
    public int SelectedAccount(string provider) =>
        SelectedAccounts.TryGetValue(provider, out int index) && index > 0 && index <= AccountsOf(provider).Length ? index : 0;

    public static Settings Defaults { get; } = new(PanelMode.Pinned, FrozenDictionary<string, ProviderVisibility>.Empty);
}

/// A window rectangle in physical pixels (virtual screen coordinates) and the DPI of the monitor it was on.
internal readonly record struct WindowBounds(int X, int Y, int Width, int Height, int Dpi);

/// Preferences in OpenControlEdge.settings.json inside the data folder (DataFolder.Path: %ProgramData%\OpenControlEdge\{SID}
/// for the installed copy, %LOCALAPPDATA%\OpenControlEdge for a copy run without elevation):
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
///     "usageRefreshMinutes": 2 | 5 | 10 | 15,
///     "checkUpdates": true | false,
///     "lastAutoUpdateCheck": "ISO-8601", // optional
///     "providers": { "claude": "auto" | "show" | "hide", ... },
///     "settingsWindow": { "x": px, "y": px, "width": px, "height": px, "dpi": 96 … }, // optional
///     "view": "ai" | "pc" | "custom",
///     "layouts": { "ai" | "pc" | "custom": { "order": ["claude", …], "hidden": ["opencode", …] } },
///     "panelBackground": "#RRGGBB", // optional; the theme's background otherwise
///     "gameMode": { "enabled": false, "processes": ["OneDrive.exe", …], "services": ["WSearch", …],
///                   "powerPlan": "balanced" | "high" | "keep", "disableGameBar": true, "relaunch": true },
///     "accounts": { "claude" | "codex" | "cursor": [ { "name": "Trabajo", "folder": "D:\\…" } ] },
///     "selectedAccounts": { "claude": 0 | 1 | … },
///     "grok": { ... } // ignored for compatibility with older settings files
///   }
///
/// Per user and next to the log, never next to the executable, which lives in a folder only administrators can
/// write to. A file left next to the executable by an earlier version is still read, and never written.
/// A missing or unreadable file means defaults (and is then never overwritten). Never throws.
internal static class SettingsStore
{
    private const string FileName = "OpenControlEdge.settings.json";
    private static bool _unreadable;
    private static bool _warnedUnreadable;
    private static bool _warnedProvider;

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
                // "checkUpdates" (2.2, on by default) replaces "autoCheckUpdates" (2.1, off by default and always saved).
                AutoCheckUpdates = GetBool(root, "checkUpdates") ?? true,
                LastAutoUpdateCheck = GetDate(root, "lastAutoUpdateCheck"),
                SettingsWindow = ParseBounds(root, "settingsWindow"),
                View = RingKeys.ParseView(GetString(root, "view")) ?? WidgetView.Ai,
                AiLayout = ParseLayout(root, WidgetView.Ai),
                PcLayout = ParseLayout(root, WidgetView.Pc),
                CustomLayout = ParseLayout(root, WidgetView.Custom),
                PanelBackground = ParseColor(GetString(root, "panelBackground")),
                GameMode = ParseGameMode(root),
                Accounts = ParseAccounts(root),
                SelectedAccounts = ParseSelected(root),
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
                writer.WriteBoolean("checkUpdates", settings.AutoCheckUpdates);
                if (settings.LastAutoUpdateCheck is DateTimeOffset checkedAt) writer.WriteString("lastAutoUpdateCheck", checkedAt.ToString("o"));
                if (settings.SettingsWindow is WindowBounds bounds)
                {
                    writer.WriteStartObject("settingsWindow");
                    writer.WriteNumber("x", bounds.X);
                    writer.WriteNumber("y", bounds.Y);
                    writer.WriteNumber("width", bounds.Width);
                    writer.WriteNumber("height", bounds.Height);
                    writer.WriteNumber("dpi", bounds.Dpi);
                    writer.WriteEndObject();
                }
                writer.WriteString("view", RingKeys.ViewKey(settings.View));
                writer.WriteStartObject("layouts");
                foreach (WidgetView view in new[] { WidgetView.Ai, WidgetView.Pc, WidgetView.Custom })
                {
                    ViewLayout layout = settings.Layout(view);
                    writer.WriteStartObject(RingKeys.ViewKey(view));
                    WriteStrings(writer, "order", layout.Order);
                    WriteStrings(writer, "hidden", layout.Order.Where(layout.Hidden.Contains));
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
                if (settings.PanelBackground is Color background) writer.WriteString("panelBackground", ColorToHex(background));
                GameModeOptions game = settings.GameMode;
                writer.WriteStartObject("gameMode");
                writer.WriteBoolean("enabled", game.Enabled);
                WriteStrings(writer, "processes", game.Processes);
                WriteStrings(writer, "services", game.Services);
                writer.WriteString("powerPlan", game.PowerPlan switch { GamePowerPlan.HighPerformance => "high", GamePowerPlan.Keep => "keep", _ => "balanced" });
                writer.WriteBoolean("disableGameBar", game.DisableGameBar);
                writer.WriteBoolean("relaunch", game.Relaunch);
                writer.WriteEndObject();
                if (settings.Accounts.Count > 0)
                {
                    writer.WriteStartObject("accounts");
                    foreach (KeyValuePair<string, ImmutableArray<ExtraAccount>> entry in settings.Accounts.OrderBy(p => p.Key, StringComparer.Ordinal))
                    {
                        writer.WriteStartArray(entry.Key);
                        foreach (ExtraAccount account in entry.Value)
                        {
                            writer.WriteStartObject();
                            writer.WriteString("name", account.Name);
                            writer.WriteString("folder", account.Folder);
                            writer.WriteEndObject();
                        }
                        writer.WriteEndArray();
                    }
                    writer.WriteEndObject();
                }
                if (settings.SelectedAccounts.Count > 0)
                {
                    writer.WriteStartObject("selectedAccounts");
                    foreach (KeyValuePair<string, int> entry in settings.SelectedAccounts.OrderBy(p => p.Key, StringComparer.Ordinal))
                        writer.WriteNumber(entry.Key, entry.Value);
                    writer.WriteEndObject();
                }
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

    /// An entry that is not "auto", "show" or "hide" is ignored (that provider is automatic) and logged once; it does
    /// not make the whole file unreadable, which would stop every later change from being saved.
    private static FrozenDictionary<string, ProviderVisibility> ParseProviders(JsonElement root)
    {
        if (!root.TryGetProperty("providers", out JsonElement providers) || providers.ValueKind != JsonValueKind.Object)
            return FrozenDictionary<string, ProviderVisibility>.Empty;

        var map = new Dictionary<string, ProviderVisibility>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in providers.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String
                && ParseVisibility(property.Value.GetString()) is ProviderVisibility parsed)
                map[property.Name] = parsed;
            else if (!_warnedProvider)
            {
                _warnedProvider = true;
                Log.Warn("Settings", $"visibilidad no válida para «{property.Name}»: se usa automático");
            }
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

    /// Anything but an object with the five whole numbers (positive size and DPI) means "no saved position".
    private static WindowBounds? ParseBounds(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out JsonElement value) || value.ValueKind != JsonValueKind.Object) return null;
        if (GetInt(value, "x") is not int x || GetInt(value, "y") is not int y || GetInt(value, "width") is not int width
            || GetInt(value, "height") is not int height || GetInt(value, "dpi") is not int dpi) return null;
        return width > 0 && height > 0 && dpi > 0 ? new WindowBounds(x, y, width, height, dpi) : null;
    }

    private static void WriteStrings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (string value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    /// Strings of an array, at most max of them, each trimmed and at most maxLength long; anything else is skipped.
    private static List<string> GetStrings(JsonElement obj, string key, int max = 64, int maxLength = 260)
    {
        var values = new List<string>();
        if (!obj.TryGetProperty(key, out JsonElement array) || array.ValueKind != JsonValueKind.Array) return values;
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (values.Count >= max) break;
            if (item.ValueKind == JsonValueKind.String && item.GetString()?.Trim() is { Length: > 0 } text && text.Length <= maxLength)
                values.Add(text);
        }
        return values;
    }

    private static ViewLayout ParseLayout(JsonElement root, WidgetView view)
    {
        if (!root.TryGetProperty("layouts", out JsonElement layouts) || layouts.ValueKind != JsonValueKind.Object
            || !layouts.TryGetProperty(RingKeys.ViewKey(view), out JsonElement layout) || layout.ValueKind != JsonValueKind.Object)
            return ViewLayout.Default(view);
        return ViewLayout.Normalize(view, GetStrings(layout, "order"), GetStrings(layout, "hidden"));
    }

    /// "#RRGGBB" (or "RRGGBB"); anything else means the theme's background.
    internal static Color? ParseColor(string? value)
    {
        string? hex = value?.Trim().TrimStart('#');
        if (hex is not { Length: 6 } || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint rgb)) return null;
        return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    internal static string ColorToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static GameModeOptions ParseGameMode(JsonElement root)
    {
        var defaults = new GameModeOptions();
        if (!root.TryGetProperty("gameMode", out JsonElement game) || game.ValueKind != JsonValueKind.Object) return defaults;
        return new GameModeOptions
        {
            Enabled = GetBool(game, "enabled") ?? false,
            Processes = game.TryGetProperty("processes", out _)
                ? [.. GetStrings(game, "processes").Where(GameModeService.IsValidProcessName).Distinct(StringComparer.OrdinalIgnoreCase)]
                : defaults.Processes,
            Services = game.TryGetProperty("services", out _)
                ? [.. GetStrings(game, "services").Where(GameModeService.IsValidServiceName).Distinct(StringComparer.OrdinalIgnoreCase)]
                : defaults.Services,
            PowerPlan = GetString(game, "powerPlan") switch { "high" => GamePowerPlan.HighPerformance, "keep" => GamePowerPlan.Keep, _ => GamePowerPlan.Balanced },
            DisableGameBar = GetBool(game, "disableGameBar") ?? true,
            Relaunch = GetBool(game, "relaunch") ?? true,
        };
    }

    /// Accounts with a name and a fully qualified folder; at most AccountLimit per provider.
    public const int AccountLimit = 8;

    private static FrozenDictionary<string, ImmutableArray<ExtraAccount>> ParseAccounts(JsonElement root)
    {
        if (!root.TryGetProperty("accounts", out JsonElement accounts) || accounts.ValueKind != JsonValueKind.Object)
            return FrozenDictionary<string, ImmutableArray<ExtraAccount>>.Empty;
        var map = new Dictionary<string, ImmutableArray<ExtraAccount>>(StringComparer.Ordinal);
        foreach (string provider in new[] { AiProviderSettings.Claude, AiProviderSettings.Codex, AiProviderSettings.Cursor })
        {
            if (!accounts.TryGetProperty(provider, out JsonElement list) || list.ValueKind != JsonValueKind.Array) continue;
            var parsed = new List<ExtraAccount>();
            foreach (JsonElement item in list.EnumerateArray())
            {
                if (parsed.Count >= AccountLimit || item.ValueKind != JsonValueKind.Object) continue;
                string? name = GetString(item, "name")?.Trim(), folder = GetString(item, "folder")?.Trim();
                if (name is { Length: > 0 and <= 40 } && folder is { Length: > 0 and <= 260 } && Path.IsPathFullyQualified(folder))
                    parsed.Add(new ExtraAccount(name, folder));
            }
            if (parsed.Count > 0) map[provider] = [.. parsed];
        }
        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static FrozenDictionary<string, int> ParseSelected(JsonElement root)
    {
        if (!root.TryGetProperty("selectedAccounts", out JsonElement selected) || selected.ValueKind != JsonValueKind.Object)
            return FrozenDictionary<string, int>.Empty;
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string provider in new[] { AiProviderSettings.Claude, AiProviderSettings.Codex, AiProviderSettings.Cursor })
            if (GetInt(selected, provider) is int index && index is >= 0 and <= AccountLimit) map[provider] = index;
        return map.ToFrozenDictionary(StringComparer.Ordinal);
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
