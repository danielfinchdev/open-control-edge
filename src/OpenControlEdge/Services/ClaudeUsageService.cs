using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace OpenControlEdge.Services;

/// Fetches Claude usage. Never throws: every failure becomes a ClaudeSnapshot with a Message.
/// Never refreshes tokens and never touches the refresh token (see CredentialReader).
internal sealed class ClaudeUsageService
{
    public const string RenewMessage = "Sesión caducada: pulsa el anillo para renovarla";
    public const string FreeAccountMessage = "Cuenta gratuita: sin límites de uso medibles";

    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string ProfileUrl = "https://api.anthropic.com/api/oauth/profile";

    /// The subscription is asked only when the card's info bubble opens, and at most once per this long.
    private static readonly TimeSpan SubscriptionCacheTime = TimeSpan.FromHours(6);
    private (DateTimeOffset At, ClaudeSubscription? Subscription, string? Error)? _subscription;
    private static readonly HttpClient Http = UsageHttp.Create();
    internal DateTimeOffset? RetryAfterUntil { get; private set; }
    private readonly string _credentialsPath;

    /// credentialsPath: another account's .credentials.json (its CLAUDE_CONFIG_DIR); the default one otherwise.
    public ClaudeUsageService(string? credentialsPath = null) => _credentialsPath = credentialsPath ?? CredentialReader.DefaultPath;

    /// After an HTTP 429 the endpoint is not asked again before this (seen 2026-09-30: 429 every 2 minutes for
    /// hours when retried at the refresh interval).
    internal static readonly TimeSpan MinRateLimitBackoff = TimeSpan.FromMinutes(5);

    /// On a worker thread: the login file (maybe on a slow or sleeping drive) is never read on the UI thread.
    public Task<ClaudeSnapshot> FetchAsync() => Task.Run(FetchCoreAsync);

    private async Task<ClaudeSnapshot> FetchCoreAsync()
    {
        try
        {
            var credentials = CredentialReader.Read(_credentialsPath);
            // No token (never signed in, or the CLI dropped the session after a failed refresh): sign in again.
            if (credentials is null)
                return ClaudeSnapshot.Failed(ClaudeSessionRenewer.SignedOutMessage);

            string? plan = credentials.SubscriptionType;
            DateTimeOffset? expiry = credentials.ExpiresAt;
            bool free = string.Equals(plan, "free", StringComparison.OrdinalIgnoreCase);

            // Token already expired (or no expiry to check): do not even send the request.
            if (credentials.ExpiresAt is not DateTimeOffset expiresAt || DateTimeOffset.UtcNow >= expiresAt)
                return ClaudeSnapshot.Failed(RenewMessage) with { Plan = plan, TokenExpiresAt = expiry };

            using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
            request.Headers.TryAddWithoutValidation("User-Agent", UsageHttp.UserAgent);

            using var response = await Http.SendAsync(request).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return ClaudeSnapshot.Failed(RenewMessage) with { Plan = plan, TokenExpiresAt = expiry };

            // A free account has no session or weekly limits to measure. Whether the endpoint answers it with an
            // error or with empty limits has not been observed, so both lead to the same explanation.
            if (!response.IsSuccessStatusCode)
            {
                Log.Warn("Claude", $"HTTP {(int)response.StatusCode}");
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    // Retry-After when it asks for longer; never sooner than MinRateLimitBackoff.
                    TimeSpan delay = response.Headers.RetryAfter?.Delta
                        ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow)
                        ?? TimeSpan.Zero;
                    RetryAfterUntil = DateTimeOffset.UtcNow + (delay > MinRateLimitBackoff ? delay : MinRateLimitBackoff);
                }
                else if ((int)response.StatusCode >= 500)
                {
                    RetryAfterUntil = DateTimeOffset.UtcNow.AddMinutes(1);
                }
                return free && response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound
                    ? ClaudeSnapshot.Failed(FreeAccountMessage) with { Plan = plan, TokenExpiresAt = expiry }
                    : ClaudeSnapshot.Failed($"Error HTTP {(int)response.StatusCode}") with
                    {
                        Plan = plan, TokenExpiresAt = expiry, HttpStatus = (int)response.StatusCode,
                    };
            }

            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var usage = ClaudeUsageParser.Parse(body);
            if (usage.Session is null)
            {
                Log.Warn("Claude", "200 response without session data");
                return ClaudeSnapshot.Failed(free ? FreeAccountMessage : "Respuesta sin datos de sesión") with { Plan = plan, TokenExpiresAt = expiry };
            }
            RetryAfterUntil = null;
            return new ClaudeSnapshot(false, usage.Session, usage.Weekly, usage.Spent, null) { Plan = plan, TokenExpiresAt = expiry };
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

    /// The account's subscription (GET /api/oauth/profile) for the info bubble: (subscription, null) or (null, why not).
    /// Cached for SubscriptionCacheTime; a token that has expired is not even sent. Never throws.
    public async Task<(ClaudeSubscription? Subscription, string? Error)> FetchSubscriptionAsync()
    {
        // A failure is kept a couple of minutes too, so clicks on the (i) during an HTTP 429 do not ask again each time.
        if (_subscription is { } cached && DateTimeOffset.UtcNow - cached.At < (cached.Error is null ? SubscriptionCacheTime : TimeSpan.FromMinutes(2)))
            return (cached.Subscription, cached.Error);
        (ClaudeSubscription?, string?) result;
        try
        {
            var credentials = CredentialReader.Read(_credentialsPath);
            if (credentials is null) return (null, ClaudeSessionRenewer.SignedOutMessage);
            if (credentials.ExpiresAt is not DateTimeOffset expiresAt || DateTimeOffset.UtcNow >= expiresAt) return (null, RenewMessage);

            using var request = new HttpRequestMessage(HttpMethod.Get, ProfileUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
            request.Headers.TryAddWithoutValidation("User-Agent", UsageHttp.UserAgent);
            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return (null, RenewMessage);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warn("Claude", $"profile: HTTP {(int)response.StatusCode}");
                return (null, $"Error HTTP {(int)response.StatusCode}");
            }
            ClaudeSubscription? subscription = ClaudeProfileParser.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            if (subscription is null) Log.Warn("Claude", "profile: 200 response without organization.subscription_status");
            result = subscription is null ? (null, "Respuesta no válida") : (subscription, null);
        }
        catch (HttpRequestException) { result = (null, "Sin conexión"); }
        catch (TaskCanceledException) { result = (null, "Tiempo de espera agotado"); }
        catch (JsonException) { result = (null, "Respuesta no válida"); }
        catch (Exception ex)
        {
            Log.Error("Claude", ex);
            result = (null, "No se pudo leer la suscripción");
        }
        _subscription = (DateTimeOffset.UtcNow, result.Item1, result.Item2);
        return result;
    }
}
