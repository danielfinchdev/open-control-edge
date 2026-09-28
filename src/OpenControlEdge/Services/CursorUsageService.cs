using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace OpenControlEdge.Services;

/// Fetches Cursor plan usage. Never throws: every failure becomes a CursorSnapshot.
internal sealed class CursorUsageService
{
    private const string UsageUrl = "https://cursor.com/api/usage-summary";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly string _databasePath;

    public CursorUsageService(string? databasePath = null) => _databasePath = databasePath ?? CursorCredentialReader.DefaultPath;

    public CursorSnapshot Initial()
    {
        try { return CursorCredentialReader.Read(_databasePath) is null
            ? CursorSnapshot.NotAvailable(AiDetector.CursorLoginMessage) : CursorSnapshot.Failed("Cargando…"); }
        catch { return CursorSnapshot.NotAvailable(AiDetector.CursorLoginMessage); }
    }

    public async Task<CursorSnapshot> FetchAsync()
    {
        try
        {
            var credentials = CursorCredentialReader.Read(_databasePath);
            if (credentials is null) return CursorSnapshot.NotAvailable(AiDetector.CursorLoginMessage);
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            string cookieValue = Uri.EscapeDataString(credentials.UserId + "::" + credentials.AccessToken);
            request.Headers.TryAddWithoutValidation("Cookie", "WorkosCursorSessionToken=" + cookieValue);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", "OpenControlEdge/2.0");
            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return CursorSnapshot.NotAvailable(AiDetector.CursorLoginMessage);
            if (!response.IsSuccessStatusCode)
                return CursorSnapshot.Failed($"Error HTTP {(int)response.StatusCode}") with { Plan = credentials.MembershipType };
            byte[] body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            try
            {
                string json = Encoding.UTF8.GetString(body);
                var (cycle, onDemand, membership) = CursorUsageParser.Parse(json);
                string? plan = string.IsNullOrWhiteSpace(membership) ? credentials.MembershipType : membership;
                return cycle is null
                    ? CursorSnapshot.Failed("Respuesta sin datos") with { Plan = credentials.MembershipType }
                    : new CursorSnapshot(false, cycle, onDemand, null) { Plan = plan };
            }
            finally { CryptographicOperations.ZeroMemory(body); }
        }
        catch (HttpRequestException) { return CursorSnapshot.Failed("Sin conexión"); }
        catch (TaskCanceledException) { return CursorSnapshot.Failed("Tiempo de espera agotado"); }
        catch (System.Text.Json.JsonException) { return CursorSnapshot.Failed("Respuesta no válida"); }
        catch (Microsoft.Data.Sqlite.SqliteException) { return CursorSnapshot.NotAvailable(AiDetector.CursorLoginMessage); }
        catch (UnauthorizedAccessException) { return CursorSnapshot.NotAvailable(AiDetector.CursorLoginMessage); }
        catch (IOException) { return CursorSnapshot.NotAvailable(AiDetector.CursorLoginMessage); }
        catch (Exception) { return CursorSnapshot.Failed("No se pudo leer el uso"); }
    }
}
