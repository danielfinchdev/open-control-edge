using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenControlEdge.Services;

internal sealed record UpdateRelease(Version Version, string Tag, string Notes, Uri ZipUri, string Digest);
internal sealed record UpdateCheckResult(UpdateRelease? Release, string? Error)
{
    internal bool IsAvailable => Release is not null;
}

/// Release metadata, archive digest verification and safe ZIP staging for the in-app updater.
internal static class UpdateService
{
    internal const string LatestApi = "https://api.github.com/repos/danielfinchdev/open-control-edge/releases/latest";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(4) };
    private const long MaxArchiveBytes = 300L * 1024 * 1024;
    private const long MaxExpandedBytes = 900L * 1024 * 1024;

    internal static async Task<UpdateCheckResult> CheckAsync(string apiUrl = LatestApi, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Uri.TryCreate(apiUrl, UriKind.Absolute, out Uri? endpoint)
                || endpoint.Scheme != Uri.UriSchemeHttps && !(endpoint.IsLoopback && endpoint.Scheme == Uri.UriSchemeHttp))
                return new(null, "La dirección de releases no es segura.");
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.UserAgent.ParseAdd("OpenControlEdge/2.1");
            using HttpResponseMessage response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
            JsonElement root = json.RootElement;
            string tag = RequiredString(root, "tag_name");
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out Version? version))
                return new(null, "La versión de GitHub no tiene un formato válido.");
            Version current = typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0);
            if (version <= current) return new(null, null);
            string notes = root.TryGetProperty("body", out JsonElement body) && body.ValueKind == JsonValueKind.String ? body.GetString() ?? "" : "";
            if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
                return new(null, "El release no contiene assets.");
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string name = RequiredString(asset, "name");
                if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                string download = RequiredString(asset, "browser_download_url");
                if (!Uri.TryCreate(download, UriKind.Absolute, out Uri? uri)
                    || uri.Scheme != Uri.UriSchemeHttps && !(uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp))
                    return new(null, "La URL del ZIP no es segura.");
                if (!asset.TryGetProperty("digest", out JsonElement digestElement) || digestElement.ValueKind != JsonValueKind.String
                    || digestElement.GetString() is not string digest || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                    || digest.Length != 71 || !digest.AsSpan(7).ToArray().All(Uri.IsHexDigit))
                    return new(null, "El ZIP no tiene un digest SHA-256 verificable; no se instalará.");
                return new(new(version, tag, notes, uri, digest[7..].ToLowerInvariant()), null);
            }
            return new(null, "El release no incluye un ZIP de instalación.");
        }
        catch (OperationCanceledException) { return new(null, "La búsqueda se ha cancelado."); }
        catch (HttpRequestException) { return new(null, "No se pudo conectar con GitHub."); }
        catch (JsonException) { return new(null, "La respuesta de releases no tiene el formato esperado."); }
        catch (Exception ex) { Log.Warn("Updates", "release check: " + ex.GetType().Name); return new(null, "No se pudo comprobar si hay actualizaciones."); }
    }

    private static string RequiredString(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : throw new JsonException("Missing " + key);

    internal static async Task<string> DownloadAndStageAsync(UpdateRelease release, string stageRoot, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, release.ZipUri);
        request.Headers.UserAgent.ParseAdd("OpenControlEdge/2.1");
        using HttpResponseMessage download = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        download.EnsureSuccessStatusCode();
        if (download.Content.Headers.ContentLength is long length && (length <= 0 || length > MaxArchiveBytes))
            throw new InvalidDataException("El ZIP tiene un tamaño no válido.");
        using var archiveBuffer = new MemoryStream();
        await using (Stream source = await download.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            byte[] chunk = new byte[64 * 1024];
            while (true)
            {
                int count = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                if (archiveBuffer.Length + count > MaxArchiveBytes) throw new InvalidDataException("El ZIP supera el tamaño permitido.");
                await archiveBuffer.WriteAsync(chunk.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }
        }
        byte[] archive = archiveBuffer.ToArray();
        if (archive.LongLength <= 0) throw new InvalidDataException("El ZIP está vacío.");
        byte[] digest = System.Security.Cryptography.SHA256.HashData(archive);
        string actual = Convert.ToHexString(digest).ToLowerInvariant();
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(digest);
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(release.Digest)))
        {
            CryptographicOperations.ZeroMemory(archive);
            throw new InvalidDataException("El digest SHA-256 del ZIP no coincide. No se instalará.");
        }

        string root = Path.GetFullPath(stageRoot);
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        string payload = Path.Combine(root, "payload");
        Directory.CreateDirectory(payload);
        try
        {
            using var memory = new MemoryStream(archive, writable: false);
            using var zip = new ZipArchive(memory, ZipArchiveMode.Read);
            long expanded = 0;
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                string name = entry.FullName.Replace('\\', '/');
                if (name.StartsWith('/') || name.Split('/').Any(part => part is ".." or ".")
                    || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new InvalidDataException("El ZIP contiene una ruta o enlace no seguro.");
                expanded = checked(expanded + entry.Length);
                if (expanded > MaxExpandedBytes) throw new InvalidDataException("El contenido extraído supera el límite permitido.");
                string target = Path.GetFullPath(Path.Combine(payload, name.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(payload + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("El ZIP intenta salir de staging.");
                if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using Stream input = entry.Open();
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
            if (File.Exists(Path.Combine(payload, "OpenControlEdge.exe"))) return payload;
            string[] roots = Directory.GetDirectories(payload);
            if (roots.Length == 1 && File.Exists(Path.Combine(roots[0], "OpenControlEdge.exe"))) return roots[0];
            throw new InvalidDataException("El ZIP no contiene OpenControlEdge.exe en la raíz o en un único directorio.");
        }
        catch { try { Directory.Delete(root, recursive: true); } catch { } throw; }
        finally { CryptographicOperations.ZeroMemory(archive); }
    }

    internal static bool IsLoopback(Uri uri) => uri.IsLoopback;
}
