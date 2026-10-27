using System.Net.Http;
using System.Text.Json;

namespace UsageLimitViewer;

internal static class Program
{
    // Matches the Start menu shortcut, so Windows shows that name and icon for this app's windows and notifications.
    internal const string AppUserModelId = "hinatamaxxx.UsageLimitViewer";
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string id);
    [STAThread]
    static int Main(string[] args)
    {
        SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        if (UsageLimitViewerSetup.SetupProgram.Requested(args)) return UsageLimitViewerSetup.SetupProgram.Run(args);
        if (args.Contains("--self-test")) return Verification.Run();
        if (args.Contains("--diagnose"))
        {
            ClaudeCodeUsage.EnsureBridge();
            Reading c;
            try { c = Providers.Codex(CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception e) { c = new("Codex", [], null, e.Message); }
            Reading a;
            try { a = Providers.AntigravityLive(CancellationToken.None).GetAwaiter().GetResult(); } catch { a = new("Antigravity", [], null, L.T("受信ファイルを読み取れません")); }
            Reading g;
            try { g = GrokUsage.ReadLive(CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception e) { g = new("Grok", [], null, e.Message); }
            Reading cl;
            try { cl = ClaudeUsageApi.ReadLive(CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception e) { cl = new("Claude Code", [], null, e.Message); }
            Directory.CreateDirectory(Preferences.Folder);
            File.WriteAllText(Path.Combine(Preferences.Folder, "diagnostics.json"), JsonSerializer.Serialize(new { codex = c, antigravity = a, claudeCode = cl, claudeCodeStatusLine = ClaudeCodeUsage.ReadSnapshot(), grok = g }, new JsonSerializerOptions { WriteIndented = true }));
            return c.Quotas.Count > 0 ? 0 : 1;
        }
        L.English = args.Contains("--english") || (!args.Contains("--japanese") && Preferences.Load().Language == "en");
        ApplicationConfiguration.Initialize();
        if (args.Contains("--render-preview")) return Verification.Render();
        if (args.Contains("--render-live")) return Verification.Render(true);
        if (args.Contains("--install-finished")) return InstallFinished();
        // After an update the previous copy hands over: wait until it has exited.
        int waitPid = Array.IndexOf(args, "--wait-pid") is var at && at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out var pid) ? pid : 0;
        if (waitPid != 0)
            try { using var previous = System.Diagnostics.Process.GetProcessById(waitPid); previous.WaitForExit(30000); }
            catch (ArgumentException) { } // Already gone.
        using var mutex = new Mutex(false, "Local\\UsageLimitViewer");
        bool owns;
        // After a language switch or an update the previous copy is still closing; wait for it instead of quitting.
        try { owns = mutex.WaitOne(args.Contains("--restarted") || waitPid != 0 ? 15000 : 0); }
        catch (AbandonedMutexException) { owns = true; }
        if (!owns)
        {
            // Starting the app again (for example from the Start menu) opens the running copy's panel.
            try { using var show = EventWaitHandle.OpenExisting(ShowSignal); show.Set(); }
            catch (Exception e) when (e is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException) { }
            return 0;
        }
        try
        {
            ClaudeCodeUsage.EnsureBridge();
            Application.Run(new TrayApplicationContext(openPanel: args.Contains("--open")));
        }
        finally { mutex.ReleaseMutex(); }
        return 0;
    }
    internal const string ShowSignal = "Local\\UsageLimitViewer_Show";

    // Shown by the one-line installer when it is done: what was set up, and whether to start the app now.
    private static int InstallFinished()
    {
        using var dialog = new AppDialog(L.T("インストールが完了しました"),
            L.T("タスクバーの通知領域に、AIサービスの残量が表示されます。スタートメニューの「Usage Limit Viewer」からも開けます。") + "\n\n" +
            L.T("Windowsにサインインすると自動で起動します。オフにするには、右クリックメニューの「その他」から切り替えてください。"),
            primary: L.T("完了"), check: L.T("Usage Limit Viewer を起動して、使用状況を開く"));
        if (dialog.ShowDialog() == DialogResult.OK && dialog.Checked)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Compat.ProcessPath, "--open") { UseShellExecute = false })?.Dispose();
        return 0;
    }
}
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly Preferences prefs = Preferences.Load();
    private readonly NotifyIcon tray = new();
    private readonly NotifyIcon agyTray = new();
    // Keep four slots: Windows 11 hides newly added tray icons in the overflow, which put a fifth slot under the ^ button.
    private readonly NotifyIcon[] extraSlots = Enumerable.Range(0, 2).Select(_ => new NotifyIcon()).ToArray();
    private NotifyIcon? selectedTray;
    private readonly TrayMenu menu = new();
    private readonly System.Windows.Forms.Timer poll = new() { Interval = 60000 };
    private readonly System.Windows.Forms.Timer hoverClose = new() { Interval = 30 };
    // The first check runs a minute after start, then once an hour decides whether a day has passed.
    private readonly System.Windows.Forms.Timer updateTimer = new() { Interval = 60 * 1000 };
    private readonly EventWaitHandle showSignal = new(false, EventResetMode.AutoReset, Program.ShowSignal);
    private readonly RegisteredWaitHandle showWait;
    private readonly SynchronizationContext ui;

    private readonly CancellationTokenSource stop = new();
    private readonly DetailsForm form;
    private readonly TrayWidget widget;
    private Reading codex = new("Codex", [], null), agy = new("Antigravity", [], null);
    private Reading? claude = ClaudeUsageApi.LoadCache();
    private Reading grok = new("Grok", [], null);
    private Reading shownTop = new("Codex", [], null), shownBottom = new("Antigravity", [], null);
    private readonly UpdateState updateState = Updater.LoadState();
    private ReleaseInfo? available;          // A newer release found while automatic updates are off.
    private string? restartInto;             // The updated exe, started once no window of this app is open.
    private bool busy, closing, hoverOpened, forceQueued, confirmingExit, updating, claudeLoginNotified;
    private Action? balloonClick;            // What clicking the last notification does.

    internal TrayApplicationContext(bool openPanel = false)
    {
        form = new(prefs);
        ClockTextRenderer.FiveHourFirst = prefs.FiveHourFirst;
        prefs.Pinned = false;
        widget = new(new[] { tray, agyTray }.Concat(extraSlots).ToArray(), menu);
        widget.OpenDetails += () => { hoverOpened = false; if (form.Visible) form.HideImmediately(); else form.Reveal(true); };
        widget.HoverDetailsEnabled = () => prefs.OpenDetailsOnHover;
        widget.TooltipText = () => TooltipText.For(TooltipText.Title,
            TooltipText.Status(shownTop) + " · " + TooltipText.Status(shownBottom), UpdateNotice());
        widget.UpdateTooltip();
        widget.HoverDetails += () => { if (!form.Visible) { hoverOpened = true; form.Reveal(true, fromHover: true); } };
        form.VisibleChanged += (_, _) => { if (!form.Visible) hoverOpened = false; };
        menu.Opening += () => { if (hoverOpened) form.HideImmediately(); };
        menu.Build = BuildMenu;
        hoverClose.Tick += (_, _) =>
        {
            if (!hoverOpened || !form.Visible || prefs.Pinned) return;
            if (widget.IsHovered || form.Bounds.Contains(Cursor.Position))
            {
                if (form.IsDismissing) form.Reveal(true, fromHover: true);
                return;
            }
            form.Dismiss();
        };
        form.TrayBounds = () => widget.Visible ? widget.ScreenBounds : TrayPresentation.GetBounds(selectedTray ?? tray);
        form.RefreshRequested += () => _ = Refresh(force: true);
        // Dragging a card into the first two places of the usage popup picks the taskbar's two rows.
        form.TaskbarChanged += Apply;
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        showWait = ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => ui.Post(_ => OpenPanel(), null), null, -1, false);
        tray.Icon = TrayPresentation.ReservationIcon(); tray.Text = "Usage Limit Viewer"; tray.Visible = true;
        agyTray.Icon = TrayPresentation.ReservationIcon(); agyTray.Text = "Antigravity"; agyTray.Visible = true;
        int slotNumber = 0;
        foreach (var slot in extraSlots)
        {
            slot.Icon = (Icon)tray.Icon.Clone(); slot.Text = $"Usage Limit Viewer 表示スペース {++slotNumber}";
            slot.Visible = true;
        }
        foreach (var icon in new[] { tray, agyTray }.Concat(extraSlots))
            icon.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) menu.Show(TrayPresentation.GetBounds(icon) ?? Rectangle.Empty); };
        form.PositionAtTray();
        foreach (var icon in new[] { tray, agyTray })
        {
            icon.MouseDown += (_, _) => selectedTray = icon;
            icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) { selectedTray = icon; hoverOpened = false; if (form.Visible && !prefs.Pinned) form.HideImmediately(); else form.Reveal(true); } };
            icon.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenPanel(); };
        }
        poll.Tick += (_, _) => _ = Refresh();
        // Nobody reads the taskbar while the session is locked or asleep: pause polling, and refresh right after return.
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        updateTimer.Tick += (_, _) =>
        {
            updateTimer.Interval = 60 * 60 * 1000;
            if (restartInto != null) { RestartWhenIdle(); return; }
            // Once a day; after a failed check (offline, for example) try again every hour.
            var due = updateState.Failures > 0 ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
            if (updateState.LastCheck is not { } last || DateTimeOffset.UtcNow - last >= due) _ = CheckForUpdate(manual: false);
        };
        form.VisibleChanged += (_, _) => { if (!form.Visible && restartInto != null) RestartWhenIdle(); };
        tray.BalloonTipClicked += (_, _) => balloonClick?.Invoke();
        form.LoginRequested += OpenClaudeLogin;

        poll.Start();
        hoverClose.Start();
        updateTimer.Start();
        if (prefs.Pinned) { form.PositionAtTray(); form.Show(); }
        _ = Refresh();
        if (openPanel) OpenPanel();
    }
    private void OpenPanel()
    {
        if (closing) return;
        hoverOpened = false;
        form.Reveal(true);
    }
    // Rebuilt on every open, so check marks always match the current settings and Windows' startup switch.
    private IReadOnlyList<MenuEntry> BuildMenu()
    {
        var entries = new List<MenuEntry> { new(TooltipText.Title, Enabled: false), new(StatusLine(), Enabled: false) };
        // A new version is announced here only when automatic updates are off; otherwise it just installs itself.
        if (!prefs.AutoUpdate && available is { } release)
            entries.Add(new(L.F("このアプリの新しい版 v{version} に更新", ("version", release.Version.ToString(3))), () => _ = CheckForUpdate(manual: true), Enabled: !updating));
        if (claude?.NeedsLogin == true) entries.Add(new(L.T("Claude Code にログイン"), OpenClaudeLogin));
        entries.Add(MenuEntry.Separator);
        entries.Add(new(L.T("使用状況を開く"), OpenPanel, Default: true));
        entries.Add(new(L.T("残量を今すぐ取得"), () => _ = Refresh(force: true), Enabled: !busy));
        MenuEntry Order(bool fiveHourFirst, string label) => new(label, () =>
        {
            prefs.FiveHourFirst = fiveHourFirst; prefs.Save();
            ClockTextRenderer.FiveHourFirst = fiveHourFirst;
            Apply();
        }, Checked: prefs.FiveHourFirst == fiveHourFirst, Radio: true);
        entries.Add(new(L.T("その他"), Children:
        [
            new(L.T("残量の表示順"), Children: [Order(true, L.T("5時間（週間）の順")), Order(false, L.T("週間（5時間）の順"))]),
            MenuEntry.Separator,
            new(L.T("ホバーで詳細を開く"), () => { prefs.OpenDetailsOnHover = !prefs.OpenDetailsOnHover; prefs.Save(); widget.UpdateTooltip(); }, Checked: prefs.OpenDetailsOnHover),
            new(L.T("Windows起動時に自動起動"), ToggleStartup, Checked: StartupShortcut.IsEnabled),
            MenuEntry.Separator,
            new("Language / 言語", Children: new[] { ("日本語", "ja"), ("English", "en") }.Select(language =>
                new MenuEntry(language.Item1, () => SwitchLanguage(language.Item2), Checked: prefs.Language == language.Item2, Radio: true)).ToList()),
        ]));
        entries.Add(new(L.T("アップデート"), Children:
        [
            new(L.T("このアプリ") + " v" + Updater.Current.ToString(3) + UpdateStatus(), Enabled: false),
            MenuEntry.Separator,
            new(updating ? L.T("更新しています…") : L.T("今すぐ確認して更新"), () => _ = CheckForUpdate(manual: true), Enabled: !updating),
            new(L.T("自動で更新する（毎日）"), () => { prefs.AutoUpdate = !prefs.AutoUpdate; prefs.Save(); Apply(); }, Checked: prefs.AutoUpdate),
        ]));
        entries.Add(MenuEntry.Separator);
        entries.Add(new(L.T("アプリを終了する"), ConfirmExit));
        return entries;
    }
    // The two taskbar values, so the menu answers "how much is left" even when the taskbar text is hidden.
    private string StatusLine() => busy ? L.T("残量を更新しています…")
        : $"{ClockTextRenderer.TaskbarName(shownTop.Provider)} {ClockTextRenderer.TaskbarValue(shownTop)} · {ClockTextRenderer.TaskbarName(shownBottom.Provider)} {ClockTextRenderer.TaskbarValue(shownBottom)}";
    private void ToggleStartup()
    {
        try
        {
            if (StartupShortcut.IsEnabled) StartupShortcut.SetEnabled(false);
            else if (!StartupShortcut.SetEnabled(true))
            {
                // Windows keeps its own on/off switch for the shortcut; the app never overrides the user's choice there.
                MessageBox.Show(L.T("Windowsのスタートアップ設定でオフになっています。開いた設定画面で「Usage Limit Viewer」をオンにしてください。"),
                    "Usage Limit Viewer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                StartupShortcut.OpenWindowsSettings();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            MessageBox.Show(L.T("自動起動の設定を保存できませんでした。"), "Usage Limit Viewer");
        }
    }
    private void SwitchLanguage(string language)
    {
        if (prefs.Language == language) return;
        prefs.Language = language; prefs.Save();
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(UsageLimitViewer.Compat.ProcessPath!, "--restarted") { UseShellExecute = false })?.Dispose(); }
        catch (Exception) { return; }
        ExitThread();
    }
    private void ConfirmExit()
    {
        if (confirmingExit) return;
        confirmingExit = true;
        try
        {
            using var dialog = new AppDialog(L.T("Usage Limit Viewer を終了しますか？"),
                L.T("終了すると、タスクバーの残量表示と定期的な更新が止まります。") + "\n\n" +
                L.T("もう一度使うときは、スタートメニューの「Usage Limit Viewer」から起動してください。自動起動がオンなら、次にサインインしたときにも起動します。"),
                primary: L.T("終了する"), cancel: L.T("キャンセル"));
            if (dialog.ShowDialog() == DialogResult.OK) ExitThread();
        }
        finally { confirmingExit = false; }
    }
    private string UpdateStatus() =>
        updating ? L.T("（更新しています…）")
        : available is { } release ? L.F("（v{version} があります）", ("version", release.Version.ToString(3)))
        : updateState.LastCheck == null ? L.T("（未確認）")
        : updateState.Failures > 0 ? L.T("（確認できませんでした）") : L.T("（最新です）");
    private string? UpdateNotice() => !prefs.AutoUpdate && available is { } release
        ? L.F("このアプリの新しい版 v{version} があります", ("version", release.Version.ToString(3))) : null;

    // manual: the reader chose "check and update now" (or clicked the new-version notice): always install, report the result.
    // Otherwise automatic: install only when automatic updates are on, and stay quiet unless it fails twice in a row.
    private async Task CheckForUpdate(bool manual)
    {
        if (updating || closing || restartInto != null) return;
        updating = true;
        try
        {
            var release = await Updater.FindNewer(stop.Token);
            updateState.LastCheck = DateTimeOffset.UtcNow;
            updateState.Latest = (release?.Version ?? Updater.Current).ToString(3);
            if (release == null)
            {
                available = null;
                if (manual) TrayPresentation.Notify(tray, L.T("このアプリは最新です"), "Usage Limit Viewer v" + Updater.Current.ToString(3));
            }
            else if (manual || prefs.AutoUpdate)
            {
                var exe = await Updater.Install(release, stop.Token);
                if (manual) TrayPresentation.Notify(tray, L.F("v{version} に更新しました", ("version", release.Version.ToString(3))), L.T("アプリを再起動します。"));
                available = null;
                restartInto = exe;
            }
            else if (available?.Version != release.Version)
            {
                available = release;
                balloonClick = () => _ = CheckForUpdate(manual: true);
                TrayPresentation.Notify(tray, L.F("このアプリの新しい版 v{version} があります", ("version", release.Version.ToString(3))), L.T("クリックすると更新します。"));
            }
            updateState.Failures = 0;
            updateState.LastError = null;
        }
        catch (Exception e) when (e is not OperationCanceledException || !closing)
        {
            updateState.LastCheck = DateTimeOffset.UtcNow;
            updateState.Failures++;
            updateState.LastError = e.Message;
            // A single failure is usually the network; tell the reader only when it keeps failing, or when they asked.
            if (manual || updateState.Failures == 2)
                TrayPresentation.Notify(tray, L.T("このアプリを更新できませんでした"), e.Message);
        }
        finally
        {
            updating = false;
            Updater.SaveState(updateState);
            if (!closing) Apply();
            if (restartInto != null) RestartWhenIdle();
        }
    }
    // Restart into the updated exe, but never close a window the reader is using.
    private void RestartWhenIdle()
    {
        if (closing || restartInto == null || form.Visible || confirmingExit || menu.IsOpen) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(restartInto, "--wait-pid " + Compat.ProcessId)
            { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(restartInto)! })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException) { return; }
        ExitThread();
    }
    // force: a manual refresh, which asks Claude right away instead of waiting out the polling interval.
    private async Task Refresh(bool force = false)
    {
        // A manual refresh during a running one is replayed right after it, not dropped.
        if (busy) { forceQueued |= force; return; }
        if (closing) return;
        busy = true; Apply();
        var agyTask = RefreshAntigravity(force);
        var grokTask = RefreshGrok(force);
        var claudeTask = RefreshClaude(force);
        try { codex = await Providers.Codex(stop.Token); }
        catch (OperationCanceledException) { codex = codex with { Error = L.T("接続がタイムアウトしました") }; }
        catch (Exception e) { codex = codex with { Error = e.Message }; }
        finally
        {
            // Always clear busy, or one unexpected provider error would stop every later refresh.
            try { await Task.WhenAll(agyTask, grokTask, claudeTask); }
            catch (Exception) { }
            finally
            {
                busy = false;
                if (!closing) Apply();
                if (forceQueued && !closing) { forceQueued = false; _ = Refresh(force: true); }
            }
        }
    }
    // Weekly limits that start a CLI process change slowly; ask every few minutes unless the reader asked to refresh now.
    private static readonly TimeSpan SlowInterval = TimeSpan.FromMinutes(3);
    private static bool RecentlyFetched(Reading reading) =>
        reading.Error == null && reading.Updated is { } at && DateTimeOffset.UtcNow - at < SlowInterval;
    private async Task RefreshAntigravity(bool force)
    {
        if (!force && RecentlyFetched(agy)) return;
        try { agy = await Providers.AntigravityLive(stop.Token); }
        catch (OperationCanceledException) { agy = agy with { Error = L.T("取得がタイムアウトしました") }; }
        catch (Exception e) { agy = agy with { Error = e.Message }; }
    }
    private async Task RefreshClaude(bool force)
    {
        // Prefer the account-wide API; fall back to the status line snapshot, keeping the last values grayed out on failure.
        try
        {
            claude = await ClaudeUsageApi.ReadLive(stop.Token, force);
            claudeLoginNotified = false;
            return;
        }
        catch (Exception e)
        {
            Reading? snapshot;
            try { snapshot = ClaudeCodeUsage.ReadSnapshot(); } catch (Exception) { snapshot = null; }
            var message = e is OperationCanceledException ? L.T("取得がタイムアウトしました")
                : e is IOException or HttpRequestException ? e.Message : L.T("Claudeの使用状況を取得できません");
            bool needsLogin = e is ClaudeLoginRequiredException;
            if (snapshot is { Quotas.Count: > 0 } && !snapshot.Stale) claude = snapshot with { NeedsLogin = needsLogin };
            else if (claude is { Quotas.Count: > 0 }) claude = claude with { Error = message, NeedsLogin = needsLogin };
            else claude = snapshot is { Quotas.Count: > 0 } ? snapshot with { NeedsLogin = needsLogin } : new Reading("Claude Code", [], null, message, NeedsLogin: needsLogin);
            // Say so once when the sign-in runs out, not on every refresh.
            if (needsLogin && !claudeLoginNotified)
            {
                claudeLoginNotified = true;
                ui.Post(_ =>
                {
                    balloonClick = OpenClaudeLogin;
                    TrayPresentation.Notify(tray, L.T("Claude Code のログインが切れています"), L.T("クリックするとログイン画面を開きます。"));
                }, null);
            }
        }
    }

    // Opens claude auth login; when its window closes, ask Claude again right away instead of waiting a minute.
    private void OpenClaudeLogin()
    {
        if (closing) return;
        var login = ClaudeLogin.Start();
        if (login == null) return;
        login.EnableRaisingEvents = true;
        login.Exited += (_, _) => { login.Dispose(); ui.Post(_ => _ = Refresh(force: true), null); };
    }
    private async Task RefreshGrok(bool force)
    {
        if (!force && RecentlyFetched(grok)) return;
        try { grok = await GrokUsage.ReadLive(stop.Token); }
        catch (OperationCanceledException) { grok = grok with { Error = L.T("取得がタイムアウトしました") }; }
        catch (Exception e) { grok = grok with { Error = e.Message }; }
    }
    private void Apply()
    {
        if (closing) return;
        Reading? displayedClaude = claude;
        if (displayedClaude == null && (prefs.TaskbarTop == "Claude Code" || prefs.TaskbarBottom == "Claude Code"))
            displayedClaude = ClaudeCodeUsage.Waiting;
        // Show Grok whenever its CLI is installed, so a failed fetch explains itself instead of hiding the card.
        Reading? displayedGrok = grok.Quotas.Count > 0 || prefs.TaskbarTop == "Grok" || prefs.TaskbarBottom == "Grok" ||
            (grok.Error != null && grok.Error != L.T("Grok CLIが見つかりません")) ? grok : null;
        form.UpdateReadings(codex, agy, displayedClaude, displayedGrok, busy);
        var top = ForTaskbar(prefs.TaskbarTop, displayedClaude);
        var bottom = ForTaskbar(prefs.TaskbarBottom, displayedClaude);
        widget.UpdateReadings(top, bottom);
        form.PositionAtTray();
        shownTop = top; shownBottom = bottom;
        var extra = UpdateNotice() ?? L.T("クリックで詳細");
        tray.Text = TooltipText.For(TooltipText.Title, TooltipText.Status(top), extra);
        agyTray.Text = TooltipText.For(TooltipText.Title, TooltipText.Status(bottom), extra);
    }
    private Reading ForTaskbar(string provider, Reading? displayedClaude) => provider switch
    {
        "Antigravity" => agy,
        "Claude Code" => displayedClaude ?? ClaudeCodeUsage.Waiting,
        "Grok" => grok,
        _ => codex
    };
    private void OnSessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionLock or Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect
            or Microsoft.Win32.SessionSwitchReason.RemoteDisconnect) Pause();
        else if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionUnlock or Microsoft.Win32.SessionSwitchReason.ConsoleConnect
            or Microsoft.Win32.SessionSwitchReason.RemoteConnect) Resume();
    }
    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Suspend) Pause();
        else if (e.Mode == Microsoft.Win32.PowerModes.Resume) Resume();
    }
    // System events may arrive on their own thread; timers and readings belong to the UI thread.
    private void Pause() => ui.Post(_ => { if (!closing) poll.Stop(); }, null);
    private void Resume() => ui.Post(_ => ResumePolling(), null);
    private void ResumePolling()
    {
        if (closing) return;
        poll.Stop(); poll.Start();
        // The network may need a moment after waking; the first refresh simply retries on the next tick if it fails.
        _ = Refresh();
    }
    protected override void ExitThreadCore()
    {
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        closing = true; stop.Cancel(); poll.Dispose(); hoverClose.Dispose(); updateTimer.Dispose();
        showWait.Unregister(null); showSignal.Dispose();
        tray.Visible = false; agyTray.Visible = false;
        widget.Dispose(); tray.Icon?.Dispose(); tray.Dispose(); agyTray.Icon?.Dispose(); agyTray.Dispose(); menu.Dispose(); form.Dispose();
        foreach (var slot in extraSlots) { slot.Visible = false; slot.Icon?.Dispose(); slot.Dispose(); }
        base.ExitThreadCore();
    }
}
