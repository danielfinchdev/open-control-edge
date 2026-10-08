using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using static OpenControlEdge.Interop.GameNative;

namespace OpenControlEdge.Services;

/// What the last switch of Modo juego did; Errors are short Spanish texts for the card and the log.
internal sealed record GameModeResult(bool Active, int ClosedApps, int StoppedServices, GamePowerPlan? PowerPlan,
    bool GameBarOff, IReadOnlyList<string> Errors);

/// "Modo juego": closes the background programs and stops the services listed in the settings, switches the power
/// plan (Balanced by default: smooth without running a laptop hot) and turns off Xbox Game Bar captures. Switching it off undoes exactly what it did: the previous plan, the previous registry
/// values, only the services it stopped, and the programs it closed are opened again as the plain user.
///
/// Before changing anything it writes what it is about to change to gamemode.json in the data folder, and updates it
/// after every step, so a crash or a power cut never leaves the PC in game mode: the next start (RestoreLeftover) and
/// the application's exit undo it. Only the executables and services named in the settings are touched, never a
/// critical process, the foreground window's process or the widget itself, and only in this session.
internal static partial class GameModeService
{
    private const string StateFile = "gamemode.json";

    private static readonly Guid BalancedScheme = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid HighPerformanceScheme = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    /// Game Bar / Game DVR captures (HKCU, DWORD): the values the LoL script switched off.
    private static readonly (string Key, string Value)[] GameBarValues =
    [
        (@"Software\Microsoft\GameBar", "AppCaptureEnabled"),
        (@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled"),
        (@"System\GameConfigStore", "GameDVR_Enabled"),
    ];

    /// Never closed, whatever the settings say: Windows itself, security software, the shell, the widget, the Claude Code
    /// CLI (it renews the session the Claude ring reads) and the OpenLogi agent (mouse buttons stop working without it).
    private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe", "lsaiso.exe",
        "svchost.exe", "dwm.exe", "explorer.exe", "sihost.exe", "fontdrvhost.exe", "ctfmon.exe", "audiodg.exe", "conhost.exe",
        "taskhostw.exe", "RuntimeBroker.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe", "SearchHost.exe",
        "TextInputHost.exe", "LogonUI.exe", "userinit.exe", "spoolsv.exe", "MsMpEng.exe", "NisSrv.exe", "SecurityHealthService.exe",
        "SecurityHealthSystray.exe", "MpDefenderCoreService.exe", "WmiPrvSE.exe", "dllhost.exe", "OpenControlEdge.exe",
        "claude.exe", "openlogi-agent.exe", "Memory Compression",
    };

