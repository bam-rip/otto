using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Otto;

/// Click-through, always-on-top, per-pixel-alpha window that's hidden from screenshots.
/// The picture is uploaded once; fading it is just the window's overall alpha, which costs nothing.
class LayeredWindow : Form
{
    const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000;
    const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    protected LayeredWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected void HideFromCapture() => SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE);

    /// Send a new picture (premultiplied ARGB) to the window at its current position.
    protected void Upload(Bitmap bmp, byte alpha)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero), memDc = CreateCompatibleDC(screenDc);
        IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(memDc, hbmp);
        try
        {
            var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new POINT();
            var dst = new POINT { x = Left, y = Top };
            var blend = new BLENDFUNCTION { SourceConstantAlpha = alpha, AlphaFormat = 1 }; // AC_SRC_ALPHA
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2); // ULW_ALPHA
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hbmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// Change only the overall opacity; Windows keeps the picture it already has.
    protected void SetAlpha(byte alpha)
    {
        var blend = new BLENDFUNCTION { SourceConstantAlpha = alpha, AlphaFormat = 1 };
        UpdateLayeredWindow(Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref blend, 2);
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, IntPtr psize, IntPtr hdcSrc, IntPtr pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
}

/// Soft glow around the screen edges + banner while Otto has the mouse and keyboard.
sealed class ControlOverlay : LayeredWindow
{
    static ControlOverlay? instance;
    static Ripple? ripple;
    static StopPill? stop;
    /// The banner's position on screen, so the Stop pill can sit beside it.
    internal static Rectangle BannerRect;
    /// Clicked the Stop pill.
    public static event Action? StopClicked;

    const int GlowDepth = 70; // px the glow reaches in from each edge

    readonly System.Windows.Forms.Timer anim = new() { Interval = 50 }; // breathing is slow; 20 fps is plenty
    float phase, fade; // breathing; fade-in 0 → 1

    /// Create on the UI thread at startup so worker threads can toggle it.
    public static void Init()
    {
        instance = new ControlOverlay();
        _ = instance.Handle;
        ripple = new Ripple();
        _ = ripple.Handle;
        stop = new StopPill();
        _ = stop.Handle;
        stop.Clicked += () => StopClicked?.Invoke();
    }

    public static void Show(bool on)
    {
        var o = instance;
        if (o == null) return;
        o.BeginInvoke(() =>
        {
            if (on && !o.Visible)
            {
                o.Bounds = Desktop.Screen;
                o.fade = 0;
                o.Visible = true;
                o.HideFromCapture();
                // the glow is drawn and uploaded once; the full-screen bitmap is freed straight after
                using (var frame = Render(o.Width, o.Height)) o.Upload(frame, 0);
                o.anim.Start();
                stop?.ShowBeside(BannerRect, o.Location);
                Sfx.Takeover();
            }
            else if (!on && o.Visible)
            {
                o.anim.Stop();
                o.Visible = false;
                ripple?.Stop();
                stop?.Hide();
            }
        });
    }

    /// Called just before Otto moves the mouse somewhere: if that's on the Stop pill, hide it for a moment
    /// so Otto can't stop itself by accident. Blocks until done, since the click follows immediately.
    public static void Avoid(Point screen)
    {
        var s = stop;
        if (s == null || !StopPill.ScreenBounds.Contains(screen)) return;
        try { s.Invoke(() => s.Dodge()); } catch { }
    }

    public static void Pulse(Point screen)
    {
        var r = ripple;
        if (r == null || instance is not { Visible: true }) return;
        r.BeginInvoke(() => r.Start(screen));
    }

    ControlOverlay()
    {
        anim.Tick += (_, _) =>
        {
            phase += 0.09f;
            fade = Math.Min(1, fade + 0.12f);
            float breathe = 0.7f + 0.3f * (float)Math.Sin(phase);
            SetAlpha((byte)(255 * breathe * fade));
        };
    }

