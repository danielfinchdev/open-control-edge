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
        ["Añade la clave API de DeepSeek"] = "Msg.AddDeepSeekKey",
        ["Añade la clave API de OpenRouter"] = "Msg.AddOpenRouterKey",
        ["Añade la clave API desde Claves de API…"] = "Msg.AddKeyFromMenu",
        ["Clave API no válida"] = "Msg.InvalidKey",
        ["Cursor ocupado, se reintentará"] = "Msg.CursorBusy",
        [AiDetector.ClaudeLoginMessage] = "Msg.ClaudeLogin",
        [AiDetector.CodexLoginMessage] = "Msg.CodexLogin",
        [AiDetector.CursorLoginMessage] = "Msg.CursorLogin",
        [ClaudeUsageService.RenewMessage] = "Msg.ClaudeRenew",
        [ClaudeUsageService.FreeAccountMessage] = "Msg.FreeAccount",
        ["Abre Codex para renovar"] = "Msg.CodexRenew",
        [App.RenewTaskMissingMessage] = "Msg.RenewTaskMissing",
        ["No se puede renovar: la tarea «Claude - Mantener sesion» no ha arrancado"] = "Msg.RenewTaskFailed",
        ["No se pudo renovar la sesión"] = "Msg.RenewFailed",
        ["PawnIO no está instalado"] = "Msg.PawnIoMissing",
        ["Sensor de temperatura no disponible"] = "Msg.SensorUnavailable",
        ["Requiere ejecutar como administrador"] = "Msg.NeedsAdmin",
        ["Detenido"] = "Msg.Stopped",
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
        Match http = HttpError().Match(message);
        return http.Success ? Format("Msg.HttpError", http.Groups[1].Value) : message;
    }

    [GeneratedRegex(@"^Error HTTP (\d{3})$")]
    private static partial Regex HttpError();
}
