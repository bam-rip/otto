using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Otto;

/// The conversation, drawn by hand: bubbles, tool notes, Allow/Deny cards, a typing indicator and the
/// start screen. Owner-drawn so spacing is exact and things can animate (slide/fade in, typewriter replies).
sealed class ChatView : Control
{
    abstract class Item
    {
        public readonly DateTime Born = DateTime.Now;
        public Rectangle Box; // content coordinates (before scrolling)
    }
    sealed class Msg : Item { public string Text = ""; public bool User, Reveal; }
    sealed class Note : Item { public string Text = ""; public bool Tool; }
    sealed class Ask : Item
    {
        public string Text = "";
        public TaskCompletionSource<bool> Tcs = null!;
        public bool? Answer;
        public Rectangle Allow, Deny;
    }

    // Examples that show the range of what Otto can do; four are picked at random for each new chat.
    static readonly string[] Pool =
    {
        "Tidy my Downloads folder into subfolders by file type",
        "Make a Word doc with a one-page summary of my newest PDF",
        "Switch Windows to dark mode and turn on night light",
        "Find a cheap flight to Sydney next Friday and show me the options",
        "Open Spotify and play something chill",
        "Make a to-do list for this week and save it to my desktop",
        "What's using the most space on my C: drive?",
        "Search the web for today's weather and tell me if I need a jacket",
        "Rename the photos in my Pictures folder by the date they were taken",
        "Write a short polite email asking my landlord to fix the heater",
        "Install VLC for me",
        "Close every window except this one",
    };
    string[] suggestions = Pick();

    static string[] Pick() => Pool.OrderBy(_ => Random.Shared.Next()).Take(4).ToArray();
    static readonly (string keys, string what)[] Hotkeys =
    {
        ("Ctrl+Shift+J", "open or hide Otto"),
        ("Ctrl+Alt+J", "talk"),
        ("Ctrl+Alt+End", "stop everything"),
    };

    readonly ChatPanel owner;
    readonly List<Item> items = new();
    readonly List<(Rectangle r, string text)> suggestionRects = new();
    readonly Font body = new("Segoe UI", 10.5f);
    readonly Font small = new("Segoe UI", 8.5f);
    readonly Font smallBold = new("Segoe UI Semibold", 8.5f);
    readonly Font heading = new("Segoe UI Light", 15f);
    readonly Font glyph = new(ChatPanel.GlyphFont, 9f);
    readonly StringFormat wrap = new() { Trimming = StringTrimming.None };
    bool dirty = true, stickToBottom = true;
    float scroll, scrollTarget;
    int contentH;
    Point mouse = new(-1, -1);
    Rectangle typingBox;
    DateTime typingSince;
    bool showTyping;

    public event Action<string>? SuggestionClicked;
    /// Retry: regenerate Otto's last reply. Edit: take back your last message to change it.
    public event Action? RetryRequested, EditRequested, UndoRequested;
    /// Set by the panel while a request runs; retry/edit wait until it's done.
    public bool Busy { get; set; }

    // small buttons that appear beside a bubble while the mouse is over it
    const string GlyphCopy = "\uE8C8", GlyphRetry = "\uE72C", GlyphEdit = "\uE70F", GlyphUndo = "\uE7A7";

    /// Did Otto use tools (i.e. maybe change something) since your last message?
    bool LastTurnActed()
    {
        int lastUser = items.FindLastIndex(it => it is Msg { User: true });
        return lastUser >= 0 && items.Skip(lastUser).Any(it => it is Note { Tool: true });
    }
    /// TextRenderer ignores Graphics transforms and clips unless told to; text drawn while scrolled needs this.
    const TextFormatFlags Scrolled = TextFormatFlags.PreserveGraphicsTranslateTransform | TextFormatFlags.PreserveGraphicsClipping;
    readonly List<(Rectangle r, string glyph, Action act, string tip)> actionRects = new();
    readonly ToolTip tips = new() { InitialDelay = 400 };
    string? shownTip;

