namespace Otto;

/// The settings window's sidebar: its section buttons and title, painted so they also work on the glass
/// (blurred) window the dark theme puts behind them.
static partial class SettingsWindow
{
    sealed class NavButton : Control
    {
        public readonly string Section;
        readonly string glyph;
        bool selected, hot;
        public bool Selected { get => selected; set { selected = value; Invalidate(); } }

        public NavButton(string section, string glyph)
        {
            Section = section;
            this.glyph = glyph;
            Text = section;
            Height = 40;
            Margin = new Padding(0, 0, 0, 2);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = true;
        }

        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode is Keys.Enter or Keys.Space) OnClick(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            bool glass = Parent is NavStack { SeeThrough: true };
            g.Clear(glass ? Color.Transparent : Parent?.BackColor ?? Ui.Side);
            if (selected || hot)
                using (var b = new SolidBrush(glass ? Theme.Over(selected ? 34 : 18) : selected ? Ui.Selected : Ui.Hover)) g.FillRectangle(b, ClientRectangle);
            if (selected) using (var a = new SolidBrush(ChatPanel.Accent)) g.FillRectangle(a, 0, 8, LogicalToDeviceUnits(3), Height - 16);
            using var gf = new Font(ChatPanel.GlyphFont, 11f);
            Ui.Text(g, glass, glyph, gf, new Rectangle(LogicalToDeviceUnits(14), 0, LogicalToDeviceUnits(24), Height), center: true);
            Ui.Text(g, glass, Text, Font, new Rectangle(LogicalToDeviceUnits(48), 0, Width - LogicalToDeviceUnits(52), Height));
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, ClientRectangle);
        }
    }

    /// "Settings" at the top of the sidebar, painted so it works on glass too.
    sealed class NavTitle : Control
    {
        public NavTitle(string text)
        {
            Text = text;
            Font = new Font("Segoe UI Light", 18f);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            bool glass = Parent is NavStack { SeeThrough: true };
            e.Graphics.Clear(glass ? Color.Transparent : Parent?.BackColor ?? Ui.Side);
            Ui.Text(e.Graphics, glass, Text, Font, new Rectangle(LogicalToDeviceUnits(8), 0, Width, Height));
        }
    }

    /// The sidebar's list. On glass its background is painted fully transparent, so the blur shows.
    sealed class NavStack : FlowLayoutPanel
    {
        public bool SeeThrough { get; set; }
        public NavStack() => SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        protected override void OnPaintBackground(PaintEventArgs e) { if (SeeThrough) e.Graphics.Clear(Color.Transparent); else base.OnPaintBackground(e); }
    }

    /// The dark-theme sidebar: a small borderless window with Windows' live blur (the same as the panel's),
    /// laid exactly over the settings window's left edge and moved with it. Blur applies to whole windows, and
    /// ordinary controls look washed out on it, which is why only the self-drawn sidebar gets one. It never
    /// takes focus, so clicking it doesn't make the settings window look inactive.
    sealed class GlassSidebar : Form
    {
        readonly Form owner;
        readonly Control spot;
        public bool IsGlass { get; }
        /// Lighter than the panel's (92%): over a big flat sidebar the panel's tint hides the blur almost completely.
        const uint Tint = 0xC0_1A1A1A; // 75% dark tint

        public GlassSidebar(Form owner, Control spot)
        {
            this.owner = owner;
            this.spot = spot;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;
            Font = owner.Font;
            AutoScaleMode = AutoScaleMode.None;
            Owner = owner;
            Follow();
            Show(owner);
            IsGlass = Acrylic.Set(Handle, true, Tint);
        }

        public void Follow()
        {
            if (owner.WindowState == FormWindowState.Minimized || !spot.IsHandleCreated) return;
            Bounds = spot.RectangleToScreen(spot.ClientRectangle);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */ | 0x80 /* WS_EX_TOOLWINDOW */;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x21 /* WM_MOUSEACTIVATE */) { m.Result = (IntPtr)3 /* MA_NOACTIVATE */; return; }
            base.WndProc(ref m);
        }

        protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(Color.Transparent);
    }
}
