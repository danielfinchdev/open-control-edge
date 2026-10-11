using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace OpenControlEdge.Services;

/// "Abrir Claude", "Abrir Codex"… on each card, and the Orb ring: the agent's desktop app when it is installed, its
/// web page otherwise. Always started without elevation (UnelevatedLauncher), never with this process's rights.
/// Never throws: the result is null, or a short Spanish message.
internal static class AppLauncher
{
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Profile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string Explorer => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    /// Store (MSIX) apps: the folder prefix under %LOCALAPPDATA%\Packages and the application id in its manifest
    /// (Claude_pzs8sxrjxfjjc!Claude and OpenAI.Codex_2p2nqsd0c76g0!App verified 2026-10-11).
    private static readonly (AiProviderId Provider, string Prefix, string AppId)[] StoreApps =
    [
        (AiProviderId.Claude, "Claude_", "Claude"),
        (AiProviderId.Codex, "OpenAI.Codex_", "App"),
    ];

    private static string WebPage(AiProviderId provider) => provider switch
    {
        AiProviderId.Claude => "https://claude.ai/",
        AiProviderId.Codex => "https://chatgpt.com/codex",
        AiProviderId.Cursor => "https://cursor.com/dashboard",
        AiProviderId.OpenCode => "https://opencode.ai/",
        AiProviderId.DeepSeek => "https://platform.deepseek.com/usage",
        _ => "https://openrouter.ai/activity",
    };

    public static string? Open(AiProviderId provider)
    {
        try
        {
            foreach ((AiProviderId id, string prefix, string appId) in StoreApps)
                if (id == provider && PackageFamily(prefix) is string family)
                    return Start(Explorer, UnelevatedLauncher.Quote($"shell:AppsFolder\\{family}!{appId}"));

            string? exe = provider switch
            {
                AiProviderId.Claude => ExistingFile(Path.Combine(LocalAppData, "AnthropicClaude", "claude.exe")),
                AiProviderId.Codex => ExistingFile(Path.Combine(LocalAppData, "Programs", "ChatGPT", "ChatGPT.exe")),
                AiProviderId.Cursor => ExistingFile(Path.Combine(LocalAppData, "Programs", "cursor", "Cursor.exe")),
                _ => null,
            };
            if (exe is not null) return Start(exe, "");

            // OpenCode is a terminal program: a console window with it, in the user's folder.
            if (provider == AiProviderId.OpenCode && OnPath("opencode") is string cli)
            {
                string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                return Start(cmd, "/d /k " + UnelevatedLauncher.Quote(cli));
            }
            return OpenUrl(WebPage(provider));
        }
        catch (Exception ex)
        {
            Log.Warn("Apps", $"abrir {provider}: {ex.GetType().Name}");
            return "No se pudo abrir la aplicación";
        }
    }

    /// Opens a web address in the default browser, without elevation.
    public static string? OpenUrl(string url) => Start(Explorer, UnelevatedLauncher.Quote(url));

    // ───────────────────────────── Orb ─────────────────────────────

    /// Orb.exe of this user's installation (its uninstall entry, or the installer's default folder); with Orb running,
    /// the executable of the pid orb.json names when it is an Orb.exe. Null: Orb is not installed.
    public static string? OrbExecutable(int? runningPid = null)
    {
        try
        {
            if (OrbFromUninstallEntry() is string registered) return registered;
            if (ExistingFile(Path.Combine(LocalAppData, "Programs", "Orb", "Orb.exe")) is string standard) return standard;
            if (runningPid is int pid and > 0)
            {
                using Process process = Process.GetProcessById(pid);
                string? path = process.MainModule?.FileName;
                if (path is not null && Path.GetFileName(path).Equals("Orb.exe", StringComparison.OrdinalIgnoreCase)) return ExistingFile(path);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception
                                       or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Trace("Apps", "Orb: " + ex.GetType().Name);
        }
        return null;
    }

    public static string? OpenOrb(string executable) => Start(executable, "");

    /// The per-user NSIS installer of Orb (appId dev.orb.app) registers DisplayName "Orb". Its InstallLocation is empty
    /// (seen 2026-10-11): the folder comes from DisplayIcon ("…\Orb\Orb.exe,0"), InstallLocation when it has one.
    private static string? OrbFromUninstallEntry()
    {
        using RegistryKey? uninstall = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
        if (uninstall is null) return null;
        foreach (string name in uninstall.GetSubKeyNames())
        {
            using RegistryKey? entry = uninstall.OpenSubKey(name);
            if (entry?.GetValue("DisplayName") is not "Orb") continue;
            string? folder = entry.GetValue("InstallLocation") as string;
            if (string.IsNullOrWhiteSpace(folder) && entry.GetValue("DisplayIcon") is string icon)
            {
                // "C:\…\Orb.exe,0" or "\"C:\…\Orb.exe\"": the path without its icon index and quotes.
                string path = icon.Trim();
                int comma = path.LastIndexOf(',');
                if (comma > 0 && int.TryParse(path[(comma + 1)..], out _)) path = path[..comma];
                path = path.Trim().Trim('"');
                folder = Path.GetFileName(path).Equals("Orb.exe", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(path) : null;
            }
            if (folder is null || folder.Length is 0 or > 260 || !Path.IsPathFullyQualified(folder)) continue;
            if (ExistingFile(Path.Combine(folder.Trim('"'), "Orb.exe")) is string exe) return exe;
        }
        return null;
    }

    // ───────────────────────────── Helpers ─────────────────────────────

    private static string? Start(string application, string arguments)
    {
        UnelevatedLauncher.Result result = UnelevatedLauncher.Run(application, arguments, Profile, hidden: false, wait: null);
        if (result.Started) return null;
        Log.Warn("Apps", $"{Path.GetFileName(application)}: {result.Error}");
        return result.Error ?? "No se pudo abrir la aplicación";
    }

    private static string? ExistingFile(string path) =>
        File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 ? path : null;

    /// The package family name of a Store app registered for this user: its entry in the AppModel repository
    /// ("Claude_2.31226.1.0_x64__pzs8sxrjxfjjc" → "Claude_pzs8sxrjxfjjc"). A leftover %LOCALAPPDATA%\Packages folder of an
    /// uninstalled app is not enough, so the web page is opened instead.
    private static string? PackageFamily(string prefix)
    {
        using RegistryKey? packages = Registry.CurrentUser.OpenSubKey(
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
        if (packages is null) return null;
        foreach (string fullName in packages.GetSubKeyNames())
        {
            if (!fullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || fullName.Length > 128) continue;
            int publisher = fullName.LastIndexOf("__", StringComparison.Ordinal);
            int name = fullName.IndexOf('_');
            if (publisher <= name || name <= 0) continue;
            string family = fullName[..name] + "_" + fullName[(publisher + 2)..];
            // Family names are Name_PublisherId: letters, digits, dots and one underscore. Nothing else is passed on.
            if (family.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')) return family;
        }
        return null;
    }

    private static string? OnPath(string name)
    {
        string? value = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(value)) return null;
        foreach (string folder in value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Path.IsPathFullyQualified(folder)) continue;
            foreach (string extension in new[] { ".exe", ".cmd" })
                if (ExistingFile(Path.Combine(folder, name + extension)) is string file) return file;
        }
        return null;
    }
}
