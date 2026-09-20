using System.Globalization;
using System.Text.Json;

namespace EdgeWidget.Services;

/// Parser for GET https://api.anthropic.com/api/oauth/usage (undocumented endpoint).
///
/// Shape verified against real 200 responses on 2026-09-13:
///   "limits": [ { "kind": "session",    "percent": 32, "resets_at": "2026-09-13T02:40:00.820248+00:00", ... },
///               { "kind": "weekly_all", "percent": 11, "resets_at": "2026-09-16T06:00:00.820269+00:00", ... } ]
///   "five_hour": { "utilization": 32.0, "resets_at": "..." }   (same data, used only as a fallback)
///   "seven_day": { "utilization": 11.0, "resets_at": "..." }
///   "spend": { "used": { "amount_minor": 1053, "currency": "EUR", "exponent": 2 }, "balance": null, ... }
/// The response carries no remaining credit balance: spend.balance was null in every capture.
internal static class ClaudeUsageParser
{
    internal sealed record Result(UsageWindow? Session, UsageWindow? Weekly, Money? Spent);

    public static Result Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return new Result(null, null, null);

        UsageWindow? session = null;
        UsageWindow? weekly = null;

        if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in limits.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || GetNumber(item, "percent") is not double percent) continue;
                switch (GetString(item, "kind"))
                {
                    case "session":
                        session ??= new UsageWindow(percent, GetDate(item, "resets_at"));
                        break;
                    case "weekly_all":
                        weekly ??= new UsageWindow(percent, GetDate(item, "resets_at"));
                        break;
                }
            }
        }

        session ??= FromWindowObject(root, "five_hour");
        weekly ??= FromWindowObject(root, "seven_day");
        return new Result(session, weekly, ParseSpent(root));
    }

    /// spend.used = { amount_minor, currency, exponent } → amount_minor / 10^exponent.
    private static Money? ParseSpent(JsonElement root)
    {
        if (!root.TryGetProperty("spend", out var spend) || spend.ValueKind != JsonValueKind.Object) return null;
        if (!spend.TryGetProperty("used", out var used) || used.ValueKind != JsonValueKind.Object) return null;

        if (!used.TryGetProperty("amount_minor", out var minorElement) || minorElement.ValueKind != JsonValueKind.Number
            || !minorElement.TryGetInt64(out long minor)) return null;
        if (!used.TryGetProperty("exponent", out var exponentElement) || exponentElement.ValueKind != JsonValueKind.Number
            || !exponentElement.TryGetInt32(out int exponent) || exponent is < 0 or > 6) return null;
        if (GetString(used, "currency") is not string currency || currency.Length == 0) return null;

        decimal divisor = 1m;
        for (int i = 0; i < exponent; i++) divisor *= 10m;
        return new Money(minor / divisor, currency);
    }

    private static UsageWindow? FromWindowObject(JsonElement root, string name) =>
        root.TryGetProperty(name, out var window) && window.ValueKind == JsonValueKind.Object
        && GetNumber(window, "utilization") is double utilization
            ? new UsageWindow(utilization, GetDate(window, "resets_at"))
            : null;

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
