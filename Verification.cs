using System.Text.Json;

namespace UsageLimitViewer;

internal static class Verification
{
    internal static int Run()
    {
        var results = new List<string>();
        try
        {
            Check(!new Preferences().OpenDetailsOnHover, "Hover details defaults off", results);
            var display = new Preferences();
            Check(display.TaskbarTop == "Codex" && display.TaskbarBottom == "Antigravity", "Taskbar defaults to two providers", results);
            display.SelectTaskbarProvider(false, "Claude Code", persist: false);
            Check(display.TaskbarTop == "Codex" && display.TaskbarBottom == "Claude Code", "Taskbar row can show Claude Code", results);
            display.SelectTaskbarProvider(true, "Claude Code", persist: false);
            Check(display.TaskbarTop == "Claude Code" && display.TaskbarBottom == "Codex", "Selecting the other row swaps providers", results);
            display.SelectTaskbarProvider(false, "Grok", persist: false);
            Check(display.TaskbarTop == "Claude Code" && display.TaskbarBottom == "Grok", "Taskbar row can show Grok", results);
            var noon = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 27, 12, 0, 0)));
            Check(DetailsForm.FormatReset(noon.AddHours(4).AddMinutes(7), noon) == "今日 16:07 にリセット（あと4時間7分）", "Same-day reset shows today and time left", results);
            Check(DetailsForm.FormatReset(noon.AddHours(21), noon) == "明日 9:00 にリセット（あと21時間0分）", "Next-day reset shows tomorrow", results);
            Check(DetailsForm.FormatReset(noon.AddDays(6).AddHours(6), noon) == "10月3日(土) 18:00 にリセット（あと6日6時間）", "Later reset shows date and weekday", results);
            Check(DetailsForm.FormatReset(noon.AddHours(6).AddMilliseconds(-100), noon) == "今日 18:00 にリセット（あと6時間0分）", "Reset a moment before the hour shows the hour", results);
            Check(DetailsForm.FormatReset(noon.AddSeconds(20), noon) == "今日 12:00 にリセット（あと1分）", "Under a minute never shows zero", results);
            Check(DetailsForm.FormatReset(null, noon) == "リセット時刻不明" && DetailsForm.FormatReset(noon, noon) == "リセット時刻経過 · 更新待ち", "Reset missing and expired", results);
            Check(DetailsForm.ResetParts(noon.AddDays(6).AddHours(6), noon) == ("リセット 10月3日(土) 18:00", "あと6日6時間") &&
                DetailsForm.ResetParts(null, noon) == ("リセット時刻不明", null), "Reset line splits into date and time left", results);
            L.English = true;
            Check(DetailsForm.FormatReset(noon.AddDays(6).AddHours(6), noon) == "Resets Sat, Oct 3 18:00 (in 6d 6h)", "English reset date", results);
            Check(DetailsForm.FormatReset(noon.AddHours(4).AddMinutes(7), noon) == "Resets today 16:07 (in 4h 7m)", "English same-day reset", results);
            Check(L.F("取得 {age}", ("age", "09/22 03:00:00")) == "Fetched 09/22 03:00:00", "English timestamp", results);
            Check(L.QuotaLabel("週間") == "Weekly" && L.T("使用状況を開く") == "Open usage" &&
                L.T("Codexアプリ経由") == "Via Codex app" &&
                L.T("Grok CLI経由") == "Via Grok CLI" &&
                L.T("ホバーで詳細を開く") == "Open details on hover", "English labels", results);
            L.English = false;
            using var codex = JsonDocument.Parse("""{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":22.5,"windowDurationMins":300,"resetsAt":1800000000},"secondary":{"usedPercent":90,"windowDurationMins":10080}},"extra":{"primary":{"usedPercent":15,"windowDurationMins":60}}}}""");
            var c = QuotaParser.Codex(codex.RootElement);
            Check(c.Quotas.Count == 3 && c.Quotas[0].Remaining == 77.5 && c.Quotas[1].Label == "週間", "Codex multiple windows and fractional percentage", results);
            using var command = JsonDocument.Parse("""{"status":"SUCCESS","command":{"name":"usage","data":{"groups":[{"buckets":[{"id":"gemini-weekly","remaining_fraction":0.9974,"reset_time":"2026-10-01T00:00:00Z"},{"id":"gemini-5h","remaining_fraction":1}]},{"buckets":[{"id":"3p-weekly","remaining_fraction":0}]}]}}}""");
            var live = QuotaParser.AntigravityCommand(command.RootElement);
            Check(live.Quotas.Count == 3 && Math.Abs(live.Quotas[0].Remaining - 99.74) < .001 && live.Quotas[2].Remaining == 0, "AGY command groups retain precise quotas", results);
            using var modelReply = JsonDocument.Parse("""{"status":"SUCCESS","response":"","usage":{"total_tokens":12647},"denied_actions":[{"action":"command"}]}""");
            Check(QuotaParser.AgyRanModel(modelReply.RootElement) && !QuotaParser.AgyRanModel(command.RootElement),
                "agy replies that ran the model are detected", results);
            using var badCommand = JsonDocument.Parse("""{"status":"ERROR"}""");
            bool rejected = false;
            try { QuotaParser.AntigravityCommand(badCommand.RootElement); } catch (IOException) { rejected = true; }
            Check(rejected, "Failed AGY command never becomes fresh quota", results);
            using var legacy = JsonDocument.Parse("""{"rateLimits":{"primary":{"usedPercent":0,"windowDurationMins":300}}}""");
            Check(QuotaParser.Codex(legacy.RootElement).Quotas.Single().Remaining == 100, "Codex legacy response", results);
            using var empty = JsonDocument.Parse("""{"rateLimits":{"primary":null}}""");
            Check(QuotaParser.Codex(empty.RootElement).Quotas.Count == 0, "Missing quota is not zero or unlimited", results);
            using var agy = JsonDocument.Parse("""{"quota":{"gemini-weekly":{"remaining_fraction":0.9378,"reset_time":"2026-10-01T00:00:00Z"},"missing":{},"invalid":{"remaining_fraction":2},"zero":{"remaining_fraction":0}}}""");
            var a = QuotaParser.Antigravity(agy.RootElement, DateTimeOffset.UtcNow.AddMinutes(-11));
            Check(a.Quotas.Count == 2 && Math.Abs(a.Quotas[0].Remaining - 93.78) < .001 && a.Quotas[1].Remaining == 0, "AGY percentage and invalid data", results);
            Check(a.Stale && !c.Stale && (c with { Error = "offline" }).Stale, "Stale and failed refresh state", results);
            using var claudeData = JsonDocument.Parse("""{"rate_limits":{"five_hour":{"used_percentage":23.5,"resets_at":2200000000},"seven_day":{"used_percentage":41.2,"resets_at":2200000000}}}""");
            var claude = ClaudeCodeUsage.Parse(claudeData.RootElement, DateTimeOffset.UtcNow);
            Check(claude.Quotas.Count == 2 && Math.Abs(claude.Quotas[0].Remaining - 76.5) < .001 && Math.Abs(claude.Quotas[1].Remaining - 58.8) < .001,
                "Claude Code status line usage becomes remaining quota", results);
            using var freeClaudeData = JsonDocument.Parse("""{"rate_limits":{}}""");
            Check(ClaudeCodeUsage.Parse(freeClaudeData.RootElement, DateTimeOffset.UtcNow).Quotas.Count == 0,
                "Missing Claude Code limits never become zero usage", results);
            using var expiredClaudeData = JsonDocument.Parse("""{"rate_limits":{"five_hour":{"used_percentage":20,"resets_at":1000000000}}}""");
            Check(ClaudeCodeUsage.Parse(expiredClaudeData.RootElement, DateTimeOffset.UtcNow).Quotas.Count == 0,
                "Expired Claude Code limits are not displayed", results);
            using var grokData = JsonDocument.Parse("""{"config":{"creditUsagePercent":27.5,"currentPeriod":{"type":"USAGE_PERIOD_TYPE_WEEKLY","end":"2026-10-01T00:00:00Z"}}}""");
            var grok = GrokUsage.Parse(grokData.RootElement, new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero));
            Check(grok.Quotas.Count == 1 && grok.Quotas[0].Remaining == 72.5 && grok.Quotas[0].Label == "週間" && grok.Quotas[0].Reset?.UtcDateTime == new DateTime(2026, 10, 1),
                "Grok weekly usage becomes remaining quota and reset date", results);
            using var grokMonthly = JsonDocument.Parse("""{"config":{"creditUsagePercent":27.5,"currentPeriod":{"type":"USAGE_PERIOD_TYPE_MONTHLY","end":"2026-10-01T00:00:00Z"}}}""");
            bool invalidGrok = false;
            try { GrokUsage.Parse(grokMonthly.RootElement, DateTimeOffset.UtcNow); } catch (IOException) { invalidGrok = true; }
            Check(invalidGrok, "Grok non-weekly data is not mislabeled", results);
            var area = new Rectangle(0, 0, 1920, 1040);
            var anchor = new Rectangle(1700, 1045, 24, 24);
            var compact = TrayPresentation.Above(anchor, new Size(344, 54), area);
            var expanded = TrayPresentation.Above(anchor, new Size(460, 610), area);
            Check(compact.Y + 54 == expanded.Y + 610 && compact.Y + 54 < anchor.Top, "Tray anchored expansion", results);
            Check(TrayPresentation.Above(new Rectangle(1900, 1045, 24, 24), new Size(460, 610), area).X == 1460, "Right edge containment", results);
            Check(TrayPresentation.Above(new Rectangle(500, 0, 24, 24), new Size(460, 610), new Rectangle(0, 40, 1920, 1040)).Y >= 40, "Top taskbar containment", results);
            Check(ClaudeCodeUsage.IsBridgeCommand("""{"statusLine":{"type":"command","command":"powershell -File \"C:\\Users\\a\\AppData\\Local\\UsageLimitViewer\\ClaudeCodeStatusLine.ps1\""}}""") &&
                !ClaudeCodeUsage.IsBridgeCommand("""{"statusLine":{"type":"command","command":"other.ps1"}}""") &&
                !ClaudeCodeUsage.IsBridgeCommand("not json"), "Claude Code bridge command is recognized", results);
            Check(ClaudeCodeUsage.Waiting.Quotas.Count == 0 && ClaudeCodeUsage.Waiting.Compact == "—",
                "Claude Code waiting state never becomes zero", results);
            using var claudeApi = JsonDocument.Parse("""{"five_hour":{"utilization":12.5,"resets_at":"2099-01-01T00:00:00+00:00"},"seven_day":{"utilization":40,"resets_at":"2099-01-02T00:00:00Z"},"seven_day_opus":{"utilization":0,"resets_at":null},"seven_day_oauth_apps":null}""");
            var claudeLive = ClaudeUsageApi.Parse(claudeApi.RootElement, DateTimeOffset.UtcNow);
            Check(claudeLive.Quotas.Count == 2 && claudeLive.Quotas[0].Remaining == 87.5 && claudeLive.Quotas[1].Label == "週間" &&
                claudeLive.Quotas[1].Remaining == 60 && claudeLive.Error == null, "Claude usage API becomes remaining quota", results);
            var stored = """{"claudeAiOauth":{"accessToken":"old","refreshToken":"r1","expiresAt":1000,"refreshTokenExpiresAt":2000,"scopes":["user:inference"],"subscriptionType":"pro","rateLimitTier":"default_claude_ai"},"other":1}""";
            using var renewal = JsonDocument.Parse("""{"access_token":"new","refresh_token":"r2","expires_in":28800,"refresh_token_expires_in":2592000,"scope":"user:inference user:profile"}""");
            var renewedAt = new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);
            var renewedJson = ClaudeUsageApi.ApplyRenewal(stored, renewal.RootElement, renewedAt);
            var renewed = ClaudeUsageApi.ParseToken(renewedJson);
            Check(renewed is { Access: "new", Refresh: "r2" } && renewed.Value.Expires == renewedAt.AddHours(8) && renewed.Value.Scopes.Length == 2 &&
                renewedJson.Contains("\"subscriptionType\":\"pro\"") && renewedJson.Contains("\"other\":1") &&
                renewedJson.Contains(renewedAt.AddDays(30).ToUnixTimeMilliseconds().ToString()),
                "Claude sign-in renewal keeps CLI fields and rotates tokens", results);
            using var renewalWithoutRotation = JsonDocument.Parse("""{"access_token":"new","expires_in":60}""");
            Check(ClaudeUsageApi.ParseToken(ClaudeUsageApi.ApplyRenewal(stored, renewalWithoutRotation.RootElement, renewedAt))?.Refresh == "r1",
                "Claude renewal without a new refresh token keeps the old one", results);
            Check(ClaudeUsageApi.ParseToken("not json") == null && ClaudeUsageApi.ParseToken("""{"claudeAiOauth":{}}""") == null,
                "Invalid Claude sign-in is ignored", results);
            using var claudeEmpty = JsonDocument.Parse("""{"five_hour":null}""");
            Check(ClaudeUsageApi.Parse(claudeEmpty.RootElement, DateTimeOffset.UtcNow).Quotas.Count == 0, "Missing Claude usage never becomes zero", results);
            var order = new Preferences();
            order.SelectTaskbarProvider(true, "Claude Code", persist: false);
            order.SelectTaskbarProvider(false, "Grok", persist: false);
            var sorted = DetailsForm.OrderedReadings(order, new("Codex", [], null), new("Antigravity", [], null), null, new("Claude Code", [], null), new("Grok", [], null)).Select(r => r.Provider).ToArray();
            Check(sorted.SequenceEqual(new[] { "Claude Code", "Grok", "Codex", "Antigravity" }), "Details list starts with taskbar providers", results);
            Check(new Preferences().FiveHourFirst && ClockTextRenderer.TaskbarValue(new Reading("Claude Code", [new("5時間", 20.4, null), new("週間", 79.6, null)], DateTimeOffset.UtcNow)) == "20%(80%)",
                "Taskbar shows 5-hour (weekly) by default", results);
            ClockTextRenderer.FiveHourFirst = false;
            Check(ClockTextRenderer.TaskbarValue(new Reading("Claude Code", [new("5時間", 20.4, null), new("週間", 79.6, null)], DateTimeOffset.UtcNow)) == "80%(20%)",
                "Taskbar order can be switched to weekly (5-hour)", results);
            Check(ClockTextRenderer.TaskbarValue(new Reading("Codex", [new("5時間", 41, null), new("週間", 71, null), new("extra · 5時間", 10, null)], DateTimeOffset.UtcNow)) == "71%(41%)",
                "Codex with a 5-hour window shows weekly and 5-hour quota", results);
            ClockTextRenderer.FiveHourFirst = true;
            Check(ClockTextRenderer.TaskbarValue(new Reading("Codex", [new("週間", 80, null)], DateTimeOffset.UtcNow)) == "80%" &&
                ClockTextRenderer.TaskbarValue(new Reading("Claude Code", [new("週間", 71, null)], DateTimeOffset.UtcNow)) == "71%" &&
                ClockTextRenderer.TaskbarValue(new Reading("Antigravity", [new("gemini-5h", 50, null), new("gemini-weekly", 90, null)], DateTimeOffset.UtcNow)) == "50%",
                "Services without both windows keep a single taskbar value", results);
            Check(ClockTextRenderer.TaskbarName("Claude Code") == "Claude" && ClockTextRenderer.TaskbarName("Codex") == "Codex", "Taskbar shortens Claude Code to Claude", results);
            Check(StartupShortcut.IsApprovedByWindows(null) && StartupShortcut.IsApprovedByWindows([2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]) &&
                StartupShortcut.IsApprovedByWindows([6]) && !StartupShortcut.IsApprovedByWindows([3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]) &&
                !StartupShortcut.IsApprovedByWindows([1]), "Windows startup switch is read from StartupApproved", results);
            Check(Updater.Newer("v1.2.0", new Version(1, 1, 0)) == new Version(1, 2, 0) && Updater.Newer("v1.1.0", new Version(1, 1, 0)) == null &&
                Updater.Newer("1.0.9", new Version(1, 1, 0)) == null && Updater.Newer("nightly", new Version(1, 1, 0)) == null,
                "Only a newer release tag is offered as an update", results);
            var tip = TooltipText.For(TooltipText.Title, TooltipText.Status(new Reading("Codex", [new("5時間", 41, null), new("週間", 71, null)], DateTimeOffset.UtcNow)), L.T("クリックで詳細"));
            Check(tip.Length <= TooltipText.Limit && tip.StartsWith("Usage Limit Viewer ") && tip.Split('\n').Length == 3, "Tray tooltip has three short lines", results);
            Check(TooltipText.For("Usage Limit Viewer 1.1.0", new string('a', 30), new string('b', 30)).Split('\n').Length == 2, "Tooltip drops its last line to fit", results);
            var npmRoot = Path.Combine(Path.GetTempPath(), "clv-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var native = Path.Combine(npmRoot, "node_modules", "@openai", "codex", "vendor", "x86_64-pc-windows-msvc", "codex", "codex.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(native)!);
                File.WriteAllText(native, "");
                File.WriteAllText(Path.Combine(npmRoot, "codex.cmd"), "");
                var missing = Path.Combine(npmRoot, "none");
                Check(Providers.FindCodex(missing, npmRoot, missing) == (native, false) && Providers.FindCodex(missing, missing, missing).Exe == null,
                    "Codex CLI installed with npm is found behind codex.cmd", results);
            }
            finally { Directory.Delete(npmRoot, true); }
            results.Add("All tests passed.");

            File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "test-results.txt"), results); return 0;
        }
        catch (Exception ex)
        {
            results.Add(ex.ToString()); File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "test-results.txt"), results); return 1;
        }
    }
    private static void Check(bool value, string name, List<string> results)
    {
        if (!value) throw new Exception(name); results.Add("PASS " + name);
    }
    internal static int Render(bool live = false)
    {
        using var f = new DetailsForm(new Preferences());
        var c = new Reading("Codex", [new("5時間", 72, DateTimeOffset.UtcNow.AddHours(2)), new("週間", 43, DateTimeOffset.UtcNow.AddDays(3))], DateTimeOffset.UtcNow, Source: L.T("Codexアプリ経由"));
        var a = new Reading("Antigravity", [new("gemini-weekly", 86, DateTimeOffset.UtcNow.AddDays(4))], DateTimeOffset.UtcNow, Source: L.T("agy CLI経由"));
        if (live) { c = Task.Run(() => Providers.Codex(CancellationToken.None)).GetAwaiter().GetResult(); a = Task.Run(() => Providers.AntigravityLive(CancellationToken.None)).GetAwaiter().GetResult(); }
        f.UpdateReadings(c, a, null, null, false);
        f.Show(); f.Expand(false, false); Application.DoEvents();
        using (var bmp = new Bitmap(f.Width, f.Height)) { f.DrawToBitmap(bmp, f.ClientRectangle); bmp.Save(Path.Combine(AppContext.BaseDirectory, "preview-compact.png")); }
        f.Expand(true, false); Application.DoEvents();
        using (var bmp = new Bitmap(f.Width, f.Height)) { f.DrawToBitmap(bmp, f.ClientRectangle); bmp.Save(Path.Combine(AppContext.BaseDirectory, "preview-expanded.png")); }
        using (var hover = ClockTextRenderer.Render(192, 72, 144, c, a, true))
            hover.Save(Path.Combine(AppContext.BaseDirectory, "preview-hover.png"));
        var exampleClaude = new Reading("Claude Code", [new("5時間", 41, DateTimeOffset.UtcNow.AddHours(3)), new("週間", 71, DateTimeOffset.UtcNow.AddDays(5))], DateTimeOffset.UtcNow);
        using (var selectedClaude = ClockTextRenderer.Render(192, 72, 144, c, exampleClaude))
            selectedClaude.Save(Path.Combine(AppContext.BaseDirectory, "preview-claude.png"));
        var exampleGrok = new Reading("Grok", [new("週間", 73, DateTimeOffset.UtcNow.AddDays(4))], DateTimeOffset.UtcNow, Source: L.T("Grok CLI経由"));
        f.UpdateReadings(c, a, null, exampleGrok, false);
        f.Expand(true, false); Application.DoEvents();
        using (var grokDetails = new Bitmap(f.Width, f.Height)) { f.DrawToBitmap(grokDetails, f.ClientRectangle); grokDetails.Save(Path.Combine(AppContext.BaseDirectory, "preview-grok-details.png")); }
        using (var selectedGrok = ClockTextRenderer.Render(192, 72, 144, c, exampleGrok))
            selectedGrok.Save(Path.Combine(AppContext.BaseDirectory, "preview-grok-taskbar.png"));
        // Claude signed out: once with the last values kept, once with nothing to show.
        foreach (var (name, signedOut) in new[]
        {
            ("preview-login-values.png", exampleClaude with { Error = L.T("Claude Code のログインが切れています"), NeedsLogin = true }),
            ("preview-login-empty.png", new Reading("Claude Code", [], null, L.T("Claude Code のログインが切れています"), NeedsLogin: true)),
        })
        {
            f.UpdateReadings(c, a, signedOut, null, false);
            f.Expand(true, false); Application.DoEvents();
            using var shot = new Bitmap(f.Width, f.Height);
            f.DrawToBitmap(shot, f.ClientRectangle);
            shot.Save(Path.Combine(AppContext.BaseDirectory, name));
        }
        f.Hide();
        RenderMenu(Path.Combine(AppContext.BaseDirectory, "preview-menu.png"));
        using (var exit = new AppDialog(L.T("Usage Limit Viewer を終了しますか？"), L.T("終了すると、タスクバーの残量表示と定期的な更新が止まります。"), primary: L.T("終了する"), cancel: L.T("キャンセル")))
        {
            exit.Show(); exit.Refresh(); Application.DoEvents(); Thread.Sleep(300); Application.DoEvents();
            using var shot = new Bitmap(exit.Width, exit.Height);
            using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(exit.Location, Point.Empty, exit.Size);
            shot.Save(Path.Combine(AppContext.BaseDirectory, "preview-exit.png"));
        }
        return 0;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EndMenu();
    // Opens the real Win32 tray menu near the bottom-right corner, captures that screen area, then closes it.
    private static void RenderMenu(string output)
    {
        using var menu = new TrayMenu();
        var screen = Screen.PrimaryScreen!.Bounds;
        var anchor = new Rectangle(screen.Right - 420, screen.Bottom - 48, 96, 48);
        menu.Build = () =>
        [
            new(TooltipText.Title, Enabled: false), new("Claude 52%(78%) · Codex 100%", Enabled: false), MenuEntry.Separator,
            new(L.T("使用状況を開く"), Default: true), new(L.T("残量を今すぐ取得")),
            new(L.T("その他"), Children: [new(L.T("ホバーで詳細を開く"), Checked: true)]),
            new(L.T("アップデート"), Children: [new(L.T("今すぐ確認して更新"))]),
            MenuEntry.Separator, new(L.T("アプリを終了する")),
        ];
        using var timer = new System.Windows.Forms.Timer { Interval = 700 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var area = new Rectangle(screen.Right - 900, screen.Bottom - 640, 900, 640);
            using var shot = new Bitmap(area.Width, area.Height);
            using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
            shot.Save(output);
            EndMenu();
        };
        timer.Start();
        menu.Show(anchor, new Point(anchor.Left + 48, anchor.Top + 24));
    }
}
