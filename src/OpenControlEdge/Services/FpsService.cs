using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenControlEdge.Services;

/// Frames per second of the program in the foreground, the way PresentMon counts them: every Present a program asks
/// Windows for. Elevated, it listens to the presentation events of DirectX 9 to 12 (Microsoft-Windows-DXGI and -D3D9)
/// and of the graphics kernel for Vulkan and OpenGL (Microsoft-Windows-DxgKrnl, only its Present keyword and a handful of
/// event ids, filtered by the kernel), in a real-time ETW session of its own. Each event only increments a counter of
/// its process; nothing is parsed or stored. Per process the busiest kind of event wins, so a DirectX game that also
/// shows up in the kernel's events is not counted twice.
///
/// Without that (not elevated, or nothing presenting in the foreground) it falls back to the frames the desktop
/// composes (DwmGetCompositionTimingInfo), which is what the screen actually shows outside a full-screen game.
///
/// The session only exists while the FPS ring is on screen (Start / Stop); while it runs it costs a callback per
/// presented frame and one Sample a second.
internal sealed class FpsService : IDisposable
{
    internal sealed record Sample(double? Fps, string? Process, bool Desktop, double RefreshHz, string? Message);

    private const string SessionName = "OpenControlEdge-Fps";

    private static readonly Guid DxgiProvider = new("ca11c036-0102-4a2d-a6ad-f03cfed5d3c9");
    private static readonly Guid D3D9Provider = new("783aca0a-790e-4d7f-8451-aa850511c6b9");
    private static readonly Guid DxgKrnlProvider = new("802ec45a-1e99-4b83-9920-87c98277ba9d");

    // DXGI Present start (42), multiplane overlay present start (55); D3D9 Present start (1); DxgKrnl Blit (166),
    // Flip (168), PresentHistory start (171), Present (184), FlipMultiPlaneOverlay (252). Ids from the providers'
    // manifests (wevtutil gp … /ge).
    private static readonly ushort[] DxgiEvents = [42, 55];
    private static readonly ushort[] D3D9Events = [1];
    private static readonly ushort[] DxgKrnlEvents = [166, 168, 171, 184, 252];
    private const ulong DxgiKeywordEvents = 0x2, D3D9KeywordEvents = 0x2, DxgKrnlKeywordPresent = 0x8000000;

    private readonly object _gate = new();
    private Dictionary<(uint Pid, int Kind), int> _counts = new();
    private readonly Stopwatch _clock = new();
    private ulong _session;
    private ulong _trace = InvalidTrace;
    private Thread? _thread;
    private EventRecordCallback? _callback;
    private string? _etwError;
    private DWM_TIMING_INFO? _lastDwm;

    private const ulong InvalidTrace = ulong.MaxValue;

#if DEBUG
    internal List<string> LastCounts { get; private set; } = new();
#endif

    public bool Running => _session != 0 || _lastDwm is not null;

    /// Starts counting (ETW when elevated, the desktop otherwise). Never throws.
    public void Start()
    {
        _clock.Restart();
        _lastDwm = ReadDwm();
        if (_session != 0 || !UnelevatedLauncher.IsElevated) return;
        try
        {
            _etwError = StartSession();
            if (_etwError is not null) Log.Warn("FPS", _etwError);
        }
        catch (Exception ex)
        {
            _etwError = ex.GetType().Name;
            Log.Warn("FPS", "ETW: " + ex.GetType().Name);
            StopSession();
        }
    }

    public void Stop()
    {
        StopSession();
        _lastDwm = null;
    }

    public void Dispose() => Stop();

    /// FPS since the previous call, for the program in the foreground (or the desktop).
    public Sample Read()
    {
        double seconds = Math.Max(0.05, _clock.Elapsed.TotalSeconds);
        _clock.Restart();
        double refresh = RefreshRate();

        Dictionary<(uint Pid, int Kind), int> counts;
        lock (_gate)
        {
            counts = _counts;
            _counts = new Dictionary<(uint, int), int>();
        }

        // Frames the desktop composed since the previous sample; none at all is a still desktop (0 FPS).
        DWM_TIMING_INFO? dwm = ReadDwm();
        double? desktopFps = dwm is DWM_TIMING_INFO now && _lastDwm is DWM_TIMING_INFO before && now.cFrame >= before.cFrame
            ? (now.cFrame - before.cFrame) / seconds : null;
        if (dwm is not null) _lastDwm = dwm;

#if DEBUG
        LastCounts = counts.GroupBy(c => c.Key.Pid).Select(g => (g.Key, g.Max(c => c.Value))).OrderByDescending(c => c.Item2).Take(5)
            .Select(c => $"{ProcessName(c.Key) ?? c.Key.ToString()}={c.Item2}").ToList();
#endif
        uint foreground = ForegroundProcess();
        if (_session != 0 && foreground != 0 && counts.Count > 0)
        {
            // Games present from their own process; browsers and Electron apps from a child (the GPU process).
            int best = Busiest(counts, pid => pid == foreground);
            if (best == 0)
            {
                HashSet<uint> children = CachedChildrenOf(foreground);
                best = Busiest(counts, children.Contains);
            }
            if (best > 0) return new Sample(best / seconds, ProcessName(foreground), false, refresh, null);
        }

        if (desktopFps is double fps) return new Sample(fps, null, true, refresh,
            UnelevatedLauncher.IsElevated ? null : "Requiere ejecutar como administrador");
        return new Sample(null, null, true, refresh, _etwError is null ? null : "No se pueden leer los FPS");
    }

