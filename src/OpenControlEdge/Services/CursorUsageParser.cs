using System.Globalization;
using System.Text.Json;

namespace OpenControlEdge.Services;

/// Parser for GET https://cursor.com/api/usage-summary (undocumented endpoint).
/// Observed 2026-09-29: {"billingCycleEnd":"ISO-8601","membershipType":"pro","isUnlimited":false,
/// "individualUsage":{"plan":{"totalPercentUsed":number},"onDemand":{"enabled":bool,"used":number,"limit":number|null}}.
/// Identity fields are deliberately never read.
///
/// Amounts are US cents. Verified 2026-09-29 on a "pro" account: individualUsage.plan = { "used": 1429, "limit": 2000,
/// "remaining": 571, … } against the $20 included in Pro, and onDemand = { "enabled": false, "used": 0, "limit": null,
/// "remaining": null }. On-demand spend is shown only when it is enabled and used is above zero.
internal static class CursorUsageParser
{
    internal sealed record Result(UsageWindow? Cycle, UsageWindow? OnDemand, string? Membership, Money? OnDemandSpent, Money? OnDemandLimit);

    private static readonly Result Empty = new(null, null, null, null, null);

    public static Result Parse(string json)
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
            || GetNumber(plan, "totalPercentUsed") is not double percent || !double.IsFinite(percent)) return Empty;

        UsageWindow? onDemand = null;
        Money? spent = null, spendLimit = null;
        if (individual.TryGetProperty("onDemand", out var demand) && demand.ValueKind == JsonValueKind.Object
            && demand.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True
            && GetNumber(demand, "used") is double used && double.IsFinite(used) && used > 0)
        {
            spent = Cents(used);
            if (GetNumber(demand, "limit") is double limit && double.IsFinite(limit) && limit > 0)
            {
                spendLimit = Cents(limit);
                onDemand = new UsageWindow(Math.Clamp(used * 100 / limit, 0, 100), end);
            }
        }

        return new Result(new UsageWindow(Math.Clamp(percent, 0, 100), end), onDemand, membership.GetString(), spent, spendLimit);
    }

    private static Money Cents(double cents) => new(Math.Round((decimal)cents / 100m, 2), "USD");

    private static double? GetNumber(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number)
            ? number : null;
}
