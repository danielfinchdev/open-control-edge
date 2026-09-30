using System.Globalization;
using System.Text.Json;

namespace OpenControlEdge.Services;

/// GET https://api.deepseek.com/user/balance: the total balance in USD when the account has one, otherwise in CNY.
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
            if (currency == "USD") return new Money(amount, currency);
            if (currency == "CNY") cny = new Money(amount, currency);
        }
        if (cny is Money yuan) return yuan;
        throw new JsonException("DeepSeek balance unavailable");
    }
}

/// GET https://openrouter.ai/api/v1/key: usage in USD, and the key's limit with what remains of it (both null when
/// the key has no limit).
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
