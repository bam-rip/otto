using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace Otto;

/// Reads the front window's controls through UI Automation (the same API screen readers use) and
/// lists them as compact text: "[7] button "Save" @412,88". A list like that costs a small fraction
/// of a screenshot. Chrome, Edge and Firefox expose web pages this way too.
static class UiTree
{
    const int MaxElements = 220;
    const int MaxChars = 4_000; // ~1000 tokens; past that a screenshot is cheaper

    // ids from the last listing → element centre in real screen pixels
    static readonly Dictionary<int, Point> lastIds = new();

    static readonly HashSet<ControlType> Wanted = new()
    {
        ControlType.Button, ControlType.SplitButton, ControlType.Edit, ControlType.Hyperlink, ControlType.MenuItem,
        ControlType.ListItem, ControlType.TabItem, ControlType.CheckBox, ControlType.RadioButton, ControlType.ComboBox,
        ControlType.TreeItem, ControlType.DataItem, ControlType.Slider, ControlType.Spinner, ControlType.Document,
        ControlType.Text, ControlType.Header, ControlType.HeaderItem, ControlType.Image, ControlType.MenuBar,
    };

    static readonly Dictionary<ControlType, string> Short = new()
    {
        [ControlType.Button] = "button", [ControlType.SplitButton] = "button", [ControlType.Edit] = "field",
        [ControlType.Hyperlink] = "link", [ControlType.MenuItem] = "menu", [ControlType.ListItem] = "item",
        [ControlType.TabItem] = "tab", [ControlType.CheckBox] = "check", [ControlType.RadioButton] = "radio",
        [ControlType.ComboBox] = "dropdown", [ControlType.TreeItem] = "tree", [ControlType.DataItem] = "cell",
        [ControlType.Slider] = "slider", [ControlType.Spinner] = "spinner", [ControlType.Document] = "doc",
        [ControlType.Text] = "text", [ControlType.Header] = "header", [ControlType.HeaderItem] = "header",
        [ControlType.Image] = "image", [ControlType.MenuBar] = "menubar",
    };

    public static bool TryGetElement(int id, out Point centre)
    {
        lock (lastIds) return lastIds.TryGetValue(id, out centre);
    }

    /// Text listing of the front window. 'toShot' maps real screen pixels to screenshot pixels.
    /// Returns the element count too, so callers can fall back to a screenshot for canvases and games.
    public static (string text, int count) Describe(Func<Point, Point> toShot, CancellationToken ct)
    {
        var hwnd = GetForegroundWindow();
        var sb = new StringBuilder();
        var title = WindowTitle(hwnd);
        sb.AppendLine($"Front window: {title}");
        sb.AppendLine("Other windows: " + string.Join(" | ", OtherWindows(hwnd).Take(12)));

        var work = Task.Run(() => CollectFront(hwnd));
        // big web pages can be slow to walk; don't let one hang the agent
        if (!work.Wait(TimeSpan.FromSeconds(8), ct))
            return (sb.AppendLine("(The window's controls took too long to read; take a screenshot instead.)").ToString(), 0);
        var items = work.Result;

        lock (lastIds)
        {
            lastIds.Clear();
            int id = 0;
            foreach (var (type, name, value, rect) in items)
            {
                var c = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
                lastIds[++id] = c;
                var p = toShot(c);
                var line = new StringBuilder($"[{id}] {Short[type]}");
                if (name.Length > 0) line.Append($" \"{Clip(name, 80)}\"");
                if (value.Length > 0 && value != name && type != ControlType.Hyperlink) line.Append($" ={Clip(value, 120)}");
                line.Append($" @{p.X},{p.Y}");
                if (sb.Length + line.Length > MaxChars) { sb.AppendLine("…(more; scroll or zoom to see the rest)"); break; }
                sb.AppendLine(line.ToString());
            }
        }
        return (sb.ToString(), items.Count);
    }

    static List<(ControlType, string, string, Rectangle)> CollectFront(IntPtr hwnd)
    {
        // Right-click menus, dropdown lists and some dialogs are separate top-level windows, not part of the
        // front window's tree. List any that are open first: they're on top and usually what matters next.
        var found = new List<(ControlType, string, string, Rectangle)>();
        foreach (var popup in Popups(hwnd)) found.AddRange(Collect(popup));
        found.AddRange(Collect(hwnd));
        // Chromium/Firefox only build the web page's tree once an accessibility client asks, so the first
        // read of a fresh page shows just the browser chrome. Give it a moment and read again.
        if (IsBrowser(hwnd) && !found.Any(f => f.Item1 == ControlType.Document))
        {
            for (int i = 0; i < 4 && !found.Any(f => f.Item1 == ControlType.Document); i++)
            {
                Thread.Sleep(250);
                found = Collect(hwnd);
            }
        }
        return found;
    }

