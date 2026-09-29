using System.Globalization;
using OpenControlEdge.Services;

namespace OpenControlEdge.Ui;

/// Number, date and label formats in the current interface language (Loc).
internal static class Fmt
{
    private static CultureInfo Culture => Loc.Culture;

    public static string Percent(double value) =>
        Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", Culture) + "%";

    public static string Celsius(double value) =>
        Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", Culture) + "°C";

    /// "783 MB", "4.096 MB" ("4,096 MB" in English).
    public static string Megabytes(double value) =>
        Math.Round(value, MidpointRounding.AwayFromZero).ToString("N0", Culture) + " MB";

    /// "10,53 €" ("10.53 €" in English); other currencies keep their ISO code ("10,53 USD").
    public static string Amount(Money money)
    {
        string number = money.Amount.ToString("N2", Culture);
        return money.Currency == "EUR" ? $"{number} €" : $"{number} {money.Currency}";
    }

    /// "{0} usado" / "{0} used".
    public static string Used(double percent) => Loc.Format("Value.Used", Percent(percent));

    /// Plan badge text from a provider's raw plan id: "free" → "Free", "pro_plus" → "Pro+", "free_trial" → "Prueba".
    /// Unknown ids are shown capitalised rather than hidden; null or blank means no badge.
    public static string? Plan(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        string key = id.Trim().ToLowerInvariant();
        return key switch
        {
            "free" => "Free",
            "free_trial" or "trial" => Loc.Get("Plan.Trial"),
            "go" => "Go",
            "plus" => "Plus",
            "pro" => "Pro",
            "pro_plus" or "pro+" => "Pro+",
            "max" => "Max",
            "ultra" => "Ultra",
            "team" => "Team",
            "business" => "Business",
            "enterprise" => "Enterprise",
            "edu" => "Edu",
            _ => char.ToUpperInvariant(key[0]) + key[1..].Replace('_', ' '),
        };
    }

    /// Codex window names by length: 5 h "Sesión", 7 days "Semanal", 30 days "Mensual".
    public static string WindowLabel(TimeSpan? length)
    {
        if (length is not TimeSpan span) return Loc.Get("Window.Limit");
        long seconds = (long)Math.Round(span.TotalSeconds);
        return seconds switch
        {
            5 * 3600 => Loc.Get("Window.Session"),
            86400 => Loc.Get("Window.Daily"),
            7 * 86400 => Loc.Get("Window.Weekly"),
            30 * 86400 => Loc.Get("Window.Monthly"),
            _ when seconds % 86400 == 0 => Loc.Format("Window.Days", seconds / 86400),
            _ when seconds % 3600 == 0 => Loc.Format("Window.Hours", seconds / 3600),
            _ => Loc.Get("Window.Limit"),
        };
    }

    /// "Se reinicia en 2 h 05 min" / "Se reinicia en 38 min" within 24 h, "Se reinicia el mié 16, 08:00" within a
    /// week, otherwise "Se reinicia el 13 oct, 01:02". English: "Resets in 38 min", "Resets Wed 16, 8:00 AM".
    public static string Reset(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is not DateTimeOffset at) return string.Empty;

        TimeSpan left = at - now;
        if (left <= TimeSpan.Zero) return Loc.Get("Reset.Now");

        if (left < TimeSpan.FromHours(24))
        {
            int totalMinutes = (int)Math.Ceiling(left.TotalMinutes);
            int hours = totalMinutes / 60;
            int minutes = totalMinutes % 60;
            return hours > 0 ? Loc.Format("Reset.InHours", hours, minutes) : Loc.Format("Reset.InMinutes", minutes);
        }

        DateTimeOffset local = at.ToLocalTime();
        string pattern = Loc.Get(left < TimeSpan.FromDays(7) ? "Reset.DayFormat" : "Reset.DateFormat");
        string day = local.ToString(pattern, Culture).Replace(".", string.Empty);
        return Loc.Format("Reset.On", day, local.ToString(Loc.Get("Reset.TimeFormat"), Culture));
    }
}
