using System.IO;

namespace OpenControlEdge.Services;

/// Identifies which AI providers the widget can show. More entries can be registered in <see cref="All"/>.
internal enum AiProviderId
{
    Claude,
    Codex,
    Cursor,
}

/// Cheap, offline checks for whether an AI client is present on this machine. Never reads credential files.
internal static class AiDetector
{
    internal static IReadOnlyList<AiProviderId> All { get; } = new[] { AiProviderId.Claude, AiProviderId.Codex, AiProviderId.Cursor };

    internal const string ClaudeLoginMessage = "Inicia sesión en Claude Code";
    internal const string CodexLoginMessage = "Inicia sesión en ChatGPT o Codex";
    internal const string CursorLoginMessage = "Inicia sesión en Cursor";

    public static bool IsInstalled(AiProviderId provider) => provider switch
    {
        AiProviderId.Claude => IsClaudeInstalled(),
        AiProviderId.Codex => IsCodexInstalled(),
        AiProviderId.Cursor => IsCursorInstalled(),
        _ => false,
    };

    private static bool IsClaudeInstalled()
    {
        if (Directory.Exists(ClaudeConfigDir)) return true;
        if (File.Exists(NpmClaudeCmd)) return true;
        if (CommandOnPath("claude")) return true;
        if (Directory.Exists(AnthropicClaudeDir)) return true;
        return AnyPackageFolder("Claude_");
    }

    private static bool IsCodexInstalled()
    {
        if (Directory.Exists(CodexConfigDir)) return true;
        if (File.Exists(NpmCodexCmd)) return true;
        if (CommandOnPath("codex")) return true;
        if (Directory.Exists(ChatGptProgramsDir)) return true;
        if (Directory.Exists(OpenAiCodexDir)) return true;
        return AnyPackageFolder("OpenAI.ChatGPT");
    }

    private static bool IsCursorInstalled()
    {
        if (Directory.Exists(CursorProgramsDir)) return true;
        if (Directory.Exists(CursorAppDataDir)) return true;
        if (Directory.Exists(CursorAgentDir)) return true;
        return CommandOnPath("cursor");
    }

    private static string ClaudeConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    private static string CodexConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    private static string NpmClaudeCmd => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "claude.cmd");

    private static string NpmCodexCmd => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "codex.cmd");

    private static string AnthropicClaudeDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnthropicClaude");

    private static string ChatGptProgramsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ChatGPT");

    private static string OpenAiCodexDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex");

    private static string CursorProgramsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "cursor");

    private static string CursorAppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cursor");

    private static string CursorAgentDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cursor-agent");

    private static string PackagesDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");

    private static bool AnyPackageFolder(string prefix)
    {
        try
        {
            if (!Directory.Exists(PackagesDir)) return false;
            foreach (string path in Directory.EnumerateDirectories(PackagesDir))
            {
                if (Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (Exception ex)
        {
            Log.Trace("AiDetector", "Packages scan: " + ex.Message);
        }

        return false;
    }

    private static bool CommandOnPath(string name)
    {
        string? pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue)) return false;

        foreach (string segment in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string trimmed = segment.Trim();
                if (trimmed.Length == 0) continue;
                string basePath = Path.Combine(trimmed, name);
                if (File.Exists(basePath)) return true;
                if (File.Exists(basePath + ".exe")) return true;
                if (File.Exists(basePath + ".cmd")) return true;
                if (File.Exists(basePath + ".bat")) return true;
            }
            catch (Exception ex)
            {
                Log.Trace("AiDetector", "PATH segment: " + ex.Message);
            }
        }

        return false;
    }
}

internal enum ProviderVisibility
{
    Auto,
    Show,
    Hide,
}

internal static class AiRingPolicy
{
    internal static bool ShouldShowRing(AiProviderId provider, Settings settings)
    {
        string key = AiProviderSettings.Key(provider);
        ProviderVisibility pref = settings.Providers.TryGetValue(key, out ProviderVisibility value) ? value : ProviderVisibility.Auto;
        return pref switch
        {
            ProviderVisibility.Show => true,
            ProviderVisibility.Hide => false,
            _ => AiDetector.IsInstalled(provider),
        };
    }

    /// Network usage is only queried when the client is actually present on disk.
    internal static bool ShouldFetchUsage(AiProviderId provider, Settings settings) =>
        ShouldShowRing(provider, settings) && AiDetector.IsInstalled(provider);
}

internal static class AiProviderSettings
{
    internal const string Claude = "claude";
    internal const string Codex = "codex";
    internal const string Cursor = "cursor";

    internal static string Key(AiProviderId provider) => provider switch
    {
        AiProviderId.Claude => Claude,
        AiProviderId.Codex => Codex,
        AiProviderId.Cursor => Cursor,
        _ => provider.ToString().ToLowerInvariant(),
    };
}
