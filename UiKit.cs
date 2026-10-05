using System.Runtime.InteropServices;

namespace Otto;

/// The small building blocks the settings window is made of, in the panel's colours.
static class Ui
{
    public static Color Back => Theme.Light ? Color.FromArgb(249, 249, 249) : Color.FromArgb(32, 32, 32);
    public static Color Side => Theme.Light ? Color.FromArgb(238, 238, 238) : Color.FromArgb(26, 26, 26);
    public static Color CardBack => Theme.Light ? Color.White : Color.FromArgb(43, 43, 43);
    public static Color Field => Theme.Light ? Color.FromArgb(251, 251, 251) : Color.FromArgb(56, 56, 56);
    public static Color Hover => Theme.Light ? Color.FromArgb(228, 228, 228) : Color.FromArgb(45, 45, 45);
    public static Color Selected => Theme.Light ? Color.FromArgb(220, 220, 220) : Color.FromArgb(52, 52, 52);
    public static Color Fg => Theme.Fg;
    public static Color Dim => Theme.Dim;
    const int CardWidth = 580; // fields inside are 520 wide plus a 10px gap, so 20px padding each side fits them

    /// A titled group with a light background; returns the panel to put its controls in.
    public static FlowLayoutPanel Card(FlowLayoutPanel page, string title, string? note)
    {
        var card = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            MinimumSize = new Size(CardWidth, 0), MaximumSize = new Size(CardWidth, 0), // every card the same width
            BackColor = CardBack, Padding = new Padding(20, 16, 20, 16), Margin = new Padding(0, 0, 0, 12),
        };
        card.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font("Segoe UI Semibold", 11f), ForeColor = Fg, Margin = new Padding(0, 0, 0, 4) });
        if (note != null) card.Controls.Add(Hint(note, CardWidth - 50));
        page.Controls.Add(card);
        return card;
    }

    public static Label Caption(string text) => new() { Text = text, AutoSize = true, ForeColor = Fg, Margin = new Padding(0, 10, 0, 4) };

    public static Label Hint(string text, int width) =>
        new() { Text = text, UseMnemonic = false, AutoSize = true, MaximumSize = new Size(width, 0), ForeColor = Dim, Margin = new Padding(0, 4, 0, 6) };

    public static FlowLayoutPanel Row() => new() { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 4) };

    /// A text field the same height as the buttons, with room around the text (see FieldBox).
    public static FieldBox TextBox(int width, string text = "", string placeholder = "", bool password = false, bool multiline = false) =>
        new(multiline) { Width = width, Text = text, PlaceholderText = placeholder, UseSystemPasswordChar = password, Margin = new Padding(0, 2, 10, 2) };

    public const int ControlHeight = 34; // buttons, fields and dropdowns all line up at this height

    /// A dropdown that draws its own items, so the list is dark in the dark theme (Windows' default list is always
    /// white), and is the same height as the buttons beside it.
    public static ComboBox Combo(int width, bool editable = false)
    {
        var c = new ThemedCombo
        {
            Width = width, DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
            BackColor = Field, ForeColor = Fg, Margin = new Padding(0, 2, 10, 2), Font = new Font("Segoe UI", 10f),
            DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = ControlHeight - 8, MaxDropDownItems = 12,
        };
        c.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            bool chosen = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            using (var b = new SolidBrush(chosen ? ChatPanel.Accent : Field)) e.Graphics.FillRectangle(b, e.Bounds);
            TextRenderer.DrawText(e.Graphics, c.GetItemText(c.Items[e.Index]), c.Font, Rectangle.Inflate(e.Bounds, -6, 0), chosen ? Color.White : Fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        };
        // Windows' dark style for the dropdown's own scrollbar and edges
        if (!Theme.Light) c.HandleCreated += (_, _) => SetWindowTheme(c.Handle, "DarkMode_CFD", null);
        return c;
    }

    /// A vertical stack for item cards (quick actions, reminders).
    public static FlowLayoutPanel Stack() => new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0, 8, 0, 8) };

    /// Replaces a stack's cards; shows 'empty' when there are none.
    public static void Refill(FlowLayoutPanel stack, IEnumerable<Control> cards, string empty)
    {
        stack.SuspendLayout();
        foreach (Control c in stack.Controls) c.Dispose();
        stack.Controls.Clear();
        foreach (var c in cards) stack.Controls.Add(c);
        if (stack.Controls.Count == 0) stack.Controls.Add(Hint(empty, CardWidth - 50));
        stack.ResumeLayout();
    }

    static Color Inset => Theme.Light ? Color.FromArgb(244, 244, 244) : Color.FromArgb(52, 52, 52);

    /// One item as its own block: an optional small label (TASK), a title, the detail wrapped underneath, and its
    /// buttons on the right.
    public static Control ItemCard(string title, string detail, string? tag, params Button[] buttons)
    {
        var card = new TableLayoutPanel
        {
            ColumnCount = 2, RowCount = 1, AutoSize = true, BackColor = Inset, Width = CardWidth - 40,
            MinimumSize = new Size(CardWidth - 40, 0), MaximumSize = new Size(CardWidth - 40, 0),
            Padding = new Padding(14, 10, 10, 10), Margin = new Padding(0, 0, 0, 8),
        };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var text = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = Padding.Empty };
        int textWidth = CardWidth - 40 - 24 - buttons.Length * 90;
        if (tag != null) text.Controls.Add(new Label { Text = tag, AutoSize = true, ForeColor = ChatPanel.Accent, Font = new Font("Segoe UI Semibold", 7.5f), Margin = new Padding(0, 0, 0, 2) });
        text.Controls.Add(new Label { Text = title, UseMnemonic = false, AutoSize = true, MaximumSize = new Size(textWidth, 0), ForeColor = Fg, Font = new Font("Segoe UI Semibold", 10f), Margin = new Padding(0, 0, 0, 2) });
        text.Controls.Add(new Label { Text = detail, UseMnemonic = false, AutoSize = true, MaximumSize = new Size(textWidth, 0), ForeColor = Dim, Margin = Padding.Empty });
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = Padding.Empty };
        actions.Controls.AddRange(buttons);
        card.Controls.Add(text, 0, 0);
        card.Controls.Add(actions, 1, 0);
        return card;
    }

    /// Text that also works on glass: there GDI+ is used, because ordinary (GDI) text leaves see-through holes.
    public static void Text(Graphics g, bool glass, string text, Font font, Rectangle r, bool center = false)
    {
        if (!glass)
        {
            TextRenderer.DrawText(g, text, font, r, Fg, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis |
                                                         (center ? TextFormatFlags.HorizontalCenter : 0));
            return;
        }
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var fmt = new StringFormat { LineAlignment = StringAlignment.Center, Alignment = center ? StringAlignment.Center : StringAlignment.Near, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using var b = new SolidBrush(Fg);
        g.DrawString(text, font, b, r, fmt);
    }

    public static Button Button(string text, bool primary = false, bool small = false)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, MinimumSize = new Size(small ? 76 : 96, small ? 30 : ControlHeight), Padding = new Padding(10, 0, 10, 0), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            BackColor = primary ? ChatPanel.Accent : Theme.Light ? Color.FromArgb(230, 230, 230) : Color.FromArgb(62, 62, 62),
            ForeColor = primary ? Color.White : Fg, Margin = new Padding(0, 2, 8, 2), Font = new Font("Segoe UI", 9.75f),
        };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    public static LinkLabel Link(string text) => new()
    {
        Text = text, AutoSize = true, LinkColor = Theme.Light ? Color.FromArgb(0, 95, 184) : Color.FromArgb(96, 175, 255),
        ActiveLinkColor = ChatPanel.Accent, LinkBehavior = LinkBehavior.HoverUnderline, Margin = new Padding(0, 6, 16, 2),
    };

    public static Toggle Toggle(string text, bool on) => new() { Text = text, Checked = on, Width = CardWidth - 40 };

    /// A row of options where one is chosen (Dark | Light).
    public static FlowLayoutPanel Segmented(string[] options, int chosen, Action<int> picked)
    {
        var row = Row();
        var buttons = new List<Button>();
        for (int i = 0; i < options.Length; i++)
        {
            int index = i;
            var b = Button(options[i], primary: i == chosen);
            b.Margin = new Padding(0, 4, 4, 4);
            b.Click += (_, _) =>
            {
                for (int k = 0; k < buttons.Count; k++)
                {
                    buttons[k].BackColor = k == index ? ChatPanel.Accent : Theme.Light ? Color.FromArgb(230, 230, 230) : Color.FromArgb(62, 62, 62);
                    buttons[k].ForeColor = k == index ? Color.White : Fg;
                }
                picked(index);
            };
            buttons.Add(b);
            row.Controls.Add(b);
        }
        return row;
    }

    /// A small themed dialog (title bar, padding, colours); add fields to 'body', then DialogButtons.
    public static Form Dialog(string title, out FlowLayoutPanel body)
    {
        var f = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, TopMost = true, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Font = new Font("Segoe UI", 9.75f), Padding = new Padding(16),
            BackColor = Back, ForeColor = Fg,
        };
        f.HandleCreated += (_, _) => DarkTitleBar(f);
        body = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Fill };
        f.Controls.Add(body);
        return f;
    }

    /// OK and Cancel at the bottom of a Dialog; returns the OK button.
    public static Button DialogButtons(Form f, FlowLayoutPanel body, string okText)
    {
        var row = Row();
        row.Margin = new Padding(0, 14, 0, 0);
        var ok = Button(okText, primary: true);
        ok.DialogResult = DialogResult.OK;
        var cancel = Button("Cancel");
        cancel.DialogResult = DialogResult.Cancel;
        row.Controls.AddRange(new Control[] { ok, cancel });
        body.Controls.Add(row);
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        return ok;
    }

    public static void DarkTitleBar(Form f, bool? dark = null)
    {
        int on = dark ?? !Theme.Light ? 1 : 0;
        DwmSetWindowAttribute(f.Handle, 20, ref on, sizeof(int));
        // Windows only repaints the title bar's colours when the frame changes, so ask it to (matters when switching theme)
        SetWindowPos(f.Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020); // NOSIZE NOMOVE NOZORDER NOACTIVATE FRAMECHANGED
        if (f.Visible)
        {
            // and on Windows 10 it only takes the new colour on the next active/inactive change, so make one,
            // ending on the window's real state
            bool active = Form.ActiveForm == f;
            SendMessage(f.Handle, 0x0086 /* WM_NCACTIVATE */, (IntPtr)(active ? 0 : 1), IntPtr.Zero);
            SendMessage(f.Handle, 0x0086, (IntPtr)(active ? 1 : 0), IntPtr.Zero);
        }
        DarkScrollbars(f);
    }

    /// Windows' own dark scrollbars for this window and everything in it, now and as controls are added.
    public static void DarkScrollbars(Control root)
    {
        if (Theme.Light) return;
        void Apply(Control c)
        {
            if (c.IsHandleCreated) SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
            else c.HandleCreated += (_, _) => SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
            foreach (Control child in c.Controls) Apply(child);
            c.ControlAdded += (_, e) => { if (e.Control != null) Apply(e.Control); };
        }
        Apply(root);
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string? app, string? idList);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}

