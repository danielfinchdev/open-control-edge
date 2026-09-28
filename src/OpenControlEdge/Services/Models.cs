namespace OpenControlEdge.Services;

internal sealed record UsageWindow(double Percent, DateTimeOffset? ResetsAt);

/// An amount in the currency's major unit (10.53 EUR), built from the API's minor units and exponent.
internal sealed record Money(decimal Amount, string Currency);

/// Session is null whenever the data could not be obtained; Message then explains why.
/// Spent (spend.used) is optional: null when the response does not carry it.
/// Hidden: the provider is not installed (or forced off in settings), so the ring is omitted entirely.
internal sealed record ClaudeSnapshot(bool Hidden, UsageWindow? Session, UsageWindow? Weekly, Money? Spent, string? Message)
{
    public static ClaudeSnapshot Absent() => new(true, null, null, null, null);
    public static ClaudeSnapshot Failed(string message) => new(false, null, null, null, message);
}

/// Temperature is null whenever it could not be read; Message then explains why.
internal sealed record CpuSnapshot(string? Name, double? Temperature, double? MaxTemperature, double? Load, string? Message);

/// Detected is false when no NVIDIA GPU is found, and the ring is then hidden. Otherwise any value may be null
/// when its sensor is missing or unreadable; Message explains a missing temperature.
internal sealed record GpuSnapshot(bool Detected, string? Name, double? Temperature, double? Load,
    double? MemoryUsedMb, double? MemoryTotalMb, string? Message)
{
    public static GpuSnapshot NotDetected { get; } = new(false, null, null, null, null, null, null);
}

internal sealed record HardwareSnapshot(CpuSnapshot Cpu, GpuSnapshot Gpu);

/// One Codex rate-limit window. Length is limit_window_seconds (e.g. 30 days).
internal sealed record CodexWindow(double Percent, TimeSpan? Length, DateTimeOffset? ResetsAt);

/// Hidden: the provider is not installed (or forced off in settings). Otherwise Primary is null when there is no
/// usable session yet; Message then explains why (including «Inicia sesión en …»).
internal sealed record CodexSnapshot(bool Hidden, CodexWindow? Primary, CodexWindow? Secondary, string? Message)
{
    public static CodexSnapshot Absent() => new(true, null, null, null);
    public static CodexSnapshot NotAvailable(string reason) => new(false, null, null, reason);
    public static CodexSnapshot Failed(string message) => new(false, null, null, message);
}

internal sealed record CursorSnapshot(bool Hidden, UsageWindow? Cycle, UsageWindow? OnDemand, string? Message)
{
    public static CursorSnapshot Absent() => new(true, null, null, null);
    public static CursorSnapshot NotAvailable(string reason) => new(false, null, null, reason);
    public static CursorSnapshot Failed(string message) => new(false, null, null, message);
}