    public ChatView(ChatPanel owner)
    {
        this.owner = owner;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    int D(int px) => owner.D(px);
    int G => owner.G;
    int Inner => Width - 2 * G;

    public bool HasPendingConfirm => items.OfType<Ask>().Any(a => a.Answer == null);

    public bool ShowTyping
    {
        get => showTyping;
        set { if (showTyping == value) return; showTyping = value; typingSince = DateTime.Now; Changed(); }
    }

    // ---------------- content ----------------

    void Changed()
    {
        dirty = true;
        stickToBottom = true;
        Invalidate();
        owner.Kick();
    }

    /// reveal: type the text out letter by letter (for replies that arrive whole while animation is on)
    public void AddMessage(string text, bool user, bool reveal = false) { items.Add(new Msg { Text = text.Trim(), User = user, Reveal = reveal && !user }); Changed(); }

    Msg? streaming;

    /// Word-by-word replies: the first chunk adds a bubble, later chunks just grow its text.
    public void StreamMessage(string textSoFar, bool newBubble)
    {
        if (newBubble || streaming == null || items.Count == 0 || items[^1] != streaming)
        {
            streaming = new Msg { User = false };
            items.Add(streaming);
        }
        streaming.Text = textSoFar.Trim();
        Changed();
    }
    public void AddNote(string text, bool tool) { items.Add(new Note { Text = text.Trim(), Tool = tool }); Changed(); }

    public void AddAsk(string text, TaskCompletionSource<bool> tcs)
    {
        var ask = new Ask { Text = text.Trim(), Tcs = tcs };
        items.Add(ask);
        // runs however it gets answered: a click or the kill switch
        tcs.Task.ContinueWith(t => BeginInvoke(() => { ask.Answer = t.Result; Changed(); }));
        Changed();
    }

    public void CancelAsks()
    {
        foreach (var a in items.OfType<Ask>()) a.Tcs.TrySetResult(false);
    }

    /// Removes your last message and everything after it (for retry and edit). Returns its text.
    public string? RemoveLastTurn()
    {
        int i = items.FindLastIndex(it => it is Msg { User: true });
        if (i < 0) return null;
        var text = ((Msg)items[i]).Text;
        items.RemoveRange(i, items.Count - i);
        streaming = null;
        Changed();
        return text;
    }

    public void Clear()
    {
        CancelAsks();
        items.Clear();
        suggestions = Pick();
        scroll = scrollTarget = 0;
        Changed();
    }

    // ---------------- layout ----------------

    protected override void OnResize(EventArgs e) { base.OnResize(e); dirty = true; }

    const int PadX = 12, PadY = 9;
    /// Room kept under every message for its hover buttons, so showing them never moves anything.
    const int ActionRow = 26;

    void LayoutItems(Graphics g)
    {
        int y = D(6);
        Item? prev = null;
        foreach (var it in items)
        {
            // more air between turns than between a turn and its tool notes
            // (a message's action row already leaves some room under it)
            y += prev == null ? 0 : prev is Msg ? D(2) : it is Note ? D(4) : D(12);
            switch (it)
            {
                case Msg m:
                {
                    int maxText = (int)(Inner * 0.86) - 2 * D(PadX);
                    var size = g.MeasureString(m.Text.Length == 0 ? " " : m.Text, body, maxText, wrap);
                    int bw = (int)Math.Ceiling(size.Width) + 2 * D(PadX);
                    int bh = (int)Math.Ceiling(size.Height) + 2 * D(PadY);
                    m.Box = new Rectangle(m.User ? Width - G - bw : G, y, bw, bh);
                    break;
                }
                case Note n:
                {
                    var size = g.MeasureString(n.Text, small, Inner - D(18), wrap);
                    n.Box = new Rectangle(G, y, Inner, (int)Math.Ceiling(size.Height));
                    break;
                }
                case Ask a:
                {
                    var size = g.MeasureString(a.Text, body, Inner - 2 * D(14), wrap);
                    int textBottom = y + D(12) + (int)Math.Ceiling(size.Height);
                    a.Allow = new Rectangle(G + D(14), textBottom + D(12), D(96), D(32));
                    a.Deny = new Rectangle(a.Allow.Right + D(8), a.Allow.Top, D(96), D(32));
                    int bottom = a.Answer == null ? a.Allow.Bottom : textBottom + D(8) + D(18);
                    a.Box = new Rectangle(G, y, Inner, bottom + D(14) - y);
                    break;
                }
            }
            y = it.Box.Bottom + (it is Msg ? D(ActionRow) : 0);
            prev = it;
        }
        if (showTyping)
        {
            y += items.Count == 0 ? 0 : D(12);
            typingBox = new Rectangle(G, y, D(58), D(34));
            y = typingBox.Bottom;
        }
        contentH = y + D(10);
        dirty = false;
        ClampScroll();
        if (stickToBottom) scrollTarget = MaxScroll;
    }

    float MaxScroll => Math.Max(0, contentH - Height);
    void ClampScroll() { scrollTarget = Math.Clamp(scrollTarget, 0, MaxScroll); }

    // ---------------- animation ----------------

    static double Ease(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);
    static double Age(Item it) => (DateTime.Now - it.Born).TotalMilliseconds;
    static double TypeDuration(Msg m) => Math.Clamp(m.Text.Length * 7.0, 250, 1600);

    /// Advances animations; returns true while anything is still moving.
    public bool Animate()
    {
        bool more = showTyping || Math.Abs(scrollTarget - scroll) > 0.5f;
        scroll += (scrollTarget - scroll) * 0.28f;
        if (Math.Abs(scrollTarget - scroll) <= 0.5f) scroll = scrollTarget;
        foreach (var it in items)
        {
            double age = Age(it);
            if (age < 300 || it is Msg { Reveal: true } m && age < TypeDuration(m)) { more = true; break; }
        }
        if (more) Invalidate();
        return more;
    }

    // ---------------- painting ----------------

    protected override void OnPaintBackground(PaintEventArgs e) => owner.PaintBackdrop(e.Graphics);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        if (dirty) LayoutItems(g);

        if (items.Count == 0 && !showTyping) { DrawStart(g); return; }
        suggestionRects.Clear();

        var state = g.Save();
        g.TranslateTransform(0, -scroll);
        foreach (var it in items)
        {
            if (it.Box.Bottom < scroll || it.Box.Top > scroll + Height) continue;
            double t = Ease(Age(it) / 260);
            int alpha = (int)(255 * t);
            var dy = (float)((1 - t) * D(10));
            var s = g.Save();
            g.TranslateTransform(0, dy);
            switch (it)
            {
                case Msg m: DrawMsg(g, m, alpha); break;
                case Note n: DrawNote(g, n, alpha); break;
                case Ask a: DrawAsk(g, a, alpha); break;
            }
            g.Restore(s);
        }
        if (showTyping) DrawTyping(g);
        DrawActions(g);
        g.Restore(state);

        // thin overlay scrollbar, only when there's something to scroll
        if (contentH > Height)
        {
            int track = Height - D(8);
            int thumbH = Math.Max(D(30), (int)(track * (Height / (float)contentH)));
            int thumbY = D(4) + (int)((track - thumbH) * (scroll / MaxScroll));
            using var b = new SolidBrush(Color.FromArgb(mouse.X > Width - D(14) ? 130 : 60, 255, 255, 255));
            g.FillRectangle(b, Width - D(7), thumbY, D(3), thumbH);
        }
    }

