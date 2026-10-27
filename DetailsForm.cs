using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace UsageLimitViewer;

// The usage popup. Everything is painted on one surface from a layout in 96-DPI units scaled by the system DPI,
// so text and boxes grow together at any display scale. Cards can be reordered by dragging.
internal sealed class DetailsForm : Form
{
    private const int LogicalWidth = 272, HeaderHeight = 44, Pad = 8, Gap = 6, CardPad = 12;
    private const int TitleHeight = 20, LoginRow = 30, RowHeight = 48, FooterLine = 16, FoldedHeight = 34, MaxListHeight = 460;
    private static readonly Color Surface = Color.FromArgb(32, 32, 32), Border = Color.FromArgb(62, 62, 62);
    private static readonly Color CardFill = Color.FromArgb(43, 43, 43), CardHover = Color.FromArgb(50, 50, 50), CardBorder = Color.FromArgb(58, 58, 58);
    private static readonly Color Ink = Color.White, Muted = Color.FromArgb(200, 200, 200), Faint = Color.FromArgb(150, 150, 150);
    private static readonly Color Track = Color.FromArgb(64, 64, 64), Accent = Color.FromArgb(76, 194, 255), Amber = Color.FromArgb(252, 225, 0);

    private readonly Preferences prefs;
    private readonly System.Windows.Forms.Timer clock = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer fade = new() { Interval = 15 };
    private readonly System.Windows.Forms.Timer autoScroll = new() { Interval = 30 };
    private readonly ToolTip tips = new() { InitialDelay = 500 };
    private const int FadeDurationMs = 160;
    private long fadeStarted;
    private double fadeFrom, fadeTo;
    private bool openedFromHover, expanded, refreshing;
    private Reading codex = new("Codex", [], null), agy = new("Antigravity", [], null);
    private Reading? claude, grok;
    // Folded services the reader opened; cleared each time the popup opens so they start folded again.
    private readonly HashSet<string> openedProviders = [];
    private List<Card> cards = [];
    private int scroll, contentHeight, listHeight;
    private string? hoverButton, tipButton, hoverCard;
    // Drag state: the card under the pointer at mouse down, and once it moved far enough, the card being dragged.
    private Card? pressed, dragging;
    private Point pressPoint;
    private int dragOffset, dragY, dropIndex;
    internal event Action? RefreshRequested;
    // Raised after a drag changed which services sit in the first two places, i.e. the taskbar's two rows.
    internal event Action? TaskbarChanged;
    // The "Sign in" button on a card whose service needs signing in again.
    internal event Action? LoginRequested;
    private readonly Dictionary<string, Rectangle> loginButtons = [];
    private bool hoverLogin;
    internal Func<Rectangle?>? TrayBounds;

    private sealed record Card(Reading Reading, bool Foldable, bool Folded, int Height)
    {
        internal int Top { get; set; }
    }

    internal DetailsForm(Preferences preferences)
    {
        prefs = preferences;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        Text = "Usage Limit Viewer";
        AccessibleName = L.T("使用状況");
        BackColor = Surface;
        ForeColor = Ink;
        DoubleBuffered = true;
        TopMost = true;
        KeyPreview = true;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(S(LogicalWidth), S(HeaderHeight));
        clock.Tick += (_, _) => { if (Visible) { PositionAtTray(); if (expanded) Invalidate(); } };
        clock.Start();
        fade.Tick += (_, _) => AdvanceFade();
        autoScroll.Tick += (_, _) => AutoScrollWhileDragging();
        Deactivate += (_, _) => { if (!prefs.Pinned && dragging == null) CloseDetails(); };
    }