    /// Finds a control in the front window by its label, for clicks that must still work next time
    /// (saved routines) where element numbers and positions would have moved.
    public static Point FindByName(string label)
    {
        var items = CollectFront(GetForegroundWindow());
        bool Clickable((ControlType t, string, string, Rectangle) i) => i.t != ControlType.Text && i.t != ControlType.Image;
        var hit = items.Where(i => i.Item2.Equals(label, StringComparison.OrdinalIgnoreCase)).OrderByDescending(Clickable).FirstOrDefault();
        if (hit.Item2 == null)
            hit = items.Where(i => i.Item2.Contains(label, StringComparison.OrdinalIgnoreCase))
                       .OrderByDescending(Clickable).ThenBy(i => i.Item2.Length).FirstOrDefault();
        if (hit.Item2 == null) throw new ArgumentException($"Nothing labelled \"{label}\" in the front window.");
        var r = hit.Item4;
        return new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
    }

    static List<(ControlType, string, string, Rectangle)> Collect(IntPtr hwnd)
    {
        var list = new List<(ControlType, string, string, Rectangle)>();
        AutomationElement root;
        try { root = AutomationElement.FromHandle(hwnd); }
        catch { return list; }

        var cache = new CacheRequest { TreeScope = TreeScope.Element };
        cache.Add(AutomationElement.ControlTypeProperty);
        cache.Add(AutomationElement.NameProperty);
        cache.Add(AutomationElement.BoundingRectangleProperty);
        cache.Add(AutomationElement.IsOffscreenProperty);
        cache.Add(ValuePattern.ValueProperty);
        cache.Add(TogglePattern.ToggleStateProperty);

        var screen = Desktop.Screen;
        AutomationElementCollection all;
        using (cache.Activate())
        {
            try
            {
                all = root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.IsOffscreenProperty, false));
            }
            catch { return list; }
        }

        var seen = new HashSet<(string, int, int)>();
        foreach (AutomationElement e in all)
        {
            if (list.Count >= MaxElements) break;
            try
            {
                var type = (ControlType)e.GetCachedPropertyValue(AutomationElement.ControlTypeProperty);
                if (!Wanted.Contains(type)) continue;
                var r = (System.Windows.Rect)e.GetCachedPropertyValue(AutomationElement.BoundingRectangleProperty);
                if (r.IsEmpty || r.Width < 2 || r.Height < 2) continue;
                var rect = new Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
                if (!rect.IntersectsWith(screen)) continue;

                string name = Clean(e.GetCachedPropertyValue(AutomationElement.NameProperty) as string);
                string value = Clean(e.GetCachedPropertyValue(ValuePattern.ValueProperty, true) as string);
                if (e.GetCachedPropertyValue(TogglePattern.ToggleStateProperty, true) is ToggleState ts)
                    value = ts == ToggleState.On ? "on" : "off";

                // unlabeled decoration and empty containers are noise
                if (name.Length == 0 && value.Length == 0 && type != ControlType.Edit) continue;
                if (type == ControlType.Document && name.Length == 0) continue;
                if (!seen.Add((name, rect.X / 4, rect.Y / 4))) continue; // same label nested twice
                list.Add((type, name, value, rect));
            }
            catch (ElementNotAvailableException) { }
        }
        return list;
    }

    static string Clean(string? s) => string.IsNullOrWhiteSpace(s) ? "" : string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    /// Visible popup windows: classic menus (#32768), or windows owned by the front one / its process.
    static List<IntPtr> Popups(IntPtr front)
    {
        GetWindowThreadProcessId(front, out uint frontPid);
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            if (h == front || !IsWindowVisible(h)) return true;
            var cls = new StringBuilder(64);
            GetClassName(h, cls, cls.Capacity);
            GetWindowThreadProcessId(h, out uint pid);
            bool menu = cls.ToString() == "#32768";
            bool owned = GetWindow(h, 4 /* GW_OWNER */) == front;
            bool samePopup = pid == frontPid && (GetWindowLong(h, -20 /* GWL_EXSTYLE */) & 0x8 /* WS_EX_TOPMOST */) != 0;
            if (menu || owned || samePopup) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    static readonly string[] Browsers = { "chrome", "msedge", "firefox", "zen", "brave", "opera", "vivaldi" };

    static bool IsBrowser(IntPtr h)
    {
        GetWindowThreadProcessId(h, out uint pid);
        try { return Browsers.Contains(System.Diagnostics.Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant()); }
        catch { return false; }
    }

    static string WindowTitle(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetWindowText(h, sb, sb.Capacity);
        return sb.Length > 0 ? sb.ToString() : "(desktop)";
    }

    static IEnumerable<string> OtherWindows(IntPtr front)
    {
        var titles = new List<string>();
        EnumWindows((h, _) =>
        {
            if (h != front && IsWindowVisible(h) && GetWindowTextLength(h) > 0 && GetWindow(h, 4 /* GW_OWNER */) == IntPtr.Zero)
            {
                var t = WindowTitle(h);
                if (t is not ("Program Manager" or "Otto") && !titles.Contains(t)) titles.Add(Clip(t, 50));
            }
            return true;
        }, IntPtr.Zero);
        return titles;
    }

    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
}
