using System.Runtime.InteropServices;
using System.Text;

namespace Otto;

/// Windows' live "acrylic" blur behind a window (Win10 1803+): it updates as things move behind and costs no
/// CPU. With it on, pixels painted fully transparent show the blur, which is why black (alpha 0 from GDI)
/// means see-through. Used only in the dark theme: dark text would vanish the same way.
static class Acrylic
{
    [StructLayout(LayoutKind.Sequential)]
    struct AccentPolicy { public int AccentState, AccentFlags; public uint GradientColor; public int AnimationId; }
    [StructLayout(LayoutKind.Sequential)]
    struct CompositionData { public int Attribute; public IntPtr Data; public int SizeOfData; }
    [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref CompositionData data);

    /// Turns the blur on or off; true if it's on now. 'tint' is AABBGGRR (alpha = how solid the tint is).
    public static bool Set(IntPtr hwnd, bool on, uint tint)
    {
        var accent = new AccentPolicy { AccentState = on ? 4 /* ACRYLICBLURBEHIND */ : 0, AccentFlags = 2, GradientColor = tint };
        int size = Marshal.SizeOf(accent);
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new CompositionData { Attribute = 19 /* WCA_ACCENT_POLICY */, Data = ptr, SizeOfData = size };
            return SetWindowCompositionAttribute(hwnd, ref data) != 0 && on;
        }
        catch { return false; }
        finally { Marshal.FreeHGlobal(ptr); }
    }
}

/// Window queries shared by the tools that look at the desktop (UiTree, Apps, Desktop) and the windows
/// that must stay out of screenshots (Overlay).
static class Win32
{
    public static IntPtr Foreground() => GetForegroundWindow();

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);

    /// Lower-case process name that owns the window ("chrome", "otto"), or "" if it's gone.
    public static string ProcessName(IntPtr h)
    {
        GetWindowThreadProcessId(h, out uint pid);
        try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return ""; } // exited in the meantime
    }

    public static uint ProcessId(IntPtr h) { GetWindowThreadProcessId(h, out uint pid); return pid; }

    public static string Title(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    /// The windows a person would call "open": visible, top-level (not owned by another window), titled,
    /// minus the desktop itself and Otto's own panel.
    public static List<(IntPtr h, string title)> AppWindows()
    {
        var list = new List<(IntPtr, string)>();
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && GetWindow(h, GW_OWNER) == IntPtr.Zero)
            {
                var t = Title(h);
                if (t.Length > 0 && t is not ("Program Manager" or "Otto")) list.Add((h, t));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// Keeps a window out of screenshots, screen recordings and Otto's own captures (Win10 2004+).
    public static void ExcludeFromCapture(IntPtr h) => SetWindowDisplayAffinity(h, WDA_EXCLUDEFROMCAPTURE);

    public const uint GW_OWNER = 4;
    const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
}
