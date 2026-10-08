using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenControlEdge.Services;

/// SignatureUri: the archive's signature made by the Release workflow with the project's own key (UpdateSignature).
internal sealed record UpdateRelease(Version Version, string Tag, string Notes, Uri ZipUri, string Digest, Uri? SignatureUri = null);
internal sealed record UpdateCheckResult(UpdateRelease? Release, string? Error)
{
    internal bool IsAvailable => Release is not null;
}

/// Release metadata, archive digest verification and safe ZIP staging for the in-app updater.
internal static class UpdateService
{
    internal const string LatestApi = "https://api.github.com/repos/danielfinchdev/open-control-edge/releases/latest";
    internal const string SignatureAsset = "OpenControlEdge-win-x64.zip.sig";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(4) };
    private const long MaxArchiveBytes = 300L * 1024 * 1024;
    private const long MaxExpandedBytes = 900L * 1024 * 1024;

    internal static async Task<UpdateCheckResult> CheckAsync(string apiUrl = LatestApi, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Uri.TryCreate(apiUrl, UriKind.Absolute, out Uri? endpoint) || !IsSecureUri(endpoint))
                return new(null, "La dirección de releases no es segura.");
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.UserAgent.ParseAdd(UsageHttp.UserAgent);
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
            Uri? signature = null;
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                if (asset.TryGetProperty("name", out JsonElement n) && n.GetString() is string sigName
                    && sigName.Equals(SignatureAsset, StringComparison.OrdinalIgnoreCase)
                    && asset.TryGetProperty("browser_download_url", out JsonElement u) && Uri.TryCreate(u.GetString(), UriKind.Absolute, out Uri? sigUri)
                    && IsReleaseAsset(sigUri))
                    signature = sigUri;
            }
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string name = RequiredString(asset, "name");
                if (!name.Equals("OpenControlEdge-win-x64.zip", StringComparison.OrdinalIgnoreCase)) continue;
                string download = RequiredString(asset, "browser_download_url");
                if (!Uri.TryCreate(download, UriKind.Absolute, out Uri? uri) || !IsReleaseAsset(uri))
                    return new(null, "La URL del ZIP no es segura.");
                if (!asset.TryGetProperty("digest", out JsonElement digestElement) || digestElement.ValueKind != JsonValueKind.String
                    || digestElement.GetString() is not string digest || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                    || digest.Length != 71 || !digest.AsSpan(7).ToArray().All(Uri.IsHexDigit))
                    return new(null, "El ZIP no tiene un digest SHA-256 verificable; no se instalará.");
                // Without the project's signature nothing is installed, whatever else the release carries.
                if (signature is null && !IsFixture(uri)) return new(null, UnsignedReleaseMessage);
                return new(new(version, tag, notes, uri, digest[7..].ToLowerInvariant(), signature), null);
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
        if (!IsReleaseAsset(release.ZipUri)) throw new InvalidDataException("La URL del ZIP no es segura.");
        using var request = new HttpRequestMessage(HttpMethod.Get, release.ZipUri);
        request.Headers.UserAgent.ParseAdd(UsageHttp.UserAgent);
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

        // The project's own signature over the whole archive, mandatory: every file inside is then the one the Release
        // workflow packed.
        if (!IsFixture(release.ZipUri))
        {
            if (release.SignatureUri is not Uri signatureUri)
            {
                CryptographicOperations.ZeroMemory(archive);
                throw new InvalidDataException(UnsignedReleaseMessage);
            }
            byte[] signature = await DownloadSmallAsync(signatureUri, cancellationToken).ConfigureAwait(false);
            if (!UpdateSignature.Verify(archive, signature))
            {
                CryptographicOperations.ZeroMemory(archive);
                throw new InvalidDataException(UpdateSignature.InvalidMessage);
            }
        }

        string root = Path.GetFullPath(stageRoot);
        if (Directory.Exists(root)) throw new IOException("El directorio de staging ya existe.");
        string payload = Path.Combine(root, "payload");
        try
        {
            using var memory = new MemoryStream(archive, writable: false);
            using var zip = new ZipArchive(memory, ZipArchiveMode.Read);
            List<(ZipArchiveEntry Entry, string Name)> files = ApplicationEntries(zip);
            Directory.CreateDirectory(payload);
            foreach ((ZipArchiveEntry entry, string name) in files)
            {
                await using Stream input = entry.Open();
                await using var output = new FileStream(Path.Combine(payload, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
            VerifyPayload(payload, release);
            return payload;
        }
        catch { try { Directory.Delete(root, recursive: true); } catch { } throw; }
        finally { CryptographicOperations.ZeroMemory(archive); }
    }

    /// The files of the archive, which must be exactly OpenControlEdge.exe and the native libraries this build ships
    /// (NativeLibraries), at its root or inside one single folder. Anything else — another file (a DLL the exe would
    /// load elevated from Program Files), a missing library, a link, a nested path — rejects the whole archive before a
    /// byte is written. A future version that ships a different set of libraries is installed from its ZIP once.
    private static List<(ZipArchiveEntry Entry, string Name)> ApplicationEntries(ZipArchive zip)
    {
        var expected = new HashSet<string>(NativeLibraries.Names, StringComparer.OrdinalIgnoreCase) { "OpenControlEdge.exe" };
        var files = new List<(ZipArchiveEntry, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? folder = null;
        bool rootFiles = false;
        long expanded = 0;
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            string path = entry.FullName.Replace('\\', '/');
            bool directory = path.EndsWith('/');
            string[] parts = path.TrimEnd('/').Split('/');
            if (path.StartsWith('/') || path.Contains(':') || parts.Any(part => part is "" or "." or "..")
                || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("El ZIP contiene una ruta o enlace no seguro.");

            // Either every file at the root, or every file inside one and the same folder.
            string? entryFolder = directory ? parts[0] : parts.Length == 2 ? parts[0] : null;
            if (parts.Length > 2 || directory && parts.Length != 1) throw new InvalidDataException(UnexpectedFileMessage);
            if (entryFolder is not null)
            {
                if (rootFiles || folder is not null && folder != entryFolder) throw new InvalidDataException(UnexpectedFileMessage);
                folder = entryFolder;
            }
            if (directory) continue;
            if (parts.Length == 1)
            {
                if (folder is not null) throw new InvalidDataException(UnexpectedFileMessage);
                rootFiles = true;
            }

            string name = parts[^1];
            if (!expected.Contains(name) || !seen.Add(name)) throw new InvalidDataException(UnexpectedFileMessage);
            expanded = checked(expanded + entry.Length);
            if (expanded > MaxExpandedBytes) throw new InvalidDataException("El contenido extraído supera el límite permitido.");
            files.Add((entry, name));
        }
        if (seen.Count != expected.Count) throw new InvalidDataException(IncompleteMessage);
        return files;
    }

    internal const string VersionMismatchMessage = "El ejecutable no corresponde a esta versión; no se instalará.";
    internal const string UnsignedReleaseMessage = "La versión publicada no lleva la firma de Open Control Edge; no se instalará.";
    internal const string UnexpectedFileMessage = "El ZIP contiene archivos que no son de Open Control Edge; no se instalará.";
    internal const string IncompleteMessage = "El ZIP no contiene todos los archivos de Open Control Edge; no se instalará.";

    /// The archive is signed (DownloadAndStageAsync): its exe must still declare Open Control Edge and exactly this
    /// version, so an older signed archive republished under a newer tag is refused.
    private static void VerifyPayload(string payload, UpdateRelease release)
    {
        if (IsFixture(release.ZipUri)) return;
        if (!AuthenticodeVerifier.DeclaresVersion(Path.Combine(payload, "OpenControlEdge.exe"), release.Version))
            throw new InvalidDataException(VersionMismatchMessage);
    }

    /// Debug only: the local release fixture (--test-update-fixture) serves unsigned test archives over loopback HTTP.
    private static bool IsFixture(Uri uri)
    {
#if DEBUG
        return uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp;
#else
        return false;
#endif
    }

    /// A small file of a Release (the signature): at most 1 KB.
    private static async Task<byte[]> DownloadSmallAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!IsReleaseAsset(uri)) throw new InvalidDataException(UpdateSignature.InvalidMessage);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd(UsageHttp.UserAgent);
        using HttpResponseMessage response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new MemoryStream();
        byte[] chunk = new byte[1024];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > 1024) throw new InvalidDataException(UpdateSignature.InvalidMessage);
            buffer.Write(chunk, 0, count);
        }
        return buffer.ToArray();
    }

    /// A file of a Release of this repository, over HTTPS. Debug also accepts the local fixture over loopback HTTP.
    private static bool IsReleaseAsset(Uri uri)
    {
#if DEBUG
        if (uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp) return true;
#endif
        return uri.Scheme == Uri.UriSchemeHttps && uri.Host == "github.com"
               && uri.AbsolutePath.StartsWith("/danielfinchdev/open-control-edge/releases/download/", StringComparison.Ordinal);
    }

    private static bool IsSecureUri(Uri uri)
    {
        if (uri.Scheme == Uri.UriSchemeHttps) return true;
#if DEBUG
        return uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp;
#else
        return false;
#endif
    }
}