    private int S(float value) => (int)Math.Round(value * UsageLimitViewer.Compat.SystemDpi / 96f);
    private Font UiFont(float size, bool semibold = false) =>
        new(semibold ? "Segoe UI Semibold" : "Segoe UI", size * UsageLimitViewer.Compat.SystemDpi / 96f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly string IconFamily = FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2, dark = 1; // Windows 11 rounded corners with a dark frame, like the shell's own flyouts.
        DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
    }
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x80; return p; } // WS_EX_TOOLWINDOW: no Alt+Tab entry.
    }
    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); Relayout(); }

    internal void PositionAtTray()
    {
        if (TrayBounds == null) return;
        var bounds = TrayBounds();
        var area = bounds is Rectangle r ? Screen.FromRectangle(r).WorkingArea : Screen.PrimaryScreen!.WorkingArea;
        var anchor = bounds ?? new Rectangle(area.Right - 100, area.Bottom, 24, 24);
        Location = TrayPresentation.Above(anchor, Size, area);
    }
    internal void SetPinned(bool value) { prefs.Pinned = value; prefs.Save(); Invalidate(); }
    internal void UpdateReadings(Reading c, Reading a, Reading? cl, Reading? gr, bool busy)
    {
        codex = c; agy = a; claude = cl; grok = gr; refreshing = busy;
        if (dragging == null) Relayout();
        Invalidate();
    }
    internal void Reveal(bool nearTray = false, bool fromHover = false)
    {
        openedFromHover = fromHover;
        if (!Visible) { openedProviders.Clear(); scroll = 0; }
        Expand(true, false);
        PositionAtTray();
        if (!Visible) { Opacity = 0; Show(); }
        Activate();
        FadeTo(1);
    }
    internal bool IsDismissing => fade.Enabled && fadeTo == 0;
    internal void CloseDetails() { if (openedFromHover) Dismiss(); else HideImmediately(); }
    internal void HideImmediately()
    {
        fade.Stop();
        CancelDrag();
        if (Visible) Hide();
        Opacity = 1;
    }
    internal void Dismiss() { if (Visible && !IsDismissing) FadeTo(0); }
    private void FadeTo(double targetOpacity)
    {
        if (fade.Enabled && fadeTo == targetOpacity) return;
        fadeFrom = Opacity; fadeTo = targetOpacity; fadeStarted = UsageLimitViewer.Compat.TickCount64;
        fade.Start();
    }
    private void AdvanceFade()
    {
        double progress = UsageLimitViewer.Compat.Clamp((UsageLimitViewer.Compat.TickCount64 - fadeStarted) / (double)FadeDurationMs, 0, 1);
        Opacity = fadeFrom + (fadeTo - fadeFrom) * progress * progress * (3 - 2 * progress);
        if (progress < 1) return;
        fade.Stop();
        if (fadeTo == 0) { CancelDrag(); Hide(); Opacity = 1; }
    }
    internal void Expand(bool value, bool animate = true)
    {
        expanded = value;
        Relayout();
        Invalidate();
    }

    // The taskbar's top and bottom services always come first, so the list order and the taskbar agree;
    // the rest follow the order the reader dragged them into (default order until then).
    internal static IEnumerable<Reading> OrderedReadings(Preferences display, params Reading?[] readings)
    {
        var present = readings.Where(r => r != null).Cast<Reading>().ToList();
        var order = display.CardOrder ?? [];
        return present.OrderBy(r => r.Provider == display.TaskbarTop ? 0 : r.Provider == display.TaskbarBottom ? 1 : 2)
            .ThenBy(r => order.IndexOf(r.Provider) is var i && i >= 0 ? i : order.Count + present.IndexOf(r));
    }

    private int CardHeight(Reading reading, bool folded)
    {
        if (folded) return S(FoldedHeight);
        int rows = reading.Quotas.Count == 0 ? S(34) : reading.Quotas.Count * S(RowHeight) + S(FooterLine);
        if (reading.NeedsLogin && reading.Quotas.Count > 0) rows += S(LoginRow);
        return S(CardPad) + S(TitleHeight) + S(6) + rows + S(CardPad) - S(2);
    }

    private void Relayout()
    {
        if (!expanded)
        {
            cards = [];
            ClientSize = new Size(S(LogicalWidth), S(HeaderHeight));
            return;
        }
        var ordered = OrderedReadings(prefs, codex, agy, claude, grok).ToList();
        cards = ordered.Select(r =>
        {
            // A card that needs signing in stays open so its Sign in button is in view.
            bool foldable = ordered.IndexOf(r) >= 2 && !r.NeedsLogin;
            bool folded = foldable && !openedProviders.Contains(r.Provider);
            return new Card(r, foldable, folded, CardHeight(r, folded));
        }).ToList();
        int y = 0;
        foreach (var card in cards) { card.Top = y; y += card.Height + S(Gap); }
        contentHeight = Math.Max(0, y - S(Gap));
        // Size for every foldable card folded, so opening one scrolls instead of resizing the popup.
        int baseline = cards.Sum(c => c.Foldable ? S(FoldedHeight) : c.Height) + Math.Max(0, cards.Count - 1) * S(Gap);
        var area = Screen.FromRectangle(Bounds).WorkingArea;
        int maxList = Math.Min(S(MaxListHeight), area.Height - S(HeaderHeight) - S(Pad) - S(24));
        listHeight = UsageLimitViewer.Compat.Clamp(baseline, S(80), Math.Max(S(80), maxList));
        ClientSize = new Size(S(LogicalWidth), S(HeaderHeight) + listHeight + S(Pad));
        ClampScroll();
    }
    private void ClampScroll() => scroll = UsageLimitViewer.Compat.Clamp(scroll, 0, Math.Max(0, contentHeight - listHeight));
    private Rectangle ListBounds => new(S(Pad), S(HeaderHeight), ClientSize.Width - 2 * S(Pad), listHeight);

    // Header buttons, right to left: close, refresh, pin.
    private IEnumerable<(string Id, Rectangle Bounds, string Glyph, string Tip)> Buttons()
    {
        int size = S(26), top = (S(HeaderHeight) - S(26)) / 2 + S(2), right = ClientSize.Width - S(Pad) - S(CardPad) + S(6);
        yield return ("hide", new Rectangle(right - size, top, size, size), "", L.T("通知領域に収納"));
        yield return ("refresh", new Rectangle(right - 2 * size - S(2), top, size, size), "", L.T("使用状況を更新"));
        yield return ("pin", new Rectangle(right - 3 * size - S(4), top, size, size), prefs.Pinned ? "" : "", L.T("常時表示を切り替える"));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Surface);
        if (!expanded) { PaintCompact(g); return; }
        var list = ListBounds;
        loginButtons.Clear();
        var state = g.Save();
        g.SetClip(list);
        foreach (var card in cards)
        {
            if (card == dragging) continue;
            int top = list.Top + DisplayTop(card) - scroll;
            if (top > list.Bottom || top + card.Height < list.Top) continue;
            PaintCard(g, card, new Rectangle(list.Left, top, list.Width, card.Height), lifted: false);
        }
        g.Restore(state);
        // GDI text ignores the clip above, so cover anything that scrolled past the list before drawing the header.
        using (var surface = new SolidBrush(Surface))
        {
            g.FillRectangle(surface, 0, 0, ClientSize.Width, list.Top);
            g.FillRectangle(surface, 0, list.Bottom, ClientSize.Width, ClientSize.Height - list.Bottom);
        }
        using (var title = UiFont(13, true))
        {
            TextRenderer.DrawText(g, L.T("使用状況"), title, new Rectangle(S(Pad + CardPad), S(2), S(140), S(HeaderHeight)), Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            if (refreshing)
                using (var small = UiFont(11))
                    TextRenderer.DrawText(g, L.T("更新中…"), small, new Rectangle(S(Pad + CardPad + 6) + TextRenderer.MeasureText(g, L.T("使用状況"), title, Size.Empty, TextFormatFlags.NoPadding).Width, S(2), S(80), S(HeaderHeight)),
                        Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        using (var glyphs = new Font(IconFamily, 12 * UsageLimitViewer.Compat.SystemDpi / 96f, GraphicsUnit.Pixel))
            foreach (var b in Buttons())
            {
                if (hoverButton == b.Id)
                {
                    using var hover = new SolidBrush(Color.FromArgb(55, 55, 55));
                    using var shape = Rounded(b.Bounds, S(4));
                    g.FillPath(hover, shape);
                }
                var color = b.Id == "pin" && prefs.Pinned ? Accent : b.Id == "refresh" && refreshing ? Faint : Muted;
                TextRenderer.DrawText(g, b.Glyph, glyphs, b.Bounds, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

        if (dragging != null)
        {
            // The dragged card floats above the others, free of the list clip, with a soft shadow.
            var r = new Rectangle(list.Left, dragY, list.Width, dragging.Height);
            for (int i = 3; i >= 1; i--)
            {
                using var shadow = new SolidBrush(Color.FromArgb(28, 0, 0, 0));
                using var path = Rounded(new Rectangle(r.X - i, r.Y - i + S(3), r.Width + 2 * i, r.Height + 2 * i), S(8) + i);
                g.FillPath(shadow, path);
            }
            PaintCard(g, dragging, r, lifted: true);
        }
        if (contentHeight > listHeight)
        {
            int track = list.Height - S(8);
            int thumb = Math.Max(S(24), track * listHeight / contentHeight);
            int y = list.Top + S(4) + (track - thumb) * scroll / Math.Max(1, contentHeight - listHeight);
            using var bar = new SolidBrush(Color.FromArgb(110, 110, 110));
            using var path = Rounded(new Rectangle(ClientSize.Width - S(5), y, S(3), thumb), S(1.5f));
            g.FillPath(bar, path);
        }
        using var border = new Pen(Border);
        g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    // While dragging, the other cards close up around the slot where the dragged card would land.
    private int DisplayTop(Card card)
    {
        if (dragging == null) return card.Top;
        var others = cards.Where(c => c != dragging).ToList();
        int y = 0, index = 0;
        foreach (var c in others)
        {
            if (index == dropIndex) y += dragging.Height + S(Gap);
            if (c == card) return y;
            y += c.Height + S(Gap);
            index++;
        }
        return y;
    }

    private void PaintCard(Graphics g, Card card, Rectangle r, bool lifted)
    {
        var reading = card.Reading;
        using (var fill = new SolidBrush(lifted ? Color.FromArgb(56, 56, 56) : hoverCard == reading.Provider && dragging == null ? CardHover : CardFill))
        using (var edge = new Pen(lifted ? Color.FromArgb(90, 90, 90) : CardBorder))
        using (var shape = Rounded(new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), S(8)))
        {
            g.FillPath(fill, shape);
            g.DrawPath(edge, shape);
        }
        int x = r.Left + S(CardPad), right = r.Right - S(CardPad), y = r.Top + (card.Folded ? (r.Height - S(TitleHeight)) / 2 : S(CardPad));
        int place = cards.IndexOf(card);
        // A gripper shows the card can be dragged; it appears on hover so the resting card stays calm.
        if (hoverCard == reading.Provider || lifted)
            using (var dots = new SolidBrush(Faint))
                for (int row = 0; row < 3; row++)
                    for (int col = 0; col < 2; col++)
                        g.FillEllipse(dots, r.Left + S(3) + col * S(3.5f), y + S(5) + row * S(4), S(2), S(2));
        using var glyphs = new Font(IconFamily, 9 * UsageLimitViewer.Compat.SystemDpi / 96f, GraphicsUnit.Pixel);
        if (card.Foldable)
        {
            TextRenderer.DrawText(g, card.Folded ? "" : "", glyphs, new Rectangle(x, y, S(12), S(TitleHeight)), Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            x += S(16);
        }
        using var name = UiFont(12, true);
        using var small = UiFont(11);
        string side = card.Folded ? ClockTextRenderer.TaskbarValue(reading)
            : reading.Updated == null ? L.T("未接続") : reading.Stale ? L.T("前回の値") : reading.Source ?? L.T("取得元不明");
        using var sideFont = card.Folded ? UiFont(12, true) : UiFont(11);
        int sideWidth = Math.Min(S(100), TextRenderer.MeasureText(g, side, sideFont, Size.Empty, TextFormatFlags.NoPadding).Width + S(2));
        TextRenderer.DrawText(g, side, sideFont, new Rectangle(right - sideWidth, y, sideWidth, S(TitleHeight)),
            card.Folded && !reading.Stale ? Ink : Faint, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        int nameRoom = right - sideWidth - S(8) - x;
        int nameWidth = Math.Min(nameRoom, TextRenderer.MeasureText(g, reading.Provider, name, Size.Empty, TextFormatFlags.NoPadding).Width + S(1));
        TextRenderer.DrawText(g, reading.Provider, name, new Rectangle(x, y, nameWidth, S(TitleHeight)), Ink,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        // The first two cards are the taskbar's two rows; a small tag says which.
        if (place is 0 or 1)
            using (var tagFont = UiFont(9.5f))
            {
                string tag = L.T(place == 0 ? "上段" : "下段");
                var size = TextRenderer.MeasureText(g, tag, tagFont, Size.Empty, TextFormatFlags.NoPadding);
                var tagBounds = new Rectangle(x + nameWidth + S(6), y + (S(TitleHeight) - size.Height - S(2)) / 2, size.Width + S(8), size.Height + S(2));
                if (tagBounds.Right < right - sideWidth - S(4))
                {
                    using var fill = new SolidBrush(Color.FromArgb(40, Accent));
                    using var shape = Rounded(tagBounds, S(3));
                    g.FillPath(fill, shape);
                    TextRenderer.DrawText(g, tag, tagFont, tagBounds, Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
            }
        if (card.Folded) return;

        x = r.Left + S(CardPad);
        y += S(TitleHeight) + S(6);
        int width = right - x;
        if (reading.Quotas.Count == 0)
        {
            if (reading.NeedsLogin) { PaintLogin(g, reading.Provider, x, y, width, lifted); return; }
            TextRenderer.DrawText(g, reading.Error ?? L.T("接続しています…"), small, new Rectangle(x, y, width, S(30)), Faint,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            return;
        }
        using var label = UiFont(12);
        using var value = UiFont(12, true);
        foreach (var quota in reading.Quotas)
        {
            TextRenderer.DrawText(g, L.QuotaLabel(quota.Label), label, new Rectangle(x, y, width - S(56), S(17)), Ink,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, $"{quota.Remaining:0.#}%", value, new Rectangle(right - S(56), y, S(56), S(17)), reading.Stale ? Faint : Ink,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            var barBounds = new Rectangle(x, y + S(20), width, S(3));
            using (var trackBrush = new SolidBrush(Track))
            using (var trackPath = Rounded(barBounds, S(1.5f)))
                g.FillPath(trackBrush, trackPath);
            int filled = (int)Math.Round(width * UsageLimitViewer.Compat.Clamp(quota.Remaining, 0, 100) / 100);
            if (filled > 0)
                using (var barBrush = new SolidBrush(reading.Stale ? Faint : LevelColor(quota.Remaining)))
                using (var barPath = Rounded(new Rectangle(x, barBounds.Y, Math.Max(filled, S(3)), barBounds.Height), S(1.5f)))
                    g.FillPath(barBrush, barPath);
            // When on the left, time left on the right, so the line fits the narrow popup.
            var (when, remaining) = ResetParts(quota.Reset, DateTimeOffset.Now);
            int leftWidth = remaining == null ? 0 : TextRenderer.MeasureText(g, remaining, small, Size.Empty, TextFormatFlags.NoPadding).Width + S(2);
            if (remaining != null)
                TextRenderer.DrawText(g, remaining, small, new Rectangle(right - leftWidth, y + S(25), leftWidth, S(15)), Muted,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, when, small, new Rectangle(x, y + S(25), width - leftWidth - S(6), S(15)), Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            y += S(RowHeight);
        }
        var age = reading.Updated?.ToLocalTime().ToString("MM/dd HH:mm:ss") ?? "—";
        TextRenderer.DrawText(g, L.F("取得 {age}", ("age", age)) + (reading.Error == null || reading.NeedsLogin ? "" : L.T(" · 更新できません")), small,
            new Rectangle(x, y, width, S(15)), Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        if (reading.NeedsLogin) PaintLogin(g, reading.Provider, x, y + S(FooterLine), width, lifted);
    }

    // "The sign-in has run out" with a Sign in button on the right. The cursor stays the arrow (it is a button, not a link).
    private void PaintLogin(Graphics g, string provider, int x, int y, int width, bool lifted)
    {
        using var small = UiFont(11);
        using var label = UiFont(11, true);
        string text = L.T("ログイン");
        int buttonWidth = TextRenderer.MeasureText(g, text, label, Size.Empty, TextFormatFlags.NoPadding).Width + S(20);
        var button = new Rectangle(x + width - buttonWidth, y + S(3), buttonWidth, S(LoginRow - 6));
        TextRenderer.DrawText(g, L.T("ログインが切れています"), small, new Rectangle(x, y, width - buttonWidth - S(8), S(LoginRow)), Amber,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        bool hover = hoverLogin && !lifted && loginButtons.TryGetValue(provider, out var last) && last == button;
        using (var fill = new SolidBrush(hover ? Color.FromArgb(100, 206, 255) : Accent))
        using (var shape = Rounded(button, S(4)))
            g.FillPath(fill, shape);
        TextRenderer.DrawText(g, text, label, button, Color.Black, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        if (!lifted) loginButtons[provider] = button;
    }

    // Plenty left: accent blue; getting low: amber; nearly gone: red.
    private static Color LevelColor(double remaining) =>
        remaining >= 30 ? Accent : remaining >= 10 ? Color.FromArgb(252, 225, 0) : Color.FromArgb(255, 153, 164);

    private void PaintCompact(Graphics g)
    {
        using var font = UiFont(12);
        using var bold = UiFont(12, true);
        int y = S(HeaderHeight) / 2 - S(10);
        TextRenderer.DrawText(g, "Codex", font, new Point(S(14), y), Muted, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, codex.Compact, bold, new Point(S(62), y), Ink, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, "Antigravity", font, new Point(S(132), y), Muted, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, agy.Compact, bold, new Point(S(214), y), Ink, TextFormatFlags.NoPadding);
        using var border = new Pen(Border);
        g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    // The provider whose Sign in button is under the point (buttons are only on cards that are on screen).
    private string? LoginButtonAt(Point p) => ListBounds.Contains(p) ? loginButtons.FirstOrDefault(b => b.Value.Contains(p)).Key : null;

    private Card? CardAt(Point p)
    {
        var list = ListBounds;
        if (!list.Contains(p)) return null;
        int y = p.Y - list.Top + scroll;
        return cards.FirstOrDefault(c => y >= c.Top && y < c.Top + c.Height);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging != null) { MoveDrag(e.Location); return; }
        if (pressed != null && e.Button == MouseButtons.Left)
        {
            var drag = SystemInformation.DragSize;
            if (Math.Abs(e.Y - pressPoint.Y) > drag.Height || Math.Abs(e.X - pressPoint.X) > drag.Width) { StartDrag(); MoveDrag(e.Location); }
            return;
        }
        var button = expanded ? Buttons().FirstOrDefault(b => b.Bounds.Contains(e.Location)).Id : null;
        var card = expanded ? CardAt(e.Location)?.Reading.Provider : null;
        bool overLogin = expanded && LoginButtonAt(e.Location) != null;
        if (button != hoverButton || card != hoverCard || overLogin != hoverLogin) { hoverButton = button; hoverCard = card; hoverLogin = overLogin; Invalidate(); }
        if (button != tipButton)
        {
            tipButton = button;
            var tip = Buttons().FirstOrDefault(b => b.Id == button).Tip;
            if (tip != null) tips.SetToolTip(this, tip); else tips.SetToolTip(this, null);
        }
    }
    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (dragging == null && (hoverButton != null || hoverCard != null)) { hoverButton = hoverCard = null; Invalidate(); }
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (!expanded) { Expand(true); PositionAtTray(); return; }
        pressed = CardAt(e.Location);
        pressPoint = e.Location;
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        if (dragging != null) { FinishDrag(); return; }
        var card = pressed;
        pressed = null;
        if (card != null && LoginButtonAt(e.Location) == card.Reading.Provider && LoginButtonAt(pressPoint) == card.Reading.Provider)
        {
            LoginRequested?.Invoke();
            return;
        }
        if (card != null && card == CardAt(e.Location))
        {
            if (card.Foldable) ToggleFold(card);
            return;
        }
        switch (Buttons().FirstOrDefault(b => b.Bounds.Contains(e.Location)).Id)
        {
            case "pin": SetPinned(!prefs.Pinned); break;
            case "refresh": RefreshRequested?.Invoke(); break;
            case "hide": CloseDetails(); break;
        }
    }
    private void ToggleFold(Card card)
    {
        var provider = card.Reading.Provider;
        bool opening = !openedProviders.Remove(provider);
        if (opening) openedProviders.Add(provider);
        Relayout();
        // Bring a card that just opened fully into view.
        var opened = cards.FirstOrDefault(c => c.Reading.Provider == provider);
        if (opening && opened != null && opened.Top + opened.Height > scroll + listHeight)
            scroll = Math.Min(opened.Top, opened.Top + opened.Height - listHeight);
        ClampScroll();
        Invalidate();
    }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!expanded || contentHeight <= listHeight) return;
        scroll -= e.Delta * S(48) / 120;
        ClampScroll();
        if (dragging != null) MoveDrag(PointToClient(Cursor.Position));
        Invalidate();
    }

    private void StartDrag()
    {
        dragging = pressed;
        pressed = null;
        if (dragging == null) return;
        dragOffset = pressPoint.Y - (ListBounds.Top + dragging.Top - scroll);
        dropIndex = cards.IndexOf(dragging);
        Capture = true;
        autoScroll.Start();
    }
    private void MoveDrag(Point p)
    {
        if (dragging == null) return;
        var list = ListBounds;
        dragY = UsageLimitViewer.Compat.Clamp(p.Y - dragOffset, list.Top - dragging.Height / 2, list.Bottom - dragging.Height / 2);
        // Drop slot: before the first remaining card whose middle lies below the dragged card's middle.
        int middle = dragY - list.Top + scroll + dragging.Height / 2, y = 0, index = 0;
        foreach (var c in cards.Where(c => c != dragging))
        {
            if (middle < y + c.Height / 2) break;
            y += c.Height + S(Gap);
            index++;
        }
        dropIndex = index;
        Invalidate();
    }
    private void AutoScrollWhileDragging()
    {
        if (dragging == null) { autoScroll.Stop(); return; }
        var p = PointToClient(Cursor.Position);
        var list = ListBounds;
        int step = p.Y < list.Top + S(24) ? -S(12) : p.Y > list.Bottom - S(24) ? S(12) : 0;
        if (step == 0) return;
        int before = scroll;
        scroll += step;
        ClampScroll();
        if (scroll != before) MoveDrag(p);
    }
    private void FinishDrag()
    {
        var card = dragging!;
        var order = cards.Where(c => c != card).Select(c => c.Reading.Provider).ToList();
        order.Insert(UsageLimitViewer.Compat.Clamp(dropIndex, 0, order.Count), card.Reading.Provider);
        // Keep services that are not shown right now (for example Grok without its CLI) in their saved place at the end.
        foreach (var provider in prefs.CardOrder ?? []) if (!order.Contains(provider)) order.Add(provider);
        prefs.CardOrder = order;
        // The first two places are the taskbar's top and bottom rows.
        var visible = cards.Select(c => c.Reading.Provider).ToHashSet();
        var shown = order.Where(visible.Contains).ToList();
        bool taskbarChanged = shown.Count >= 2 && (shown[0] != prefs.TaskbarTop || shown[1] != prefs.TaskbarBottom);
        if (taskbarChanged) { prefs.TaskbarTop = shown[0]; prefs.TaskbarBottom = shown[1]; }
        prefs.Save();
        if (taskbarChanged) TaskbarChanged?.Invoke();
        CancelDrag();
        Relayout();
        Invalidate();
    }
    private void CancelDrag()
    {
        if (dragging == null && pressed == null) return;
        dragging = pressed = null;
        autoScroll.Stop();
        Capture = false;
        Invalidate();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            if (dragging != null) CancelDrag();
            else if (expanded && prefs.Pinned) Expand(false);
            else CloseDetails();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    internal static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath(); float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.Left, r.Top, d, d, 180, 90); p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p;
    }
    internal static void DrawText(Graphics g, string text, int x, int y, float size, Color color)
    {
        using var f = new Font("Segoe UI", size * g.DpiY / 96f, FontStyle.Regular, GraphicsUnit.Pixel);
        TextRenderer.DrawText(g, text, f, new Point(x, y), color, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { clock.Dispose(); fade.Dispose(); autoScroll.Dispose(); tips.Dispose(); }
        base.Dispose(disposing);
    }

    // One line per quota: the local reset date with weekday first, then the time left, e.g. "10月3日(土) 18:00 にリセット（あと6日3時間）".
    internal static string FormatReset(DateTimeOffset? reset, DateTimeOffset now)
    {
        if (reset == null) return L.T("リセット時刻不明");
        var left = reset.Value - now;
        if (left <= TimeSpan.Zero) return L.T("リセット時刻経過 · 更新待ち");
        // Services report resets like 17:59:59.9; round to the minute so it reads 18:00 as the Claude app does.
        var at = new DateTimeOffset((reset.Value.UtcTicks + TimeSpan.TicksPerMinute / 2) / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute, TimeSpan.Zero).ToLocalTime();
        var today = now.ToLocalTime().Date;
        var ja = System.Globalization.CultureInfo.GetCultureInfo("ja-JP");
        var en = System.Globalization.CultureInfo.InvariantCulture;
        string day = at.Date == today ? L.T("今日")
            : at.Date == today.AddDays(1) ? L.T("明日")
            : L.English ? at.ToString(at.Year == today.Year ? "ddd, MMM d" : "ddd, MMM d, yyyy", en)
            : at.ToString(at.Year == today.Year ? "M月d日(ddd)" : "yyyy年M月d日(ddd)", ja);
        return L.F("{day} {time} にリセット（あと{left}）", ("day", day), ("time", at.ToString("H:mm", en)), ("left", FormatRemaining(at > now ? at - now : left)));
    }
    // The same reset split in two: "リセット 10月24日(土) 18:00" and "あと4日1時間" (null when already reset or unknown).
    internal static (string When, string? Left) ResetParts(DateTimeOffset? reset, DateTimeOffset now)
    {
        if (reset == null || reset.Value <= now) return (FormatReset(reset, now), null);
        var at = new DateTimeOffset((reset.Value.UtcTicks + TimeSpan.TicksPerMinute / 2) / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute, TimeSpan.Zero).ToLocalTime();
        var today = now.ToLocalTime().Date;
        var en = System.Globalization.CultureInfo.InvariantCulture;
        string day = at.Date == today ? L.T("今日")
            : at.Date == today.AddDays(1) ? L.T("明日")
            : L.English ? at.ToString(at.Year == today.Year ? "ddd, MMM d" : "ddd, MMM d, yyyy", en)
            : at.ToString(at.Year == today.Year ? "M月d日(ddd)" : "yyyy年M月d日(ddd)", System.Globalization.CultureInfo.GetCultureInfo("ja-JP"));
        return (L.F("リセット {day} {time}", ("day", day), ("time", at.ToString("H:mm", en))),
            L.F("あと{left}", ("left", FormatRemaining(at > now ? at - now : reset.Value - now))));
    }
    internal static string FormatRemaining(TimeSpan t) =>
        t.Days > 0 ? L.F("{d}日{h}時間", ("d", t.Days), ("h", t.Hours))
        : t.Hours > 0 ? L.F("{h}時間{m}分", ("h", t.Hours), ("m", t.Minutes))
        : L.F("{m}分", ("m", Math.Max(1, t.Minutes)));
}
