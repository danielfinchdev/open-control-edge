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
            bool pawnIo = false;
            try { pawnIo = LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled; } catch { }
            if (!pawnIo) PawnIoButton.Visibility = Visibility.Visible;
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
            MessageBox.Show(error ?? Loc.Get("Install.PawnIoDone"), "Open Control Edge", MessageBoxButton.OK,
                error is null ? MessageBoxImage.Information : MessageBoxImage.Warning);
            if (error is null) PawnIoButton.Visibility = Visibility.Collapsed;
        }
        finally { PawnIoButton.IsEnabled = true; }
    }

    private async Task InstallAsync()
    {
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

        WarningsText.Text = string.Join(Environment.NewLine, result.Warnings);
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
