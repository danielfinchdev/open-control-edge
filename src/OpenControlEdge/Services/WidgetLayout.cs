using System.Collections.Immutable;

namespace OpenControlEdge.Services;

/// What the panel shows: the AI rings, the PC rings, or a mix chosen in Settings > Personalización.
internal enum WidgetView
{
    Ai,
    Pc,
    Custom,
}

/// The rings of the panel, by the key used in the settings file.
internal static class RingKeys
{
    public const string Claude = "claude";
    public const string Codex = "codex";
    public const string Cursor = "cursor";
    public const string OpenCode = "opencode";
    public const string DeepSeek = "deepseek";
    public const string OpenRouter = "openrouter";
    public const string Cpu = "cpu";
    public const string Gpu = "gpu";
    public const string Ram = "ram";
    public const string Fps = "fps";
    public const string GameMode = "gamemode";

    /// Alphabetical by the name shown, which is the default order of every view (FPS goes first in the PC view).
    public static ImmutableArray<string> Ai { get; } = [Claude, Codex, Cursor, DeepSeek, OpenCode, OpenRouter];
    public static ImmutableArray<string> Pc { get; } = [Fps, Cpu, Gpu, GameMode, Ram];
    public static ImmutableArray<string> All { get; } =
        [Claude, Codex, Cpu, Cursor, DeepSeek, Fps, Gpu, GameMode, OpenCode, OpenRouter, Ram];

    public static ImmutableArray<string> For(WidgetView view) => view switch
    {
        WidgetView.Ai => Ai,
        WidgetView.Pc => Pc,
        _ => All,
    };

    public static bool IsAi(string key) => Ai.Contains(key);

    public static string ViewKey(WidgetView view) => view switch { WidgetView.Pc => "pc", WidgetView.Custom => "custom", _ => "ai" };

    public static WidgetView? ParseView(string? value) => value switch
    {
        "ai" => WidgetView.Ai,
        "pc" => WidgetView.Pc,
        "custom" => WidgetView.Custom,
        _ => null,
    };

    /// The view after this one, for the panel's view button: IA → PC → Personalizada → IA.
    public static WidgetView Next(WidgetView view) => view switch
    {
        WidgetView.Ai => WidgetView.Pc,
        WidgetView.Pc => WidgetView.Custom,
        _ => WidgetView.Ai,
    };
}

/// Order of the rings of one view and the ones switched off. Always holds every ring of the view exactly once.
internal sealed record ViewLayout(ImmutableArray<string> Order, ImmutableHashSet<string> Hidden)
{
    public static ViewLayout Default(WidgetView view) => new(RingKeys.For(view), ImmutableHashSet<string>.Empty);

    /// Unknown keys and duplicates are dropped, missing rings are added at the end in their default order.
    public static ViewLayout Normalize(WidgetView view, IEnumerable<string> order, IEnumerable<string> hidden)
    {
        ImmutableArray<string> eligible = RingKeys.For(view);
        var seen = new List<string>();
        foreach (string key in order)
            if (eligible.Contains(key) && !seen.Contains(key)) seen.Add(key);
        foreach (string key in eligible)
            if (!seen.Contains(key)) seen.Add(key);
        return new ViewLayout([.. seen], hidden.Where(eligible.Contains).ToImmutableHashSet());
    }

    public IEnumerable<string> Shown => Order.Where(key => !Hidden.Contains(key));

    public bool Equals(ViewLayout? other) =>
        other is not null && Order.SequenceEqual(other.Order) && Hidden.SetEquals(other.Hidden);

    public override int GetHashCode() => Order.Length;
}

/// What "Modo juego" does when it is switched on, and undoes when it is switched off.
internal sealed record GameModeOptions
{
    /// Off by default: the ring only explains how to turn it on (it closes programs).
    public bool Enabled { get; init; }

    /// Executables closed while it is on (image names such as "OneDrive.exe"), in this session only.
    public ImmutableArray<string> Processes { get; init; } = DefaultProcesses;

    /// Windows services stopped while it is on (service names), and started again only if they were running.
    public ImmutableArray<string> Services { get; init; } = DefaultServices;

    public GamePowerPlan PowerPlan { get; init; } = GamePowerPlan.Balanced;

    /// Turn off Xbox Game Bar captures (Game DVR) while it is on; the previous values come back afterwards.
    public bool DisableGameBar { get; init; } = true;

    /// Open again, as the plain user, the programs it closed.
    public bool Relaunch { get; init; } = true;

    /// Cloud sync, phone link and Xbox Game Bar helpers: background programs that can be closed safely.
    public static ImmutableArray<string> DefaultProcesses { get; } =
    [
        "OneDrive.exe", "GoogleDriveFS.exe", "Dropbox.exe", "ProtonDrive.exe", "SyncTrayzor.exe", "syncthing.exe",
        "PhoneExperienceHost.exe", "CrossDeviceService.exe", "MSPCManager.exe", "ms-teams.exe",
        "GameBar.exe", "GameBarFTServer.exe", "XboxGameBarWidgets.exe", "GameBarPresenceWriter.exe",
    ];

    /// Search indexing, SysMain (prefetching) and telemetry: disk and CPU work nobody needs mid-game.
    public static ImmutableArray<string> DefaultServices { get; } = ["WSearch", "SysMain", "DiagTrack"];

    public bool Equals(GameModeOptions? other) =>
        other is not null && Enabled == other.Enabled && Processes.SequenceEqual(other.Processes)
        && Services.SequenceEqual(other.Services) && PowerPlan == other.PowerPlan
        && DisableGameBar == other.DisableGameBar && Relaunch == other.Relaunch;

    public override int GetHashCode() => HashCode.Combine(Enabled, PowerPlan, Processes.Length, Services.Length);
}

internal enum GamePowerPlan
{
    /// Windows' Balanced plan: smooth without running the laptop hot (what the LoL script used).
    Balanced,

    /// High performance: the fastest, and the hottest.
    HighPerformance,

    /// Leave the power plan alone.
    Keep,
}

/// One more account of Claude Code, Codex or Cursor: its own configuration folder (CLAUDE_CONFIG_DIR, CODEX_HOME or
/// Cursor's --user-data-dir), shown next to the default one under a name.
internal sealed record ExtraAccount(string Name, string Folder);