    void DrawMsg(Graphics g, Msg m, int alpha)
    {
        var back = m.User ? ChatPanel.Accent : Color.FromArgb(40, 255, 255, 255);
        using (var b = new SolidBrush(Color.FromArgb(back.A * alpha / 255, back))) g.FillRectangle(b, m.Box);
        var text = m.Text;
        if (m.Reveal) // typewriter
        {
            int chars = (int)Math.Ceiling(text.Length * Math.Min(1, Age(m) / TypeDuration(m)));
            text = text[..Math.Clamp(chars, 0, text.Length)];
        }
        var r = new RectangleF(m.Box.X + D(PadX), m.Box.Y + D(PadY), m.Box.Width - 2 * D(PadX) + 2, m.Box.Height - 2 * D(PadY) + 2);
        using var fg = new SolidBrush(Color.FromArgb(alpha, ChatPanel.Fg));
        g.DrawString(text, body, fg, r, wrap);
    }

    void DrawNote(Graphics g, Note n, int alpha)
    {
        using var dim = new SolidBrush(Color.FromArgb(alpha * 200 / 255, ChatPanel.Dim));
        if (n.Tool) g.DrawString("", glyph, dim, n.Box.X - D(1), n.Box.Y + D(2)); // chevron
        var x = n.Tool ? n.Box.X + D(18) : n.Box.X;
        g.DrawString(n.Text, small, dim, new RectangleF(x, n.Box.Y, n.Box.Right - x, n.Box.Height + 2), wrap);
    }

