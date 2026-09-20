using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EdgeWidget.Services;
using EdgeWidget.Ui;
using Shape = System.Windows.Shapes.Shape;

namespace EdgeWidget.Views;

/// `EdgeWidget.exe --snapshot <dir>` renders the real windows in several states to PNG and exits.
/// Lets the visuals be reviewed without elevation, UAC or mouse interaction. Data here is sample data.
/// Never reads or writes the settings file, never touches any credentials and never opens the sensors.
internal static class Snapshot
{
    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        DateTimeOffset now = DateTimeOffset.Now;

        var claude = new ClaudeSnapshot(new UsageWindow(32, now.AddMinutes(125)), new UsageWindow(11, now.AddDays(3).AddHours(5)),
            new Money(10.53m, "EUR"), null);
        var codexMonthly = new CodexSnapshot(false, new CodexWindow(0, TimeSpan.FromDays(30), now.AddDays(30)), null, null);
        var grok = new GrokSnapshot(false, new GrokWindow(64, "Semanal", now.AddDays(2).AddHours(3)),
            new GrokWindow(18, "Bajo demanda", null), null);
        var cpu = new CpuSnapshot("Intel Core i7-8750H", 64, 78, 23, null);
        var gpu = new GpuSnapshot(true, "NVIDIA GeForce GTX 1050", 41, 12, 783, 4096, null);

        var window = new EdgeWindow(DateTime.Now.AddMinutes(-42), PanelMode.Auto) { PreviewMode = true, AnimationsEnabled = false };
        window.SetCodex(codexMonthly);
        window.SetGrok(grok);
        window.SetGpu(gpu);
        window.Show();
        window.SetClaude(claude);
        window.SetCpu(cpu);
        window.SaveSnapshot(Path.Combine(directory, "01_auto_collapsed.png"));

        window.ExpandNow();
        window.SaveSnapshot(Path.Combine(directory, "02_auto_expanded_fijar.png"));

        window.ApplyMode(PanelMode.Pinned);
        window.SaveSnapshot(Path.Combine(directory, "03_pinned_five_rings.png"));

        PaintHover(window.ModeButton, hovered: true);
        PaintHover(window.CloseButton, hovered: true);
        window.SaveSnapshot(Path.Combine(directory, "04_pinned_hover_ocultar_close.png"));
        PaintHover(window.ModeButton, hovered: false);
        PaintHover(window.CloseButton, hovered: false);

        PaintHover(window.RefreshButton, hovered: true);
        window.SaveSnapshot(Path.Combine(directory, "05_pinned_hover_actualizar.png"));
        PaintHover(window.RefreshButton, hovered: false);

        window.SetRefreshing(true);
        window.SaveSnapshot(Path.Combine(directory, "06_pinned_actualizando.png"));
        window.SetRefreshing(false);

        window.ShowCardNow(EdgeWindow.RingClaude);
        window.SaveSnapshot(Path.Combine(directory, "07_card_claude_spend.png"));

        window.ShowCardNow(EdgeWindow.RingCodex);
        window.SaveSnapshot(Path.Combine(directory, "08_card_codex_monthly.png"));

        window.ShowCardNow(EdgeWindow.RingGrok);
        window.SaveSnapshot(Path.Combine(directory, "09_card_grok.png"));

        window.ShowCardNow(EdgeWindow.RingCpu);
        window.SaveSnapshot(Path.Combine(directory, "10_card_cpu.png"));

        window.ShowCardNow(EdgeWindow.RingGpu);
        window.SaveSnapshot(Path.Combine(directory, "11_card_gpu.png"));

        window.SetClaude(ClaudeSnapshot.Failed(ClaudeUsageService.RenewMessage));
        window.SetCodex(CodexSnapshot.Failed("Error HTTP 503"));
        window.SetCpu(new CpuSnapshot("Intel Core i7-8750H", null, null, 12, "Requiere ejecutar como administrador"));
        window.ShowCardNow(EdgeWindow.RingClaude);
        window.SaveSnapshot(Path.Combine(directory, "12_error_claude.png"));

        window.SetClaude(claude with { Session = new UsageWindow(86, now.AddMinutes(38)) });
        window.SetCpu(cpu);
        window.ShowCardNow(EdgeWindow.RingCpu);
        window.SetCodex(CodexSnapshot.NotAvailable("auth.json not found"));
        window.SetGpu(GpuSnapshot.NotDetected);
        window.SaveSnapshot(Path.Combine(directory, "13_codex_and_gpu_hidden_card_follows.png"));

        window.ApplyMode(PanelMode.Auto);
        window.SaveSnapshot(Path.Combine(directory, "14_back_to_auto.png"));

        window.Close();

        var menu = new TrayMenuWindow { ShowActivated = false, Left = -32000, Top = -32000 };
        menu.Show();
        menu.UpdateLayout();
        SaveElement((FrameworkElement)menu.Content, Path.Combine(directory, "15_tray_menu.png"));
        menu.CloseMenu();
    }

    /// Paints the end state of a panel button's hover transition (the triggers need a real cursor).
    private static void PaintHover(Button button, bool hovered)
    {
        button.ApplyTemplate();

        if (button.Template.FindName("Chrome", button) is Border chrome && button.Template.FindName("Label", button) is TextBlock label)
        {
            if (hovered)
            {
                chrome.Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E));
                label.Foreground = Brushes.White;
            }
            else
            {
                chrome.ClearValue(Border.BackgroundProperty);
                label.ClearValue(TextBlock.ForegroundProperty);
            }
        }
        else if (button.Template.FindName("Disc", button) is Shape disc && button.Template.FindName("Glyph", button) is Shape glyph)
        {
            if (hovered)
            {
                disc.Fill = Palette.Red;
                glyph.Stroke = Brushes.White;
            }
            else
            {
                disc.ClearValue(Shape.FillProperty);
                glyph.ClearValue(Shape.StrokeProperty);
            }
        }
    }

    private static void SaveElement(FrameworkElement element, string path)
    {
        const double scale = 2;
        double width = element.ActualWidth + element.Margin.Left + element.Margin.Right;
        double height = element.ActualHeight + element.Margin.Top + element.Margin.Bottom;
        var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);

        var backdrop = new DrawingVisual();
        using (DrawingContext dc = backdrop.RenderOpen())
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3A, 0x4A, 0x6B)), null, new Rect(0, 0, width, height));
        bitmap.Render(backdrop);
        bitmap.Render(element);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}
