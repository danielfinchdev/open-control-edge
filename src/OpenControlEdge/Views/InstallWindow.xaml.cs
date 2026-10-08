using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using OpenControlEdge.Services;
using OpenControlEdge.Ui;

namespace OpenControlEdge.Views;

/// Welcome window shown when the executable runs outside C:\Program Files\OpenControlEdge (the unzipped release):
/// installs with the one UAC prompt already accepted to start it (see Installer). Also the uninstall confirmation.
public partial class InstallWindow : Window
{
    internal enum Mode { Install, Uninstall }

    internal enum Outcome
    {
        /// Closed without doing anything.
        None,

        /// Installed: the installed copy is already running, this process should exit.
        Installed,

        /// "Usar sin instalar": carry on with this copy.
        Portable,

        /// Uninstalled: this process should exit.
        Uninstalled,
    }

    private readonly Mode _mode;
    private readonly Ellipse[] _dots;
    private readonly Dictionary<string, (Ellipse Dot, TextBlock Status)> _checkRows = new();
    private IReadOnlyList<SystemCheck.Item> _checks = Array.Empty<SystemCheck.Item>();
    private bool _busy;
    private bool _finished;

    internal Outcome Result { get; private set; }

    internal InstallWindow(Mode mode)
    {
        _mode = mode;
        InitializeComponent();
        _dots = new[] { DotStop, DotCopy, DotData, DotTask, DotStart };
        if (mode == Mode.Install)
        {
#if !DEBUG
            if (UnelevatedLauncher.IsElevated) SecondaryButton.Visibility = Visibility.Collapsed;
#endif
            // PawnIO is now installed on its own with the rest (see the checks); the button stays for the snapshots only.
            Loaded += async (_, _) => ShowChecks(await Task.Run(SystemCheck.Run));
        }

        if (mode == Mode.Uninstall)
        {
            TitleText.SetResourceReference(TextBlock.TextProperty, "Uninstall.Title");
            SetResourceReference(TitleProperty, "Uninstall.Title");
            IntroText.SetResourceReference(TextBlock.TextProperty, "Uninstall.Intro");
            Steps.Visibility = Visibility.Collapsed;
            SecondaryButton.SetResourceReference(ContentProperty, "Install.Cancel");
            PrimaryButton.SetResourceReference(ContentProperty, "Uninstall.Button");
        }

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !_busy) Close();
        };
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private async void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_finished)
        {
            Close();
            return;
        }

        _busy = true;
        PrimaryButton.IsEnabled = SecondaryButton.IsEnabled = false;
        try
        {
            if (_mode == Mode.Install) await InstallAsync();
            else await UninstallAsync();
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnSecondaryClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_mode == Mode.Install && !_finished) Result = Outcome.Portable;
        Close();
    }

    private async void OnPawnIoClick(object sender, RoutedEventArgs e)
    {
        PawnIoButton.IsEnabled = false;
        try
        {
            string? error = await PawnIoInstaller.InstallAsync();
            MessageBox.Show(Loc.Message(error) ?? Loc.Get("Install.PawnIoDone"), "Open Control Edge", MessageBoxButton.OK,
                error is null ? MessageBoxImage.Information : MessageBoxImage.Warning);
            if (error is null) PawnIoButton.Visibility = Visibility.Collapsed;
        }
        finally { PawnIoButton.IsEnabled = true; }
    }

    /// One row per check: a dot (green ready, amber to fix, red missing, grey for information) and its status.
    internal void ShowChecks(IReadOnlyList<SystemCheck.Item> checks)
    {
        _checks = checks;
        Checks.Children.Clear();
        _checkRows.Clear();
        foreach (SystemCheck.Item item in checks)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var dot = new Ellipse { Style = (Style)FindResource("StepDot") };
            var status = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            status.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextSecondary");
            DockPanel.SetDock(dot, Dock.Left);
            DockPanel.SetDock(status, Dock.Right);
            row.Children.Add(dot);
            row.Children.Add(status);
            var label = new TextBlock { Text = Loc.Get("Check." + item.Key), Style = (Style)FindResource("StepText") };
            row.Children.Add(label);
            Checks.Children.Add(row);
            _checkRows[item.Key] = (dot, status);
            Paint(item.Key, item.State, item.Detail);
        }
        ChecksPanel.Visibility = Visibility.Visible;
        bool blocked = checks.Any(c => c.State == SystemCheck.State.Missing);
        ChecksTitle.Text = Loc.Get(blocked ? "Check.TitleProblems" : "Check.TitleReady");
    }

    private void Paint(string key, SystemCheck.State state, string? detail, string? statusKey = null)
    {
        if (!_checkRows.TryGetValue(key, out var row)) return;
        PaintDot(row.Dot, state switch
        {
            SystemCheck.State.Ready => Palette.Low,
            SystemCheck.State.WillFix => Palette.Medium,
            SystemCheck.State.Missing => Palette.Red,
            _ => null,
        });
        string text = Loc.Get(statusKey ?? state switch
        {
            SystemCheck.State.Ready => "Check.Ready",
            SystemCheck.State.WillFix => key == "PawnIo" ? "Check.WillInstall" : "Check.WillEnable",
            SystemCheck.State.Missing => "Check.Missing",
            _ => detail is null ? "Check.None" : "Check.Detected",
        });
        row.Status.Text = state == SystemCheck.State.Info && detail is not null ? detail
            : detail is not null && state == SystemCheck.State.Ready && key == "Windows" ? detail : text;
    }

    /// Before installing: what the checks found missing and can be fixed here (Secondary Logon, PawnIO). PawnIO is
    /// optional: if it cannot be installed the widget installs anyway and only the temperatures are missing.
    private async Task FixSystemAsync()
    {
        if (_checks.FirstOrDefault(c => c.Key == "Seclogon") is { State: SystemCheck.State.WillFix })
        {
            Paint("Seclogon", SystemCheck.State.WillFix, null, "Check.Fixing");
            string? error = await Task.Run(SystemCheck.FixSeclogon);
            Paint("Seclogon", error is null ? SystemCheck.State.Ready : SystemCheck.State.Missing, null, error is null ? "Check.Enabled" : "Check.Failed");
            if (error is not null) Log.Warn("Install", error);
        }
        if (_checks.FirstOrDefault(c => c.Key == "PawnIo") is { State: SystemCheck.State.WillFix })
        {
            Paint("PawnIo", SystemCheck.State.WillFix, null, "Check.Installing");
            string? error = await PawnIoInstaller.InstallAsync();
            Paint("PawnIo", error is null ? SystemCheck.State.Ready : SystemCheck.State.Missing, null, error is null ? "Check.Installed" : "Check.Failed");
            if (error is not null) Log.Warn("Install", "PawnIO: " + error);
        }
    }

    private async Task InstallAsync()
    {
        await FixSystemAsync();
        ShowStatus(Loc.Get("Install.Working"), error: false);
        var progress = new Progress<InstallStep>(ShowStep);
        InstallResult result = await Installer.InstallAsync(progress);
        ShowResult(result);
        if (!result.Ok) return;

        Result = Outcome.Installed;
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Close();
    }

    private async Task UninstallAsync()
    {
        ShowStatus(Loc.Get("Uninstall.Working"), error: false);
        string? error = await Task.Run(AutoStartService.Uninstall);
        if (error is null)
        {
            Result = Outcome.Uninstalled;
            ShowStatus(Loc.Get("Uninstall.Done"), error: false);
            Finish();
        }
        else
        {
            ShowStatus(Loc.Format("Uninstall.Failed", Loc.Message(error) ?? error), error: true);
            PrimaryButton.IsEnabled = SecondaryButton.IsEnabled = true;
        }
    }

    /// Marks every step before this one as done and this one as running.
    internal void ShowStep(InstallStep step)
    {
        for (int i = 0; i < _dots.Length; i++)
            PaintDot(_dots[i], i < (int)step ? Palette.Low : i == (int)step ? Palette.Medium : null);
    }

    /// Final state of the installation: all dots done, or the failed one red with the reason below.
    internal void ShowResult(InstallResult result)
    {
        if (result.Ok)
        {
            foreach (Ellipse dot in _dots) PaintDot(dot, Palette.Low);
            ShowStatus(Loc.Get("Install.Done"), error: false);
            Finish();
        }
        else
        {
            Ellipse? running = _dots.FirstOrDefault(d => ReferenceEquals(d.Fill, Palette.Medium));
            if (running is not null) PaintDot(running, Palette.Red);
            ShowStatus(Loc.Format("Install.Failed", Loc.Message(result.Error) ?? string.Empty), error: true);
            PrimaryButton.SetResourceReference(ContentProperty, "Install.Retry");
            PrimaryButton.IsEnabled = SecondaryButton.IsEnabled = true;
        }

        WarningsText.Text = string.Join(Environment.NewLine, result.Warnings.Select(w => Loc.Message(w) ?? w));
        WarningsText.Visibility = result.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Finish()
    {
        _finished = true;
        SecondaryButton.Visibility = Visibility.Collapsed;
        PrimaryButton.SetResourceReference(ContentProperty, "Install.Close");
        PrimaryButton.IsEnabled = true;
    }

    private void ShowStatus(string text, bool error)
    {
        StatusText.Text = text;
        if (error) StatusText.Foreground = Palette.Red;
        else StatusText.ClearValue(TextBlock.ForegroundProperty);
        StatusText.Visibility = Visibility.Visible;
    }

    private static void PaintDot(Ellipse dot, SolidColorBrush? fill)
    {
        dot.Fill = fill;
        if (fill is null) dot.SetResourceReference(Shape.StrokeProperty, "Theme.TextSecondary");
        else dot.Stroke = fill;
    }
}
