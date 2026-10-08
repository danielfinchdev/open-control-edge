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

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
