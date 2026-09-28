using System.Globalization;
using OpenControlEdge.Services;

namespace OpenControlEdge.Ui;

internal static class Fmt
{
    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-ES");

    public static string Percent(double value) =>
        Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", Spanish) + "%";

    public static string Celsius(double value) =>
        Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", Spanish) + "°C";

    /// "783 MB", "4.096 MB".
    public static string Megabytes(double value) =>
        Math.Round(value, MidpointRounding.AwayFromZero).ToString("N0", Spanish) + " MB";

    /// "10,53 €"; other currencies keep their ISO code ("10,53 USD").
    public static string Amount(Money money)
    {
        string number = money.Amount.ToString("N2", Spanish);
        return money.Currency == "EUR" ? $"{number} €" : $"{number} {money.Currency}";
    }

    /// Plan badge text from a provider's raw plan id: "free" → "Free", "pro_plus" → "Pro+", "free_trial" → "Prueba".
    /// Unknown ids are shown capitalised rather than hidden; null or blank means no badge.
    public static string? Plan(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        string key = id.Trim().ToLowerInvariant();
        return key switch
        {
            "free" => "Free",
            "free_trial" or "trial" => "Prueba",
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
        if (length is not TimeSpan span) return "Límite";
        long seconds = (long)Math.Round(span.TotalSeconds);
        return seconds switch
        {
            5 * 3600 => "Sesión",
            86400 => "Diario",
            7 * 86400 => "Semanal",
            30 * 86400 => "Mensual",
            _ when seconds % 86400 == 0 => $"Límite de {seconds / 86400} días",
            _ when seconds % 3600 == 0 => $"Límite de {seconds / 3600} h",
            _ => "Límite",
        };
    }

    /// "Se reinicia en 2 h 05 min" / "Se reinicia en 38 min" within 24 h, "Se reinicia el mié 16, 08:00" within a
    /// week, otherwise "Se reinicia el 13 oct, 01:02".
    public static string Reset(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is not DateTimeOffset at) return string.Empty;

        TimeSpan left = at - now;
        if (left <= TimeSpan.Zero) return "Se reinicia ahora";

        if (left < TimeSpan.FromHours(24))
        {
            int totalMinutes = (int)Math.Ceiling(left.TotalMinutes);
            int hours = totalMinutes / 60;
            int minutes = totalMinutes % 60;
            return hours > 0 ? $"Se reinicia en {hours} h {minutes:00} min" : $"Se reinicia en {minutes} min";
        }

        DateTimeOffset local = at.ToLocalTime();
        string day = local.ToString(left < TimeSpan.FromDays(7) ? "ddd d" : "d MMM", Spanish).Replace(".", string.Empty);
        return $"Se reinicia el {day}, {local.ToString("HH:mm", Spanish)}";
    }
}
