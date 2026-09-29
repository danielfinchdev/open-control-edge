using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace OpenControlEdge.Services;

/// Downloads only the official PawnIO.Setup release and starts it after Windows reports valid Authenticode trust.
internal static class PawnIoInstaller
{
    private const string Releases = "https://api.github.com/repos/namazso/PawnIO.Setup/releases/latest";
    internal static async Task<string?> InstallAsync(CancellationToken cancellationToken = default)
    {
        string? file = null;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OpenControlEdge");
            using JsonDocument release = JsonDocument.Parse(await client.GetStringAsync(Releases, cancellationToken));
            JsonElement assets = release.RootElement.GetProperty("assets");
            JsonElement asset = assets.EnumerateArray().FirstOrDefault(a =>
            {
                string name = a.GetProperty("name").GetString() ?? "";
                return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && a.TryGetProperty("browser_download_url", out _);
            });
            if (asset.ValueKind == JsonValueKind.Undefined) return "El lanzamiento oficial no contiene un instalador EXE.";
            string url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Host != "github.com") return "URL de descarga no válida.";
            byte[] bytes = await client.GetByteArrayAsync(uri, cancellationToken);
            if (bytes.Length is < 1024 or > 100_000_000) return "Tamaño del instalador no válido.";
            string folder = Path.Combine(Path.GetTempPath(), "OpenControlEdge-PawnIO");
            Directory.CreateDirectory(folder);
            file = Path.Combine(folder, "PawnIO.Setup.exe");
            await File.WriteAllBytesAsync(file, bytes, cancellationToken);
            Array.Clear(bytes);
            var verify = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            verify.ArgumentList.Add("-NoProfile"); verify.ArgumentList.Add("-NonInteractive"); verify.ArgumentList.Add("-Command");
            verify.ArgumentList.Add("$s=Get-AuthenticodeSignature -LiteralPath $args[0]; if ($s.Status -eq 'Valid' -and $s.SignerCertificate) { 'VALID' }"); verify.ArgumentList.Add(file);
            using (Process? checker = Process.Start(verify))
            {
                if (checker is null) return "No se pudo verificar la firma del instalador.";
                string output = await checker.StandardOutput.ReadToEndAsync(cancellationToken);
                await checker.WaitForExitAsync(cancellationToken);
                if (checker.ExitCode != 0 || !output.Contains("VALID", StringComparison.Ordinal)) return "La firma Authenticode del instalador no es válida; no se ejecutó.";
            }
            using Process? installer = Process.Start(new ProcessStartInfo(file) { UseShellExecute = true, Verb = UnelevatedLauncher.IsElevated ? "open" : "runas" });
            if (installer is null) return "No se pudo iniciar el instalador.";
            await installer.WaitForExitAsync(cancellationToken);
            bool installed = false; try { installed = LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled; } catch { }
            return installed ? null : "El instalador terminó, pero PawnIO sigue sin estar disponible.";
        }
        catch (OperationCanceledException) { return "Instalación cancelada."; }
        catch (Exception ex) { Log.Warn("PawnIO", "installer: " + ex.GetType().Name); return "No se pudo descargar o instalar PawnIO."; }
        finally { if (file is not null) try { File.Delete(file); } catch { } }
    }
}
