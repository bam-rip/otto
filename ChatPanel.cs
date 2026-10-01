using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Otto;

/// Full-height panel that slides in from the right edge, styled after the Win10 Action Center / Start menu.
/// Everything except the text box is custom-drawn so it all sits on one grid: a 20px gutter on both sides.
sealed class ChatPanel : Form
{
    internal static readonly Color Fg = Color.White;
    internal static readonly Color Dim = Color.FromArgb(170, 170, 170);
    internal static readonly Color Accent = Color.FromArgb(0, 120, 215);
    internal static readonly Color Danger = Color.FromArgb(232, 72, 85);
    // Real acrylic (live blur by Windows, like the Start menu). Alpha = how solid: 0xEB is ~92% dark tint, 8% blur.
    const uint AcrylicTint = 0xEB_1A1A1A; // AABBGGRR
    static readonly Color FieldColor = Color.FromArgb(46, 46, 46);
    static readonly Color Ok = Color.FromArgb(108, 203, 95);
    static readonly Color Amber = Color.FromArgb(247, 181, 0);
    internal const string GlyphFont = "Segoe MDL2 Assets";
    const int BaseWidth = 400;

    readonly ChatView chat;
    readonly CueTextBox input = new();
    readonly System.Windows.Forms.Timer anim = new() { Interval = 15 };
    readonly Font titleFont = new("Segoe UI Light", 18f);
    readonly Font smallFont = new("Segoe UI", 8.5f);
    readonly Font glyphFont = new(GlyphFont, 11f);
    string? lastSent;

    string statusText = "Ready", costText = "";
    Color statusColor = Ok;
    bool busy, listening, closing, sliding;
    int slideFrom, slideTo;
    DateTime slideStart, hiddenAt;
    readonly DateTime epoch = DateTime.Now;
    Point mouse = new(-1, -1);
    Rectangle rNew, rKey, rHide, rMic, rSend, rBox;

    /// Debug: keep the panel open when it loses focus (set by --show).
    public bool Pinned { get; set; }

