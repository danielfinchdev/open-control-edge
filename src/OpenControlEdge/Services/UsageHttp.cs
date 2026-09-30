using System.Net.Http;

namespace OpenControlEdge.Services;

/// The HttpClient of every usage service. The default pool drops an idle connection after one minute, so a refresh
/// every 2 to 15 minutes paid a new TCP + TLS handshake per host each time (about two thirds of its CPU, measured).
/// Idle connections now outlive the longest refresh interval; a connection the server closed meanwhile is simply
/// replaced, and none lives more than an hour so DNS changes are still picked up.
internal static class UsageHttp
{
    public static HttpClient Create() => new(new SocketsHttpHandler
    {
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(16),
        PooledConnectionLifetime = TimeSpan.FromHours(1),
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };
}