/// An on/off switch with its label, like Windows' own settings.
sealed class Toggle : Control
{
    bool on;
    public event EventHandler? CheckedChanged;
    public bool Checked { get => on; set { if (on == value) return; on = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); } }

    public Toggle()
    {
        Height = 36;
        Cursor = Cursors.Hand;
        TabStop = true;
        Margin = new Padding(0, 4, 0, 2);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) Checked = !Checked; }
    protected override void OnGotFocus(EventArgs e) => Invalidate();
    protected override void OnLostFocus(EventArgs e) => Invalidate();

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Ui.CardBack);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int w = LogicalToDeviceUnits(40), h = LogicalToDeviceUnits(20), y = (Height - h) / 2;
        var track = new Rectangle(1, y, w, h);
        using (var path = Pill(track))
        {
            if (on) using (var b = new SolidBrush(ChatPanel.Accent)) g.FillPath(b, path);
            else using (var pen = new Pen(Ui.Dim, LogicalToDeviceUnits(1))) g.DrawPath(pen, path);
        }
        int knob = h - LogicalToDeviceUnits(8);
        var kx = on ? track.Right - knob - LogicalToDeviceUnits(4) : track.X + LogicalToDeviceUnits(4);
        using (var kb = new SolidBrush(on ? Color.White : Ui.Dim)) g.FillEllipse(kb, kx, y + (h - knob) / 2, knob, knob);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(w + LogicalToDeviceUnits(14), 0, Width - w - LogicalToDeviceUnits(14), Height), Ui.Fg,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, ClientRectangle);
    }

    static System.Drawing.Drawing2D.GraphicsPath Pill(Rectangle r)
    {
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        p.AddArc(r.X, r.Y, r.Height, r.Height, 90, 180);
        p.AddArc(r.Right - r.Height, r.Y, r.Height, r.Height, 270, 180);
        p.CloseFigure();
        return p;
    }
}

