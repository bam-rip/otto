using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Otto;

/// Saved chats, shown in place of the conversation when the history button is pressed. Grouped by day,
/// newest first, with Otto's last words under each title and a delete button on the row under the mouse.
/// The search box at the top filters by everything said in each chat.
sealed class HistoryView : Control
{
    readonly ChatPanel owner;
    List<ChatStore.Summary> chats = new();
    string? currentId;
    float scroll;
    int contentH;
    Point mouse = new(-1, -1);
    DateTime confirmAllUntil;

    readonly CueTextBox search = new();
    readonly List<(Rectangle row, Rectangle del, string id)> rows = new();
    Rectangle rBack, rSearch, rClear, rDeleteAll;

    readonly Font heading = new("Segoe UI Light", 15f);
    readonly Font body = new("Segoe UI", 10f);
    readonly Font small = new("Segoe UI", 8.5f);
    readonly Font smallBold = new("Segoe UI Semibold", 8.5f);
    readonly Font glyph = new(ChatPanel.GlyphFont, 9f);
    const string GlyphBack = "", GlyphDelete = "", GlyphSearch = "", GlyphClear = "";
    static readonly Color FieldColor = Color.FromArgb(46, 46, 46);

    /// TextRenderer ignores Graphics transforms and clips unless told to; text drawn while scrolled needs this.
    const TextFormatFlags Scrolled = TextFormatFlags.PreserveGraphicsTranslateTransform | TextFormatFlags.PreserveGraphicsClipping;
    public event Action<string>? OpenRequested, DeleteRequested;
    public event Action? DeleteAllRequested, CloseRequested;

