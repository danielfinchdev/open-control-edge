using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace OpenControlEdge.Services;

/// Reads only OpenCode's local SQLite session aggregates (LocalDatabaseReader, without administrator rights); never
/// opens a network connection.
internal sealed class OpenCodeUsageService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(25);
    private readonly string? _databasePath;

    internal OpenCodeUsageService(string? databasePath = null) => _databasePath = databasePath;

    internal async Task<OpenCodeSnapshot> FetchAsync()
    {
        try
        {
            LocalDatabaseReader.OpenCodeTotals totals = await Task.Run(() => LocalDatabaseReader.ReadOpenCode(_databasePath))
                .WaitAsync(Timeout).ConfigureAwait(false);
            return new OpenCodeSnapshot(false, totals.Input, totals.Output, totals.Reasoning, totals.CacheRead, totals.CacheWrite, totals.Cost, null);
        }
        catch (TimeoutException) { Log.Warn("OpenCode", "timeout"); return OpenCodeSnapshot.Failed("Tiempo de espera agotado"); }
        catch (LocalDatabaseReader.ReadException ex)
        {
            Log.Warn("OpenCode", ex.Message);
            return ex.Kind switch
            {
                LocalDatabaseReader.Failure.Missing => OpenCodeSnapshot.Failed("Sin base de datos de sesiones"),
                LocalDatabaseReader.Failure.Incompatible => OpenCodeSnapshot.Failed("Versión de OpenCode no compatible"),
                LocalDatabaseReader.Failure.Invalid => OpenCodeSnapshot.Failed("Respuesta local sin datos"),
                _ when ex.Message == UnelevatedLauncher.SeclogonMessage => OpenCodeSnapshot.Failed(UnelevatedLauncher.SeclogonMessage),
                _ => OpenCodeSnapshot.Failed("No se pudo leer el uso local"),
            };
        }
        catch (Exception ex) { Log.Warn("OpenCode", ex.GetType().Name + ": " + ex.Message); return OpenCodeSnapshot.Failed("No se pudo leer el uso local"); }
    }
}

internal sealed class DeepSeekUsageService
{
    private static readonly HttpClient Http = UsageHttp.Create();
    private const string BalanceUrl = "https://api.deepseek.com/user/balance";
    public const string AddKeyMessage = "Añade la clave API de DeepSeek";

    internal async Task<DeepSeekSnapshot> FetchAsync()
    {
        byte[]? key = null;
        try
        {
            key = ProviderKeyStore.Read("deepseek");
            if (key is null || key.Length == 0) return DeepSeekSnapshot.Failed(AddKeyMessage);
            using var request = new HttpRequestMessage(HttpMethod.Get, BalanceUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Encoding.UTF8.GetString(key));
            request.Headers.Accept.ParseAdd("application/json");
            using HttpResponseMessage response = await Http.SendAsync(request).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized) { Log.Warn("DeepSeek", "HTTP 401"); return DeepSeekSnapshot.Failed(ProviderKeyStore.InvalidKeyMessage); }
            if (!response.IsSuccessStatusCode) { Log.Warn("DeepSeek", $"HTTP {(int)response.StatusCode}"); return DeepSeekSnapshot.Failed($"Error HTTP {(int)response.StatusCode}"); }
            byte[] body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            try { return new DeepSeekSnapshot(false, DeepSeekBalanceParser.Parse(Encoding.UTF8.GetString(body)), null); }
            finally { CryptographicOperations.ZeroMemory(body); }
        }
        catch (TaskCanceledException) { Log.Warn("DeepSeek", "timeout"); return DeepSeekSnapshot.Failed("Tiempo de espera agotado"); }
        catch (HttpRequestException ex) { Log.Warn("DeepSeek", ex.Message); return DeepSeekSnapshot.Failed("Sin conexión"); }
        catch (System.Text.Json.JsonException ex) { Log.Warn("DeepSeek", "invalid JSON: " + ex.Message); return DeepSeekSnapshot.Failed("Respuesta no válida"); }
        catch (Exception ex) { Log.Warn("DeepSeek", ex.GetType().Name + ": " + ex.Message); return DeepSeekSnapshot.Failed("No se pudo leer el saldo"); }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }
}

internal sealed class OpenRouterUsageService
{
    private static readonly HttpClient Http = UsageHttp.Create();
    private const string KeyUrl = "https://openrouter.ai/api/v1/key";
    public const string AddKeyMessage = "Añade la clave API de OpenRouter";

    internal async Task<OpenRouterSnapshot> FetchAsync()
    {
        byte[]? key = null;
        try
        {
            key = ProviderKeyStore.Read("openrouter");
            if (key is null || key.Length == 0) return OpenRouterSnapshot.Failed(AddKeyMessage);
            using var request = new HttpRequestMessage(HttpMethod.Get, KeyUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Encoding.UTF8.GetString(key));
            request.Headers.Accept.ParseAdd("application/json");
            using HttpResponseMessage response = await Http.SendAsync(request).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized) { Log.Warn("OpenRouter", "HTTP 401"); return OpenRouterSnapshot.Failed(ProviderKeyStore.InvalidKeyMessage); }
            if (!response.IsSuccessStatusCode) { Log.Warn("OpenRouter", $"HTTP {(int)response.StatusCode}"); return OpenRouterSnapshot.Failed($"Error HTTP {(int)response.StatusCode}"); }
            byte[] body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            try
            {
                var parsed = OpenRouterKeyParser.Parse(Encoding.UTF8.GetString(body));
                return new OpenRouterSnapshot(false, parsed.Usage, parsed.Limit, parsed.Remaining, null);
            }
            finally { CryptographicOperations.ZeroMemory(body); }
        }
        catch (TaskCanceledException) { Log.Warn("OpenRouter", "timeout"); return OpenRouterSnapshot.Failed("Tiempo de espera agotado"); }
        catch (HttpRequestException ex) { Log.Warn("OpenRouter", ex.Message); return OpenRouterSnapshot.Failed("Sin conexión"); }
        catch (System.Text.Json.JsonException ex) { Log.Warn("OpenRouter", "invalid JSON: " + ex.Message); return OpenRouterSnapshot.Failed("Respuesta no válida"); }
        catch (Exception ex) { Log.Warn("OpenRouter", ex.GetType().Name + ": " + ex.Message); return OpenRouterSnapshot.Failed("No se pudo leer el uso"); }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }
}