/// Adding or editing a quick action: a name and the request it sends.
static class QuickActionPrompt
{
    public static QuickActions.Action? Ask(IWin32Window owner, QuickActions.Action? existing)
    {
        using var f = Ui.Dialog(existing == null ? "Add a quick action" : "Edit quick action", out var grid);
        grid.Controls.Add(Ui.Caption("Name (what the button says)"));
        var name = Ui.TextBox(420, existing?.Name ?? "", "Morning briefing");
        grid.Controls.Add(name);
        grid.Controls.Add(Ui.Caption("Request (what Otto is asked)"));
        var prompt = Ui.TextBox(420, existing?.Prompt ?? "", "Summarise my unread email and what's on my calendar today", multiline: true);
        prompt.Height = 90;
        grid.Controls.Add(prompt);
        Ui.DialogButtons(f, grid, existing == null ? "Add" : "Save");
        if (f.ShowDialog(owner) != DialogResult.OK || name.Text.Trim().Length == 0 || prompt.Text.Trim().Length == 0) return null;
        return new(name.Text.Trim(), prompt.Text.Trim());
    }
}

/// A text field with room around the text and the same height as the buttons, which a plain Windows text box
/// can't do: the box sits inside a padded frame that draws the border (blue while you type in it).
sealed class FieldBox : Panel
{
    public readonly TextBox Box;
    readonly bool multiline;