    public event Action<string>? Submit;
    public event Action? StopRequested, MicToggled, ClearRequested, SettingsRequested, RetryRequested, EditRequested;

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);

    internal int D(int px) => (int)Math.Round(px * DeviceDpi / 96.0);
    internal int G => D(20);

    public ChatPanel()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        // with acrylic on, black pixels are where the blur shows through; everything drawn adds light on top of it
        BackColor = Color.Black;
        DoubleBuffered = true;
        KeyPreview = true;
        Text = "Otto";

        chat = new ChatView(this);
        chat.SuggestionClicked += s => Submit?.Invoke(s);
        chat.RetryRequested += () => RetryRequested?.Invoke();
        chat.EditRequested += () => EditRequested?.Invoke();
        Controls.Add(chat);

        input.Multiline = true;
        input.AcceptsReturn = true;
        input.BorderStyle = BorderStyle.None;
        input.BackColor = FieldColor;
        input.ForeColor = Fg;
        input.Font = new Font("Segoe UI", 10.5f);
        input.Cue = "Ask Otto anything…";
        input.CueColor = Dim;
        input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; SendInput(); }
            // Up in an empty box brings back what you last sent, like a terminal
            else if (e.KeyCode == Keys.Up && input.TextLength == 0 && lastSent != null)
            {
                e.SuppressKeyPress = true;
                input.Text = lastSent;
                input.SelectionStart = input.TextLength;
            }
        };
        input.TextChanged += (_, _) => Relayout();
        input.GotFocus += (_, _) => Invalidate(Inflate(rBox));
        input.LostFocus += (_, _) => Invalidate(Inflate(rBox));
        Controls.Add(input);

        anim.Tick += (_, _) => Animate();
        Deactivate += (_, _) => { if (!chat.HasPendingConfirm && !Pinned) HidePanel(); };
    }

    static Rectangle Inflate(Rectangle r) { r.Inflate(2, 2); return r; }

    // ---------------- layout ----------------

    protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); }

    void Relayout()
    {
        int w = ClientSize.Width, h = ClientSize.Height, g = G;
        if (w == 0 || h == 0) return;
        int lineH = input.Font.Height;
        int lines = Math.Clamp(input.GetLineFromCharIndex(input.TextLength) + 1, 1, 5);
        int textH = lineH * lines;
        int boxH = Math.Max(D(44), textH + D(22));
        int boxBottom = h - g - D(24); // room for the hint line
        rBox = new Rectangle(g, boxBottom - boxH, w - 2 * g, boxH);

        int btn = D(36);
        int btnY = rBox.Bottom - (D(44) + btn) / 2; // centred on a one-line box, stays at the bottom as it grows
        rSend = new Rectangle(rBox.Right - D(4) - btn, btnY, btn, btn);
        rMic = new Rectangle(rSend.Left - btn, btnY, btn, btn);
        input.SetBounds(rBox.Left + D(12), rBox.Top + (boxH - textH) / 2, rMic.Left - rBox.Left - D(16), textH);

        // header icons: glyphs optically line up with the right gutter
        int iconRight = w - g + D(10);
        rHide = new Rectangle(iconRight - btn, D(16), btn, btn);
        rKey = new Rectangle(rHide.Left - btn, D(16), btn, btn);
        rNew = new Rectangle(rKey.Left - btn, D(16), btn, btn);

        int chatTop = D(80);
        chat.SetBounds(0, chatTop, w, rBox.Top - D(12) - chatTop);
        Invalidate();
    }

    // ---------------- painting ----------------

    bool acrylic;

    /// With acrylic, the background must be painted fully transparent (alpha 0) or it hides the blur:
    /// GDI+ writes real alpha, so Clear(Black) gave an opaque black panel.
    internal void PaintBackdrop(Graphics g, Point offset) => g.Clear(acrylic ? Color.Transparent : BackColor);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        EnableAcrylic();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct AccentPolicy { public int AccentState, AccentFlags; public uint GradientColor; public int AnimationId; }
    [StructLayout(LayoutKind.Sequential)]
    struct CompositionData { public int Attribute; public IntPtr Data; public int SizeOfData; }
    [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref CompositionData data);

    /// Ask the compositor for a live blur behind the window (Win10 1803+). It updates as things move
    /// behind the panel and costs Otto no CPU. Falls back to plain dark if Windows refuses.
    void EnableAcrylic()
    {
        var accent = new AccentPolicy { AccentState = 4 /* ACRYLICBLURBEHIND */, AccentFlags = 2, GradientColor = AcrylicTint };
        int size = Marshal.SizeOf(accent);
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new CompositionData { Attribute = 19 /* WCA_ACCENT_POLICY */, Data = ptr, SizeOfData = size };
            acrylic = SetWindowCompositionAttribute(Handle, ref data) != 0;
            if (!acrylic) BackColor = Color.FromArgb(26, 26, 26);
            else input.BackColor = Color.Black; // black = see-through on acrylic
        }
        catch { BackColor = Color.FromArgb(26, 26, 26); }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    double Seconds => (DateTime.Now - epoch).TotalSeconds;

    protected override void OnPaintBackground(PaintEventArgs e) => PaintBackdrop(e.Graphics, Point.Empty);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        int w = ClientSize.Width, gut = G;


        // title + status
        using (var fg = new SolidBrush(Fg)) g.DrawString("Otto", titleFont, fg, gut - D(4), D(14));
        double pulse = busy || listening ? 0.55 + 0.45 * Math.Sin(Seconds * 5) : 1;
        using (var dot = new SolidBrush(Color.FromArgb((int)(255 * pulse), statusColor)))
            g.FillEllipse(dot, gut, D(57), D(7), D(7));
        var status = costText.Length > 0 ? $"{statusText}   ·   {costText}" : statusText;
        TextRenderer.DrawText(g, status, smallFont, new Point(gut + D(14), D(52)), Dim, TextFormatFlags.NoPadding);

        DrawGlyph(g, "", rNew, Fg);
        DrawGlyph(g, "", rKey, Fg);
        DrawGlyph(g, "", rHide, Fg);

        // working line under the header
        if (busy)
        {
            int seg = D(110);
            double t = Seconds % 1.4 / 1.4;
            int x = (int)(t * (w + seg)) - seg;
            using var b = new SolidBrush(Accent);
            g.FillRectangle(b, x, D(78), seg, D(2));
        }

        // composer
        // the text box is a native control that can't do alpha, so on acrylic the field is just the blur
        // with a border (a filled field would never match the box's own background)
        if (!acrylic) using (var f = new SolidBrush(FieldColor)) g.FillRectangle(f, rBox);
        using (var p = new Pen(input.Focused ? Accent : Color.FromArgb(80, 255, 255, 255), input.Focused ? 2 : 1))
        {
            var r = rBox;
            if (input.Focused) { r.Inflate(-1, -1); }
            else { r.Width -= 1; r.Height -= 1; }
            g.DrawRectangle(p, r);
        }
        var micColor = listening ? Color.FromArgb((int)(255 * pulse), Danger) : Dim;
        DrawGlyph(g, "", rMic, micColor, hoverBg: false);
        var sendColor = busy ? Danger : input.TextLength > 0 ? Accent : Dim;
        DrawGlyph(g, busy ? "" : "", rSend, sendColor, hoverBg: false);

        TextRenderer.DrawText(g, "Enter to send  ·  Shift+Enter for a new line",
            smallFont, new Point(gut, rBox.Bottom + D(8)), Dim, TextFormatFlags.NoPadding);
    }

    void DrawGlyph(Graphics g, string glyph, Rectangle r, Color color, bool hoverBg = true)
    {
        bool hover = r.Contains(mouse);
        if (hover && hoverBg)
            using (var hb = new SolidBrush(Color.FromArgb(30, 255, 255, 255))) g.FillRectangle(hb, r);
        if (hover && !hoverBg) color = ControlPaint.Light(color, 0.6f);
        TextRenderer.DrawText(g, glyph, glyphFont, r, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    // ---------------- animation ----------------

    internal void Kick() { if (!anim.Enabled) anim.Start(); }

    void Animate()
    {
        bool more = false;
        if (sliding)
        {
            double t = Math.Min(1, (DateTime.Now - slideStart).TotalMilliseconds / (closing ? 150 : 260));
            double e = closing ? t * t * t : 1 - Math.Pow(1 - t, 3);
            Left = slideFrom + (int)Math.Round((slideTo - slideFrom) * e);
            if (t >= 1)
            {
                sliding = false;
                if (closing) { closing = false; Hide(); hiddenAt = DateTime.Now; TrimMemory(); }
            }
            else more = true;
        }
        if (Visible && (busy || listening))
        {
            Invalidate(new Rectangle(0, D(50), ClientSize.Width, D(32)));
            Invalidate(Inflate(rMic));
            more = true;
        }
        if (Visible && chat.Animate()) more = true;
        if (!more) anim.Stop();
    }

    // ---------------- input ----------------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        mouse = e.Location;
        bool overButton = rNew.Contains(mouse) || rKey.Contains(mouse) || rHide.Contains(mouse) || rMic.Contains(mouse) || rSend.Contains(mouse);
        Cursor = overButton ? Cursors.Hand : rBox.Contains(mouse) ? Cursors.IBeam : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e) { mouse = new Point(-1, -1); Invalidate(); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (rNew.Contains(e.Location)) ClearRequested?.Invoke();
        else if (rKey.Contains(e.Location)) SettingsRequested?.Invoke();
        else if (rHide.Contains(e.Location)) HidePanel();
        else if (rMic.Contains(e.Location)) MicToggled?.Invoke();
        else if (rSend.Contains(e.Location)) { if (busy) StopRequested?.Invoke(); else SendInput(); }
        else if (rBox.Contains(e.Location)) input.Focus();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { HidePanel(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    void SendInput()
    {
        var text = input.Text.Trim();
        if (text.Length == 0 || busy) return;
        lastSent = text;
        input.Clear();
        Submit?.Invoke(text);
    }

    // ---------------- show / hide ----------------

    public void Toggle()
    {
        if (Visible && !closing) HidePanel();
        // a tray click first deactivates (hides) the panel; don't immediately reopen it
        else if ((DateTime.Now - hiddenAt).TotalMilliseconds > 300) ShowPanel();
    }

    public void ShowPanel()
    {
        if (InvokeRequired) { BeginInvoke(ShowPanel); return; }
        Llm.Warm();
        var wa = Screen.PrimaryScreen!.WorkingArea;
        int width = D(BaseWidth);
        if (!Visible || closing)
        {
            closing = false;
            Bounds = new Rectangle(Visible ? Left : wa.Right, wa.Top, width, wa.Height);
            slideFrom = Left;
            slideTo = wa.Right - width;
            slideStart = DateTime.Now;
            sliding = true;
            Show();
            Kick();
        }
        Activate();
        SetForegroundWindow(Handle);
        input.Focus();
        BeginInvoke(() => input.Focus()); // after activation settles
    }

    public void HidePanel()
    {
        if (InvokeRequired) { BeginInvoke(HidePanel); return; }
        if (!Visible || closing) return;
        closing = true;
        slideFrom = Left;
        slideTo = Screen.PrimaryScreen!.WorkingArea.Right;
        slideStart = DateTime.Now;
        sliding = true;
        Kick();
    }

    // ---------------- thread-safe API used by TrayApp / Agent ----------------

    void Ui(Action a) { if (InvokeRequired) BeginInvoke(a); else a(); }

    public void AddUser(string t) => Ui(() => chat.AddMessage(t, user: true));
    public void AddOtto(string t) => Ui(() => chat.AddMessage(StripMarkdown(t), user: false, reveal: Prefs.AnimateReplies));

    public void StreamOtto(string textSoFar, bool newBubble) => Ui(() =>
    {
        chat.StreamMessage(StripMarkdown(textSoFar), newBubble);
    });

    /// The panel shows plain text, so drop stray markdown (**bold**, # headings, `code`) instead of showing symbols.
    static string StripMarkdown(string t)
    {
        t = System.Text.RegularExpressions.Regex.Replace(t, @"(\*\*|__)(.+?)\1", "$2");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"(?m)^#{1,6}\s+", "");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"(?m)^\s*[-*]\s+", "• ");
        return Plain(t.Replace("`", ""));
    }

    /// Otto is told not to write like a chatbot; this catches what slips through. Em/en dashes become commas
    /// (or a hyphen between numbers), emojis and decorative symbols go (the font can't draw most of them anyway).
    static string Plain(string t)
    {
        t = System.Text.RegularExpressions.Regex.Replace(t, @"(?<=\d)\s*[\u2013\u2014]\s*(?=\d)", "-");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\s*[\u2013\u2014]\s*", ", ");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"[\uD800-\uDBFF][\uDC00-\uDFFF]|[\u2600-\u27BF\u2B00-\u2BFF\uFE0F\u200D\u20E3]", "");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"[ \t]{2,}", " ");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"[ \t]+([,.!?;:])", "$1");
        t = System.Text.RegularExpressions.Regex.Replace(t, @",\s*([,.!?])", "$1");
        return t.Trim();
    }

    [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr process);

    /// While hidden Otto does nothing, so hand memory back to Windows.
    void TrimMemory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle);
    }
    public void AddTool(string t) => Ui(() => chat.AddNote(t, tool: true));
    public void AddSystem(string t) => Ui(() => chat.AddNote(t, tool: false));
    public void ClearLog() => Ui(chat.Clear);
    public void SetCost(string text) => Ui(() => { costText = text; Invalidate(); });

    void SetStatus(string text, Color color) { statusText = text; statusColor = color; Invalidate(); }

    /// Removes your last message and Otto's answer from view; returns your message's text.
    public string? RemoveLastTurn() => chat.RemoveLastTurn();

    /// Puts text in the message box (dictation, edit) for you to check, without sending it.
    public void SetInput(string text) => Ui(() =>
    {
        var current = input.Text.TrimEnd();
        input.Text = current.Length > 0 ? current + " " + text : text;
        input.SelectionStart = input.TextLength;
        ShowPanel();
        input.Focus();
    });

    public void SetTranscribing(bool on) => Ui(() =>
    {
        if (on) SetStatus("Transcribing…", Amber);
        else SetStatus(busy ? "Working…" : "Ready", busy ? Amber : Ok);
        Invalidate();
    });

    public void SetBusy(bool on) => Ui(() =>
    {
        chat.Busy = on;
        busy = on;
        chat.ShowTyping = on;
        if (!listening) SetStatus(on ? "Working…" : "Ready", on ? Amber : Ok);
        Kick();
    });

    public void SetListening(bool on) => Ui(() =>
    {
        listening = on;
        if (on) { SetStatus("Listening — press the mic again to send", Danger); Sfx.ListenOn(); }
        else { SetStatus(busy ? "Working…" : "Ready", busy ? Amber : Ok); Sfx.ListenOff(); }
        Kick();
    });

    /// Inline Allow / Deny card. Blocks the calling (worker) thread until answered or cancelled.
    public bool Confirm(string question)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Invoke(() =>
        {
            ShowPanel();
            chat.AddAsk(question, tcs);
            Sfx.Attention();
        });
        return tcs.Task.Result;
    }

    /// Kill switch: deny anything still waiting for an answer.
    public void CancelConfirms() => Ui(chat.CancelAsks);
}

/// Multiline text box that draws a grey placeholder while empty.
sealed class CueTextBox : TextBox
{
    public string Cue = "";
    public Color CueColor = Color.Gray;

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == 0x000F && TextLength == 0) // WM_PAINT
        {
            using var g = Graphics.FromHwnd(Handle);
            TextRenderer.DrawText(g, Cue, Font, new Point(1, 0), CueColor, BackColor, TextFormatFlags.NoPadding);
        }
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
}
