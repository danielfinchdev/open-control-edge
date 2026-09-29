using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace OpenControlEdge.Services;

/// Fetches Codex (ChatGPT) usage. Never throws: every failure becomes a CodexSnapshot.
/// Never refreshes tokens and never touches the refresh token (see CodexCredentialReader).
/// Verified 2026-09-13: the endpoint answers 200 with the bearer token alone; ChatGPT-Account-Id is not needed.
internal sealed class CodexUsageService
{
    private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly string _authPath;

    public CodexUsageService(string? authPath = null) => _authPath = authPath ?? CodexCredentialReader.DefaultPath;

    /// No network: hidden when there is no usable login, otherwise a placeholder until the first fetch.
    public CodexSnapshot Initial()
    {
        string? reason = UnusableReason(out _);
        return reason is null ? CodexSnapshot.Failed("Cargando…")
            : CodexSnapshot.NotAvailable(reason == "access token expired" ? "Abre Codex para renovar" : AiDetector.CodexLoginMessage);
    }

    public async Task<CodexSnapshot> FetchAsync()
    {
        try
        {
            // Missing file, no token or expired token: hide the ring without sending the request.
            string? unusable = UnusableReason(out var credentials);
            if (unusable is not null) return CodexSnapshot.NotAvailable(unusable == "access token expired"
                ? "Abre Codex para renovar" : AiDetector.CodexLoginMessage);

            using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials!.AccessToken);
            request.Headers.TryAddWithoutValidation("User-Agent", "OpenControlEdge/2.0");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var response = await Http.SendAsync(request).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return CodexSnapshot.NotAvailable(AiDetector.CodexLoginMessage);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warn("Codex", $"HTTP {(int)response.StatusCode}");
                return CodexSnapshot.Failed($"Error HTTP {(int)response.StatusCode}") with { Plan = credentials.PlanType };
            }

            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var (primary, secondary) = CodexUsageParser.Parse(body, DateTimeOffset.UtcNow);
            if (primary is null)
            {
                Log.Warn("Codex", "200 response without rate_limit.primary_window");
                return CodexSnapshot.Failed("Respuesta sin datos de uso") with { Plan = credentials.PlanType };
            }
            return new CodexSnapshot(false, primary, secondary, null) { Plan = credentials.PlanType };
        }
        catch (HttpRequestException ex)
        {
            Log.Warn("Codex", ex.Message);
            return CodexSnapshot.Failed("Sin conexión");
        }
        catch (TaskCanceledException)
        {
            Log.Warn("Codex", "timeout");
            return CodexSnapshot.Failed("Tiempo de espera agotado");
        }
        catch (JsonException ex)
        {
            Log.Warn("Codex", "invalid JSON: " + ex.Message);
            return CodexSnapshot.Failed("Respuesta no válida");
        }
        catch (Exception ex)
        {
            Log.Error("Codex", ex);
            return CodexSnapshot.Failed("No se pudo leer el uso");
        }
    }

    /// Null when the login is usable; otherwise why the ring must be hidden. Never contains secrets.
    private string? UnusableReason(out CodexCredentialReader.Credentials? credentials)
    {
        credentials = null;
        if (!File.Exists(_authPath)) return "auth.json not found";

        try
        {
            credentials = CodexCredentialReader.Read(_authPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return "auth.json unreadable: " + ex.GetType().Name;
        }

        if (credentials is null) return "no tokens.access_token";
        if (credentials.ExpiresAt is DateTimeOffset expiresAt && DateTimeOffset.UtcNow >= expiresAt) return "access token expired";
        return null;
    }
}
