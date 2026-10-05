namespace Otto;

/// First-run walkthrough: what Otto is, the three hotkeys, how it keeps the user in charge, picking an AI,
/// and the optional extras. Shown once on a fresh install; Tray → "Welcome tour" shows it again.
static class Welcome
{
    static readonly Color Back = Color.FromArgb(32, 32, 32), Card = Color.FromArgb(44, 44, 44);
    static readonly Color Fg = Color.White, Dim = Color.FromArgb(170, 170, 170);

    public static void Show(Action openSettings, Func<bool> startsWithWindows, Action<bool> setStartWithWindows)
    {
        using var f = new Form
        {
            Text = "Welcome to Otto", Icon = Icons.App(), BackColor = Back, ForeColor = Fg,
            FormBorderStyle = FormBorderStyle.FixedSingle, MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterScreen, TopMost = true,
            AutoScaleMode = AutoScaleMode.Font, Font = new Font("Segoe UI", 10.5f), ClientSize = new Size(560, 470),
        };
        Win32Dark(f);
        var page = new Panel { Location = new Point(36, 28), Size = new Size(488, 360) };
        // page dots drawn as circles: the ● and ○ characters come out at different sizes in Segoe UI
        int at = 0, pageCount = 5;
        var dots = new Panel { Location = new Point(36, 410), Size = new Size(140, 24) };
        dots.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int d = dots.LogicalToDeviceUnits(9), gap = dots.LogicalToDeviceUnits(10), y = (dots.Height - d) / 2;
            using var on = new SolidBrush(Fg);
            using var off = new Pen(Dim, dots.LogicalToDeviceUnits(1));
            for (int k = 0; k < pageCount; k++)
            {
                var r = new Rectangle(k * (d + gap) + 1, y, d, d);
                if (k == at) e.Graphics.FillEllipse(on, r); else e.Graphics.DrawEllipse(off, r);
            }
        };
        var back = Button("Back", primary: false);
        var next = Button("Next", primary: true);
        back.Location = new Point(300, 404);
        next.Location = new Point(412, 404);
        f.Controls.AddRange(new Control[] { page, dots, back, next });

        Label Title(string t) => new() { Text = t, AutoSize = true, Font = new Font("Segoe UI Light", 20f), ForeColor = Fg, Margin = new Padding(0, 0, 0, 14) };
        Label Body(string t) => new() { Text = t, AutoSize = true, MaximumSize = new Size(480, 0), ForeColor = Dim, Margin = new Padding(0, 0, 0, 10) };
        FlowLayoutPanel Stack() => new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill, AutoScroll = false };
        Control Row(string key, string what)
        {
            var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Card, Padding = new Padding(14, 10, 14, 10), Margin = new Padding(0, 0, 0, 8), Width = 480 };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
            row.Controls.Add(new Label { Text = key, AutoSize = true, ForeColor = Fg, Font = new Font("Segoe UI Semibold", 10.5f) }, 0, 0);
            row.Controls.Add(new Label { Text = what, AutoSize = true, MaximumSize = new Size(300, 0), ForeColor = Dim }, 1, 0);
            return row;
        }

        var pages = new List<Func<Control>>
        {
            () =>
            {
                var s = Stack();
                s.Controls.Add(Title("Hi, I'm Otto"));
                s.Controls.Add(Body("I use your PC the way you do: I open apps, click and type, sort files, browse the web, read and draft your email, check your calendar and set reminders."));
                s.Controls.Add(Body("Just ask in plain words, like \"tidy my downloads folder\" or \"what's on tomorrow?\". I live in the tray, next to the clock."));
                return s;
            },
            () =>
            {
                var s = Stack();
                s.Controls.Add(Title("Keys to remember"));
                s.Controls.Add(Row("Ctrl + Shift + J", "Open or hide me"));
                s.Controls.Add(Row("Ctrl + Alt + J", "Talk instead of typing: press, speak, press again"));
                s.Controls.Add(Row("Ctrl + Alt + A", "Ask me about whatever you've selected, in any app"));
                s.Controls.Add(Row("Ctrl + Alt + End", "Stop everything I'm doing, straight away"));
                return s;
            },
            () =>
            {
                var s = Stack();
                s.Controls.Add(Title("You stay in charge"));
                s.Controls.Add(Body("While I'm using your mouse and keyboard, a soft glow goes round the screen and a Stop button appears."));
                s.Controls.Add(Body("I ask before anything that matters: buying or paying, signing, submitting forms, sending email, permanently deleting, and running commands after I've read a web page or email (in case it tried to trick me)."));
                s.Controls.Add(Body("I never type passwords. Sign in to things yourself."));
                return s;
            },
            () =>
            {
                var s = Stack();
                s.Controls.Add(Title("Pick an AI"));
                s.Controls.Add(Body("I need an AI service to think with. You use your own account and pay them directly (usually a few cents a task), or use Google Gemini's free tier."));
                s.Controls.Add(Body("Claude is the one I'm most tested with. Your key is kept in Windows Credential Manager, never in a file."));
                var choose = Button("Choose AI and add key…", primary: true);
                choose.AutoSize = true;
                choose.Margin = new Padding(0, 8, 0, 8);
                var status = Body("");
                void Refresh() => status.Text = Providers.Problem(Providers.Current()) is null
                    ? $"Ready: {Providers.Current().Provider.Label}." : "Not set up yet.";
                choose.Click += (_, _) => { openSettings(); Refresh(); };
                Refresh();
                s.Controls.Add(choose);
                s.Controls.Add(status);
                return s;
            },
            () =>
            {
                var s = Stack();
                s.Controls.Add(Title("Optional extras"));
                s.Controls.Add(Body("In settings (the gear in my panel) you can connect your email (Gmail, Outlook and others) and your calendar, change my voice and sounds, and see what I've remembered."));
                var start = new CheckBox { Text = "Start Otto when I sign in to Windows", AutoSize = true, ForeColor = Fg, Checked = startsWithWindows(), Margin = new Padding(0, 8, 0, 8) };
                start.CheckedChanged += (_, _) => setStartWithWindows(start.Checked);
                s.Controls.Add(start);
                s.Controls.Add(Body("Try asking me: \"what's using the most space on my C drive?\""));
                return s;
            },
        };

        void Go(int i)
        {
            at = i;
            page.Controls.Clear();
            page.Controls.Add(pages[at]());
            dots.Invalidate();
            back.Visible = at > 0;
            next.Text = at == pages.Count - 1 ? "Done" : "Next";
        }
        pageCount = pages.Count;
        back.Click += (_, _) => Go(at - 1);
        next.Click += (_, _) => { if (at == pages.Count - 1) f.Close(); else Go(at + 1); };
        f.AcceptButton = next;
        Go(0);
        f.ShowDialog();
        Prefs.Welcomed = true;
    }

    static Button Button(string text, bool primary) => new()
    {
        Text = text, Size = new Size(110, 36), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
        BackColor = primary ? ChatPanel.Accent : Color.FromArgb(58, 58, 58), ForeColor = Color.White,
        FlatAppearance = { BorderSize = 0 }, Font = new Font("Segoe UI", 10f),
    };

    /// Dark title bar to match (Windows 10 2004+; ignored elsewhere).
    static void Win32Dark(Form f) => f.HandleCreated += (_, _) =>
    {
        int on = 1;
        DwmSetWindowAttribute(f.Handle, 20, ref on, sizeof(int));
    };

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
