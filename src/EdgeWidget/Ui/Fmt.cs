using System.Globalization;
using EdgeWidget.Services;

namespace EdgeWidget.Ui;

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

    /// "Reinicia en 2 h 05 min" / "Reinicia en 38 min" within 24 h, "Reinicia el mié 16, 08:00" within a week,
    /// otherwise "Reinicia el 13 oct, 01:02".
    public static string Reset(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is not DateTimeOffset at) return string.Empty;

        TimeSpan left = at - now;
        if (left <= TimeSpan.Zero) return "Reinicia ahora";

        if (left < TimeSpan.FromHours(24))
        {
            int totalMinutes = (int)Math.Ceiling(left.TotalMinutes);
            int hours = totalMinutes / 60;
            int minutes = totalMinutes % 60;
            return hours > 0 ? $"Reinicia en {hours} h {minutes:00} min" : $"Reinicia en {minutes} min";
        }

        DateTimeOffset local = at.ToLocalTime();
        string day = local.ToString(left < TimeSpan.FromDays(7) ? "ddd d" : "d MMM", Spanish).Replace(".", string.Empty);
        return $"Reinicia el {day}, {local.ToString("HH:mm", Spanish)}";
    }
}
