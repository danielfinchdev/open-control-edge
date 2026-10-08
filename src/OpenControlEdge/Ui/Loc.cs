using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using OpenControlEdge.Services;

namespace OpenControlEdge.Ui;

/// Interface language. Every visible text lives in Assets/Strings/Strings.es.xaml and Strings.en.xaml; XAML reads
/// them with DynamicResource and code with Get / Format, so switching swaps one merged dictionary in place.
/// Views that compose texts in code listen to Changed and compose them again.
internal static partial class Loc
{
    private const string DictionaryPrefix = "pack://application:,,,/Assets/Strings/Strings.";

    /// Messages the services produce (always in Spanish, the canonical form) and their dictionary keys.
    private static readonly Dictionary<string, string> MessageKeys = new(StringComparer.Ordinal)
    {
        ["No se pueden leer los FPS"] = "Msg.FpsUnavailable",
        ["Base de datos de Cursor no válida"] = "Msg.CursorDbInvalid",
        ["No se pudo leer sin permisos de administrador"] = "Msg.ReaderUnavailable",
        ["La firma de la actualización no es válida; no se instalará."] = "Msg.UpdSignature",
        ["No se pudo detener"] = "Msg.GameStopFailed",
        ["No se pudo iniciar"] = "Msg.GameStartFailed",
        ["No se pudo cerrar"] = "Msg.GameCloseFailed",
        ["No se pudo leer el plan de energía"] = "Msg.GamePlanUnreadable",
        ["Este equipo no tiene el plan Alto rendimiento"] = "Msg.GameNoHighPlan",
        ["No se pudo cambiar el plan de energía"] = "Msg.GamePlanFailed",
        ["No se pudo desactivar la Game Bar"] = "Msg.GameBarFailed",
        ["No se pudo restaurar el plan de energía"] = "Msg.GamePlanRestoreFailed",
        ["No se pudo restaurar la Game Bar"] = "Msg.GameBarRestoreFailed",
        ["El envío sin cuenta no está disponible en esta versión"] = "Msg.FeedbackNoForm",
        ["Escribe un mensaje antes de enviarlo"] = "Msg.FeedbackEmpty",
        ["No se pudo enviar el mensaje"] = "Msg.FeedbackFailed",
        ["Cargando…"] = "Msg.Loading",
        ["Sin conexión"] = "Msg.NoConnection",
        ["Tiempo de espera agotado"] = "Msg.Timeout",
        ["Respuesta no válida"] = "Msg.InvalidResponse",
        ["Respuesta sin datos"] = "Msg.EmptyResponse",
        ["Respuesta sin datos de uso"] = "Msg.NoUsageData",
        ["Respuesta sin datos de sesión"] = "Msg.NoSessionData",
        ["Respuesta local sin datos"] = "Msg.NoLocalData",
        ["No se pudo leer el uso"] = "Msg.UsageUnreadable",
        ["No se pudo leer el uso local"] = "Msg.LocalUsageUnreadable",
        ["No se pudo leer el saldo"] = "Msg.BalanceUnreadable",
        ["Sin base de datos de sesiones"] = "Msg.NoSessionDatabase",
        ["Versión de OpenCode no compatible"] = "Msg.OpenCodeUnsupported",
        ["Instala OpenCode para activar este anillo"] = "Msg.InstallOpenCode",
        ["No se detecta OpenCode"] = "Msg.OpenCodeNotDetected",
        [DeepSeekUsageService.AddKeyMessage] = "Msg.AddDeepSeekKey",
        [OpenRouterUsageService.AddKeyMessage] = "Msg.AddOpenRouterKey",
        [AiDetector.AddKeyMessage] = "Msg.AddKeyFromMenu",
        [ProviderKeyStore.InvalidKeyMessage] = "Msg.InvalidKey",
        ["Cursor ocupado, se reintentará"] = "Msg.CursorBusy",
        [AiDetector.ClaudeLoginMessage] = "Msg.ClaudeLogin",
        [AiDetector.CodexLoginMessage] = "Msg.CodexLogin",
        [AiDetector.CursorLoginMessage] = "Msg.CursorLogin",
        [ClaudeUsageService.RenewMessage] = "Msg.ClaudeRenew",
        [ClaudeUsageService.FreeAccountMessage] = "Msg.FreeAccount",
        [CodexUsageService.RenewMessage] = "Msg.CodexRenew",
        [ClaudeSessionRenewer.CliMissingMessage] = "Msg.ClaudeCliMissing",
        [ClaudeSessionRenewer.TimedOutMessage] = "Msg.ClaudeCliTimeout",
        [ClaudeSessionRenewer.StartFailedMessage] = "Msg.ClaudeCliStartFailed",
        [ClaudeSessionRenewer.NotRenewedMessage] = "Msg.ClaudeNotRenewed",
        ["No se pudo leer la memoria"] = "Msg.RamUnreadable",
        ["No se pudo renovar la sesión"] = "Msg.RenewFailed",
        [HardwareSensorService.PawnIoMissingMessage] = "Msg.PawnIoMissing",
        ["Sensor de temperatura no disponible"] = "Msg.SensorUnavailable",
        ["Requiere ejecutar como administrador"] = "Msg.NeedsAdmin",
        ["Detenido"] = "Msg.Stopped",
        [ClaudeSessionRenewer.SignedOutMessage] = "Msg.ClaudeSignedOut",
        [ClaudeSessionRenewer.LoginOpenedMessage] = "Msg.ClaudeLoginOpened",
        [UnelevatedLauncher.SeclogonMessage] = "Msg.Seclogon",
        ["Hace falta ejecutar como administrador"] = "Msg.RunAsAdmin",
        ["Hace falta ejecutar como administrador."] = "Msg.RunAsAdminDot",
        ["Instala Open Control Edge primero"] = "Msg.InstallFirst",
        ["No se pudo quitar el inicio con Windows"] = "Msg.AutoStartRemoveFailed",
        ["No se pudo registrar el inicio con Windows"] = "Msg.AutoStartRegisterFailed",
        ["La carpeta de instalación no está protegida; no se registra el inicio"] = "Msg.AutoStartUnprotected",
        [AutoStartService.QueryTimeoutMessage] = "Msg.AutoStartQueryTimeout",
        [AutoStartService.QueryFailedMessage] = "Msg.AutoStartQueryFailed",
        ["No se pudo desinstalar"] = "Msg.UninstallFailed",
        ["Esta ya es la copia instalada."] = "Msg.AlreadyInstalled",
        ["La instalación debe aceptarse con la misma cuenta que tiene la sesión abierta."] = "Msg.InstallOtherAccount",
        ["Se conserva la carpeta antigua C:\\Program Files\\EdgeWidget."] = "Msg.InstallKeepsOldFolder",
        ["Usuarios sin privilegios pueden escribir en la carpeta de instalación."] = "Msg.InstallDirWritable",
        ["Error inesperado durante la instalación."] = "Msg.InstallUnexpected",
        ["No se encuentra OpenControlEdge.exe en la carpeta de origen."] = "Msg.InstallNoExe",
        ["La carpeta de datos es un enlace o punto de reanálisis; se cancela para evitar escrituras elevadas fuera de ella."] = "Msg.DataFolderLink",
        ["La carpeta de datos no tiene padre."] = "Msg.DataNoParent",
        ["La dirección de releases no es segura."] = "Msg.UpdReleasesUrl",
        ["La versión de GitHub no tiene un formato válido."] = "Msg.UpdBadVersion",
        ["El release no contiene assets."] = "Msg.UpdNoAssets",
        ["La URL del ZIP no es segura."] = "Msg.UpdZipUrl",
        ["El ZIP no tiene un digest SHA-256 verificable; no se instalará."] = "Msg.UpdNoDigest",
        ["El release no incluye un ZIP de instalación."] = "Msg.UpdNoZip",
        ["La búsqueda se ha cancelado."] = "Msg.UpdCancelled",
        ["No se pudo conectar con GitHub."] = "Msg.UpdNoGitHub",
        ["La respuesta de releases no tiene el formato esperado."] = "Msg.UpdBadResponse",
        ["No se pudo comprobar si hay actualizaciones."] = "Msg.UpdCheckFailed",
        ["El ZIP tiene un tamaño no válido."] = "Msg.UpdZipSize",
        ["El ZIP supera el tamaño permitido."] = "Msg.UpdZipTooBig",
        ["El ZIP está vacío."] = "Msg.UpdZipEmpty",
        ["El digest SHA-256 del ZIP no coincide. No se instalará."] = "Msg.UpdDigestMismatch",
        ["El ZIP contiene una ruta o enlace no seguro."] = "Msg.UpdUnsafePath",
        ["El contenido extraído supera el límite permitido."] = "Msg.UpdTooLarge",
        ["El ZIP intenta salir de staging."] = "Msg.UpdEscapes",
        [UpdateService.UnsignedMessage] = "Msg.UpdUnsigned",
        [UpdateService.UnexpectedFileMessage] = "Msg.UpdUnexpectedFiles",
        [UpdateService.IncompleteMessage] = "Msg.UpdIncomplete",
        ["Instala Open Control Edge antes de actualizar."] = "Msg.UpdInstallFirst",
        ["La actualización requiere iniciar la app instalada con permisos de administrador."] = "Msg.UpdNeedsAdmin",
        ["La carpeta de instalación no está protegida; se cancela la actualización."] = "Msg.UpdUnprotected",
        ["No se pudo proteger staging; no se ejecutará la actualización."] = "Msg.UpdStagingUnprotected",
        ["Se canceló el permiso para actualizar."] = "Msg.UpdUacCancelled",
        ["No se pudo preparar la actualización; la instalación actual sigue intacta."] = "Msg.UpdPrepareFailed",
        ["El lanzamiento oficial no contiene un instalador EXE."] = "Msg.PawnNoExe",
        ["URL de descarga no válida."] = "Msg.PawnBadUrl",
        ["Tamaño del instalador no válido."] = "Msg.PawnBadSize",
        ["No se pudo proteger staging."] = "Msg.PawnStaging",
        ["La firma Authenticode del instalador no coincide con PawnIO; no se ejecutó."] = "Msg.PawnSignature",
        ["No se pudo iniciar el instalador."] = "Msg.PawnStartFailed",
        ["El instalador terminó, pero PawnIO sigue sin estar disponible."] = "Msg.PawnStillMissing",
        ["Instalación cancelada."] = "Msg.PawnCancelled",
        ["No se pudo descargar o instalar PawnIO."] = "Msg.PawnFailed",
    };