    void DrawAsk(Graphics g, Ask a, int alpha)
    {
        using (var b = new SolidBrush(Color.FromArgb(34 * alpha / 255, 255, 255, 255))) g.FillRectangle(b, a.Box);
        using (var bar = new SolidBrush(Color.FromArgb(alpha, a.Answer == false ? ChatPanel.Danger : ChatPanel.Accent)))
            g.FillRectangle(bar, a.Box.X, a.Box.Y, D(3), a.Box.Height);
        using var fg = new SolidBrush(Color.FromArgb(alpha, ChatPanel.Fg));
        g.DrawString(a.Text, body, fg, new RectangleF(a.Box.X + D(14), a.Box.Y + D(12), a.Box.Width - 2 * D(14) + 2, a.Box.Height), wrap);
        if (a.Answer == null)
        {
            DrawButton(g, a.Allow, "Allow", ChatPanel.Accent, alpha);
            DrawButton(g, a.Deny, "Deny", Color.FromArgb(60, 255, 255, 255), alpha);
        }
        else
        {
            using var dim = new SolidBrush(Color.FromArgb(alpha, ChatPanel.Dim));
            g.DrawString(a.Answer == true ? "Allowed" : "Denied", small, dim, a.Box.X + D(14), a.Box.Bottom - D(14) - D(18));
        }
    }

