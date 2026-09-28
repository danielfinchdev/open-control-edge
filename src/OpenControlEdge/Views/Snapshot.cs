using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenControlEdge.Services;
using OpenControlEdge.Ui;
using Shape = System.Windows.Shapes.Shape;

namespace OpenControlEdge.Views;

/// `OpenControlEdge.exe --snapshot <dir>` renders the real windows in several states to PNG and exits.
/// Lets the visuals be reviewed without elevation, UAC or mouse interaction. Data here is sample data.
/// Never reads or writes the settings file, never touches any credentials and never opens the sensors.
internal static class Snapshot
{
    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        DateTimeOffset now = DateTimeOffset.Now;

        var claude = new ClaudeSnapshot(false, new UsageWindow(32, now.AddMinutes(125)), new UsageWindow(11, now.AddDays(3).AddHours(5)),
            new Money(10.53m, "EUR"), null) { Plan = "max" };
        var codexMonthly = new CodexSnapshot(false, new CodexWindow(0, TimeSpan.FromDays(30), now.AddDays(30)), null, null) { Plan = "go" };
        var cursor = new CursorSnapshot(false, new UsageWindow(64, now.AddDays(12)),
            new UsageWindow(18, now.AddDays(12)), null) { Plan = "pro" };
        const string openCodeFixture = """{"totalTokens":{"input":12345,"output":6789,"reasoning":321,"cache":{"read":2100,"write":55}},"totalCost":1.2345,"totalSessions":7}""";
        var (fixtureInput, fixtureOutput, fixtureReasoning, fixtureCacheRead, fixtureCacheWrite, fixtureCost) = OpenCodeUsageParser.Parse(openCodeFixture);
        if (fixtureInput != 12345 || fixtureOutput != 6789 || fixtureReasoning != 321 || fixtureCacheRead != 2100
            || fixtureCacheWrite != 55 || fixtureCost != 1.2345m)
            throw new InvalidDataException("OpenCode fixture parser check failed");
        var openCode = new OpenCodeSnapshot(false, fixtureInput, fixtureOutput, fixtureReasoning, fixtureCacheRead, fixtureCacheWrite, fixtureCost, null);
        var deepSeek = new DeepSeekSnapshot(false, DeepSeekBalanceParser.Parse("""{"is_available":true,"balance_infos":[{"currency":"CNY","total_balance":"110.00","granted_balance":"10.00","topped_up_balance":"100.00"},{"currency":"USD","total_balance":"4.25","granted_balance":"1.00","topped_up_balance":"3.25"}]}"""), null);
        var openRouterParsed = OpenRouterKeyParser.Parse("""{"data":{"usage":25.5,"limit":100,"limit_remaining":74.5}}""");
        var openRouter = new OpenRouterSnapshot(false, openRouterParsed.Usage, openRouterParsed.Limit, openRouterParsed.Remaining, null);
        var cpu = new CpuSnapshot("Intel Core i7-8750H", 64, 78, 23, null);
        var gpu = new GpuSnapshot(true, "NVIDIA GeForce GTX 1050", 41, 12, 783, 4096, null);

        var window = new EdgeWindow(DateTime.Now.AddMinutes(-42), PanelMode.Auto) { PreviewMode = true, AnimationsEnabled = false };
        window.SetCodex(codexMonthly);
        window.SetCursor(cursor);
        window.SetOpenCode(openCode);
        window.SetDeepSeek(deepSeek);
        window.SetOpenRouter(openRouter);
        window.SetGpu(gpu);
        window.Show();
        window.SetClaude(claude);
        window.SetCpu(cpu);
        window.SaveSnapshot(Path.Combine(directory, "01_auto_collapsed.png"));

        window.ExpandNow();
        window.SaveSnapshot(Path.Combine(directory, "02_auto_expanded_fijar.png"));

        window.ApplyMode(PanelMode.Pinned);
        window.SaveSnapshot(Path.Combine(directory, "03_pinned_eight_rings.png"));

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

