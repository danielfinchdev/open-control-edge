using System.Text.Json;

namespace OpenControlEdge.Services;

/// Parser for GET https://api.anthropic.com/api/oauth/profile (undocumented endpoint), read only for the info bubble of
/// the Claude card.
///
/// Shape verified against a real 200 response on 2026-10-11 (Max plan):
///   "organization": { "organization_type": "claude_max", "billing_type": "stripe_subscription",
///                     "subscription_status": "active", "subscription_created_at": "2026-03-07T21:50:52.429894Z", … }
/// Only those three organization fields are read; the account (name, e-mail, ids) is never turned into strings.
/// No subscription_status string means the shape is not the known one: null, never a guess.
internal static class ClaudeProfileParser
{
    public static ClaudeSubscription? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("organization", out JsonElement organization) || organization.ValueKind != JsonValueKind.Object
            || !organization.TryGetProperty("subscription_status", out JsonElement status) || status.ValueKind != JsonValueKind.String
            || status.GetString() is not { Length: > 0 and <= 40 } statusText)
            return null;

        string? billing = organization.TryGetProperty("billing_type", out JsonElement b) && b.ValueKind == JsonValueKind.String
            ? b.GetString() : null;
        DateTimeOffset? created = organization.TryGetProperty("subscription_created_at", out JsonElement c)
            && c.ValueKind == JsonValueKind.String && c.TryGetDateTimeOffset(out DateTimeOffset date) ? date : null;
        return new ClaudeSubscription(statusText, billing is { Length: <= 40 } ? billing : null, created);
    }

    /// The next monthly anniversary of the day the subscription was taken out, after now (the 31st becomes the last
    /// day of shorter months). Only an estimate: the endpoint does not say whether it is paid monthly or yearly.
    public static DateTimeOffset NextMonthlyRenewal(DateTimeOffset created, DateTimeOffset now)
    {
        DateTime start = created.ToLocalTime().DateTime;
        DateTime local = now.ToLocalTime().DateTime;
        for (int months = Math.Max(0, (local.Year - start.Year) * 12 + local.Month - start.Month - 1); ; months++)
        {
            DateTime month = new DateTime(start.Year, start.Month, 1).AddMonths(months);
            var candidate = new DateTime(month.Year, month.Month, Math.Min(start.Day, DateTime.DaysInMonth(month.Year, month.Month)),
                start.Hour, start.Minute, start.Second);
            if (candidate > local) return new DateTimeOffset(candidate, TimeZoneInfo.Local.GetUtcOffset(candidate));
        }
    }
}
