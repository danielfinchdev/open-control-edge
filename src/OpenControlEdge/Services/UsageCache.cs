using System.Globalization;
using System.IO;
using System.Text.Json;

namespace OpenControlEdge.Services;

/// The last good reading of Claude, Codex and Cursor in cache.json in the data folder (DataFolder.Path), painted the
/// moment the widget opens so it shows data in 2–3 s instead of "Cargando…"; the first refresh replaces it.
///
///   { "savedAt": "ISO-8601",
///     "claude": { "plan", "session": {"percent","resetsAt"}, "weekly": {…}, "spent": {"amount","currency"} },
///     "codex":  { "plan", "primary": {"percent","lengthSeconds","resetsAt"}, "secondary": {…}, "credits": {…} },
///     "cursor": { "plan", "cycle": {…}, "onDemand": {…}, "onDemandSpent": {…}, "onDemandLimit": {…} } }
///
/// Only percentages, dates, plan names and amounts: no token, no account id, no e-mail. A provider is left out of
/// the cache (and of the first paint) when one of its windows has already reset, so a stale percentage is never
/// shown as current; the whole file is ignored after 24 h. Written only when it changes, atomically, and only into
/// a folder that non-administrators cannot write to when this process is elevated (DataFolder). Never throws.
internal static class UsageCache
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    private static byte[]? _lastWritten;

    public static string FilePath => Path.Combine(DataFolder.Path, "cache.json");

    /// SavedAt: when the cached readings were taken; null when there are none.
    internal sealed record Cached(ClaudeSnapshot? Claude, CodexSnapshot? Codex, CursorSnapshot? Cursor, DateTimeOffset? SavedAt = null);

    public static Cached Load(DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(FilePath)) return new Cached(null, null, null);
            using var document = JsonDocument.Parse(File.ReadAllBytes(FilePath));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Date(root, "savedAt") is not DateTimeOffset saved
                || now - saved > MaxAge || saved > now.AddMinutes(5))
                return new Cached(null, null, null);

            return new Cached(ReadClaude(root, now), ReadCodex(root, now), ReadCursor(root, now), saved);
        }
        catch (Exception ex)
        {
            Log.Warn("Cache", "ignorada: " + ex.GetType().Name);
            return new Cached(null, null, null);
        }
    }

    /// Saves whichever snapshots carry data; the others keep their previous cached entry out (a failing provider is
    /// simply not painted from the cache next time).
    public static void Save(ClaudeSnapshot? claude, CodexSnapshot? codex, CursorSnapshot? cursor, DateTimeOffset now)
    {
        try
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("savedAt", now.ToString("o", CultureInfo.InvariantCulture));
                if (claude is { Hidden: false, Session: UsageWindow session })
                {
                    writer.WriteStartObject("claude");
                    WritePlan(writer, claude.Plan);
                    WriteWindow(writer, "session", session);
                    if (claude.Weekly is UsageWindow weekly) WriteWindow(writer, "weekly", weekly);
                    if (claude.Spent is Money spent) WriteMoney(writer, "spent", spent);
                    writer.WriteEndObject();
                }
                if (codex is { Hidden: false, Primary: CodexWindow primary })
                {
                    writer.WriteStartObject("codex");
                    WritePlan(writer, codex.Plan);
                    WriteCodexWindow(writer, "primary", primary);
                    if (codex.Secondary is CodexWindow secondary) WriteCodexWindow(writer, "secondary", secondary);
                    if (codex.Credits is CodexCredits credits)
                    {
                        writer.WriteStartObject("credits");
                        if (credits.Balance is decimal balance) writer.WriteNumber("balance", balance);
                        writer.WriteBoolean("unlimited", credits.Unlimited);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndObject();
                }
                if (cursor is { Hidden: false, Cycle: UsageWindow cycle })
                {
                    writer.WriteStartObject("cursor");
                    WritePlan(writer, cursor.Plan);
                    WriteWindow(writer, "cycle", cycle);
                    if (cursor.OnDemand is UsageWindow onDemand) WriteWindow(writer, "onDemand", onDemand);
                    if (cursor.OnDemandSpent is Money spent) WriteMoney(writer, "onDemandSpent", spent);
                    if (cursor.OnDemandLimit is Money limit) WriteMoney(writer, "onDemandLimit", limit);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }

            // Compare without the timestamp: an unchanged reading is not written again.
            byte[] bytes = buffer.ToArray();
            int key = bytes.AsSpan().IndexOf("\"savedAt\""u8);
            int lineEnd = key < 0 ? -1 : bytes.AsSpan(key).IndexOf((byte)'\n');
            byte[] body = lineEnd < 0 ? bytes : bytes[(key + lineEnd + 1)..];
            if (_lastWritten is not null && body.AsSpan().SequenceEqual(_lastWritten)) return;
            if (!DataFolder.WriteAtomic("cache.json", bytes)) return;
            _lastWritten = body;
        }
        catch (Exception ex)
        {
            Log.Warn("Cache", "no se pudo guardar: " + ex.GetType().Name);
        }
    }

    private static ClaudeSnapshot? ReadClaude(JsonElement root, DateTimeOffset now)
    {
        if (!Object(root, "claude", out JsonElement o) || Window(o, "session", now) is not UsageWindow session) return null;
        UsageWindow? weekly = null;
        if (o.TryGetProperty("weekly", out _) && (weekly = Window(o, "weekly", now)) is null) return null;
        return new ClaudeSnapshot(false, session, weekly, MoneyOf(o, "spent"), null) { Plan = Text(o, "plan") };
    }

    private static CodexSnapshot? ReadCodex(JsonElement root, DateTimeOffset now)
    {
        if (!Object(root, "codex", out JsonElement o) || CodexWindowOf(o, "primary", now) is not CodexWindow primary) return null;
        CodexWindow? secondary = null;
        if (o.TryGetProperty("secondary", out _) && (secondary = CodexWindowOf(o, "secondary", now)) is null) return null;
        CodexCredits? credits = null;
        if (Object(o, "credits", out JsonElement c))
            credits = new CodexCredits(Number(c, "balance") is double b ? (decimal)b : null,
                c.TryGetProperty("unlimited", out var u) && u.ValueKind == JsonValueKind.True);
        return new CodexSnapshot(false, primary, secondary, null) { Plan = Text(o, "plan"), Credits = credits };
    }

    private static CursorSnapshot? ReadCursor(JsonElement root, DateTimeOffset now)
    {
        if (!Object(root, "cursor", out JsonElement o) || Window(o, "cycle", now) is not UsageWindow cycle) return null;
        return new CursorSnapshot(false, cycle, Window(o, "onDemand", now), null)
        {
            Plan = Text(o, "plan"),
            OnDemandSpent = MoneyOf(o, "onDemandSpent"),
            OnDemandLimit = MoneyOf(o, "onDemandLimit"),
        };
    }

    private static void WritePlan(Utf8JsonWriter writer, string? plan)
    {
        if (!string.IsNullOrWhiteSpace(plan)) writer.WriteString("plan", plan);
    }

    private static void WriteWindow(Utf8JsonWriter writer, string name, UsageWindow window)
    {
        writer.WriteStartObject(name);
        writer.WriteNumber("percent", window.Percent);
        if (window.ResetsAt is DateTimeOffset at) writer.WriteString("resetsAt", at.ToString("o", CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }

    private static void WriteCodexWindow(Utf8JsonWriter writer, string name, CodexWindow window)
    {
        writer.WriteStartObject(name);
        writer.WriteNumber("percent", window.Percent);
        if (window.Length is TimeSpan length) writer.WriteNumber("lengthSeconds", (long)length.TotalSeconds);
        if (window.ResetsAt is DateTimeOffset at) writer.WriteString("resetsAt", at.ToString("o", CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }

    private static void WriteMoney(Utf8JsonWriter writer, string name, Money money)
    {
        writer.WriteStartObject(name);
        writer.WriteNumber("amount", money.Amount);
        writer.WriteString("currency", money.Currency);
        writer.WriteEndObject();
    }

    /// A window whose reset time has passed is stale: null, so the provider is not painted from the cache.
    private static UsageWindow? Window(JsonElement parent, string name, DateTimeOffset now)
    {
        if (!Object(parent, name, out JsonElement o) || Number(o, "percent") is not double percent || !double.IsFinite(percent)) return null;
        DateTimeOffset? resets = Date(o, "resetsAt");
        return resets is DateTimeOffset at && at <= now ? null : new UsageWindow(Math.Clamp(percent, 0, 100), resets);
    }

    private static CodexWindow? CodexWindowOf(JsonElement parent, string name, DateTimeOffset now)
    {
        if (Window(parent, name, now) is not UsageWindow window) return null;
        parent.TryGetProperty(name, out JsonElement o);
        TimeSpan? length = Number(o, "lengthSeconds") is double s && s > 0 ? TimeSpan.FromSeconds(s) : null;
        return new CodexWindow(window.Percent, length, window.ResetsAt);
    }

    private static Money? MoneyOf(JsonElement parent, string name) =>
        Object(parent, name, out JsonElement o) && Number(o, "amount") is double amount && double.IsFinite(amount)
        && Text(o, "currency") is string currency && currency.Length is > 0 and <= 8
            ? new Money((decimal)amount, currency)
            : null;

    private static bool Object(JsonElement parent, string name, out JsonElement value) =>
        parent.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;

    private static double? Number(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) ? d : null;

    private static string? Text(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? Date(JsonElement o, string name) =>
        Text(o, name) is string text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)
            ? date
            : null;
}