    public FieldBox(bool multiline = false)
    {
        this.multiline = multiline;
        Box = new TextBox
        {
            BorderStyle = BorderStyle.None, Multiline = multiline, AcceptsReturn = multiline,
            ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None,
            BackColor = Ui.Field, ForeColor = Ui.Fg, Font = new Font("Segoe UI", 10f),
        };
        BackColor = Ui.Field;
        Height = multiline ? 120 : Ui.ControlHeight;
        Cursor = Cursors.IBeam;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
        Controls.Add(Box);
        Box.TextChanged += (_, e) => OnTextChanged(e);
        Box.GotFocus += (_, _) => Invalidate();
        Box.LostFocus += (_, _) => Invalidate();
        if (multiline && !Theme.Light) Box.HandleCreated += (_, _) => Ui.DarkScrollbars(Box);
    }

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text { get => Box?.Text ?? ""; set { if (Box != null) Box.Text = value ?? ""; } }
    public string PlaceholderText { get => Box.PlaceholderText; set => Box.PlaceholderText = value; }
    public bool UseSystemPasswordChar { get => Box.UseSystemPasswordChar; set => Box.UseSystemPasswordChar = value; }
    public bool ReadOnly { get => Box.ReadOnly; set { Box.ReadOnly = value; Invalidate(); } }
    public new bool Focus() => Box.Focus();

    protected override void OnClick(EventArgs e) { Box.Focus(); base.OnClick(e); }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int pad = LogicalToDeviceUnits(10);
        if (multiline) Box.SetBounds(pad, pad / 2 + 2, Width - pad - 4, Height - pad - 4);
        else Box.SetBounds(pad, (Height - Box.Height) / 2, Width - 2 * pad, Box.Height); // text centred top to bottom
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var border = Box.Focused ? ChatPanel.Accent : Theme.Light ? Color.FromArgb(200, 200, 200) : Color.FromArgb(78, 78, 78);
        using var pen = new Pen(border, Box.Focused ? 2 : 1);
        e.Graphics.DrawRectangle(pen, Box.Focused ? new Rectangle(1, 1, Width - 2, Height - 2) : new Rectangle(0, 0, Width - 1, Height - 1));
    }
}

