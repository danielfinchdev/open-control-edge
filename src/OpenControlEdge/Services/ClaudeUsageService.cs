using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace OpenControlEdge.Services;

/// Fetches Claude usage. Never throws: every failure becomes a ClaudeSnapshot with a Message.
/// Never refreshes tokens and never touches the refresh token (see CredentialReader).
internal sealed class ClaudeUsageService
{
    public const string RenewMessage = "Abre Claude Code para renovar";
    public const string FreeAccountMessage = "Cuenta gratuita: sin límites de uso medibles";

    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<ClaudeSnapshot> FetchAsync()
    {
        try
        {
            var credentials = CredentialReader.Read(CredentialReader.DefaultPath);
            if (credentials is null)
                return ClaudeSnapshot.Failed(AiDetector.ClaudeLoginMessage);

            string? plan = credentials.SubscriptionType;
            bool free = string.Equals(plan, "free", StringComparison.OrdinalIgnoreCase);

            // Token already expired (or no expiry to check): do not even send the request.
            if (credentials.ExpiresAt is not DateTimeOffset expiresAt || DateTimeOffset.UtcNow >= expiresAt)
                return ClaudeSnapshot.Failed(RenewMessage) with { Plan = plan };

            using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
            request.Headers.TryAddWithoutValidation("User-Agent", "OpenControlEdge/2.0");

            using var response = await Http.SendAsync(request).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return ClaudeSnapshot.Failed(RenewMessage) with { Plan = plan };

            // A free account has no session or weekly limits to measure. Whether the endpoint answers it with an
            // error or with empty limits has not been observed, so both lead to the same explanation.
            if (!response.IsSuccessStatusCode)
            {
                Log.Warn("Claude", $"HTTP {(int)response.StatusCode}");
                return free && response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound
                    ? ClaudeSnapshot.Failed(FreeAccountMessage) with { Plan = plan }
                    : ClaudeSnapshot.Failed($"Error HTTP {(int)response.StatusCode}") with { Plan = plan };
            }

            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var usage = ClaudeUsageParser.Parse(body);
            if (usage.Session is null)
            {
                Log.Warn("Claude", "200 response without session data");
                return ClaudeSnapshot.Failed(free ? FreeAccountMessage : "Respuesta sin datos de sesión") with { Plan = plan };
            }
            return new ClaudeSnapshot(false, usage.Session, usage.Weekly, usage.Spent, null) { Plan = plan };
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