        window.ShowCardNow(EdgeWindow.RingCursor);
        window.SaveSnapshot(Path.Combine(directory, "09_card_cursor.png"));

        window.ShowCardNow(EdgeWindow.RingOpenCode);
        window.SaveSnapshot(Path.Combine(directory, "21_card_opencode_local_fixture.png"));

        window.ShowCardNow(EdgeWindow.RingDeepSeek);
        window.SaveSnapshot(Path.Combine(directory, "22_card_deepseek_balance.png"));

        window.ShowCardNow(EdgeWindow.RingOpenRouter);
        window.SaveSnapshot(Path.Combine(directory, "23_card_openrouter_limit.png"));

        window.SetOpenRouter(new OpenRouterSnapshot(false, 3.21m, null, null, null));
        window.ShowCardNow(EdgeWindow.RingOpenRouter);
        window.SaveSnapshot(Path.Combine(directory, "24_card_openrouter_spend_without_limit.png"));

        window.SetCursor(CursorSnapshot.Failed("Error HTTP 503"));
        window.ShowCardNow(EdgeWindow.RingCursor);
        window.SaveSnapshot(Path.Combine(directory, "17_cursor_error.png"));
        window.SetCursor(CursorSnapshot.NotAvailable(AiDetector.CursorLoginMessage));
        window.SaveSnapshot(Path.Combine(directory, "18_cursor_no_session.png"));
        window.SetCursor(cursor);

        window.SetOpenCode(OpenCodeSnapshot.Failed("Sin base de datos de sesiones"));
        window.ShowCardNow(EdgeWindow.RingOpenCode);
        window.SaveSnapshot(Path.Combine(directory, "25_opencode_no_local_data.png"));
        window.SetDeepSeek(DeepSeekSnapshot.Failed("Clave API no válida"));
        window.ShowCardNow(EdgeWindow.RingDeepSeek);
        window.SaveSnapshot(Path.Combine(directory, "26_deepseek_error.png"));
        window.SetOpenRouter(OpenRouterSnapshot.Failed("Añade la clave API desde Claves de API…"));
        window.ShowCardNow(EdgeWindow.RingOpenRouter);
        window.SaveSnapshot(Path.Combine(directory, "27_openrouter_no_key.png"));
        window.SetOpenCode(openCode);
        window.SetDeepSeek(deepSeek);
        window.SetOpenRouter(openRouter);

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
        window.SetCodex(CodexSnapshot.NotAvailable(AiDetector.CodexLoginMessage));
        window.SetGpu(GpuSnapshot.NotDetected);
        window.SaveSnapshot(Path.Combine(directory, "13_codex_no_session_and_gpu_hidden.png"));

        window.ApplyMode(PanelMode.Auto);
        window.SaveSnapshot(Path.Combine(directory, "14_back_to_auto.png"));

        window.ApplyMode(PanelMode.Pinned);
        window.ShowCardNow(EdgeWindow.RingClaude);
        window.SetClaudeRenewing();
        window.SetClaudeRenewFailed(App.RenewTaskMissingMessage);
        window.SaveSnapshot(Path.Combine(directory, "16_card_claude_renew_task_missing.png"));

        // "Total" tab: Claude weekly (11 %), Codex its longer window (weekly 70 % instead of the 5 h 40 %), Cursor monthly.
        var codexTwoWindows = new CodexSnapshot(false, new CodexWindow(40, TimeSpan.FromHours(5), now.AddHours(3)),
            new CodexWindow(70, TimeSpan.FromDays(7), now.AddDays(4)), null) { Plan = "plus" };
        window.SetClaude(claude);
        window.SetCodex(codexTwoWindows);
        window.SetCursor(cursor);
        window.SetGpu(gpu);
        window.ShowCardNow(EdgeWindow.RingCodex);
        window.SaveSnapshot(Path.Combine(directory, "29_tab_session_codex_two_windows.png"));
        window.ApplyUsageView(UsageView.Total);
        window.SaveSnapshot(Path.Combine(directory, "30_tab_total_codex_two_windows.png"));
        window.ApplyUsageView(UsageView.Session);

