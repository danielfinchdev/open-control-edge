using System.Text.Json;

namespace OpenControlEdge.Services;

/// Parser for GET https://chatgpt.com/backend-api/wham/usage (undocumented endpoint).
///
/// Shape verified against a real 200 response on 2026-09-13 (plan "go"):
///   "rate_limit": { "primary_window": { "used_percent": 0, "limit_window_seconds": 2592000,
///                                       "reset_after_seconds": 2592000, "reset_at": 1791848539 },
///                   "secondary_window": null }
/// reset_at is unix seconds. Identity fields in the same response (email, user_id, …) are never read.
///
/// Credits, same endpoint, verified 2026-09-29 (plan "go", account without credits):
///   "credits": { "has_credits": false, "unlimited": false, "overage_limit_reached": false, "balance": null,
///                "approx_local_messages": null, "approx_cloud_messages": null }
/// A balance has not been observed yet: it is accepted as a number or a numeric string (in credits, not money), and
/// only when has_credits is true. Anything else means no credits row.
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

    /// credits → CodexCredits when has_credits is true; null otherwise (never guessed).
    public static CodexCredits? ParseCredits(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("credits", out var credits)
            || credits.ValueKind != JsonValueKind.Object
            || !credits.TryGetProperty("has_credits", out var has) || has.ValueKind != JsonValueKind.True) return null;

        bool unlimited = credits.TryGetProperty("unlimited", out var u) && u.ValueKind == JsonValueKind.True;
        decimal? balance = null;
        if (credits.TryGetProperty("balance", out var b))
        {
            if (b.ValueKind == JsonValueKind.Number && b.TryGetDecimal(out decimal number)) balance = number;
            else if (b.ValueKind == JsonValueKind.String
                     && decimal.TryParse(b.GetString(), System.Globalization.NumberStyles.Number,
                         System.Globalization.CultureInfo.InvariantCulture, out decimal parsed)) balance = parsed;
        }
        return balance is null && !unlimited ? null : new CodexCredits(balance, unlimited);
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