    void DrawButton(Graphics g, Rectangle r, string text, Color back, int alpha)
    {
        bool hover = r.Contains(mouse.X, (int)(mouse.Y + scroll));
        var c = hover ? ControlPaint.Light(back, 0.25f) : back;
        using (var b = new SolidBrush(Color.FromArgb(c.A * alpha / 255, c))) g.FillRectangle(b, r);
        using var fg = new SolidBrush(Color.FromArgb(alpha, ChatPanel.Fg));
        using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, body, fg, r, center);
    }

    void DrawTyping(Graphics g)
    {
        double t = Ease((DateTime.Now - typingSince).TotalMilliseconds / 260);
        using (var b = new SolidBrush(Color.FromArgb((int)(40 * t), 255, 255, 255))) g.FillRectangle(b, typingBox);
        double secs = DateTime.Now.TimeOfDay.TotalSeconds;
        int size = D(6), gap = D(6);
        int x0 = typingBox.X + (typingBox.Width - (3 * size + 2 * gap)) / 2;
        for (int i = 0; i < 3; i++)
        {
            double phase = Math.Sin(secs * 6 - i * 0.7);
            int a = (int)(t * (110 + 145 * Math.Max(0, phase)));
            float lift = (float)(Math.Max(0, phase) * D(3));
            using var dot = new SolidBrush(Color.FromArgb(a, 255, 255, 255));
            g.FillEllipse(dot, x0 + i * (size + gap), typingBox.Y + (typingBox.Height - size) / 2f - lift, size, size);
        }
    }

    void DrawStart(Graphics g)
    {
        suggestionRects.Clear();
        int y = D(20);
        using var fg = new SolidBrush(ChatPanel.Fg);
        using var dim = new SolidBrush(ChatPanel.Dim);
        g.DrawString("How can I help?", heading, fg, G - D(3), y);
        y += D(40);
        const string intro = "I can use your PC the way you do: open apps, click and type, sort files, browse the web, change settings and write documents. Try one of these, or just ask.";
        const TextFormatFlags flow = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;
        var introSize = TextRenderer.MeasureText(g, intro, body, new Size(Inner, int.MaxValue), flow);
        TextRenderer.DrawText(g, intro, body, new Rectangle(G, y, Inner, introSize.Height), ChatPanel.Dim, flow);
        y += introSize.Height + D(20);

        foreach (var s in suggestions)
        {
            var r = new Rectangle(G, y, Inner, D(44));
            suggestionRects.Add((r, s));
            using (var b = new SolidBrush(Color.FromArgb(r.Contains(mouse) ? 52 : 30, 255, 255, 255))) g.FillRectangle(b, r);
            TextRenderer.DrawText(g, s, body, new Rectangle(r.X + D(14), r.Y, r.Width - D(48), r.Height), ChatPanel.Fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "", glyph, new Rectangle(r.Right - D(34), r.Y, D(20), r.Height), ChatPanel.Dim,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            y += D(44) + D(8);
        }

        y += D(20);
        foreach (var (keys, what) in Hotkeys)
        {
            TextRenderer.DrawText(g, keys, smallBold, new Point(G, y), ChatPanel.Fg, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, what, small, new Point(G + D(104), y), ChatPanel.Dim, TextFormatFlags.NoPadding);
            y += D(22);
        }
    }

    // ---------------- input ----------------

    Point Content(Point p) => new(p.X, (int)(p.Y + scroll));

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        scrollTarget -= e.Delta / 120f * D(64);
        ClampScroll();
        stickToBottom = scrollTarget >= MaxScroll - 1;
        owner.Kick();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        mouse = e.Location;
        bool hand = items.Count == 0
            ? suggestionRects.Any(s => s.r.Contains(e.Location))
            : items.OfType<Ask>().Any(a => a.Answer == null && (a.Allow.Contains(Content(e.Location)) || a.Deny.Contains(Content(e.Location))));
        var hit = actionRects.FirstOrDefault(a => a.r.Contains(Content(e.Location)));
        if (hit.tip != null) hand = true;
        if (hit.tip != shownTip) { shownTip = hit.tip; tips.SetToolTip(this, hit.tip); }
        Cursor = hand ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    string Transcript() => string.Join("\n\n", items.OfType<Msg>().Select(m => (m.User ? "You: " : "Otto: ") + m.Text));

    protected override void OnMouseLeave(EventArgs e) { mouse = new Point(-1, -1); Invalidate(); }

    /// Copy on every message; Retry and Undo on Otto's last reply; Edit on your last message. They sit in the
    /// row under the bubble, lined up with its outer edge, so they stay on screen however wide the bubble is.
    void DrawActions(Graphics g)
    {
        actionRects.Clear();
        if (mouse.X < 0) return;
        var p = Content(mouse);
        int lastUser = items.FindLastIndex(it => it is Msg { User: true });
        int lastOtto = items.FindLastIndex(it => it is Msg { User: false });
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] is not Msg m) continue;
            // hover zone: the bubble's whole row plus the buttons under it, so moving to them doesn't hide them
            var row = new Rectangle(G, m.Box.Top, Inner, m.Box.Height + D(ActionRow));
            if (!row.Contains(p)) continue;

            var acts = new List<(string glyph, Action act, string tip)> { (GlyphCopy, () => Clipboard.SetText(m.Text), "Copy") };
            // retry/undo act on the latest turn, so only offer them on Otto's answer to your last message,
            // not on an older reply you've since followed up
            bool answerToLast = !m.User && i == lastOtto && lastUser >= 0 && lastOtto > lastUser;
            if (!Busy && answerToLast) acts.Add((GlyphRetry, () => RetryRequested?.Invoke(), "Try again"));
            if (!Busy && answerToLast && LastTurnActed()) acts.Add((GlyphUndo, () => UndoRequested?.Invoke(), "Undo what Otto just did"));
            if (!Busy && m.User && i == lastUser) acts.Add((GlyphEdit, () => EditRequested?.Invoke(), "Edit and resend"));

            int size = D(ActionRow - 2), gap = D(2);
            int x = m.User ? m.Box.Right - acts.Count * size - (acts.Count - 1) * gap : m.Box.Left;
            int y = m.Box.Bottom + D(2);
            foreach (var (glyph, act, tip) in acts)
            {
                var r = new Rectangle(x, y, size, size);
                bool hot = r.Contains(p);
                if (hot) using (var hb = new SolidBrush(Color.FromArgb(40, 255, 255, 255))) g.FillRectangle(hb, r);
                bool justCopied = glyph == GlyphCopy && copiedMsg == m && copiedAt > DateTime.Now.AddSeconds(-1.2);
                TextRenderer.DrawText(g, justCopied ? "" : glyph, this.glyph, // tick for a moment after copying
                    r, hot ? ChatPanel.Fg : ChatPanel.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | Scrolled);
                actionRects.Add((r, glyph, glyph == GlyphCopy ? () => { act(); copiedAt = DateTime.Now; copiedMsg = m; Invalidate(); } : act, tip));
                x += size + gap;
            }
            break;
        }
    }

    DateTime copiedAt;
    Msg? copiedMsg;

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (items.Count == 0)
        {
            foreach (var (r, text) in suggestionRects)
                if (e.Button == MouseButtons.Left && r.Contains(e.Location)) { SuggestionClicked?.Invoke(text); return; }
            return;
        }
        var p = Content(e.Location);
        if (e.Button == MouseButtons.Left)
            foreach (var (r, _, act, _) in actionRects)
                if (r.Contains(p)) { act(); return; }
        foreach (var it in items)
        {
            if (it is Ask { Answer: null } a && e.Button == MouseButtons.Left)
            {
                if (a.Allow.Contains(p)) { a.Tcs.TrySetResult(true); return; }
                if (a.Deny.Contains(p)) { a.Tcs.TrySetResult(false); return; }
            }
            if (it is Msg m && e.Button == MouseButtons.Right && m.Box.Contains(p))
            {
                var menu = new ContextMenuStrip();
                menu.Items.Add("Copy", null, (_, _) => Clipboard.SetText(m.Text));
                menu.Items.Add("Copy whole chat", null, (_, _) => Clipboard.SetText(Transcript()));
                bool last = items.FindLastIndex(x => x is Msg mm && mm.User == m.User) == items.IndexOf(m);
                bool answerToLast = last && !m.User && items.IndexOf(m) > items.FindLastIndex(x => x is Msg { User: true });
                if (!Busy && answerToLast) menu.Items.Add("Try again", null, (_, _) => RetryRequested?.Invoke());
                if (!Busy && answerToLast && LastTurnActed()) menu.Items.Add("Undo what Otto just did", null, (_, _) => UndoRequested?.Invoke());
                if (!Busy && last && m.User) menu.Items.Add("Edit and resend", null, (_, _) => EditRequested?.Invoke());
                menu.Show(this, e.Location);
                return;
            }
        }
    }
}