    public HistoryView(ChatPanel owner)
    {
        this.owner = owner;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        search.BorderStyle = BorderStyle.None;
        search.BackColor = FieldColor;
        search.ForeColor = ChatPanel.Fg;
        search.Font = new Font("Segoe UI", 10f);
        search.Cue = "Search chats";
        search.CueColor = ChatPanel.Dim;
        search.TextChanged += (_, _) => { scroll = 0; Invalidate(); };
        search.KeyDown += (_, e) =>
        {
            // Enter opens the top match (Esc is handled by the panel: see ClearSearch)
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; if (Visible_().FirstOrDefault() is { } c) Pick(c.Id); }
        };
        Controls.Add(search);
    }

    int D(int px) => owner.D(px);
    int G => owner.G;
    int ListTop => D(100);

    /// Esc while searching clears the search; true if it did.
    public bool ClearSearch()
    {
        if (search.TextLength == 0) return false;
        search.Clear();
        return true;
    }

    public void Show(List<ChatStore.Summary> list, string current)
    {
        chats = list;
        currentId = current;
        scroll = 0;
        confirmAllUntil = default;
        search.Clear();
        Visible = true;
        search.Focus();
        Invalidate();
    }

    public void Remove(string id)
    {
        chats.RemoveAll(c => c.Id == id);
        Invalidate();
    }

    string Query => search.Text.Trim();
    IEnumerable<ChatStore.Summary> Visible_() => Query.Length == 0 ? chats : chats.Where(c => c.Matches(Query));

    void Pick(string id) { if (id == currentId) CloseRequested?.Invoke(); else OpenRequested?.Invoke(id); }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        rSearch = new Rectangle(G, D(50), Width - 2 * G, D(36));
        rClear = new Rectangle(rSearch.Right - D(34), rSearch.Y, D(34), rSearch.Height);
        int textH = search.Font.Height;
        search.SetBounds(rSearch.X + D(36), rSearch.Y + (rSearch.Height - textH) / 2, rClear.Left - rSearch.X - D(40), textH);
    }

    static string Group(DateTime d) =>
        d.Date == DateTime.Today ? "Today"
        : d.Date == DateTime.Today.AddDays(-1) ? "Yesterday"
        : d.Date > DateTime.Today.AddDays(-7) ? "This week"
        : "Earlier";

    static string When(DateTime d) =>
        d.Date == DateTime.Today || d.Date == DateTime.Today.AddDays(-1) ? d.ToString("h:mm tt").ToLowerInvariant()
        : d.Date > DateTime.Today.AddDays(-7) ? d.ToString("ddd")
        : d.ToString("d MMM");

    protected override void OnPaintBackground(PaintEventArgs e) => owner.PaintBackdrop(e.Graphics);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        rows.Clear();
        int inner = Width - 2 * G;

        // ---- fixed header: back arrow, title, search box ----
        rBack = new Rectangle(G - D(10), D(4), D(32), D(32));
        if (rBack.Contains(mouse)) using (var hb = new SolidBrush(Color.FromArgb(40, 255, 255, 255))) g.FillRectangle(hb, rBack);
        TextRenderer.DrawText(g, GlyphBack, glyph, rBack, ChatPanel.Fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        using (var fg = new SolidBrush(ChatPanel.Fg)) g.DrawString("Recent chats", heading, fg, rBack.Right + D(4), D(5));

        using (var fb = new SolidBrush(FieldColor)) g.FillRectangle(fb, rSearch);
        if (search.Focused) using (var pen = new Pen(ChatPanel.Accent, D(1))) g.DrawRectangle(pen, rSearch.X, rSearch.Y, rSearch.Width - 1, rSearch.Height - 1);
        TextRenderer.DrawText(g, GlyphSearch, glyph, new Rectangle(rSearch.X + D(4), rSearch.Y, D(30), rSearch.Height), ChatPanel.Dim,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        if (search.TextLength > 0)
            TextRenderer.DrawText(g, GlyphClear, glyph, rClear, rClear.Contains(mouse) ? ChatPanel.Fg : ChatPanel.Dim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        // ---- scrolling list, clipped below the header ----
        var state = g.Save();
        g.SetClip(new Rectangle(0, ListTop, Width, Height - ListTop));
        g.TranslateTransform(0, ListTop - scroll);
        var m = InList(mouse);
        int y = 0;

        var shown = Visible_().ToList();
        if (shown.Count == 0)
            TextRenderer.DrawText(g, chats.Count == 0 ? "No saved chats yet. Chats are saved here as you go." : $"No chats mention \"{Query}\".",
                body, new Rectangle(G, y + D(4), inner, D(40)), ChatPanel.Dim, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | Scrolled);

        string? group = null;
        int rowH = D(54);
        foreach (var c in shown)
        {
            var grp = Group(c.When);
            if (grp != group)
            {
                if (group != null) y += D(10);
                group = grp;
                TextRenderer.DrawText(g, grp.ToUpperInvariant(), smallBold, new Point(G, y), ChatPanel.Dim, TextFormatFlags.NoPadding | Scrolled);
                y += D(22);
            }
            var row = new Rectangle(G, y, inner, rowH);
            var del = new Rectangle(row.Right - D(36), row.Y + (rowH - D(30)) / 2, D(30), D(30));
            bool hot = row.Contains(m), current = c.Id == currentId;
            if (hot || current)
                using (var b = new SolidBrush(Color.FromArgb(hot ? 44 : 26, 255, 255, 255))) g.FillRectangle(b, row);
            if (current)
                using (var bar = new SolidBrush(ChatPanel.Accent)) g.FillRectangle(bar, row.X, row.Y, D(3), row.Height);

            bool showDel = hot && !current;
            int textRight = row.Right - D(showDel ? 44 : 12);
            var when = current ? "Open now" : When(c.When);
            var whenW = TextRenderer.MeasureText(g, when, small, Size.Empty, TextFormatFlags.NoPadding | Scrolled).Width;
            int whenX = showDel ? del.Left - D(6) - whenW : row.Right - D(12) - whenW;
            const TextFormatFlags line = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | Scrolled | TextFormatFlags.SingleLine;
            TextRenderer.DrawText(g, c.Title, body, new Rectangle(row.X + D(14), row.Y + D(8), whenX - row.X - D(24), D(20)), ChatPanel.Fg, line);
            TextRenderer.DrawText(g, when, small, new Point(whenX, row.Y + D(11)), ChatPanel.Dim, TextFormatFlags.NoPadding | Scrolled);
            var second = (Query.Length > 0 ? c.Snippet(Query) : null) ?? c.Preview;
            TextRenderer.DrawText(g, second.Length > 0 ? second : " ", small,
                new Rectangle(row.X + D(14), row.Y + D(30), textRight - row.X - D(14), D(18)), ChatPanel.Dim, line);

            if (showDel)
            {
                if (del.Contains(m)) using (var hb = new SolidBrush(Color.FromArgb(50, 255, 255, 255))) g.FillRectangle(hb, del);
                TextRenderer.DrawText(g, GlyphDelete, glyph, del, del.Contains(m) ? ChatPanel.Danger : ChatPanel.Dim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | Scrolled);
            }
            rows.Add((row, current ? Rectangle.Empty : del, c.Id));
            y += rowH + D(4);
        }

        if (Query.Length == 0 && (chats.Count > 1 || chats.Count == 1 && chats[0].Id != currentId))
        {
            y += D(16);
            bool confirming = DateTime.Now < confirmAllUntil;
            var text = confirming ? "Click again to delete every saved chat" : "Delete all saved chats";
            var size = TextRenderer.MeasureText(g, text, small, Size.Empty, TextFormatFlags.NoPadding | Scrolled);
            rDeleteAll = new Rectangle(G, y, size.Width + D(16), D(28));
            if (rDeleteAll.Contains(m)) using (var hb = new SolidBrush(Color.FromArgb(36, 255, 255, 255))) g.FillRectangle(hb, rDeleteAll);
            TextRenderer.DrawText(g, text, small, rDeleteAll, confirming || rDeleteAll.Contains(m) ? ChatPanel.Danger : ChatPanel.Dim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | Scrolled);
            y = rDeleteAll.Bottom;
        }
        else rDeleteAll = Rectangle.Empty;

        contentH = y + D(16);
        g.Restore(state);
    }

    /// A point on the control, in list coordinates; nowhere when it's over the fixed header.
    Point InList(Point p) => p.Y < ListTop ? new Point(-1, -1) : new Point(p.X, (int)(p.Y - ListTop + scroll));

    float MaxScroll => Math.Max(0, contentH - (Height - ListTop));

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        scroll = Math.Clamp(scroll - e.Delta / 120f * D(64), 0, MaxScroll);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        mouse = e.Location;
        var m = InList(e.Location);
        bool hand = rBack.Contains(mouse) || search.TextLength > 0 && rClear.Contains(mouse)
                    || rDeleteAll.Contains(m) || rows.Any(r => r.row.Contains(m));
        Cursor = hand ? Cursors.Hand : rSearch.Contains(mouse) ? Cursors.IBeam : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e) { mouse = new Point(-1, -1); Invalidate(); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (rBack.Contains(e.Location)) { CloseRequested?.Invoke(); return; }
        if (search.TextLength > 0 && rClear.Contains(e.Location)) { search.Clear(); search.Focus(); return; }
        if (rSearch.Contains(e.Location)) { search.Focus(); return; }
        var m = InList(e.Location);
        if (rDeleteAll.Contains(m))
        {
            // a second click within a few seconds confirms; no dialog, so the panel doesn't lose focus and hide
            if (DateTime.Now < confirmAllUntil) DeleteAllRequested?.Invoke();
            else confirmAllUntil = DateTime.Now.AddSeconds(4);
            Invalidate();
            return;
        }
        foreach (var (row, del, id) in rows)
        {
            if (del.Contains(m)) { DeleteRequested?.Invoke(id); return; }
            if (row.Contains(m)) { Pick(id); return; }
        }
    }
}
