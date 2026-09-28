using System.Globalization;
using System.Text.Json;

namespace OpenControlEdge.Services;

/// Parser for GET https://cursor.com/api/usage-summary (undocumented endpoint).
/// Observed 2026-09-29: {"billingCycleEnd":"ISO-8601","membershipType":"pro","isUnlimited":false,
/// "individualUsage":{"plan":{"totalPercentUsed":number},"onDemand":{"enabled":bool,"used":number,"limit":number|null}}.
/// Identity fields are deliberately never read.
internal static class CursorUsageParser
{
    public static (UsageWindow? Cycle, UsageWindow? OnDemand) Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("billingCycleEnd", out var endValue) || endValue.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(endValue.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var end)
            || !root.TryGetProperty("membershipType", out var membership) || membership.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("isUnlimited", out var unlimited) || unlimited.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !root.TryGetProperty("individualUsage", out var individual) || individual.ValueKind != JsonValueKind.Object
            || !individual.TryGetProperty("plan", out var plan) || plan.ValueKind != JsonValueKind.Object
            || !plan.TryGetProperty("totalPercentUsed", out var percentValue) || !percentValue.TryGetDouble(out double percent)
            || !double.IsFinite(percent)) return (null, null);

        UsageWindow? onDemand = null;
        if (individual.TryGetProperty("onDemand", out var demand) && demand.ValueKind == JsonValueKind.Object
            && demand.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True
            && GetNumber(demand, "used") is double used && GetNumber(demand, "limit") is double limit && limit > 0)
            onDemand = new UsageWindow(Math.Clamp(used * 100 / limit, 0, 100), end);

        return (new UsageWindow(Math.Clamp(percent, 0, 100), end), onDemand);
    }

    private static double? GetNumber(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number)
            ? number : null;
}
