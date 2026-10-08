using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
    /// A small opencode.db with OpenCode's session columns, read back through the real service (and so through
    /// LocalDatabaseReader and UntrustedSqlite), then deleted. Two sessions whose sums are the values shown on the card.
    private static OpenCodeSnapshot ReadOpenCodeFixture(string directory)
    {
        string path = Path.Combine(directory, "opencode-fixture.db");
        File.Delete(path);
        try
        {
            UntrustedSqlite.CreateFixture(path, """
                CREATE TABLE session (id TEXT, tokens_input INTEGER, tokens_output INTEGER, tokens_reasoning INTEGER,
                                      tokens_cache_read INTEGER, tokens_cache_write INTEGER, cost REAL);
                INSERT INTO session VALUES ('a', 12000, 6000, 300, 2000, 50, 1.0),
                                           ('b', 345, 789, 21, 100, 5, 0.2345);
                """);
            OpenCodeSnapshot read = new OpenCodeUsageService(path).FetchAsync().GetAwaiter().GetResult();
            if (read is not { Message: null, TokensIn: 12345, TokensOut: 6789, TokensReasoning: 321, TokensCacheRead: 2100, TokensCacheWrite: 55 }
                || read.CostUsd is not decimal cost || Math.Abs(cost - 1.2345m) > 0.00001m)
                throw new InvalidDataException("OpenCode fixture database check failed: " + (read.Message ?? "wrong values"));
            return read;
        }
        finally
        {
            File.Delete(path);
        }
    }

    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        DateTimeOffset now = DateTimeOffset.Now;

        var claude = new ClaudeSnapshot(false, new UsageWindow(32, now.AddMinutes(125)), new UsageWindow(11, now.AddDays(3).AddHours(5)),
            new Money(10.53m, "EUR"), null) { Plan = "max" };
        var codexMonthly = new CodexSnapshot(false, new CodexWindow(0, TimeSpan.FromDays(30), now.AddDays(30)), null, null) { Plan = "go" };
        // Parsers against the shapes observed on 2026-09-29 (and the credits/on-demand variants they accept).
        const string codexCreditsNone = """{"rate_limit":{"primary_window":{"used_percent":39,"limit_window_seconds":2592000,"reset_after_seconds":2559967,"reset_at":1793225265},"secondary_window":null},"credits":{"has_credits":false,"unlimited":false,"overage_limit_reached":false,"balance":null,"approx_local_messages":null,"approx_cloud_messages":null}}""";
        const string codexCreditsSome = """{"credits":{"has_credits":true,"unlimited":false,"balance":"1250.5"}}""";
        if (CodexUsageParser.ParseCredits(codexCreditsNone) is not null
            || CodexUsageParser.ParseCredits(codexCreditsSome) is not { Balance: 1250.5m, Unlimited: false })
            throw new InvalidDataException("Codex credits fixture parser check failed");
        const string cursorFixture = """{"billingCycleStart":"2026-09-20T16:55:07.000Z","billingCycleEnd":"2026-10-20T16:55:07.000Z","membershipType":"pro","limitType":"user","isUnlimited":false,"individualUsage":{"plan":{"enabled":true,"used":1429,"limit":2000,"remaining":571,"totalPercentUsed":2.886868686868687},"onDemand":{"enabled":true,"used":900,"limit":5000,"remaining":4100}}}""";
        CursorUsageParser.Result cursorParsed = CursorUsageParser.Parse(cursorFixture);
        if (cursorParsed.OnDemandSpent != new Money(9m, "USD") || cursorParsed.OnDemandLimit != new Money(50m, "USD")
            || cursorParsed.OnDemand is not { Percent: 18 })
            throw new InvalidDataException("Cursor on-demand fixture parser check failed");
        if (CursorUsageParser.Parse(cursorFixture.Replace("\"totalPercentUsed\":2.886868686868687", "\"totalPercentUsed\":null")).Cycle is not null)
            throw new InvalidDataException("Cursor parser accepted a non-numeric percentage");

        var cursor = new CursorSnapshot(false, new UsageWindow(64, now.AddDays(12)), cursorParsed.OnDemand, null)
        {
            Plan = "pro",
            OnDemandSpent = cursorParsed.OnDemandSpent,
            OnDemandLimit = cursorParsed.OnDemandLimit,
        };
        var ram = new RamSnapshot(63, 10_150_000_000, 17_020_000_000, 4_300_000_000, 12_600_000_000, 26_900_000_000, null);
        OpenCodeSnapshot openCode = ReadOpenCodeFixture(directory);
        var deepSeek = new DeepSeekSnapshot(false, DeepSeekBalanceParser.Parse("""{"is_available":true,"balance_infos":[{"currency":"CNY","total_balance":"110.00","granted_balance":"10.00","topped_up_balance":"100.00"},{"currency":"USD","total_balance":"4.25","granted_balance":"1.00","topped_up_balance":"3.25"}]}"""), null);
        var openRouterParsed = OpenRouterKeyParser.Parse("""{"data":{"usage":25.5,"limit":100,"limit_remaining":74.5}}""");
        var openRouter = new OpenRouterSnapshot(false, openRouterParsed.Usage, openRouterParsed.Limit, openRouterParsed.Remaining, null);
        var cpu = new CpuSnapshot("Intel Core i7-8750H", 64, 78, 23, null);
        var gpu = new GpuSnapshot(true, "NVIDIA GeForce GTX 1050", 41, 12, 783, 4096, null);

        // Dark, classic colours, Spanish: the defaults, whatever the settings file says.
        ThemeManager.Apply(AppTheme.Dark, RingColorTheme.Classic);
        Loc.Apply(UiLanguage.Spanish);

        var window = new EdgeWindow(DateTime.Now.AddMinutes(-42), PanelMode.Auto) { PreviewMode = true, AnimationsEnabled = false };
        // The custom view with every ring: the shots below show all of them, as before the views existed.
        Settings everything = Settings.Defaults with { View = WidgetView.Custom };
        window.ApplyViews(everything, animate: false);
        var fps = new FpsService.Sample(141, "League of Legends", false, 144, null);
        window.SetFps(fps);
        window.PreviewWorkArea(ReferenceWorkArea);
        window.SetCodex(codexMonthly);
        window.SetCursor(cursor);
        window.SetOpenCode(openCode);
        window.SetDeepSeek(deepSeek);
        window.SetOpenRouter(openRouter);
        window.SetGpu(gpu);
        window.SetRam(ram);
        window.Show();
        window.SetClaude(claude);
        window.SetCpu(cpu);
        CheckFont(window.ClaudeLabel, FontWeights.Normal);
        CheckFont(window.ClaudeLabel, FontWeights.Medium);
        CheckFont(window.ClaudePlanText, FontWeights.SemiBold);
        window.SaveSnapshot(Path.Combine(directory, "01_auto_collapsed.png"));

        window.ExpandNow();
        window.SaveSnapshot(Path.Combine(directory, "02_auto_expanded_fijar.png"));

        window.ApplyMode(PanelMode.Pinned);
        window.SaveSnapshot(Path.Combine(directory, "03_pinned_all_rings_custom_view.png"));

        // Four buttons, two by two: fijar / vista above, ajustes / cerrar below.
        PaintHover(window.ModeButton, hovered: true);
        window.SaveSnapshot(Path.Combine(directory, "04_buttons_2x2_hover_ocultar.png"));
        PaintHover(window.ModeButton, hovered: false);

        PaintHover(window.ViewButton, hovered: true);
        window.SaveSnapshot(Path.Combine(directory, "05_buttons_2x2_hover_view.png"));
        PaintHover(window.ViewButton, hovered: false);

        window.PreviewWorkArea(1392);
        PaintHover(window.CloseButton, hovered: true);
        window.SaveSnapshot(Path.Combine(directory, "06_buttons_2x2_1440p_hover_close.png"));
        PaintHover(window.CloseButton, hovered: false);
        window.PreviewWorkArea(ReferenceWorkArea);

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
        window.SetDeepSeek(DeepSeekSnapshot.Failed(ProviderKeyStore.InvalidKeyMessage));
        window.ShowCardNow(EdgeWindow.RingDeepSeek);
        window.SaveSnapshot(Path.Combine(directory, "26_deepseek_error.png"));
        window.SetOpenRouter(OpenRouterSnapshot.Failed(AiDetector.AddKeyMessage));
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

        // Clicking the Claude ring: "Renovando sesión…", then the new reading with the outcome, or a clear error.
        window.ApplyMode(PanelMode.Pinned);
        window.SetClaude(ClaudeSnapshot.Failed(ClaudeUsageService.RenewMessage));
        window.ShowCardNow(EdgeWindow.RingClaude);
        window.SaveSnapshot(Path.Combine(directory, "16a_card_claude_expired.png"));
        window.SetClaudeRenewing();
        window.SaveSnapshot(Path.Combine(directory, "16b_card_claude_renewing.png"));
        window.SetClaude(claude);
        window.SetClaudeNote(Loc.Format("Claude.Renewed", now.AddHours(8).LocalDateTime));
        window.SaveSnapshot(Path.Combine(directory, "16c_card_claude_renewed.png"));
        window.SetClaudeNote(Loc.Format("Claude.StillValid", now.AddHours(3).LocalDateTime));
        window.SaveSnapshot(Path.Combine(directory, "16d_card_claude_still_valid.png"));
        window.SetClaude(ClaudeSnapshot.Failed(ClaudeUsageService.RenewMessage));
        window.SetClaudeNote(Loc.Message(ClaudeSessionRenewer.NotRenewedMessage));
        window.SaveSnapshot(Path.Combine(directory, "16e_card_claude_not_renewed.png"));
        window.SetClaudeNote(Loc.Message(ClaudeSessionRenewer.CliMissingMessage));
        window.SaveSnapshot(Path.Combine(directory, "16f_card_claude_cli_missing.png"));
        // Signed out (empty token and refresh token): one click opens the sign-in.
        window.SetClaude(ClaudeSnapshot.Failed(ClaudeSessionRenewer.SignedOutMessage));
        window.SetClaudeNote(null);
        window.SaveSnapshot(Path.Combine(directory, "16g_card_claude_signed_out.png"));
        window.SetClaudeNote(Loc.Message(ClaudeSessionRenewer.LoginOpenedMessage));
        window.SaveSnapshot(Path.Combine(directory, "16h_card_claude_login_opened.png"));
        window.SetClaudeNote(Loc.Message(UnelevatedLauncher.SeclogonMessage));
        window.SaveSnapshot(Path.Combine(directory, "16i_card_claude_seclogon.png"));
        // HTTP 429: the earlier reading, with its age.
        window.SetClaudeNote(null);
        window.SetClaude(claude with { StaleSince = now.AddMinutes(-7) });
        window.SaveSnapshot(Path.Combine(directory, "16j_card_claude_stale.png"));
        window.SetClaudeNote(null);

        // RAM ring and card: the hint, then the outcome of "Liberar RAM".
        window.SetClaude(claude);
        window.ShowCardNow(EdgeWindow.RingRam);
        window.SaveSnapshot(Path.Combine(directory, "52_card_ram.png"));
        window.SetRamNote(Loc.Format("Ram.Freed", "812"));
        window.SetRam(ram with { Percent = 58, UsedBytes = 9_300_000_000, CachedBytes = 3_050_000_000 });
        window.SaveSnapshot(Path.Combine(directory, "53_card_ram_freed.png"));
        window.SetRamNote(Loc.Format("Ram.Wait", 42));
        window.SaveSnapshot(Path.Combine(directory, "54_card_ram_wait.png"));
        window.SetRamNote(null);
        window.SetRam(ram);

        // Spend: Codex credits (only with has_credits) and Cursor on-demand; Claude's spend is 07.
        window.SetCodex(codexMonthly with { Credits = CodexUsageParser.ParseCredits(codexCreditsSome) });
        window.ShowCardNow(EdgeWindow.RingCodex);
        window.SaveSnapshot(Path.Combine(directory, "55_card_codex_credits.png"));
        window.SetCodex(codexMonthly);
        window.SetCursor(cursor with { OnDemand = null, OnDemandLimit = null });
        window.ShowCardNow(EdgeWindow.RingCursor);
        window.SaveSnapshot(Path.Combine(directory, "56_card_cursor_on_demand_no_limit.png"));
        window.SetCursor(cursor);

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
        window.ShowCardNow(EdgeWindow.RingClaude);
        window.SaveSnapshot(Path.Combine(directory, "37_tab_total_claude_weekly.png"));
        window.ApplyUsageView(UsageView.Session);
        window.SaveSnapshot(Path.Combine(directory, "38_tab_session_claude.png"));

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

        window.SetCpu(new CpuSnapshot("Intel Core i7-8750H", null, null, 12, HardwareSensorService.PawnIoMissingMessage));
        window.SetGpu(new GpuSnapshot(true, "NVIDIA GeForce GTX 1050", null, 12, 783, 4096, HardwareSensorService.PawnIoMissingMessage));
        window.ShowCardNow(EdgeWindow.RingCpu);
        window.SaveSnapshot(Path.Combine(directory, "35_cpu_pawnio_required.png"));
        window.ShowCardNow(EdgeWindow.RingGpu);
        window.SaveSnapshot(Path.Combine(directory, "36_gpu_pawnio_required.png"));

        // Eight rings of the reference scene for the theme, language and scale shots.
        window.SetClaude(new ClaudeSnapshot(false, new UsageWindow(73, now.AddMinutes(51)), new UsageWindow(7, thursdayNoon),
            new Money(10.53m, "EUR"), null) { Plan = "max" });
        window.SetCodex(codexTwoWindows);
        window.SetCursor(cursor);
        window.SetOpenCode(openCode);
        window.SetDeepSeek(deepSeek);
        window.SetOpenRouter(openRouter);
        window.SetCpu(new CpuSnapshot("Intel Core i7-8750H", 76, 88, 41, null));
        window.SetGpu(gpu);

        ThemeManager.Apply(AppTheme.Light, RingColorTheme.Classic);
        window.ShowCardNow(EdgeWindow.RingClaude);
        window.SaveSnapshot(Path.Combine(directory, "39_theme_light_claude_card.png"));
        window.ShowCardNow(EdgeWindow.RingCpu);
        window.SaveSnapshot(Path.Combine(directory, "40_theme_light_cpu_card.png"));
        ThemeManager.Apply(AppTheme.Light, RingColorTheme.Neon);
        window.ShowCardNow(EdgeWindow.RingCursor);
        window.SaveSnapshot(Path.Combine(directory, "41_theme_light_colors_neon.png"));

        ThemeManager.Apply(AppTheme.Dark, RingColorTheme.Ocean);
        window.ShowCardNow(EdgeWindow.RingClaude);
        window.SaveSnapshot(Path.Combine(directory, "42_colors_ocean.png"));
        ThemeManager.Apply(AppTheme.Dark, RingColorTheme.Sunset);
        window.ShowCardNow(EdgeWindow.RingCodex);
        window.SaveSnapshot(Path.Combine(directory, "43_colors_sunset.png"));
        ThemeManager.Apply(AppTheme.Dark, RingColorTheme.Mono);
        window.SaveSnapshot(Path.Combine(directory, "44_colors_mono.png"));
        ThemeManager.Apply(AppTheme.Dark, RingColorTheme.Classic);

        Loc.Apply(UiLanguage.English);
        window.ShowCardNow(EdgeWindow.RingClaude);
        window.SaveSnapshot(Path.Combine(directory, "45_english_claude_card.png"));
        window.ApplyUsageView(UsageView.Total);
        window.ShowCardNow(EdgeWindow.RingCodex);
        window.SaveSnapshot(Path.Combine(directory, "46_english_codex_total.png"));
        window.ApplyUsageView(UsageView.Session);
        window.SetClaude(ClaudeSnapshot.Failed("Sin conexión"));
        window.ShowCardNow(EdgeWindow.RingClaude);
        window.SaveSnapshot(Path.Combine(directory, "47_english_service_message.png"));
        Loc.Apply(UiLanguage.Spanish);
        window.SetClaude(claude);

        // Scale: 1366×768 at 100 %, 1080p at 125 % (both small), 1440p at 100 % (large). All nine rings fit.
        foreach ((double work, string name) in new[] { (728.0, "48_scale_1366x768"), (824.0, "49_scale_1080p_125"), (1392.0, "50_scale_1440p") })
        {
            window.PreviewWorkArea(work);
            window.ShowCardNow(EdgeWindow.RingClaude);
            window.SaveSnapshot(Path.Combine(directory, $"{name}_x{window.Scale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}.png"));
        }
        window.PreviewWorkArea(ReferenceWorkArea);

        SaveViewsAndNewRings(window, directory, claude, everything, fps);

        // Welcome / install window: before, during, done, failed; and the uninstall confirmation.
        SaveInstallWindow(InstallWindow.Mode.Install, null, null, Path.Combine(directory, "57_install_welcome.png"));
        SaveInstallWindow(InstallWindow.Mode.Install, null, null, Path.Combine(directory, "57b_install_checks.png"), new SystemCheck.Item[]
        {
            new("Windows", SystemCheck.State.Ready, "Windows 11 · 26300"), new("Admin", SystemCheck.State.Ready, null),
            new("Seclogon", SystemCheck.State.WillFix, null), new("PawnIo", SystemCheck.State.WillFix, null),
            new("Agents", SystemCheck.State.Info, "Claude, Codex, Cursor"),
        });
        SaveInstallWindow(InstallWindow.Mode.Install, InstallStep.Copy, null, Path.Combine(directory, "58_install_progress.png"));
        SaveInstallWindow(InstallWindow.Mode.Install, InstallStep.Start,
            new InstallResult(true, null, new[] { "Se conserva la carpeta antigua C:\\Program Files\\EdgeWidget." }),
            Path.Combine(directory, "59_install_done.png"));
        SaveInstallWindow(InstallWindow.Mode.Install, InstallStep.Data,
            new InstallResult(false, "La carpeta de datos es un enlace o punto de reanálisis; se cancela para evitar escrituras elevadas fuera de ella.", Array.Empty<string>()),
            Path.Combine(directory, "60_install_error.png"));
        SaveInstallWindow(InstallWindow.Mode.Uninstall, null, null, Path.Combine(directory, "61_uninstall.png"));

        window.Close();

        var menu = new TrayMenuWindow { ShowActivated = false, Left = -32000, Top = -32000 };
        menu.SetAutoStart(true);
        menu.Show();
        menu.UpdateLayout();
        SaveElement((FrameworkElement)menu.Content, Path.Combine(directory, "15_tray_menu.png"));
        menu.CloseMenu();

        ThemeManager.Apply(AppTheme.Light, RingColorTheme.Classic);
        Loc.Apply(UiLanguage.English);
        var lightMenu = new TrayMenuWindow { ShowActivated = false, Left = -32000, Top = -32000 };
        lightMenu.Show();
        lightMenu.UpdateLayout();
        SaveElement((FrameworkElement)lightMenu.Content, Path.Combine(directory, "51_tray_menu_light_english.png"));
        lightMenu.CloseMenu();
        ThemeManager.Apply(AppTheme.Dark, RingColorTheme.Classic);
        Loc.Apply(UiLanguage.Spanish);
        SaveLogos(directory);
        SettingsWindow.SaveSnapshots(directory);
    }

    /// The views (IA, PC, a custom mix), the FPS and Modo juego rings and cards, account tabs, the update dot and
    /// custom backgrounds.
    private static void SaveViewsAndNewRings(EdgeWindow window, string directory, ClaudeSnapshot claude, Settings everything, FpsService.Sample fps)
    {
        window.ApplyMode(PanelMode.Pinned);
        window.ApplyViews(everything with { View = WidgetView.Ai }, animate: false);
        window.SaveSnapshot(Path.Combine(directory, "70_view_ai.png"));
        window.ApplyViews(everything with { View = WidgetView.Pc }, animate: false);
        window.SaveSnapshot(Path.Combine(directory, "71_view_pc.png"));
        if (window.View != WidgetView.Pc) throw new InvalidDataException("view switch failed");

        window.ShowCardNow(EdgeWindow.RingFps);
        window.SaveSnapshot(Path.Combine(directory, "72_card_fps_game.png"));
        window.SetFps(new FpsService.Sample(1, null, true, 60, null));
        window.SaveSnapshot(Path.Combine(directory, "73_card_fps_desktop.png"));
        window.SetFps(new FpsService.Sample(38, "Cyberpunk2077", false, 144, null));
        window.SaveSnapshot(Path.Combine(directory, "73b_card_fps_low.png"));
        window.SetFps(fps);

        window.SetGameMode(new GameModeSnapshot(false, false, false, null));
        window.ShowCardNow(EdgeWindow.RingGameMode);
        window.SaveSnapshot(Path.Combine(directory, "74_card_game_mode_disabled.png"));
        window.SetGameMode(new GameModeSnapshot(true, false, false, null));
        window.SaveSnapshot(Path.Combine(directory, "75_card_game_mode_off.png"));
        window.SetGameMode(new GameModeSnapshot(true, true, false,
            new GameModeResult(true, 6, 3, GamePowerPlan.Balanced, true, new[] { "No se pudo detener: DiagTrack" })));
        window.SaveSnapshot(Path.Combine(directory, "76_card_game_mode_on.png"));

        // A custom mix, in the user's order: FPS, Claude, CPU, Codex; the rest hidden.
        var custom = ViewLayout.Normalize(WidgetView.Custom, [RingKeys.Fps, RingKeys.Claude, RingKeys.Cpu, RingKeys.Codex],
            RingKeys.All.Except([RingKeys.Fps, RingKeys.Claude, RingKeys.Cpu, RingKeys.Codex]));
        window.ApplyViews(everything with { View = WidgetView.Custom, CustomLayout = custom }, animate: false);
        window.SaveSnapshot(Path.Combine(directory, "77_view_custom_mix.png"));

        window.ApplyViews(everything with { View = WidgetView.Ai }, animate: false);
        window.SetAccounts(AiProviderId.Claude, ["Principal", "Trabajo"], 1);
        window.SetClaude(claude with { Session = new UsageWindow(58, DateTimeOffset.Now.AddMinutes(90)), Plan = "pro" });
        window.ShowCardNow(EdgeWindow.RingClaude);
        window.SaveSnapshot(Path.Combine(directory, "78_card_claude_two_accounts.png"));
        window.SetAccounts(AiProviderId.Claude, ["Principal"], 0);
        window.SetClaude(claude);

        window.SetUpdateAvailable(true);
        window.SaveSnapshot(Path.Combine(directory, "79_update_dot.png"));
        window.SetUpdateAvailable(false);

        ThemeManager.Apply(AppTheme.Dark, RingColorTheme.Classic, Color.FromRgb(0x0B, 0x1E, 0x33));
        window.ShowCardNow(EdgeWindow.RingClaude);
        window.SaveSnapshot(Path.Combine(directory, "80_background_navy.png"));
        ThemeManager.Apply(AppTheme.Dark, RingColorTheme.Classic, Color.FromRgb(0xF4, 0xE9, 0xD8));
        window.SaveSnapshot(Path.Combine(directory, "81_background_light_custom.png"));
        ThemeManager.Apply(AppTheme.Dark, RingColorTheme.Classic, null);

        // The README showcase: transparent shots composed over a background of its own (docs/showcase).
        string showcase = Path.Combine(directory, "showcase");
        Directory.CreateDirectory(showcase);
        DateTimeOffset now = DateTimeOffset.Now;
        window.ApplyViews(everything with { View = WidgetView.Ai }, animate: false);
        window.SetClaude(claude with { Session = new UsageWindow(42, now.AddMinutes(132)), Plan = "max" });
        window.SetCodex(new CodexSnapshot(false, new CodexWindow(23, TimeSpan.FromHours(5), now.AddHours(3)),
            new CodexWindow(61, TimeSpan.FromDays(7), now.AddDays(4)), null) { Plan = "plus" });
        window.SetAccounts(AiProviderId.Claude, ["Personal", "Trabajo"], 0);
        window.ShowCardNow(EdgeWindow.RingClaude);
        window.SaveSnapshot(Path.Combine(showcase, "ia_claude.png"), transparent: true);
        window.ShowCardNow(EdgeWindow.RingCodex);
        window.SaveSnapshot(Path.Combine(showcase, "ia_codex.png"), transparent: true);
        window.ApplyViews(everything with { View = WidgetView.Pc }, animate: false);
        window.SetCpu(new CpuSnapshot("Intel Core i7-8750H", 68, 81, 34, null));
        window.SetFps(new FpsService.Sample(143, "League of Legends", false, 144, null));
        window.ShowCardNow(EdgeWindow.RingFps);
        window.SaveSnapshot(Path.Combine(showcase, "pc_fps.png"), transparent: true);
        window.SetGameMode(new GameModeSnapshot(true, true, false, new GameModeResult(true, 5, 3, GamePowerPlan.Balanced, true, Array.Empty<string>())));
        window.ShowCardNow(EdgeWindow.RingGameMode);
        window.SaveSnapshot(Path.Combine(showcase, "pc_game_mode.png"), transparent: true);
        window.ShowCardNow(EdgeWindow.RingCpu);
        window.SaveSnapshot(Path.Combine(showcase, "pc_cpu.png"), transparent: true);
        window.ApplyMode(PanelMode.Auto);
        window.ApplyMode(PanelMode.Pinned);
        ThemeManager.Apply(AppTheme.Light, RingColorTheme.Classic, null);
        window.ApplyViews(everything with { View = WidgetView.Ai }, animate: false);
        window.ShowCardNow(EdgeWindow.RingClaude);
        window.SaveSnapshot(Path.Combine(showcase, "ia_claude_light.png"), transparent: true);
        window.SetAccounts(AiProviderId.Claude, ["Principal"], 0);
        ThemeManager.Apply(AppTheme.Dark, RingColorTheme.Classic, null);
        window.SetGameMode(new GameModeSnapshot(false, false, false, null));
        window.SetFps(fps);
        window.SetClaude(claude);
        window.ApplyViews(everything, animate: false);
    }

    private static readonly (string Name, Drawing Icon)[] BrandLogos =
    [
        ("Claude", Icons.ClaudeSpark), ("Codex", Icons.Codex), ("Cursor", Icons.Cursor),
        ("OpenCode", Icons.OpenCode), ("DeepSeek", Icons.DeepSeek), ("OpenRouter", Icons.OpenRouter),
    ];

    /// The six provider logos in the panel ring, at card size (18) and at settings size (20), dark over light;
    /// then the dark rings alone at 3× to check the edges.
    private static void SaveLogos(string directory)
    {
        var sheet = new StackPanel();
        foreach (AppTheme theme in new[] { AppTheme.Dark, AppTheme.Light })
        {
            ThemeManager.Apply(theme, RingColorTheme.Classic);
            sheet.Children.Add(LogoRow(withSmall: true));
        }
        ThemeManager.Apply(AppTheme.Dark, RingColorTheme.Classic);
        SaveLoose(sheet, Path.Combine(directory, "62_logos_dark_light.png"), 2);
        SaveLoose(LogoRow(withSmall: false), Path.Combine(directory, "63_logos_rings_x3.png"), 3);
    }

    private static Border LogoRow(bool withSmall)
    {
        var text = new SolidColorBrush(ThemeManager.ColorOf(ThemeManager.Text));
        var rings = new StackPanel { Orientation = Orientation.Horizontal };
        var small = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        foreach ((string name, Drawing icon) in BrandLogos)
        {
            var column = new StackPanel { Width = 74 };
            column.Children.Add(new RingGauge
            {
                Width = 54, Height = 54, StrokeThickness = 5.5, IconSize = 22, Value = 0.62, Icon = icon, RingBrush = Palette.Claude,
                TrackBrush = new SolidColorBrush(ThemeManager.ColorOf(ThemeManager.RingTrack)), IconBrush = text,
            });
            column.Children.Add(new TextBlock { Text = name, FontSize = 11, Foreground = text, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) });
            rings.Children.Add(column);
            var pair = new StackPanel { Width = 74, Orientation = Orientation.Horizontal };
            pair.Children.Add(new IconView { Icon = icon, Width = 18, Height = 18, Foreground = text, Margin = new Thickness(15, 0, 8, 0) });
            pair.Children.Add(new IconView { Icon = icon, Width = 20, Height = 20, Foreground = text });
            small.Children.Add(pair);
        }
        var row = new StackPanel(); row.Children.Add(rings);
        if (withSmall) row.Children.Add(small);
        var sheet = new Border { Background = new SolidColorBrush(ThemeManager.ColorOf(ThemeManager.Background)), Padding = new Thickness(18), Child = row };
        TextElement.SetFontFamily(sheet, (FontFamily)Application.Current.FindResource("UiFont"));
        return sheet;
    }

    /// Renders an element that is not in any window.
    private static void SaveLoose(FrameworkElement element, string path, double scale)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        element.Arrange(new Rect(element.DesiredSize));
        element.UpdateLayout();
        SaveElement(element, path, scale);
    }

    /// Work area of a 1080p screen at 100 % minus the taskbar: scale 1.
    private const double ReferenceWorkArea = 1040;

    /// The embedded font must really be what the text is drawn with: fails the snapshot run otherwise.
    private static void CheckFont(TextBlock text, FontWeight weight)
    {
        var typeface = new Typeface(text.FontFamily, text.FontStyle, weight, text.FontStretch);
        if (!typeface.TryGetGlyphTypeface(out GlyphTypeface glyphs)
            || !glyphs.FamilyNames.Values.Contains("Outfit")
            || glyphs.Weight != weight)
            throw new InvalidDataException($"font check failed for weight {weight}");
    }

    private static void SaveInstallWindow(InstallWindow.Mode mode, InstallStep? step, InstallResult? result, string path,
        IReadOnlyList<SystemCheck.Item>? checks = null)
    {
        var window = new InstallWindow(mode) { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
        window.Show();
        if (checks is not null) window.ShowChecks(checks);
        if (step is InstallStep running) window.ShowStep(running);
        if (result is not null) window.ShowResult(result);
        window.UpdateLayout();
        SaveElement((FrameworkElement)window.Content, path);
        window.Close();
    }

    /// Paints the hover state of a round panel button (the triggers need a real cursor).
    private static void PaintHover(Button button, bool hovered)
    {
        button.ApplyTemplate();
        if (button.Template.FindName("Chrome", button) is not Shape chrome) return;
        if (hovered) chrome.SetResourceReference(Shape.FillProperty, ThemeManager.ButtonHover);
        else chrome.SetResourceReference(Shape.FillProperty, ThemeManager.Button);
    }

    internal static void SaveElement(FrameworkElement element, string path, double scale = 2)
    {
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
