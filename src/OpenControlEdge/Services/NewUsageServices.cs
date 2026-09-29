using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace OpenControlEdge.Services;

/// Reads only OpenCode's local SQLite session aggregates; never opens a network connection.
internal sealed class OpenCodeUsageService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private readonly string? _databasePath;

    internal OpenCodeUsageService(string? databasePath = null) => _databasePath = databasePath;

    internal async Task<OpenCodeSnapshot> FetchAsync()
    {
        try { return await Task.Run(ReadLocal).WaitAsync(Timeout).ConfigureAwait(false); }
        catch (TimeoutException) { return OpenCodeSnapshot.Failed("Tiempo de espera agotado"); }
        catch (Exception ex) { Log.Warn("OpenCode", ex.GetType().Name + ": " + ex.Message); return OpenCodeSnapshot.Failed("No se pudo leer el uso local"); }
    }

    private OpenCodeSnapshot ReadLocal()
    {
        string[] paths = _databasePath is not null ? new[] { _databasePath } : ResolveDatabasePaths();
        if (paths.Length == 0) return OpenCodeSnapshot.Failed("Sin base de datos de sesiones");

        long input = 0, output = 0, reasoning = 0, cacheRead = 0, cacheWrite = 0;
        decimal cost = 0;
        foreach (string path in paths)
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 4,
            };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using (var schema = connection.CreateCommand())
            {
                schema.CommandText = "PRAGMA table_info(session)";
                schema.CommandTimeout = 2;
                using var columnsReader = schema.ExecuteReader();
                var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (columnsReader.Read()) columns.Add(columnsReader.GetString(1));
                string[] required = { "tokens_input", "tokens_output", "tokens_reasoning", "tokens_cache_read", "tokens_cache_write", "cost" };
                if (required.Any(column => !columns.Contains(column)))
                    return OpenCodeSnapshot.Failed("Versión de OpenCode no compatible");
            }
            using var command = connection.CreateCommand();
            command.CommandTimeout = 2;
            command.CommandText = "SELECT COALESCE(SUM(tokens_input), 0), COALESCE(SUM(tokens_output), 0), COALESCE(SUM(tokens_reasoning), 0), COALESCE(SUM(tokens_cache_read), 0), COALESCE(SUM(tokens_cache_write), 0), COALESCE(SUM(cost), 0) FROM session";
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read()) return OpenCodeSnapshot.Failed("Respuesta local sin datos");
            input = checked(input + reader.GetInt64(0));
            output = checked(output + reader.GetInt64(1));
            reasoning = checked(reasoning + reader.GetInt64(2));
            cacheRead = checked(cacheRead + reader.GetInt64(3));
            cacheWrite = checked(cacheWrite + reader.GetInt64(4));
            cost += Convert.ToDecimal(reader.GetDouble(5), System.Globalization.CultureInfo.InvariantCulture);
        }

        // Funnel persisted values through the same strict parser exercised by --snapshot.
        string json = System.Text.Json.JsonSerializer.Serialize(new
        {
            totalTokens = new { input, output, reasoning, cache = new { read = cacheRead, write = cacheWrite } },
            totalCost = cost,
        });
        var values = OpenCodeUsageParser.Parse(json);
        return new OpenCodeSnapshot(false, values.Input, values.Output, values.Reasoning, values.CacheRead,
            values.CacheWrite, values.Cost, null);
    }

    private static string[] ResolveDatabasePaths()
    {
        string? overridePath = Environment.GetEnvironmentVariable("OPENCODE_DB");
        if (!string.IsNullOrWhiteSpace(overridePath) && Path.IsPathFullyQualified(overridePath))
            return File.Exists(overridePath) ? new[] { overridePath } : Array.Empty<string>();
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string[] directories =
        {
            Path.Combine(string.IsNullOrWhiteSpace(xdg) ? Path.Combine(user, ".local", "share") : xdg, "opencode"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "opencode"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "opencode"),
        };
        string? database = directories.Select(dir => Path.Combine(dir, "opencode.db")).FirstOrDefault(File.Exists);
        return database is null ? Array.Empty<string>() : new[] { Path.GetFullPath(database) };
    }
}

internal sealed class DeepSeekUsageService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private const string BalanceUrl = "https://api.deepseek.com/user/balance";

    internal async Task<DeepSeekSnapshot> FetchAsync()
    {
        byte[]? key = null;
        try
        {
            key = ProviderKeyStore.Read("deepseek");
            if (key is null || key.Length == 0) return DeepSeekSnapshot.Failed("Añade la clave API de DeepSeek");
            using var request = new HttpRequestMessage(HttpMethod.Get, BalanceUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Encoding.UTF8.GetString(key));
            request.Headers.Accept.ParseAdd("application/json");
            using HttpResponseMessage response = await Http.SendAsync(request).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized) { Log.Warn("DeepSeek", "HTTP 401"); return DeepSeekSnapshot.Failed("Clave API no válida"); }
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
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private const string KeyUrl = "https://openrouter.ai/api/v1/key";

    internal async Task<OpenRouterSnapshot> FetchAsync()
    {
        byte[]? key = null;
        try
        {
            key = ProviderKeyStore.Read("openrouter");
            if (key is null || key.Length == 0) return OpenRouterSnapshot.Failed("Añade la clave API de OpenRouter");
            using var request = new HttpRequestMessage(HttpMethod.Get, KeyUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Encoding.UTF8.GetString(key));
            request.Headers.Accept.ParseAdd("application/json");
            using HttpResponseMessage response = await Http.SendAsync(request).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized) { Log.Warn("OpenRouter", "HTTP 401"); return OpenRouterSnapshot.Failed("Clave API no válida"); }
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