/// A dropdown whose frame matches the fields: Windows draws a combo box's border, arrow and open list frame in its
/// own light colours whatever the back colour is, so this paints the border and arrow itself and gives the open
/// list a frame in the same colour.
sealed class ThemedCombo : ComboBox
{
    const int WM_PAINT = 0x000F, WM_CTLCOLORLISTBOX = 0x0134;
    static Color Border(bool active) => active ? ChatPanel.Accent : Theme.Light ? Color.FromArgb(200, 200, 200) : Color.FromArgb(78, 78, 78);

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WM_PAINT) PaintFrame();
        // the open list is about to draw: give its window our border instead of the default light one
        else if (m.Msg == WM_CTLCOLORLISTBOX && m.LParam != IntPtr.Zero && m.LParam != listFramed) FrameList(m.LParam);
    }

    /// Dropdowns you can type in have an edit box inside; give its text the same left room as the fields.
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (DropDownStyle != ComboBoxStyle.DropDown) return;
        var info = new COMBOBOXINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<COMBOBOXINFO>() };
        if (GetComboBoxInfo(Handle, ref info) && info.hwndItem != IntPtr.Zero)
            SendMessage(info.hwndItem, 0x00D3 /* EM_SETMARGINS */, (IntPtr)1 /* EC_LEFTMARGIN */, (IntPtr)LogicalToDeviceUnits(4));
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct COMBOBOXINFO { public int cbSize; public Win32.RECT rcItem, rcButton; public int stateButton; public IntPtr hwndCombo, hwndItem, hwndList; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetComboBoxInfo(IntPtr h, ref COMBOBOXINFO info);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnDropDownClosed(EventArgs e) { base.OnDropDownClosed(e); Invalidate(); }

    /// Paints over Windows' frame and arrow button: our border, a flat button area and a chevron.
    void PaintFrame()
    {
        using var g = Graphics.FromHwnd(Handle);
        var r = ClientRectangle;
        int arrowW = LogicalToDeviceUnits(26);
        var arrow = new Rectangle(r.Right - arrowW, 1, arrowW - 1, r.Height - 2);
        using (var back = new SolidBrush(BackColor)) g.FillRectangle(back, arrow);
        using (var gf = new Font(ChatPanel.GlyphFont, 8f))
            TextRenderer.DrawText(g, "\uE70D", gf, arrow, Enabled ? ForeColor : Ui.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        bool active = Focused || DroppedDown;
        using var pen = new Pen(Border(active), active ? 2 : 1);
        g.DrawRectangle(pen, active ? new Rectangle(1, 1, r.Width - 2, r.Height - 2) : new Rectangle(0, 0, r.Width - 1, r.Height - 1));
    }

    IntPtr listFramed;

    /// The open list is its own window with a WS_BORDER frame Windows paints light; swap that for a 1px frame we
    /// paint in the border colour.
    void FrameList(IntPtr list)
    {
        listFramed = list;
        NativeList.Attach(list, () => Border(false));
    }

    /// Paints the list window's 1px non-client border.
    sealed class NativeList : NativeWindow
    {
        const int WM_NCPAINT = 0x0085;
        readonly Func<Color> color;
        NativeList(Func<Color> color) => this.color = color;

        public static void Attach(IntPtr hwnd, Func<Color> color) => new NativeList(color).AssignHandle(hwnd);

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != WM_NCPAINT) return;
            var dc = GetWindowDC(Handle);
            try
            {
                if (!GetWindowRect(Handle, out var wr)) return;
                using var g = Graphics.FromHdc(dc);
                using var pen = new Pen(color());
                g.DrawRectangle(pen, 0, 0, wr.Right - wr.Left - 1, wr.Bottom - wr.Top - 1);
            }
            finally { ReleaseDC(Handle, dc); }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetWindowDC(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out Win32.RECT r);
    }
}
