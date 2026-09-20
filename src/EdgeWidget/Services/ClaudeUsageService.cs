using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace EdgeWidget.Services;

/// Fetches Claude usage. Never throws: every failure becomes a ClaudeSnapshot with a Message.
/// Never refreshes tokens and never touches the refresh token (see CredentialReader).
internal sealed class ClaudeUsageService
{
    public const string RenewMessage = "Abre Claude Code para renovar";

    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<ClaudeSnapshot> FetchAsync()
    {
        try
        {
            var credentials = CredentialReader.Read(CredentialReader.DefaultPath);
            if (credentials is null)
                return ClaudeSnapshot.Failed("No se encuentran las credenciales de Claude Code");

            // Token already expired (or no expiry to check): do not even send the request.
            if (credentials.ExpiresAt is not DateTimeOffset expiresAt || DateTimeOffset.UtcNow >= expiresAt)
                return ClaudeSnapshot.Failed(RenewMessage);

            using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
            request.Headers.TryAddWithoutValidation("User-Agent", "EdgeWidget/1.0");

            using var response = await Http.SendAsync(request).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return ClaudeSnapshot.Failed(RenewMessage);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warn("Claude", $"HTTP {(int)response.StatusCode}");
                return ClaudeSnapshot.Failed($"Error HTTP {(int)response.StatusCode}");
            }

            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var usage = ClaudeUsageParser.Parse(body);
            if (usage.Session is null)
            {
                Log.Warn("Claude", "200 response without session data");
                return ClaudeSnapshot.Failed("Respuesta sin datos de sesión");
            }
            return new ClaudeSnapshot(usage.Session, usage.Weekly, usage.Spent, null);
        }
        catch (HttpRequestException ex)
        {
            Log.Warn("Claude", ex.Message);
            return ClaudeSnapshot.Failed("Sin conexión");
        }
        catch (TaskCanceledException)
        {
            Log.Warn("Claude", "timeout");
            return ClaudeSnapshot.Failed("Tiempo de espera agotado");
        }
        catch (JsonException ex)
        {
            Log.Warn("Claude", "invalid JSON: " + ex.Message);
            return ClaudeSnapshot.Failed("Respuesta no válida");
        }
        catch (Exception ex)
        {
            Log.Error("Claude", ex);
            return ClaudeSnapshot.Failed("No se pudo leer el uso");
        }
    }
}