    // ───────────────────────────── ETW session ─────────────────────────────

    private string? StartSession()
    {
        int propertiesSize = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>() + 2 * 1024;
        IntPtr properties = Marshal.AllocHGlobal(propertiesSize);
        try
        {
            // A session left by a crash keeps its name: stop it first.
            FillProperties(properties, propertiesSize);
            ControlTraceW(0, SessionName, properties, EVENT_TRACE_CONTROL_STOP);

            FillProperties(properties, propertiesSize);
            int rc = StartTraceW(out _session, SessionName, properties);
            if (rc != 0) { _session = 0; return $"StartTrace error {rc}"; }

            if (!Enable(DxgiProvider, DxgiKeywordEvents, DxgiEvents)) Log.Warn("FPS", "sin eventos de DXGI");
            if (!Enable(D3D9Provider, D3D9KeywordEvents, D3D9Events)) Log.Warn("FPS", "sin eventos de D3D9");
            if (!Enable(DxgKrnlProvider, DxgKrnlKeywordPresent, DxgKrnlEvents)) Log.Warn("FPS", "sin eventos de DxgKrnl");

            _callback = OnEvent;
            int logfileSize = 448; // sizeof(EVENT_TRACE_LOGFILEW) on x64
            IntPtr logfile = Marshal.AllocHGlobal(logfileSize);
            IntPtr loggerName = Marshal.StringToHGlobalUni(SessionName);
            try
            {
                for (int i = 0; i < logfileSize; i++) Marshal.WriteByte(logfile, i, 0);
                Marshal.WriteIntPtr(logfile, 8, loggerName);                       // LoggerName
                Marshal.WriteInt32(logfile, 28, PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD); // ProcessTraceMode
                Marshal.WriteIntPtr(logfile, 424, Marshal.GetFunctionPointerForDelegate(_callback)); // EventRecordCallback
                _trace = OpenTraceW(logfile);
            }
            finally
            {
                Marshal.FreeHGlobal(logfile);
                Marshal.FreeHGlobal(loggerName);
            }
            if (_trace == InvalidTrace)
            {
                int error = Marshal.GetLastWin32Error();
                StopSession();
                return $"OpenTrace error {error}";
            }

            ulong trace = _trace;
            _thread = new Thread(() =>
            {
                ulong[] handles = [trace];
                ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero);
            }) { IsBackground = true, Name = "FPS ETW", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
            Log.Info("FPS", "sesión ETW iniciada");
            return null;
        }
        finally { Marshal.FreeHGlobal(properties); }
    }

    private bool Enable(Guid provider, ulong keywords, ushort[] events)
    {
        // EVENT_FILTER_EVENT_ID: FilterIn (1 byte), Reserved (1), Count (2), then the ids.
        int filterSize = 4 + 2 * events.Length;
        IntPtr filter = Marshal.AllocHGlobal(filterSize);
        IntPtr descriptor = Marshal.AllocHGlobal(16);
        IntPtr parameters = Marshal.AllocHGlobal(48);
        try
        {
            Marshal.WriteByte(filter, 0, 1);
            Marshal.WriteByte(filter, 1, 0);
            Marshal.WriteInt16(filter, 2, (short)events.Length);
            for (int i = 0; i < events.Length; i++) Marshal.WriteInt16(filter, 4 + 2 * i, (short)events[i]);

            Marshal.WriteInt64(descriptor, 0, filter.ToInt64());   // Ptr
            Marshal.WriteInt32(descriptor, 8, filterSize);          // Size
            Marshal.WriteInt32(descriptor, 12, unchecked((int)EVENT_FILTER_TYPE_EVENT_ID)); // Type

            for (int i = 0; i < 48; i++) Marshal.WriteByte(parameters, i, 0);
            Marshal.WriteInt32(parameters, 0, ENABLE_TRACE_PARAMETERS_VERSION_2); // Version
            Marshal.WriteIntPtr(parameters, 32, descriptor);                       // EnableFilterDesc
            Marshal.WriteInt32(parameters, 40, 1);                                 // FilterDescCount

            Guid id = provider;
            return EnableTraceEx2(_session, ref id, EVENT_CONTROL_CODE_ENABLE_PROVIDER, TRACE_LEVEL_INFORMATION,
                keywords, 0, 0, parameters) == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(parameters);
            Marshal.FreeHGlobal(descriptor);
            Marshal.FreeHGlobal(filter);
        }
    }