    /// Services that Windows, the network, the sensors (PawnIO) or the widget itself (seclogon) need.
    private static readonly HashSet<string> ProtectedServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "RpcSs", "RpcEptMapper", "DcomLaunch", "LSM", "SamSs", "EventLog", "PlugPlay", "Power", "ProfSvc", "Schedule", "Winmgmt",
        "BFE", "mpssvc", "WinDefend", "WdNisSvc", "SecurityHealthService", "Dhcp", "Dnscache", "nsi", "NlaSvc", "netprofm",
        "Audiosrv", "AudioEndpointBuilder", "CryptSvc", "seclogon", "PawnIO", "UserManager", "CoreMessagingRegistrar",
        "SystemEventsBroker", "TimeBrokerSvc", "StateRepository", "TokenBroker", "gpsvc", "Themes", "Wcmsvc", "WlanSvc",
    };

    /// Closed but never opened again: Windows starts them itself (and opening GameBar.exe would show the overlay).
    private static readonly HashSet<string> NoRelaunch = new(StringComparer.OrdinalIgnoreCase)
    {
        "GameBar.exe", "GameBarFTServer.exe", "XboxGameBarWidgets.exe", "GameBarPresenceWriter.exe", "CrossDeviceService.exe",
    };

    private static readonly object Gate = new();

    public static bool IsActive { get; private set; }

    [GeneratedRegex(@"^[A-Za-z0-9 _\-\.\(\)\[\]]{1,96}\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex ProcessNamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9_\-\.]{1,80}$")]
    private static partial Regex ServiceNamePattern();

    public static bool IsValidProcessName(string name) => ProcessNamePattern().IsMatch(name) && !ProtectedProcesses.Contains(name);

    public static bool IsValidServiceName(string name) => ServiceNamePattern().IsMatch(name) && !ProtectedServices.Contains(name);

    // ───────────────────────────── State on disk ─────────────────────────────

    private sealed class State
    {
        public string? PowerScheme { get; set; }
        public Dictionary<string, int?> GameBar { get; } = new(StringComparer.Ordinal);
        public List<string> Services { get; } = new();
        public List<(string Path, string Arguments, string? AppId)> Relaunch { get; } = new();
    }

    private static void Save(State state)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (state.PowerScheme is not null) writer.WriteString("powerScheme", state.PowerScheme);
            writer.WriteStartObject("gameBar");
            foreach ((string name, int? value) in state.GameBar)
            {
                if (value is int v) writer.WriteNumber(name, v);
                else writer.WriteNull(name);
            }
            writer.WriteEndObject();
            writer.WriteStartArray("services");
            foreach (string service in state.Services) writer.WriteStringValue(service);
            writer.WriteEndArray();
            writer.WriteStartArray("relaunch");
            foreach ((string path, string arguments, string? appId) in state.Relaunch)
            {
                writer.WriteStartObject();
                writer.WriteString("path", path);
                writer.WriteString("arguments", arguments);
                if (appId is not null) writer.WriteString("appId", appId);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        if (!DataFolder.WriteAtomic(StateFile, buffer.ToArray())) Log.Warn("GameMode", "no se pudo guardar el estado");
    }

    private static State? LoadState()
    {
        string path = Path.Combine(DataFolder.Path, StateFile);
        try
        {
            if (!File.Exists(path)) return null;
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length > 256 * 1024) return null;
            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement root = document.RootElement;
            var state = new State();
            if (root.TryGetProperty("powerScheme", out JsonElement scheme) && scheme.ValueKind == JsonValueKind.String
                && Guid.TryParse(scheme.GetString(), out Guid guid)) state.PowerScheme = guid.ToString();
            if (root.TryGetProperty("gameBar", out JsonElement gameBar) && gameBar.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty value in gameBar.EnumerateObject())
                    if (GameBarValues.Any(v => Name(v) == value.Name))
                        state.GameBar[value.Name] = value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetInt32(out int number) ? number : null;
            if (root.TryGetProperty("services", out JsonElement services) && services.ValueKind == JsonValueKind.Array)
                foreach (JsonElement service in services.EnumerateArray())
                    if (service.GetString() is string name && IsValidServiceName(name)) state.Services.Add(name);
            if (root.TryGetProperty("relaunch", out JsonElement relaunch) && relaunch.ValueKind == JsonValueKind.Array)
                foreach (JsonElement item in relaunch.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("path", out JsonElement p) || p.GetString() is not string exe
                        || !Path.IsPathFullyQualified(exe)) continue;
                    string arguments = item.TryGetProperty("arguments", out JsonElement a) ? a.GetString() ?? "" : "";
                    string? appId = item.TryGetProperty("appId", out JsonElement id) ? id.GetString() : null;
                    state.Relaunch.Add((exe, arguments, appId));
                }
            return state;
        }
        catch (Exception ex)
        {
            Log.Warn("GameMode", "estado ilegible: " + ex.GetType().Name);
            return null;
        }
    }

    private static string Name((string Key, string Value) value) => value.Key + "\\" + value.Value;

    private static void DeleteState()
    {
        try { File.Delete(Path.Combine(DataFolder.Path, StateFile)); }
        catch (Exception ex) { Log.Warn("GameMode", "no se pudo borrar el estado: " + ex.GetType().Name); }
    }

    // ───────────────────────────── On / off ─────────────────────────────

    public static Task<GameModeResult> ActivateAsync(GameModeOptions options) => Task.Run(() => Activate(options));

    public static Task<GameModeResult> DeactivateAsync() => Task.Run(Deactivate);

    private static GameModeResult Activate(GameModeOptions options)
    {
        lock (Gate)
        {
            if (IsActive) return new GameModeResult(true, 0, 0, null, false, Array.Empty<string>());
            var errors = new List<string>();
            var state = new State();
            Save(state);
            IsActive = true;

            GamePowerPlan? plan = null;
            if (options.PowerPlan != GamePowerPlan.Keep)
            {
                Guid target = options.PowerPlan == GamePowerPlan.HighPerformance ? HighPerformanceScheme : BalancedScheme;
                Guid? current = ActiveScheme();
                if (current is null) errors.Add("No se pudo leer el plan de energía");
                else if (current != target)
                {
                    state.PowerScheme = current.Value.ToString();
                    Save(state);
                    if (PowerSetActiveScheme(IntPtr.Zero, ref target) == 0) plan = options.PowerPlan;
                    else
                    {
                        state.PowerScheme = null;
                        errors.Add(options.PowerPlan == GamePowerPlan.HighPerformance
                            ? "Este equipo no tiene el plan Alto rendimiento" : "No se pudo cambiar el plan de energía");
                    }
                }
                else plan = options.PowerPlan;
            }

            bool gameBarOff = false;
            if (options.DisableGameBar)
            {
                foreach ((string key, string value) in GameBarValues) state.GameBar[Name((key, value))] = ReadDword(key, value);
                Save(state);
                gameBarOff = GameBarValues.All(v => WriteDword(v.Key, v.Value, 0));
                if (!gameBarOff) errors.Add("No se pudo desactivar la Game Bar");
            }

            int stopped = 0;
            foreach (string service in options.Services.Where(IsValidServiceName))
            {
                switch (StopService(service))
                {
                    case true:
                        state.Services.Add(service);
                        Save(state);
                        stopped++;
                        break;
                    case false:
                        errors.Add($"No se pudo detener: {service}");
                        break;
                }
            }

            int closed = CloseProcesses(options, state, errors);
            Save(state);
            Log.Info("GameMode", $"activado: {closed} programas cerrados, {stopped} servicios detenidos, plan {plan?.ToString() ?? "sin cambios"}, Game Bar {(gameBarOff ? "desactivada" : "sin cambios")}"
                                 + (errors.Count > 0 ? "; " + string.Join("; ", errors) : ""));
            return new GameModeResult(true, closed, stopped, plan, gameBarOff, errors);
        }
    }

    private static GameModeResult Deactivate()
    {
        lock (Gate)
        {
            State? state = LoadState();
            var errors = new List<string>();
            if (state is not null) Restore(state, errors);
            DeleteState();
            IsActive = false;
            Log.Info("GameMode", "desactivado" + (errors.Count > 0 ? ": " + string.Join("; ", errors) : ""));
            return new GameModeResult(false, 0, 0, null, false, errors);
        }
    }

    /// At start-up: a gamemode.json left by a crash or a power cut means the PC is still in game mode; undo it, off the
    /// UI thread. The widget starts with the session, often before the desktop exists, and the programs it closed can
    /// only be opened as the user once it does: the relaunch waits for it (up to RelaunchWait).
    public static Task RestoreLeftoverAsync() => Task.Run(async () =>
    {
        State? state;
        lock (Gate)
        {
            state = LoadState();
            if (state is null) return;
            Log.Info("GameMode", "se restaura un modo juego que quedó activo");
            var errors = new List<string>();
            Restore(state, errors, relaunch: false);
            DeleteState();
            IsActive = false;
        }
        if (state.Relaunch.Count == 0) return;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (Interop.ProcessNative.GetShellWindow() == IntPtr.Zero && clock.Elapsed < RelaunchWait) await Task.Delay(2000);
        lock (Gate)
        {
            // Turned on again meanwhile: these programs come back with the ones it closes now.
            if (IsActive && LoadState() is State current)
            {
                current.Relaunch.AddRange(state.Relaunch.Where(r => !current.Relaunch.Any(c => c.Path.Equals(r.Path, StringComparison.OrdinalIgnoreCase))));
                Save(current);
                return;
            }
        }
        Relaunch(state);
    });

    private static readonly TimeSpan RelaunchWait = TimeSpan.FromMinutes(3);

    /// On exit: switch it off before the widget goes away, with a time limit.
    public static void DeactivateOnExit()
    {
        if (!IsActive) return;
        try { DeactivateAsync().Wait(TimeSpan.FromSeconds(20)); }
        catch (Exception ex) { Log.Warn("GameMode", "al salir: " + ex.GetType().Name); }
    }

    private static void Restore(State state, List<string> errors, bool relaunch = true)
    {
        if (state.PowerScheme is string scheme && Guid.TryParse(scheme, out Guid previous)
            && PowerSetActiveScheme(IntPtr.Zero, ref previous) != 0)
            errors.Add("No se pudo restaurar el plan de energía");

        foreach ((string key, string value) in GameBarValues)
        {
            if (!state.GameBar.TryGetValue(Name((key, value)), out int? before)) continue;
            bool ok = before is int number ? WriteDword(key, value, number) : DeleteValue(key, value);
            if (!ok) errors.Add("No se pudo restaurar la Game Bar");
        }

        foreach (string service in state.Services)
            if (!StartService(service)) errors.Add($"No se pudo iniciar: {service}");

        if (relaunch) Relaunch(state);
    }

    /// Opens the closed programs again, as the plain user.
    private static void Relaunch(State state)
    {
        foreach ((string path, string arguments, string? appId) in state.Relaunch)
        {
            UnelevatedLauncher.Result result = appId is not null
                ? UnelevatedLauncher.Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                    UnelevatedLauncher.Quote("shell:AppsFolder\\" + appId), null, hidden: false, wait: null)
                : File.Exists(path)
                    ? UnelevatedLauncher.Run(path, arguments, Path.GetDirectoryName(path), hidden: false, wait: null)
                    : UnelevatedLauncher.Result.Fail("ya no existe");
            if (!result.Started) Log.Warn("GameMode", $"no se pudo abrir {Path.GetFileName(path)}: {result.Error}");
        }
    }

    // ───────────────────────────── Processes ─────────────────────────────

    /// Closes every process of this session whose image name is in the list. Returns how many were closed.
    private static int CloseProcesses(GameModeOptions options, State state, List<string> errors)
    {
        var targets = new HashSet<string>(options.Processes.Where(IsValidProcessName), StringComparer.OrdinalIgnoreCase);
        if (targets.Count == 0) return 0;

        int self = Environment.ProcessId;
        ProcessIdToSessionId((uint)self, out uint session);
        GetWindowThreadProcessIdSafe(GetForegroundWindow(), out uint foreground);

        List<(uint Pid, uint Parent, string Name)> all = Snapshot();
        var matches = all.Where(p => targets.Contains(p.Name) && p.Pid != self && p.Pid != foreground && p.Pid > 4
                                     && ProcessIdToSessionId(p.Pid, out uint s) && s == session).ToList();
        var matchedIds = matches.Select(p => p.Pid).ToHashSet();
        var relaunched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int closed = 0;

        foreach ((uint pid, uint parent, string name) in matches)
        {
            IntPtr handle = Interop.ProcessNative.OpenProcess(PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) continue;
            try
            {
                // The id came from the snapshot: the process behind the handle must still be that program.
                string? image = ImagePath(handle);
                if (image is null || !Path.GetFileName(image).Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                // A child of another closed program (syncthing under SyncTrayzor, an editor's helper) comes back with it.
                if (options.Relaunch && !NoRelaunch.Contains(name) && !matchedIds.Contains(parent) && !IsElevated(handle)
                    && relaunched.Add(image))
                {
                    state.Relaunch.Add((image, Arguments(handle, image), AppId(handle)));
                    Save(state);
                }
                if (Interop.ProcessNative.TerminateProcess(handle, 1)) closed++;
                else errors.Add($"No se pudo cerrar: {name}");
            }
            finally { Interop.ProcessNative.CloseHandle(handle); }
        }
        return closed;
    }

    private static void GetWindowThreadProcessIdSafe(IntPtr window, out uint pid)
    {
        pid = 0;
        if (window != IntPtr.Zero) Interop.ProcessNative.GetWindowThreadProcessId(window, out pid);
    }

    private static List<(uint Pid, uint Parent, string Name)> Snapshot()
    {
        var list = new List<(uint, uint, string)>();
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return list;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            for (bool more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                list.Add((entry.th32ProcessID, entry.th32ParentProcessID, entry.szExeFile));
        }
        finally { Interop.ProcessNative.CloseHandle(snapshot); }
        return list;
    }

    private static bool IsElevated(IntPtr process)
    {
        if (!Interop.ProcessNative.OpenProcessToken(process, Interop.ProcessNative.TOKEN_QUERY, out IntPtr token)) return true;
        try { return Interop.ProcessNative.IsTokenElevated(token); }
        finally { Interop.ProcessNative.CloseHandle(token); }
    }

    private static string? ImagePath(IntPtr process)
    {
        var name = new StringBuilder(1024);
        uint size = (uint)name.Capacity;
        return QueryFullProcessImageNameW(process, 0, name, ref size) ? name.ToString(0, (int)size) : null;
    }

    /// The Store app identity (AUMID) of a packaged process: it is opened again through shell:AppsFolder.
    private static string? AppId(IntPtr process)
    {
        uint length = 0;
        if (GetApplicationUserModelId(process, ref length, null) != 122 || length is 0 or > 512) return null; // ERROR_INSUFFICIENT_BUFFER
        var id = new StringBuilder((int)length);
        return GetApplicationUserModelId(process, ref length, id) == 0 ? id.ToString() : null;
    }

    /// The arguments the program was started with (its command line minus the executable), to start it the same way.
    /// Arguments that look like a key or token are not kept (gamemode.json is a plain file): the program then starts
    /// without arguments.
    private static string Arguments(IntPtr process, string image)
    {
        const int Size = 8192;
        IntPtr buffer = Marshal.AllocHGlobal(Size);
        try
        {
            if (NtQueryInformationProcess(process, ProcessCommandLineInformation, buffer, Size, out _) != 0) return "";
            int length = Marshal.ReadInt16(buffer) & 0xFFFF;
            IntPtr text = Marshal.ReadIntPtr(buffer, 8);
            if (text == IntPtr.Zero || length == 0) return "";
            string line = Marshal.PtrToStringUni(text, length / 2).Trim();
            string arguments;
            if (line.StartsWith('"')) arguments = line.IndexOf('"', 1) is int close and > 0 ? line[(close + 1)..] : "";
            else if (line.StartsWith(image, StringComparison.OrdinalIgnoreCase)) arguments = line[image.Length..];
            else arguments = line.IndexOf(' ') is int space and > 0 ? line[space..] : "";
            arguments = arguments.Trim();
            return UnelevatedLauncher.SecretLike().IsMatch(arguments) ? "" : arguments;
        }
        catch (Exception) { return ""; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    // ───────────────────────────── Services, power, registry ─────────────────────────────

    /// True: it was running and is now stopping. False: it was running and could not be stopped. Null: not running or
    /// not installed (nothing to undo).
    private static bool? StopService(string name)
    {
        IntPtr manager = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (manager == IntPtr.Zero) return false;
        try
        {
            IntPtr service = OpenServiceW(manager, name, SERVICE_QUERY_STATUS | SERVICE_STOP);
            if (service == IntPtr.Zero) return Marshal.GetLastWin32Error() == 1060 ? null : false; // ERROR_SERVICE_DOES_NOT_EXIST
            try
            {
                if (!QueryServiceStatus(service, out SERVICE_STATUS status) || status.CurrentState != SERVICE_RUNNING) return null;
                return ControlService(service, SERVICE_CONTROL_STOP, out _);
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    private static bool StartService(string name)
    {
        IntPtr manager = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (manager == IntPtr.Zero) return false;
        try
        {
            IntPtr service = OpenServiceW(manager, name, SERVICE_QUERY_STATUS | SERVICE_START);
            if (service == IntPtr.Zero) return false;
            try
            {
                // A service still stopping refuses to start: give it a few seconds.
                for (int i = 0; i < 20; i++)
                {
                    if (QueryServiceStatus(service, out SERVICE_STATUS status) && status.CurrentState == SERVICE_STOPPED) break;
                    Thread.Sleep(250);
                }
                return StartServiceW(service, 0, IntPtr.Zero) || Marshal.GetLastWin32Error() == 1056; // ERROR_SERVICE_ALREADY_RUNNING
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    private static Guid? ActiveScheme()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr pointer) != 0 || pointer == IntPtr.Zero) return null;
        try { return Marshal.PtrToStructure<Guid>(pointer); }
        finally { LocalFree(pointer); }
    }

    private static int? ReadDword(string key, string value)
    {
        try
        {
            using RegistryKey? k = Registry.CurrentUser.OpenSubKey(key);
            return k?.GetValue(value) is int number ? number : null;
        }
        catch (Exception) { return null; }
    }

    private static bool WriteDword(string key, string value, int number)
    {
        try
        {
            using RegistryKey k = Registry.CurrentUser.CreateSubKey(key, writable: true);
            k.SetValue(value, number, RegistryValueKind.DWord);
            return true;
        }
        catch (Exception) { return false; }
    }

    private static bool DeleteValue(string key, string value)
    {
        try
        {
            using RegistryKey? k = Registry.CurrentUser.OpenSubKey(key, writable: true);
            k?.DeleteValue(value, throwOnMissingValue: false);
            return true;
        }
        catch (Exception) { return false; }
    }

    /// The image names of the programs this session is running now, for the settings page ("add a running program").
    public static ImmutableArray<string> RunningProgramNames()
    {
        ProcessIdToSessionId((uint)Environment.ProcessId, out uint session);
        return [.. Snapshot().Where(p => ProcessIdToSessionId(p.Pid, out uint s) && s == session)
            .Select(p => p.Name).Where(IsValidProcessName).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
    }
}
