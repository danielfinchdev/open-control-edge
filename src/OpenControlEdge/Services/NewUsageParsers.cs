using System.Globalization;
using System.Text.Json;

namespace OpenControlEdge.Services;

/// Strict parser for OpenCode's local session aggregate shape. It accepts no transcript or session metadata.
internal static class OpenCodeUsageParser
{
    public static (long Input, long Output, long Reasoning, long CacheRead, long CacheWrite, decimal Cost) Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("totalTokens", out JsonElement tokens) || tokens.ValueKind != JsonValueKind.Object
            || !ReadNonNegativeInt64(tokens, "input", out long input)
            || !ReadNonNegativeInt64(tokens, "output", out long output)
            || !ReadNonNegativeInt64(tokens, "reasoning", out long reasoning)
            || !tokens.TryGetProperty("cache", out JsonElement cache) || cache.ValueKind != JsonValueKind.Object
            || !ReadNonNegativeInt64(cache, "read", out long cacheRead)
            || !ReadNonNegativeInt64(cache, "write", out long cacheWrite)
            || !root.TryGetProperty("totalCost", out JsonElement costValue)
            || costValue.ValueKind != JsonValueKind.Number || !costValue.TryGetDecimal(out decimal cost) || cost < 0)
            throw new JsonException("OpenCode stats fields missing");
        return (input, output, reasoning, cacheRead, cacheWrite, cost);
    }

    private static bool ReadNonNegativeInt64(JsonElement obj, string key, out long value)
    {
        value = 0;
        return obj.TryGetProperty(key, out JsonElement item) && item.ValueKind == JsonValueKind.Number
            && item.TryGetInt64(out value) && value >= 0;
    }

}

internal static class DeepSeekBalanceParser
{
    public static Money Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("is_available", out JsonElement available) || available.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !root.TryGetProperty("balance_infos", out JsonElement balances) || balances.ValueKind != JsonValueKind.Array
            || balances.GetArrayLength() == 0)
            throw new JsonException("DeepSeek balance_infos missing");

        Money? cny = null;
        foreach (JsonElement entry in balances.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("currency", out JsonElement currencyValue) || currencyValue.ValueKind != JsonValueKind.String
                || !entry.TryGetProperty("total_balance", out JsonElement amountValue) || amountValue.ValueKind != JsonValueKind.String
                || !decimal.TryParse(amountValue.GetString(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out decimal amount) || amount < 0)
                throw new JsonException("DeepSeek balance entry malformed");
            string currency = currencyValue.GetString()!;
            if (currency is not ("USD" or "CNY")) throw new JsonException("DeepSeek currency unsupported");
            if (currency == "USD") return new Money(amount, currency);
            if (currency == "CNY") cny = new Money(amount, currency);
        }
        if (cny is Money yuan) return yuan;
        throw new JsonException("DeepSeek balance unavailable");
    }

    private static bool ReadNonNegativeInt64(JsonElement obj, string key, out long value)
    {
        value = 0;
        return obj.TryGetProperty(key, out JsonElement item) && item.ValueKind == JsonValueKind.Number
            && item.TryGetInt64(out value) && value >= 0;
    }
}

internal static class OpenRouterKeyParser
{
    public static (decimal Usage, decimal? Limit, decimal? Remaining) Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object
            || !ReadNonNegative(data, "usage", out decimal usage)
            || !data.TryGetProperty("limit", out JsonElement limitValue)
            || !data.TryGetProperty("limit_remaining", out JsonElement remainingValue))
            throw new JsonException("OpenRouter key data missing");

        decimal? limit = limitValue.ValueKind == JsonValueKind.Number && limitValue.TryGetDecimal(out decimal l) && l >= 0 ? l : null;
        decimal? remaining = remainingValue.ValueKind == JsonValueKind.Number
            && remainingValue.TryGetDecimal(out decimal r) && r >= 0 ? r : null;
        if (limitValue.ValueKind is not (JsonValueKind.Number or JsonValueKind.Null)
            || remainingValue.ValueKind is not (JsonValueKind.Number or JsonValueKind.Null)
            || (limit is null) != (remaining is null))
            throw new JsonException("OpenRouter limit fields malformed");
        return (usage, limit, remaining);
    }

    private static bool ReadNonNegative(JsonElement obj, string key, out decimal value)
    {
        value = 0;
        return obj.TryGetProperty(key, out JsonElement item) && item.ValueKind == JsonValueKind.Number
            && item.TryGetDecimal(out value) && value >= 0;
    }
}