    /// Service messages that carry a value (a file name, an error code): pattern and key, the groups as {0}, {1}.
    private static readonly (Regex Pattern, string Key)[] MessagePatterns =
    {
        (new Regex(@"^Error HTTP (\d{3})$"), "Msg.HttpError"),
        (new Regex(@"^No se pudo quitar la tarea antigua «(.+)»\.$"), "Msg.InstallLegacyTask"),
        (new Regex(@"^La comprobación SHA-256 de (.+) ha fallado\.$"), "Msg.InstallHashFailed"),
        (new Regex(@"^Falta (.+) junto a OpenControlEdge\.exe\. Descomprime el ZIP completo\.$"), "Msg.InstallMissingFile"),
        (new Regex(@"^No se puede abrir (.+) \(error (\d+)\)\.$"), "Msg.DataOpenFailed"),
        (new Regex(@"^No se puede comprobar (.+)\.$"), "Msg.DataCheckFailed"),
        (new Regex(@"^(.+) es un enlace o punto de reanálisis; se cancela para evitar escrituras elevadas fuera de la carpeta de datos\.$"), "Msg.DataLink"),
        (new Regex(@"^(.+) ha cambiado de tipo durante la comprobación\.$"), "Msg.DataTypeChanged"),
        (new Regex(@"^(.+) tiene varios vínculos duros; se cancela\.$"), "Msg.DataHardLinks"),
        (new Regex(@"^Descriptor de seguridad no válido \(error (\d+)\)\.$"), "Msg.DataBadDescriptor"),
        (new Regex(@"^No se pudo proteger (.+) \(error (\d+)\)\.$"), "Msg.DataProtectFailed"),
        (new Regex(@"^(.+) no es el de Open Control Edge ni tiene una firma de confianza; no se instalará\.$"), "Msg.UpdLibraryUntrusted"),
    };

