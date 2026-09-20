using System.Text.Json;

namespace EdgeWidget.Services;

/// Parser for GET https://chatgpt.com/backend-api/wham/usage (undocumented endpoint).
///
/// Shape verified against a real 200 response on 2026-09-13 (plan "go"):
///   "rate_limit": { "primary_window": { "used_percent": 0, "limit_window_seconds": 2592000,
///                                       "reset_after_seconds": 2592000, "reset_at": 1791848539 },
///                   "secondary_window": null }
/// reset_at is unix seconds. Identity fields in the same response (email, user_id, …) are never read.
internal static class CodexUsageParser
{
    public static (CodexWindow? Primary, CodexWindow? Secondary) Parse(string json, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("rate_limit", out var rateLimit)
            || rateLimit.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        return (ReadWindow(rateLimit, "primary_window", now), ReadWindow(rateLimit, "secondary_window", now));
    }

    private static CodexWindow? ReadWindow(JsonElement rateLimit, string name, DateTimeOffset now)
    {
        if (!rateLimit.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object
            || GetNumber(window, "used_percent") is not double percent)
        {
            return null;
        }

        TimeSpan? length = GetNumber(window, "limit_window_seconds") is double seconds && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;

        DateTimeOffset? resetsAt =
            GetNumber(window, "reset_at") is double at && at > 0 && at < 253402300799 ? DateTimeOffset.FromUnixTimeSeconds((long)at)
            : GetNumber(window, "reset_after_seconds") is double after && after >= 0 ? now.AddSeconds(after)
            : null;

        return new CodexWindow(percent, length, resetsAt);
    }

    private static double? GetNumber(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double d)
            ? d
            : null;
}
