using System.Net.Http;
using System.Text.Json;

namespace OpenControlEdge.Services;

/// Ajustes › Más aplicaciones: the author's other apps with their latest release on GitHub (public repositories) and
/// a link to download its installer. Only links on github.com are offered; anything else falls back to the release
/// page. Never throws.
internal static class MoreAppsService
{
    internal sealed record App(string Id, string Name, string Repository);

    internal sealed record Latest(App App, string? Version, DateTimeOffset? Date, string Download, string Page);

    internal static readonly App Orb = new("orb", "Orb", "danielfinchdev/orb-dev");

    private static readonly HttpClient Client = UsageHttp.Create();
    private static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(30);
    private static (DateTimeOffset At, Latest Result)? _cache;

    public static async Task<Latest> LatestAsync(App app)
    {
        if (_cache is { } cached && cached.Result.App == app && cached.Result.Version is not null
            && DateTimeOffset.UtcNow - cached.At < CacheTime) return cached.Result;
        string page = $"https://github.com/{app.Repository}/releases/latest";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{app.Repository}/releases/latest");
            request.Headers.UserAgent.ParseAdd(UsageHttp.UserAgent);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using HttpResponseMessage response = await Client.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warn("Apps", $"{app.Name}: HTTP {(int)response.StatusCode}");
                return new Latest(app, null, null, page, page);
            }
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            Latest latest = Parse(app, json.RootElement, page);
            _cache = (DateTimeOffset.UtcNow, latest);
            return latest;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log.Warn("Apps", $"{app.Name}: {ex.GetType().Name}");
            return new Latest(app, null, null, page, page);
        }
    }

    /// tag_name "v2.7.2", published_at, and the installer among the assets ("Orb-2.7.2-instalador.exe", verified
    /// 2026-10-11): a name with setup / install / instalador ending in .exe or .msi, else the first .exe or .msi.
    internal static Latest Parse(App app, JsonElement root, string page)
    {
        string? tag = root.TryGetProperty("tag_name", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        string? version = tag is { Length: > 0 and <= 40 } ? tag.TrimStart('v', 'V') : null;
        DateTimeOffset? date = root.TryGetProperty("published_at", out JsonElement d) && d.ValueKind == JsonValueKind.String
            && d.TryGetDateTimeOffset(out DateTimeOffset published) ? published : null;
        var installers = new List<(string Name, string Url)>();
        if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (JsonElement asset in assets.EnumerateArray())
                if (asset.ValueKind == JsonValueKind.Object
                    && asset.TryGetProperty("name", out JsonElement n) && n.GetString() is string name
                    && asset.TryGetProperty("browser_download_url", out JsonElement u) && u.GetString() is string url
                    && (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                    && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps
                    && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                    && uri.AbsolutePath.StartsWith($"/{app.Repository}/releases/download/", StringComparison.OrdinalIgnoreCase))
                    installers.Add((name, uri.AbsoluteUri));
        string download = installers.FirstOrDefault(i => System.Text.RegularExpressions.Regex.IsMatch(i.Name, "setup|install|instalador",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)).Url
            ?? installers.FirstOrDefault().Url ?? page;
        return new Latest(app, version, date, download, page);
    }
}