    private static void FillProperties(IntPtr properties, int size)
    {
        for (int i = 0; i < size; i++) Marshal.WriteByte(properties, i, 0);
        var p = new EVENT_TRACE_PROPERTIES
        {
            Wnode = new WNODE_HEADER { BufferSize = (uint)size, Flags = WNODE_FLAG_TRACED_GUID, ClientContext = 1 },
            BufferSize = 16,
            MinimumBuffers = 2,
            MaximumBuffers = 8,
            LogFileMode = EVENT_TRACE_REAL_TIME_MODE,
            FlushTimer = 1,
            LoggerNameOffset = (uint)Marshal.SizeOf<EVENT_TRACE_PROPERTIES>(),
        };
        Marshal.StructureToPtr(p, properties, false);
    }

    /// One presented frame: counted per process and kind, nothing else is read.
    private void OnEvent(IntPtr record)
    {
        // EVENT_RECORD.EventHeader: ProcessId at 12, ProviderId at 24, EventDescriptor.Id at 40.
        uint pid = (uint)Marshal.ReadInt32(record, 12);
        ushort id = (ushort)Marshal.ReadInt16(record, 40);
        int provider = Marshal.ReadInt32(record, 24);
        int kind = provider * 1000 + id;
        lock (_gate)
        {
            // Bounded: a new process past the limit is not counted this second, the ones already seen still are.
            if (_counts.TryGetValue((pid, kind), out int count)) _counts[(pid, kind)] = count + 1;
            else if (_counts.Count < 4096) _counts[(pid, kind)] = 1;
        }
    }

    private void StopSession()
    {
        if (_trace != InvalidTrace)
        {
            CloseTrace(_trace);
            _trace = InvalidTrace;
        }
        if (_session != 0)
        {
            int size = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>() + 2 * 1024;
            IntPtr properties = Marshal.AllocHGlobal(size);
            try
            {
                FillProperties(properties, size);
                ControlTraceW(_session, null, properties, EVENT_TRACE_CONTROL_STOP);
            }
            finally { Marshal.FreeHGlobal(properties); }
            _session = 0;
            Log.Info("FPS", "sesión ETW detenida");
        }
        _thread?.Join(2000);
        _thread = null;
        lock (_gate) _counts = new();
    }

    // ───────────────────────────── Helpers ─────────────────────────────

    private static int Busiest(Dictionary<(uint Pid, int Kind), int> counts, Func<uint, bool> include)
    {
        int best = 0;
        foreach (((uint pid, _), int count) in counts)
            if (count > best && include(pid)) best = count;
        return best;
    }

    private static HashSet<uint> ChildrenOf(uint parent)
    {
        var children = new HashSet<uint>();
        IntPtr snapshot = Interop.GameNative.CreateToolhelp32Snapshot(Interop.GameNative.TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return children;
        try
        {
            var entry = new Interop.GameNative.PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<Interop.GameNative.PROCESSENTRY32W>() };
            for (bool more = Interop.GameNative.Process32FirstW(snapshot, ref entry); more; more = Interop.GameNative.Process32NextW(snapshot, ref entry))
                if (entry.th32ParentProcessID == parent) children.Add(entry.th32ProcessID);
        }
        finally { Interop.ProcessNative.CloseHandle(snapshot); }
        return children;
    }

