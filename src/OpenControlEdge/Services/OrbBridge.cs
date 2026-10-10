using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using static OpenControlEdge.Interop.ProcessNative;
using static OpenControlEdge.Interop.SecurityNative;

namespace OpenControlEdge.Services;

/// One usage line that Orb publishes: an account of an agent ("claude", "codex"…). Any value may be null.
internal sealed record OrbUsage(string Agent, string? Label, double? UsedPct, string? Window, DateTimeOffset? ResetAt);

internal sealed record OrbTasks(int Running, int Queued, int AwaitingApproval);

/// orb.json as OCE uses it, only while Orb is connected (see OrbBridge.Parse). Tasks and AssistantBusy are null when
/// the file does not carry them in the expected shape.
internal sealed record OrbStatus(string? Version, int Pid, DateTimeOffset At, ImmutableArray<OrbUsage> Usage,
    OrbTasks? Tasks, bool? AssistantBusy)
{
    /// The first line of that agent with a percentage, or null.
    public OrbUsage? UsageFor(string agent) => Usage.FirstOrDefault(u => u.Agent == agent && u.UsedPct is not null);
}

/// One usage line that OCE publishes, read by OCE itself (never one that came from Orb).
internal sealed record OceUsage(string Provider, double? UsedPct, DateTimeOffset? ResetAt, string? Plan);

/// What oce.json carries: the last readings of OCE's own sensors and services; null where there is none.
internal sealed record OceReading(CpuSnapshot? Cpu, GpuSnapshot? Gpu, double? RamUsedPct, double? Fps,
    ImmutableArray<OceUsage> Usage);

/// The file bridge with Orb, in %LOCALAPPDATA%\OrbPuente:
///   oce.json — written here every 2 s (oce.json.tmp, then renamed over it), deleted when sharing is switched off;
///   orb.json — written by Orb every 5 s, only read here (at most MaxOrbBytes).
/// Orb counts as connected only when orb.json parses, has schema 1, was written less than MaxAge ago and its pid is a
/// running process; otherwise nothing of it is shown. Both files stay on this PC: nothing is sent anywhere.
///
/// The folder belongs to the user (Orb runs without elevation), so the installed widget, which runs elevated, never
/// touches it with its own rights: every file operation runs impersonating a plain-user copy of this process's token
/// (no privileges, Administrators deny-only, Medium label). A link planted in the folder can then only lead where the
/// user could write anyway. Never throws; every failure means "desconectado" (and one log line).
internal sealed class OrbBridge
{
    public const int Schema = 1;
    public const string FolderName = "OrbPuente";
    public const string OceFileName = "oce.json";
    public const string OrbFileName = "orb.json";
    public const int MaxOrbBytes = 256 * 1024;
    public static readonly TimeSpan WriteInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(15);

    /// The clocks are the same PC's, but Orb's "at" may be rounded up a little.
    private static readonly TimeSpan MaxAhead = TimeSpan.FromSeconds(5);

    private const int MaxUsageLines = 32;
    private const int MaxLabelLength = 40;
    private const int MaxWindowLength = 24;
    private const int MaxVersionLength = 32;

    /// The providers oce.json may name, as the protocol spells them.
    public static readonly ImmutableArray<string> Providers = ["claude", "codex", "cursor", "opencode", "deepseek", "openrouter"];

    private readonly object _gate = new();
    private SafeAccessTokenHandle? _userToken;
    private string? _tokenError;
    private bool _warnedToken, _warnedWrite, _warnedRead;

