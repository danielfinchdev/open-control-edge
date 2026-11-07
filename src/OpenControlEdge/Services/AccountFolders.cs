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

    /// The default account's folder, as its client finds it (CLAUDE_CONFIG_DIR / CODEX_HOME when set): the one its
    /// reader takes the login file from (LoginFile of this folder). Resolving it may touch the disk.
    public static string MainFolder(string provider) => provider switch
    {
        AiProviderSettings.Claude => CredentialReader.DefaultFolder,
        AiProviderSettings.Codex => CodexCredentialReader.DefaultFolder,
        _ => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cursor"),
    };

    /// At start, before the default account's first reading: whether it already counts as signed out — its login file
    /// is missing, so its reading can only ask to sign in. The card then starts without it (HidesMain) instead of with
    /// a "Cargando…" tab next to the extra accounts. A login file there proves nothing yet (it may be expired or
    /// revoked): the account loads as usual. Only the file's presence is checked (IsPresent), never its contents.
    public static bool StartsSignedOut(string provider, string mainFolder) => !IsPresent(provider, mainFolder);

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

    /// Whether the card leaves the default account out: it has no session (signed out, never signed in) while an extra
    /// account is there. The card then shows the extra accounts alone — one is a single-account card — with a line to
    /// connect the default one.
    public static bool HidesMain(bool mainSignedOut, int visibleExtras) => mainSignedOut && visibleExtras > 0;

    /// Whether the default account still counts as signed out after a new reading of it: a sign-in message says yes, a
    /// reading with data says no, and anything else ("Cargando…", a network or server error) keeps the last decision, so
    /// the card does not jump between layouts on every refresh while the default account is signed out and offline.
    public static bool StaysSignedOut(bool before, bool needsSignIn, bool hasData) => needsSignIn || (before && !hasData);

    /// The account a card shows: 0 for the default one, n for the visible extra n. With the default account left out
    /// (HidesMain) the chosen one, or else the first extra account.
    public static int ShownAccount(Settings settings, string provider, IEnumerable<ExtraAccount> visible, bool hidesMain)
    {
        int shown = ShownIndex(settings, provider, visible);
        return hidesMain && shown == 0 ? 1 : shown;
    }

    /// The settings' index (SelectedAccounts) of the account behind a card tab: 0 for the default one.
    public static int SettingsIndex(Settings settings, string provider, ExtraAccount? shown)
    {
        if (shown is null) return 0;
        int at = settings.AccountsOf(provider).ToList().FindIndex(a => a.Folder.Equals(shown.Folder, StringComparison.OrdinalIgnoreCase));
        return at < 0 ? 0 : at + 1;
    }
}
