namespace OpenControlEdge;

/// Entry point. The database reader (LocalDatabaseReader.RunHelper) answers before WPF loads anything, so each of
/// its short runs costs a process start and no more.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
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
#endif

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