    private static uint ForegroundProcess()
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(window, out uint pid);
        return pid == (uint)Environment.ProcessId ? 0 : pid;
    }

    // The foreground program rarely changes: its name and its children are looked up again only when it does, or
    // every ChildrenRefresh (a browser starts its GPU process late).
    private static readonly TimeSpan ChildrenRefresh = TimeSpan.FromSeconds(5);
    private (uint Pid, string? Name, long At) _name;
    private (uint Pid, HashSet<uint> Children, long At) _children = (0, new HashSet<uint>(), 0);

    private string? ProcessName(uint pid)
    {
        long now = Environment.TickCount64;
        if (_name.Pid == pid && now - _name.At < ChildrenRefresh.TotalMilliseconds) return _name.Name;
        string? name = null;
        IntPtr handle = Interop.ProcessNative.OpenProcess(Interop.ProcessNative.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle != IntPtr.Zero)
        {
            try
            {
                var path = new System.Text.StringBuilder(1024);
                uint size = (uint)path.Capacity;
                if (Interop.GameNative.QueryFullProcessImageNameW(handle, 0, path, ref size))
                    name = System.IO.Path.GetFileNameWithoutExtension(path.ToString(0, (int)size));
            }
            finally { Interop.ProcessNative.CloseHandle(handle); }
        }
        _name = (pid, name, now);
        return name;
    }

    private HashSet<uint> CachedChildrenOf(uint parent)
    {
        long now = Environment.TickCount64;
        if (_children.Pid == parent && now - _children.At < ChildrenRefresh.TotalMilliseconds) return _children.Children;
        _children = (parent, ChildrenOf(parent), now);
        return _children.Children;
    }

    private static DWM_TIMING_INFO? ReadDwm()
    {
        var info = new DWM_TIMING_INFO { cbSize = (uint)Marshal.SizeOf<DWM_TIMING_INFO>() };
        return DwmGetCompositionTimingInfo(IntPtr.Zero, ref info) == 0 ? info : null;
    }

    /// Refresh rate of the primary monitor, in Hz (60 when unknown).
    private static double RefreshRate()
    {
        var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        return EnumDisplaySettingsW(null, ENUM_CURRENT_SETTINGS, ref mode) && mode.dmDisplayFrequency > 1 ? mode.dmDisplayFrequency : 60;
    }

    // ───────────────────────────── Interop ─────────────────────────────

    private const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
    private const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
    private const uint EVENT_TRACE_CONTROL_STOP = 1;
    private const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
    private const byte TRACE_LEVEL_INFORMATION = 4;
    private const int PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
    private const int PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
    private const uint EVENT_FILTER_TYPE_EVENT_ID = 0x80000200;
    private const int ENABLE_TRACE_PARAMETERS_VERSION_2 = 2;
    private const int ENUM_CURRENT_SETTINGS = -1;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void EventRecordCallback(IntPtr record);

    [StructLayout(LayoutKind.Sequential)]
    private struct WNODE_HEADER
    {
        public uint BufferSize, ProviderId;
        public ulong HistoricalContext;
        public long TimeStamp;
        public Guid Guid;
        public uint ClientContext, Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_TRACE_PROPERTIES
    {
        public WNODE_HEADER Wnode;
        public uint BufferSize, MinimumBuffers, MaximumBuffers, MaximumFileSize, LogFileMode, FlushTimer, EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers, FreeBuffers, EventsLost, BuffersWritten, LogBuffersLost, RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset, LoggerNameOffset;
    }

    /// dwmapi.h declares it with 1-byte packing; cbSize must match exactly.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DWM_TIMING_INFO
    {
        public uint cbSize;
        public ulong rateRefresh, qpcRefreshPeriod, rateCompose, qpcVBlank, cRefresh;
        public uint cDXRefresh;
        public ulong qpcCompose, cFrame;
        public uint cDXPresent;
        public ulong cRefreshFrame, cFrameSubmitted;
        public uint cDXPresentSubmitted;
        public ulong cFrameConfirmed;
        public uint cDXPresentConfirmed;
        public ulong cRefreshConfirmed;
        public uint cDXRefreshConfirmed;
        public ulong cFramesLate;
        public uint cFramesOutstanding;
        public ulong cFrameDisplayed, qpcFrameDisplayed, cRefreshFrameDisplayed, cFrameComplete, qpcFrameComplete,
            cFramePending, qpcFramePending, cFramesDisplayed, cFramesComplete, cFramesPending, cFramesAvailable,
            cFramesDropped, cFramesMissed, cRefreshNextDisplayed, cRefreshNextPresented, cRefreshesDisplayed,
            cRefreshesPresented, cRefreshStarted, cPixelsReceived, cPixelsDrawn, cBuffersEmpty;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency, dmICMMethod, dmICMIntent,
            dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int StartTraceW(out ulong session, string name, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int ControlTraceW(ulong session, string? name, IntPtr properties, uint control);

    [DllImport("advapi32.dll")]
    private static extern int EnableTraceEx2(ulong session, ref Guid provider, uint control, byte level, ulong matchAny,
        ulong matchAll, uint timeout, IntPtr parameters);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern ulong OpenTraceW(IntPtr logfile);

    [DllImport("advapi32.dll")]
    private static extern int ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);

    [DllImport("advapi32.dll")]
    private static extern int CloseTrace(ulong trace);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetCompositionTimingInfo(IntPtr window, ref DWM_TIMING_INFO info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string? device, int mode, ref DEVMODE devMode);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
