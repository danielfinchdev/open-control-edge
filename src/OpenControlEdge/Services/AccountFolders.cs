using System.Collections.Frozen;
using System.IO;

namespace OpenControlEdge.Services;

/// The extra accounts of Claude Code, Codex and Cursor that are really there. An account whose folder is gone, or has
/// no login file in it (an external drive unplugged, a folder deleted or never signed in), is kept in the settings —
/// it comes back when its folder does — but is neither shown on the card nor read: with no other account left the card
/// is a single-account one. Ajustes › Agentes › Cuentas still lists it, marked as not found, so it can be removed.
///
/// Checking only asks whether the login file exists (nothing is opened or read); it may still wait on a slow or
/// disconnected drive, so the widget does it off the UI thread.
internal static class AccountFolders
{
    /// The file an account of this provider signs in with, inside its folder.
    public static string LoginFile(string provider, string folder) => provider switch
    {
        AiProviderSettings.Claude => Path.Combine(folder, ".credentials.json"),
        AiProviderSettings.Codex => Path.Combine(folder, "auth.json"),
        _ => Path.Combine(folder, "User", "globalStorage", "state.vscdb"),
    };

    /// Whether the account's folder holds its login file.
    public static bool IsPresent(string provider, string folder)
    {
        try { return File.Exists(LoginFile(provider, folder)); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }

    public static readonly string[] Providers = [AiProviderSettings.Claude, AiProviderSettings.Codex, AiProviderSettings.Cursor];

    /// The extra accounts of every provider that are present now, as Key(provider, folder).
    public static FrozenSet<string> Check(Settings settings) =>
        Providers.SelectMany(p => settings.AccountsOf(p).Where(a => IsPresent(p, a.Folder)).Select(a => Key(p, a.Folder)))
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static string Key(string provider, string folder) => provider + "|" + folder;

    /// Every extra account in the settings, as one string: whether a check still covers them.
    public static string Describe(Settings settings) =>
        string.Join("\n", Providers.SelectMany(p => settings.AccountsOf(p).Select(a => Key(p, a.Folder))));

    /// The extra accounts the card shows, in the settings' order: the present ones.
    public static List<ExtraAccount> Visible(Settings settings, string provider, Func<string, bool> present) =>
        [.. settings.AccountsOf(provider).Where(a => present(a.Folder))];

    /// The card's tab of the account chosen in the settings among the visible ones (0, the default account, when the
    /// chosen one is not visible).
    public static int ShownIndex(Settings settings, string provider, IEnumerable<ExtraAccount> visible)
    {
        int selected = settings.SelectedAccount(provider);
        if (selected == 0) return 0;
        string folder = settings.AccountsOf(provider)[selected - 1].Folder;
        int at = visible.Select(a => a.Folder).ToList().FindIndex(f => f.Equals(folder, StringComparison.OrdinalIgnoreCase));
        return at < 0 ? 0 : at + 1;
    }

    /// The settings' index (SelectedAccounts) of the account behind a card tab: 0 for the default one.
    public static int SettingsIndex(Settings settings, string provider, ExtraAccount? shown)
    {
        if (shown is null) return 0;
        int at = settings.AccountsOf(provider).ToList().FindIndex(a => a.Folder.Equals(shown.Folder, StringComparison.OrdinalIgnoreCase));
        return at < 0 ? 0 : at + 1;
    }
}