    public static UiLanguage Language { get; private set; } = UiLanguage.Spanish;

    /// Culture for numbers and dates of the current language.
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("es-ES");

    /// Raised on the UI thread after the dictionary has been swapped.
    public static event Action? Changed;

    public static void Apply(UiLanguage language)
    {
        if (Application.Current is not Application app) return;
        string code = language == UiLanguage.English ? "en" : "es";
        var dictionary = new ResourceDictionary { Source = new Uri(DictionaryPrefix + code + ".xaml", UriKind.Absolute) };

        IList<ResourceDictionary> merged = app.Resources.MergedDictionaries;
        int index = -1;
        for (int i = 0; i < merged.Count; i++)
            if (merged[i].Source?.OriginalString.StartsWith(DictionaryPrefix, StringComparison.OrdinalIgnoreCase) == true) index = i;
        if (index >= 0) merged[index] = dictionary;
        else merged.Add(dictionary);

        Language = language;
        Culture = CultureInfo.GetCultureInfo(language == UiLanguage.English ? "en-US" : "es-ES");
        Changed?.Invoke();
    }

    public static string Get(string key) => Application.Current?.TryFindResource(key) as string ?? key;

    public static string Format(string key, params object[] args) => string.Format(Culture, Get(key), args);

    /// A service message in the current language. Unknown messages are shown as they came.
    public static string? Message(string? message)
    {
        if (message is null) return null;
        if (MessageKeys.TryGetValue(message, out string? key)) return Get(key);
        foreach ((Regex pattern, string patternKey) in MessagePatterns)
        {
            Match match = pattern.Match(message);
            if (match.Success)
                return Format(patternKey, match.Groups.Values.Skip(1).Select(g => (object)g.Value).ToArray());
        }
        return message;
    }
}
