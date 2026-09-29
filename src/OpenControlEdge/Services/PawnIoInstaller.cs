using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace OpenControlEdge.Services;

/// Downloads only the official PawnIO.Setup release and starts it after Windows reports valid Authenticode trust.
internal static class PawnIoInstaller
{
    private const string Releases = "https://api.github.com/repos/namazso/PawnIO.Setup/releases/latest";
    private const string ExpectedSubject = "E=admin@namazso.eu, CN=namazso.eu, O=namazso, L=Debrecen, C=HU";
    private const string ExpectedThumbprint = "F380DCC9F706E2756A5047B832FFE719E1BC35F5";
    internal static bool ServiceRunning
    {
        get
        {
            try
            {
                string sc = Path.Combine(Environment.SystemDirectory, "sc.exe");
                using var process = Process.Start(new ProcessStartInfo(sc, "query PawnIO")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
                if (process is null || !process.WaitForExit(3000)) { try { process?.Kill(); } catch { } return false; }
                return process.StandardOutput.ReadToEnd().Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

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
                return name.Equals("PawnIO_setup.exe", StringComparison.OrdinalIgnoreCase) && a.TryGetProperty("browser_download_url", out _);
            });
            if (asset.ValueKind == JsonValueKind.Undefined) return "El lanzamiento oficial no contiene un instalador EXE.";
            string url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps
                || uri.Host != "github.com" || !uri.AbsolutePath.StartsWith("/namazso/PawnIO.Setup/releases/download/", StringComparison.Ordinal))
                return "URL de descarga no válida.";
            byte[] bytes = await client.GetByteArrayAsync(uri, cancellationToken);
            if (bytes.Length is < 1024 or > 100_000_000) return "Tamaño del instalador no válido.";
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string folder = Path.Combine(programFiles, "OpenControlEdge.pawnio-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            file = Path.Combine(folder, "PawnIO.Setup.exe");
            await File.WriteAllBytesAsync(file, bytes, cancellationToken);
            Array.Clear(bytes);
            if (DataFolder.NonAdminsCanWrite(folder, directory: true)) return "No se pudo proteger staging.";
            if (!VerifyPawnSignature(file)) return "La firma Authenticode del instalador no coincide con PawnIO; no se ejecutó.";
            using Process? installer = Process.Start(new ProcessStartInfo(file) { UseShellExecute = true, Verb = UnelevatedLauncher.IsElevated ? "open" : "runas" });
            if (installer is null) return "No se pudo iniciar el instalador.";
            await installer.WaitForExitAsync(cancellationToken);
            bool installed = false; try { installed = LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled; } catch { }
            return installed ? null : "El instalador terminó, pero PawnIO sigue sin estar disponible.";
        }
        catch (OperationCanceledException) { return "Instalación cancelada."; }
        catch (Exception ex) { Log.Warn("PawnIO", "installer: " + ex.GetType().Name); return "No se pudo descargar o instalar PawnIO."; }
        finally
        {
            if (file is not null)
            {
                try { File.Delete(file); string? folder = Path.GetDirectoryName(file); if (folder is not null && Directory.Exists(folder)) Directory.Delete(folder); } catch { }
            }
        }
    }

    private static bool VerifyPawnSignature(string file)
    {
        var fileInfo = new WinTrustFileInfo(file);
        IntPtr filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        IntPtr dataPointer = IntPtr.Zero;
        try
        {
            Marshal.StructureToPtr(fileInfo, filePointer, false);
            var data = new WinTrustData { FileInfo = filePointer };
            dataPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(data, dataPointer, false);
            Guid action = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
            if (WinVerifyTrust(IntPtr.Zero, ref action, dataPointer) != 0) return false;
            using X509Certificate2 certificate = new(X509Certificate.CreateFromSignedFile(file));
            return string.Equals(certificate.Subject, ExpectedSubject, StringComparison.Ordinal)
                && string.Equals(certificate.Thumbprint, ExpectedThumbprint, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
        finally
        {
            if (dataPointer != IntPtr.Zero) Marshal.FreeHGlobal(dataPointer);
            Marshal.DestroyStructure<WinTrustFileInfo>(filePointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        internal uint Size;
        internal string FilePath;
        internal IntPtr FileHandle;
        internal IntPtr KnownSubject;
        internal WinTrustFileInfo(string path) { Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(); FilePath = path; FileHandle = IntPtr.Zero; KnownSubject = IntPtr.Zero; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        internal uint Size;
        internal IntPtr PolicyCallbackData, SipClientData;
        internal uint UiChoice, RevocationChecks, UnionChoice;
        internal IntPtr FileInfo;
        internal uint StateAction;
        internal IntPtr StateData, UrlReference;
        internal uint ProviderFlags, UiContext;
        public WinTrustData() { Size = (uint)Marshal.SizeOf<WinTrustData>(); PolicyCallbackData = SipClientData = StateData = UrlReference = IntPtr.Zero; UiChoice = 2; RevocationChecks = 0; UnionChoice = 1; FileInfo = IntPtr.Zero; StateAction = 0; ProviderFlags = 0x1000; UiContext = 0; }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, IntPtr data);
}