        window.SetClaude(ClaudeSnapshot.Failed(ClaudeUsageService.FreeAccountMessage) with { Plan = "free" });
        window.ShowCardNow(EdgeWindow.RingClaude);
        window.SaveSnapshot(Path.Combine(directory, "31_card_claude_free.png"));

        window.SetClaude(claude);
        window.SetCodex(codexMonthly);
        window.SetCursor(CursorSnapshot.Absent());
        window.SetOpenCode(OpenCodeSnapshot.Absent());
        window.SetDeepSeek(DeepSeekSnapshot.Absent());
        window.SetOpenRouter(OpenRouterSnapshot.Absent());
        window.SaveSnapshot(Path.Combine(directory, "19_only_claude_and_codex.png"));

        window.SetClaude(ClaudeSnapshot.Absent());
        window.SetCodex(CodexSnapshot.Absent());
        window.SetCursor(CursorSnapshot.Absent());
        window.SetOpenCode(OpenCodeSnapshot.Absent());
        window.SetDeepSeek(DeepSeekSnapshot.Absent());
        window.SetOpenRouter(OpenRouterSnapshot.Absent());
        window.SaveSnapshot(Path.Combine(directory, "20_no_ai_installed.png"));

        // The scene of the reference photos: Claude 73 % (weekly 7 %), ChatGPT/Codex 21 %, Cursor 52 %.
        DateTimeOffset thursdayNoon = now.Date.AddDays(((int)DayOfWeek.Thursday - (int)now.DayOfWeek + 6) % 7 + 1).AddHours(12);
        window.SetClaude(new ClaudeSnapshot(false, new UsageWindow(73, now.AddMinutes(51)), new UsageWindow(7, thursdayNoon), null, null));
        window.SetCodex(new CodexSnapshot(false, new CodexWindow(21, TimeSpan.FromHours(5), now.AddHours(2)),
            new CodexWindow(9, TimeSpan.FromDays(7), now.AddDays(5)), null) { Plan = "plus" });
        window.SetCursor(new CursorSnapshot(false, new UsageWindow(52, now.AddDays(12)), null, null) { Plan = "pro" });
        window.SetCpu(cpu);
        window.SetGpu(gpu);
        window.ShowCardNow(EdgeWindow.RingClaude);
        window.SaveSnapshot(Path.Combine(directory, "32_reference_scene_claude_card.png"));

        // Above 85 % the usage rings drop their brand colour: arc and percentage turn red; so do CPU/GPU above 85 °C.
        window.SetClaude(new ClaudeSnapshot(false, new UsageWindow(92, now.AddMinutes(17)), new UsageWindow(64, thursdayNoon), null, null));
        window.SetCodex(new CodexSnapshot(false, new CodexWindow(88, TimeSpan.FromHours(5), now.AddHours(1)),
            new CodexWindow(40, TimeSpan.FromDays(7), now.AddDays(5)), null));
        window.SetCursor(new CursorSnapshot(false, new UsageWindow(85, now.AddDays(12)), null, null));
        window.SetCpu(new CpuSnapshot("Intel Core i7-8750H", 91, 94, 88, null));
        window.SetGpu(new GpuSnapshot(true, "NVIDIA GeForce GTX 1050", 78, 97, 3900, 4096, null));
        window.ShowCardNow(EdgeWindow.RingClaude);
        window.SaveSnapshot(Path.Combine(directory, "33_alert_over_85_claude_card.png"));
        window.ShowCardNow(EdgeWindow.RingCpu);
        window.SaveSnapshot(Path.Combine(directory, "34_alert_card_cpu.png"));

        var keys = new ApiKeyWindow(previewMode: true) { ShowActivated = false, Left = -32000, Top = -32000 };
        keys.Show();
        keys.UpdateLayout();
        SaveElement((FrameworkElement)keys.Content, Path.Combine(directory, "28_api_key_window.png"));
        keys.Close();

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
