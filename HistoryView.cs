using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Otto;

/// Saved chats, shown in place of the conversation when the history button is pressed. Grouped by day,
/// newest first, with Otto's last words under each title and a delete button on the row under the mouse.
sealed class HistoryView : Control
{
    readonly ChatPanel owner;
    List<ChatStore.Summary> chats = new();
    string? currentId;
    float scroll;
    int contentH;
    Point mouse = new(-1, -1);
    DateTime confirmAllUntil;

    readonly List<(Rectangle row, Rectangle del, string id)> rows = new();
    Rectangle rBack, rDeleteAll;

    readonly Font heading = new("Segoe UI Light", 15f);
    readonly Font body = new("Segoe UI", 10f);
    readonly Font small = new("Segoe UI", 8.5f);
    readonly Font smallBold = new("Segoe UI Semibold", 8.5f);
    readonly Font glyph = new(ChatPanel.GlyphFont, 9f);
    const string GlyphBack = "", GlyphDelete = "";

    public event Action<string>? OpenRequested, DeleteRequested;
    public event Action? DeleteAllRequested, CloseRequested;

    public HistoryView(ChatPanel owner)
    {
        this.owner = owner;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    int D(int px) => owner.D(px);
    int G => owner.G;

    public void Show(List<ChatStore.Summary> list, string current)
    {
        chats = list;
        currentId = current;
        scroll = 0;
        confirmAllUntil = default;
        Visible = true;
        Invalidate();
    }

    public void Remove(string id)
    {
        chats.RemoveAll(c => c.Id == id);
        Invalidate();
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
        var m = new Point(mouse.X, (int)(mouse.Y + scroll));

        var state = g.Save();
        g.TranslateTransform(0, -scroll);
        int y = D(4);

        // back arrow + heading
        rBack = new Rectangle(G - D(10), y, D(32), D(32));
        if (rBack.Contains(m)) using (var hb = new SolidBrush(Color.FromArgb(40, 255, 255, 255))) g.FillRectangle(hb, rBack);
        TextRenderer.DrawText(g, GlyphBack, glyph, rBack, ChatPanel.Fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        using (var fg = new SolidBrush(ChatPanel.Fg)) g.DrawString("Recent chats", heading, fg, rBack.Right + D(4), y + D(1));
        y += D(48);

        if (chats.Count == 0)
            TextRenderer.DrawText(g, "No saved chats yet. Chats are saved here as you go.", body, new Rectangle(G, y, inner, D(40)), ChatPanel.Dim,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

        string? group = null;
        int rowH = D(54);
        foreach (var c in chats)
        {
            var grp = Group(c.When);
            if (grp != group)
            {
                if (group != null) y += D(10);
                group = grp;
                TextRenderer.DrawText(g, grp.ToUpperInvariant(), smallBold, new Point(G, y), ChatPanel.Dim, TextFormatFlags.NoPadding);
                y += D(22);
            }
            var row = new Rectangle(G, y, inner, rowH);
            var del = new Rectangle(row.Right - D(36), row.Y + (rowH - D(30)) / 2, D(30), D(30));
            bool hot = row.Contains(m), current = c.Id == currentId;
            if (hot || current)
                using (var b = new SolidBrush(Color.FromArgb(hot ? 44 : 26, 255, 255, 255))) g.FillRectangle(b, row);
            if (current)
                using (var bar = new SolidBrush(ChatPanel.Accent)) g.FillRectangle(bar, row.X, row.Y, D(3), row.Height);

            int textRight = row.Right - D(hot && !current ? 44 : 12);
            var when = current ? "Open now" : When(c.When);
            var whenW = TextRenderer.MeasureText(g, when, small, Size.Empty, TextFormatFlags.NoPadding).Width;
            int whenX = hot && !current ? del.Left - D(6) - whenW : row.Right - D(12) - whenW;
            const TextFormatFlags line = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            TextRenderer.DrawText(g, c.Title, body, new Rectangle(row.X + D(14), row.Y + D(8), whenX - row.X - D(24), D(20)), ChatPanel.Fg, line);
            TextRenderer.DrawText(g, when, small, new Point(whenX, row.Y + D(11)), ChatPanel.Dim, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, c.Preview.Length > 0 ? c.Preview : " ", small,
                new Rectangle(row.X + D(14), row.Y + D(30), textRight - row.X - D(14), D(18)), ChatPanel.Dim, line);

            if (hot && !current)
            {
                if (del.Contains(m)) using (var hb = new SolidBrush(Color.FromArgb(50, 255, 255, 255))) g.FillRectangle(hb, del);
                TextRenderer.DrawText(g, GlyphDelete, glyph, del, del.Contains(m) ? ChatPanel.Danger : ChatPanel.Dim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            rows.Add((row, current ? Rectangle.Empty : del, c.Id));
            y += rowH + D(4);
        }

        if (chats.Count > 1 || chats.Count == 1 && chats[0].Id != currentId)
        {
            y += D(16);
            bool confirming = DateTime.Now < confirmAllUntil;
            var text = confirming ? "Click again to delete every saved chat" : "Delete all saved chats";
            var size = TextRenderer.MeasureText(g, text, small, Size.Empty, TextFormatFlags.NoPadding);
            rDeleteAll = new Rectangle(G, y, size.Width + D(16), D(28));
            if (rDeleteAll.Contains(m)) using (var hb = new SolidBrush(Color.FromArgb(36, 255, 255, 255))) g.FillRectangle(hb, rDeleteAll);
            TextRenderer.DrawText(g, text, small, rDeleteAll, confirming || rDeleteAll.Contains(m) ? ChatPanel.Danger : ChatPanel.Dim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            y = rDeleteAll.Bottom;
        }
        else rDeleteAll = Rectangle.Empty;

        contentH = y + D(16);
        g.Restore(state);
    }

    float MaxScroll => Math.Max(0, contentH - Height);

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        scroll = Math.Clamp(scroll - e.Delta / 120f * D(64), 0, MaxScroll);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        mouse = e.Location;
        var m = new Point(e.X, (int)(e.Y + scroll));
        Cursor = rBack.Contains(m) || rDeleteAll.Contains(m) || rows.Any(r => r.row.Contains(m)) ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e) { mouse = new Point(-1, -1); Invalidate(); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var m = new Point(e.X, (int)(e.Y + scroll));
        if (rBack.Contains(m)) { CloseRequested?.Invoke(); return; }
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
            if (row.Contains(m)) { if (id == currentId) CloseRequested?.Invoke(); else OpenRequested?.Invoke(id); return; }
        }
    }
}