    static Bitmap Render(int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        var px = new int[w * h];
        var falloff = new float[GlowDepth];
        for (int d = 0; d < GlowDepth; d++)
        {
            float t = 1 - d / (float)GlowDepth;
            falloff[d] = t * t * t; // eased, so it reads as light rather than a band
        }
        for (int y = 0; y < h; y++)
        {
            int dy = Math.Min(y, h - 1 - y);
            for (int x = 0; x < w; x++)
            {
                int dist = Math.Min(Math.Min(x, w - 1 - x), dy);
                if (dist >= GlowDepth) continue;
                float a = falloff[dist];
                float hot = Math.Max(0, a * 1.6f - 0.6f); // whiter right at the rim, cyan/blue further in
                int A = (int)(200 * a);
                int R = (int)(A * (0.10f + 0.9f * hot));
                int G = (int)(A * (0.65f + 0.35f * hot));
                px[y * w + x] = (A << 24) | (R << 16) | (G << 8) | A;
            }
        }
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        Marshal.Copy(px, 0, data.Scan0, px.Length);
        bmp.UnlockBits(data);

        DrawBanner(bmp, w);
        return bmp;
    }

    /// Windows 10 toast look: flat dark panel, square corners, accent strip, title + grey caption.
    static void DrawBanner(Bitmap bmp, int screenW)
    {
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        float dpi = g.DpiX / 96f;
        using var title = new Font("Segoe UI Semibold", 10f);
        using var caption = new Font("Segoe UI", 9f);
        const string titleText = "Otto is controlling your laptop";
        const string captionText = "Press Ctrl+Alt+End to take back control";

        int pad = (int)(14 * dpi), icon = (int)(20 * dpi);
        var ts = g.MeasureString(titleText, title);
        var cs = g.MeasureString(captionText, caption);
        int bw = (int)(Math.Max(ts.Width, cs.Width) + icon + pad * 3);
        int bh = (int)(ts.Height + cs.Height + pad * 1.4f);
        var r = new Rectangle((screenW - bw) / 2, (int)(16 * dpi), bw, bh);
        BannerRect = r;

        // soft shadow, like the shell's flyouts
        for (int i = 6; i >= 1; i--)
        {
            using var sh = new SolidBrush(Color.FromArgb(10, 0, 0, 0));
            g.FillRectangle(sh, Rectangle.Inflate(r, i, i) with { Y = r.Y - i + 2 });
        }
        using (var bg = new SolidBrush(Color.FromArgb(242, 31, 31, 31))) g.FillRectangle(bg, r);
        using (var border = new Pen(Color.FromArgb(70, 255, 255, 255))) g.DrawRectangle(border, r.X, r.Y, r.Width - 1, r.Height - 1);

        var accent = AccentColor();
        using (var strip = new SolidBrush(accent)) g.FillRectangle(strip, r.X, r.Y, (int)(3 * dpi), r.Height);

        // icon: accent tile with a white cursor arrow
        var tile = new Rectangle(r.X + pad, r.Y + (r.Height - icon) / 2, icon, icon);
        using (var tb = new SolidBrush(accent)) g.FillRectangle(tb, tile);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float u = icon / 20f, ox = tile.X + 6 * u, oy = tile.Y + 3.5f * u;
        PointF[] arrow =
        {
            new(ox, oy), new(ox, oy + 12 * u), new(ox + 3 * u, oy + 9.2f * u), new(ox + 5.2f * u, oy + 13.5f * u),
            new(ox + 7 * u, oy + 12.6f * u), new(ox + 4.9f * u, oy + 8.5f * u), new(ox + 8.8f * u, oy + 8.5f * u),
        };
        using (var white = new SolidBrush(Color.White)) g.FillPolygon(white, arrow);
        g.SmoothingMode = SmoothingMode.None;

        float tx = tile.Right + pad * 0.85f, ty = r.Y + pad * 0.7f;
        using (var fg = new SolidBrush(Color.White)) g.DrawString(titleText, title, fg, tx, ty);
        using (var dim = new SolidBrush(Color.FromArgb(166, 166, 166))) g.DrawString(captionText, caption, dim, tx, ty + ts.Height);
    }

    /// The user's Windows accent colour (the one the taskbar and Start use), falling back to Windows blue.
    static Color AccentColor()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (k?.GetValue("AccentColor") is int abgr)
                return Color.FromArgb(255, abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);
        }
        catch { }
        return Color.FromArgb(0, 120, 215);
    }
}

