using System.Net.Http;

namespace OpenControlEdge.Services;

/// The HttpClient of every usage service. The default pool drops an idle connection after one minute, so a refresh
/// every 2 to 15 minutes paid a new TCP + TLS handshake per host each time (about two thirds of its CPU, measured).
/// Idle connections now outlive the longest refresh interval; a connection the server closed meanwhile is simply
/// replaced, and none lives more than an hour so DNS changes are still picked up.
internal static class UsageHttp
{
    /// "OpenControlEdge/2.1.0": the version of this build, for every request the app sends.
    public static string UserAgent { get; } =
        $"OpenControlEdge/{typeof(UsageHttp).Assembly.GetName().Version?.ToString(3) ?? "unknown"}";

    public static HttpClient Create() => new(new SocketsHttpHandler
    {
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(16),
        PooledConnectionLifetime = TimeSpan.FromHours(1),
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };
}