    public static string FolderPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);

    private static string Version => typeof(OrbBridge).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// Writes oce.json and reads orb.json. Blocking file work: call it from a worker thread. Nothing is logged while
    /// impersonating: the log lives in the protected data folder, which the plain-user token cannot write.
    public OrbStatus? Exchange(OceReading reading)
    {
        byte[] oce = Serialize(reading, Environment.ProcessId, Version, DateTimeOffset.UtcNow);
        byte[]? orb = null;
        string? writeError = null, readError = null;
        string folder = FolderPath;
        if (!RunAsUser(() =>
            {
                try
                {
                    Directory.CreateDirectory(folder);
                    string final = Path.Combine(folder, OceFileName);
                    string temp = final + ".tmp";
                    using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                        output.Write(oce);
                    File.Move(temp, final, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Orb may hold the file open for a moment: the next write, 2 s later, replaces it.
                    writeError = ex.GetType().Name;
                }
                orb = ReadCapped(Path.Combine(folder, OrbFileName), out readError);
            })) return null;

        if (writeError is not null && !_warnedWrite) Log.Warn("Orb", $"no se pudo escribir {OceFileName}: {writeError}");
        _warnedWrite = writeError is not null;
        if (readError is not null && !_warnedRead) Log.Warn("Orb", $"no se pudo leer {OrbFileName}: {readError}");
        _warnedRead = readError is not null;
        return orb is null ? null : Parse(orb, DateTimeOffset.UtcNow, IsRunning);
    }

    /// Sharing switched off (or the widget closing): oce.json goes, so Orb stops showing this PC's readings at once.
    public void Delete()
    {
        string folder = FolderPath;
        string? error = null;
        RunAsUser(() =>
        {
            try
            {
                File.Delete(Path.Combine(folder, OceFileName));
                File.Delete(Path.Combine(folder, OceFileName + ".tmp"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex.GetType().Name;
            }
        });
        if (error is not null) Log.Warn("Orb", $"no se pudo borrar {OceFileName}: {error}");
    }

    /// The whole file when it exists and is at most MaxOrbBytes; null otherwise (error says why, except for a
    /// missing file: Orb is simply not running).
    private static byte[]? ReadCapped(string path, out string? error)
    {
        error = null;
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (input.Length > MaxOrbBytes) { error = "demasiado grande"; return null; }
            byte[] buffer = new byte[(int)input.Length + 1];
            int total = 0, read;
            while (total < buffer.Length && (read = input.Read(buffer, total, buffer.Length - total)) > 0) total += read;
            if (total > MaxOrbBytes) { error = "demasiado grande"; return null; }
            return buffer.AsSpan(0, total).ToArray();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = ex.GetType().Name; return null; }
    }

    /// Runs action impersonating the plain-user token. False when there is no such token (nothing runs then).
    private bool RunAsUser(Action action)
    {
        lock (_gate)
        {
            SafeAccessTokenHandle? token = UserToken();
            if (token is null) return false;
            try
            {
                WindowsIdentity.RunImpersonated(token, action);
                return true;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or Win32Exception or ArgumentException)
            {
                if (!_warnedToken) Log.Warn("Orb", "no se pudo actuar como el usuario: " + ex.GetType().Name);
                _warnedToken = true;
                return false;
            }
        }
    }

    private SafeAccessTokenHandle? UserToken()
    {
        if (_userToken is not null || _tokenError is not null) return _userToken;
        _tokenError = CreateUserToken(out IntPtr token);
        if (_tokenError is not null)
        {
            if (!_warnedToken) Log.Warn("Orb", "puente desactivado: " + _tokenError);
            _warnedToken = true;
            return null;
        }
        _userToken = new SafeAccessTokenHandle(token);
        return _userToken;
    }

    /// This process's token without privileges, with Administrators as deny-only, a Medium integrity label and the
    /// user as the owner of what it creates. Null on success, otherwise why not.
    private static string? CreateUserToken(out IntPtr restricted)
    {
        restricted = IntPtr.Zero;
        IntPtr own = IntPtr.Zero, administrators = IntPtr.Zero, medium = IntPtr.Zero, user = IntPtr.Zero;
        bool complete = false;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY | TOKEN_DUPLICATE | TOKEN_ASSIGN_PRIMARY | TOKEN_ADJUST_DEFAULT, out own))
                return $"sin token propio (error {Marshal.GetLastWin32Error()})";
            if (!ConvertStringSidToSidW("S-1-5-32-544", out administrators)
                || !ConvertStringSidToSidW("S-1-16-8192", out medium)
                || WindowsIdentity.GetCurrent().User?.Value is not string userSid
                || !ConvertStringSidToSidW(userSid, out user))
                return "SID no válido";

            SID_AND_ATTRIBUTES[] disable = [new() { Sid = administrators }];
            if (!CreateRestrictedToken(own, DISABLE_MAX_PRIVILEGE, 1, disable, 0, IntPtr.Zero, 0, IntPtr.Zero, out restricted))
                return $"CreateRestrictedToken error {Marshal.GetLastWin32Error()}";

            int labelSize = Marshal.SizeOf<SID_AND_ATTRIBUTES>();
            IntPtr label = Marshal.AllocHGlobal(labelSize);
            try
            {
                Marshal.StructureToPtr(new SID_AND_ATTRIBUTES { Sid = medium, Attributes = SE_GROUP_INTEGRITY }, label, false);
                if (!SetTokenInformation(restricted, TokenIntegrityLevel, label, labelSize + GetLengthSid(medium)))
                    return $"sin nivel de integridad medio (error {Marshal.GetLastWin32Error()})";
                Marshal.WriteIntPtr(label, user);
                if (!SetTokenInformation(restricted, TokenOwner, label, IntPtr.Size))
                    return $"sin propietario de usuario (error {Marshal.GetLastWin32Error()})";
            }
            finally { Marshal.FreeHGlobal(label); }
            complete = true;
            return null;
        }
        catch (Exception ex)
        {
            return "token de usuario: " + ex.GetType().Name;
        }
        finally
        {
            if (own != IntPtr.Zero) CloseHandle(own);
            if (administrators != IntPtr.Zero) LocalFree(administrators);
            if (medium != IntPtr.Zero) LocalFree(medium);
            if (user != IntPtr.Zero) LocalFree(user);
            // Only a complete token is kept.
            if (!complete && restricted != IntPtr.Zero) { CloseHandle(restricted); restricted = IntPtr.Zero; }
        }
    }

    /// Process.GetProcessById answers for a pid that exists; one that has exited (or never was) is not running.
    public static bool IsRunning(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using Process process = Process.GetProcessById(pid);
            try { return !process.HasExited; }
            catch (Win32Exception) { return true; }  // exists, but its exit state cannot be asked
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; }
    }

    // ───────────────────────────── oce.json ─────────────────────────────

    /// The protocol's shape exactly; NaN and infinities are written as null, numbers with one decimal at most.
    public static byte[] Serialize(OceReading reading, int pid, string version, DateTimeOffset now)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", Schema);
            writer.WriteString("app", "OpenControlEdge");
            writer.WriteString("version", version);
            writer.WriteNumber("pid", pid);
            writer.WriteString("at", Iso(now));

            writer.WriteStartObject("cpu");
            Number(writer, "loadPct", reading.Cpu?.Load);
            Number(writer, "tempC", reading.Cpu?.Temperature);
            writer.WriteEndObject();

            if (reading.Gpu is { Detected: true } gpu)
            {
                writer.WriteStartObject("gpu");
                if (string.IsNullOrWhiteSpace(gpu.Name)) writer.WriteNull("name");
                else writer.WriteString("name", gpu.Name);
                Number(writer, "loadPct", gpu.Load);
                Number(writer, "tempC", gpu.Temperature);
                Number(writer, "vramUsedMb", gpu.MemoryUsedMb);
                Number(writer, "vramTotalMb", gpu.MemoryTotalMb);
                writer.WriteEndObject();
            }
            else writer.WriteNull("gpu");

            Number(writer, "ramUsedPct", reading.RamUsedPct);
            Number(writer, "fps", reading.Fps);

            writer.WriteStartArray("usage");
            foreach (OceUsage usage in reading.Usage)
            {
                if (!Providers.Contains(usage.Provider)) continue;
                writer.WriteStartObject();
                writer.WriteString("provider", usage.Provider);
                Number(writer, "usedPct", usage.UsedPct);
                if (usage.ResetAt is DateTimeOffset reset) writer.WriteString("resetAt", Iso(reset));
                else writer.WriteNull("resetAt");
                if (string.IsNullOrWhiteSpace(usage.Plan)) writer.WriteNull("plan");
                else writer.WriteString("plan", usage.Plan);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private static void Number(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is double number && double.IsFinite(number)) writer.WriteNumber(name, Math.Round(number, 1, MidpointRounding.AwayFromZero));
        else writer.WriteNull(name);
    }

    private static string Iso(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    // ───────────────────────────── orb.json ─────────────────────────────

    /// Orb's status, or null when it does not count as connected: not an object, schema other than 1, app other than
    /// "Orb", no pid or "at", "at" MaxAge old or more (or more than MaxAhead in the future) or a pid that is not
    /// running. Optional fields of the wrong type are null; nothing is ever filled in.
    public static OrbStatus? Parse(byte[] json, DateTimeOffset now, Func<int, bool> isRunning)
    {
        ReadOnlyMemory<byte> data = json;
        if (data.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) data = data[3..];
        try
        {
            using JsonDocument document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 16 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("schema", out JsonElement schema) || schema.ValueKind != JsonValueKind.Number
                || !schema.TryGetInt32(out int version) || version != Schema) return null;
            if (!root.TryGetProperty("app", out JsonElement app) || app.ValueKind != JsonValueKind.String || app.GetString() != "Orb") return null;
            if (!root.TryGetProperty("pid", out JsonElement pidElement) || pidElement.ValueKind != JsonValueKind.Number
                || !pidElement.TryGetInt32(out int pid) || pid <= 0) return null;
            if (Date(root, "at") is not DateTimeOffset at) return null;
            TimeSpan age = now - at;
            if (age >= MaxAge || age < -MaxAhead) return null;
            if (!isRunning(pid)) return null;

            var usage = ImmutableArray.CreateBuilder<OrbUsage>();
            if (root.TryGetProperty("usage", out JsonElement lines) && lines.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement line in lines.EnumerateArray())
                {
                    if (usage.Count == MaxUsageLines) break;
                    if (line.ValueKind != JsonValueKind.Object || Text(line, "agent", 24) is not string agent || !IsAgentId(agent)) continue;
                    usage.Add(new OrbUsage(agent, Text(line, "label", MaxLabelLength), Percent(line, "usedPct"),
                        Text(line, "window", MaxWindowLength), Date(line, "resetAt")));
                }
            }

            OrbTasks? tasks = null;
            if (root.TryGetProperty("tasks", out JsonElement t) && t.ValueKind == JsonValueKind.Object
                && Count(t, "running") is int running && Count(t, "queued") is int queued && Count(t, "awaitingApproval") is int awaiting)
                tasks = new OrbTasks(running, queued, awaiting);

            bool? busy = root.TryGetProperty("assistant", out JsonElement assistant) && assistant.ValueKind == JsonValueKind.Object
                && assistant.TryGetProperty("busy", out JsonElement b) && b.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? b.GetBoolean() : null;

            return new OrbStatus(Text(root, "version", MaxVersionLength), pid, at, usage.ToImmutable(), tasks, busy);
        }
        catch (JsonException) { return null; }
    }

    /// Lower-case letters, digits and dashes, as Orb names its agents ("claude", "codex"…).
    private static bool IsAgentId(string id) => id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    /// A string without control characters, trimmed and cut to max; null when missing, empty or of another type.
    private static string? Text(JsonElement element, string name, int max)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String) return null;
        string text = new string(value.GetString()!.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (text.Length == 0) return null;
        return text.Length <= max ? text : text[..max].TrimEnd() + "…";
    }

    private static double? Percent(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out double number) && double.IsFinite(number) && number is >= 0 and <= 1000 ? number : null;

    private static int? Count(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int number) && number >= 0 ? number : null;

    private static DateTimeOffset? Date(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset at) ? at : null;
}