/// The ring that spreads out where Otto clicks: a tiny window of its own, so a click redraws
/// 96x96 pixels instead of the whole screen.
sealed class Ripple : LayeredWindow
{
    const int Dim = 96;
    readonly System.Windows.Forms.Timer anim = new() { Interval = 25 };
    readonly Bitmap canvas = new(Dim, Dim, PixelFormat.Format32bppPArgb);
    float t = 1; // 0 → 1 as the ring expands

    public Ripple()
    {
        anim.Tick += (_, _) =>
        {
            t += 0.08f;
            if (t >= 1) { Stop(); return; }
            Draw();
        };
    }

    public void Start(Point centre)
    {
        Bounds = new Rectangle(centre.X - Dim / 2, centre.Y - Dim / 2, Dim, Dim);
        t = 0;
        if (!Visible) { Visible = true; HideFromCapture(); }
        Draw();
        anim.Start();
    }

    public void Stop()
    {
        anim.Stop();
        Visible = false;
    }

    void Draw()
    {
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float rad = 8 + 34 * t;
            using var pen = new Pen(Color.FromArgb((int)(230 * (1 - t)), 120, 220, 255), 2.5f);
            g.DrawEllipse(pen, Dim / 2f - rad, Dim / 2f - rad, rad * 2, rad * 2);
        }
        Upload(canvas, 255);
    }
}

/// A real, clickable Stop button beside the banner while Otto has control (the glow and banner are
/// click-through). It never takes focus away from the app Otto is using, and is hidden from screenshots.
sealed class StopPill : Form
{
    public event Action? Clicked;
    static readonly object boundsLock = new();
    static Rectangle screenBounds = Rectangle.Empty;
    /// Read from Otto's worker thread, written on the UI thread.
    internal static Rectangle ScreenBounds { get { lock (boundsLock) return screenBounds; } }
    static Rectangle ScreenBoundsBox { set { lock (boundsLock) screenBounds = value; } }
    bool hot;
    readonly System.Windows.Forms.Timer back = new() { Interval = 1500 };

    const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000, WS_EX_TOPMOST = 0x8;
    const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;

    public StopPill()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(31, 31, 31);
        DoubleBuffered = true;
        Cursor = Cursors.Hand;
        back.Tick += (_, _) => { back.Stop(); if (Tag is true) { Visible = true; } };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST; return cp; }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; } // click without stealing focus
        base.WndProc(ref m);
    }

    public void ShowBeside(Rectangle banner, Point screenOrigin)
    {
        float dpi = DeviceDpi / 96f;
        var size = new Size((int)(84 * dpi), banner.Height);
        Bounds = new Rectangle(screenOrigin.X + banner.Right + (int)(8 * dpi), screenOrigin.Y + banner.Top, size.Width, size.Height);
        ScreenBoundsBox = Bounds;
        Tag = true;
        Visible = true;
        SetWindowDisplayAffinity(Handle, 0x11);
    }

    public new void Hide()
    {
        Tag = false;
        back.Stop();
        Visible = false;
        ScreenBoundsBox = Rectangle.Empty;
    }

    /// Step out of the way of one of Otto's own clicks, then come back.
    public void Dodge()
    {
        Visible = false;
        back.Stop();
        back.Start();
    }

    protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { if (e.Button == MouseButtons.Left) Clicked?.Invoke(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        var bg = hot ? Color.FromArgb(196, 43, 28) : Color.FromArgb(31, 31, 31);
        using (var b = new SolidBrush(bg)) g.FillRectangle(b, ClientRectangle);
        using (var p = new Pen(hot ? bg : Color.FromArgb(70, 255, 255, 255))) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        float dpi = DeviceDpi / 96f;
        int sq = (int)(9 * dpi);
        using (var red = new SolidBrush(hot ? Color.White : Color.FromArgb(232, 72, 85)))
            g.FillRectangle(red, (int)(14 * dpi), (Height - sq) / 2, sq, sq);
        using var font = new Font("Segoe UI Semibold", 10f);
        TextRenderer.DrawText(g, "Stop", font, new Rectangle((int)(30 * dpi), 0, Width - (int)(30 * dpi), Height), Color.White,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
}
