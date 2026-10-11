namespace OpenControlEdge;

/// Entry point. The database reader (LocalDatabaseReader.RunHelper) answers before WPF loads anything, so each of
/// its short runs costs a process start and no more.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // DLLs loaded by name (the GPU vendors' libraries that LibreHardwareMonitor asks for) come only from System32
        // and the application folder, never from PATH: the elevated widget must not pick one from a folder the user can write.
        SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);

        if (args.Length > 0 && args[0] == Services.LocalDatabaseReader.HelperArgument)
            return Services.LocalDatabaseReader.RunHelper(args);
#if DEBUG
        // --fps-probe <file> <seconds>: samples FpsService once a second and writes the readings (FPS checks).
        if (args.Length == 3 && args[0] == "--fps-probe" && int.TryParse(args[2], out int seconds))
        {
            using var fps = new Services.FpsService();
            fps.Start();
            var lines = new List<string> { $"elevated={Services.UnelevatedLauncher.IsElevated}" };
            for (int i = 0; i < seconds; i++)
            {
                Thread.Sleep(1000);
                Services.FpsService.Sample sample = fps.Read();
                lines.Add($"{sample.Fps:0.0} fps · {sample.Process ?? "(escritorio)"} · desktop={sample.Desktop} · {sample.RefreshHz} Hz · {sample.Message} · " + string.Join(", ", fps.LastCounts));
            }
            System.IO.File.WriteAllLines(args[1], lines);
            return 0;
        }

        // --orb-probe <file>: three real exchanges with Orb (OrbBridge, with the plain-user token) and the deletion of
        // oce.json, written to the file (bridge checks; the parsing itself is checked by --snapshot).
        if (args.Length == 2 && args[0] == "--orb-probe")
        {
            var bridge = new Services.OrbBridge();
            string oce = System.IO.Path.Combine(Services.OrbBridge.FolderPath, Services.OrbBridge.OceFileName);
            var lines = new List<string> { $"elevated={Services.UnelevatedLauncher.IsElevated}" };
            for (int i = 0; i < 3; i++)
            {
                Services.RamSnapshot ram = Services.MemoryService.Read();
                Services.OrbStatus? orb = bridge.Exchange(new Services.OceReading(null, null, ram.Message is null ? ram.Percent : null, null, []));
                var written = new System.IO.FileInfo(oce);
                lines.Add($"oce.json={(written.Exists ? written.Length + " bytes" : "missing")} · orb={(orb is null ? "desconectado" : $"conectado {orb.Version} tasks={orb.Tasks} busy={orb.AssistantBusy} usage={orb.Usage.Length}")}");
                Thread.Sleep(Services.OrbBridge.WriteInterval);
            }
            bridge.Delete();
            lines.Add($"after delete: oce.json {(System.IO.File.Exists(oce) ? "still there" : "deleted")}");
            System.IO.File.WriteAllLines(args[1], lines);
            return 0;
        }
#endif

        // Run from the unzipped release: hold the exe so it cannot be swapped before it is installed (Installer). Not
        // the update helper: it runs from the staged folder that it renames into place.
        if (Array.IndexOf(args, "--apply-update") < 0) Services.Installer.PinExecutable();

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    private const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDefaultDllDirectories(uint flags);
}
