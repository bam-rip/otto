using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace Otto;

/// Reads the front window's controls through UI Automation (the same API screen readers use) and
/// lists them as compact text: "[7] button "Save" @412,88". A list like that costs a small fraction
/// of a screenshot. Chrome, Edge and Firefox expose web pages this way too.
static class UiTree
{
    /// Appears in every listing (and nowhere else Otto writes), so the agent can tell a listing from other text.
    internal const string ListingMarker = "\nOther windows: ";
    const int MaxElements = 220;
    const int UIA_E_ELEMENTNOTAVAILABLE = unchecked((int)0x80040201);
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
        var hwnd = Win32.Foreground();
        var sb = new StringBuilder();
        var title = Win32.Title(hwnd);
        sb.AppendLine($"Front window: {(title.Length > 0 ? title : "(desktop)")}");
        sb.Append(ListingMarker[1..]).AppendLine(string.Join(" | ", OtherWindows(hwnd).Take(12)));

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
                if (name.Length > 0) line.Append($" \"{name.Clip(80)}\"");
                if (value.Length > 0 && value != name && type != ControlType.Hyperlink) line.Append($" ={value.Clip(120)}");
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

    /// The label of the control under a screen point (and the one it sits in, for text inside a button),
    /// or null if UI Automation can't tell in time.
    public static string? LabelAt(Point p)
    {
        var work = Task.Run(() =>
        {
            try
            {
                var e = AutomationElement.FromPoint(new System.Windows.Point(p.X, p.Y));
                var name = e.Current.Name;
                var parent = TreeWalker.ControlViewWalker.GetParent(e);
                if (parent != null && parent.Current.ControlType is var t && (t == ControlType.Button || t == ControlType.Hyperlink))
                    name = parent.Current.Name + " " + name;
                return name;
            }
            catch { return null; }
        });
        return work.Wait(1500) ? work.Result : null;
    }

    /// The label of the control with keyboard focus, or null.
    public static string? FocusedLabel()
    {
        var work = Task.Run(() => { try { return AutomationElement.FocusedElement?.Current.Name; } catch { return null; } });
        return work.Wait(1500) ? work.Result : null;
    }

    /// Is the keyboard focus in a password box? Otto never types into those.
    public static bool FocusIsPassword()
    {
        var work = Task.Run(() =>
        {
            try { return AutomationElement.FocusedElement?.Current.IsPassword == true; }
            catch { return false; }
        });
        return work.Wait(1500) && work.Result;
    }

    /// Finds a control in the front window by its label, for clicks that must still work next time
    /// (saved routines) where element numbers and positions would have moved.
    public static Point FindByName(string label)
    {
        var items = CollectFront(Win32.Foreground());
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
            // the control went away mid-read; UIA reports that either way depending on where it notices
            catch (Exception ex) when (ex is ElementNotAvailableException || ex is COMException { HResult: UIA_E_ELEMENTNOTAVAILABLE }) { }
        }
        return list;
    }

    static string Clean(string? s) => string.IsNullOrWhiteSpace(s) ? "" : string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// Visible popup windows: classic menus (#32768), or windows owned by the front one / its process.
    static List<IntPtr> Popups(IntPtr front)
    {
        uint frontPid = Win32.ProcessId(front);
        var list = new List<IntPtr>();
        Win32.EnumWindows((h, _) =>
        {
            if (h == front || !Win32.IsWindowVisible(h)) return true;
            var cls = new StringBuilder(64);
            GetClassName(h, cls, cls.Capacity);
            bool menu = cls.ToString() == "#32768";
            bool owned = Win32.GetWindow(h, Win32.GW_OWNER) == front;
            bool samePopup = Win32.ProcessId(h) == frontPid && (GetWindowLong(h, -20 /* GWL_EXSTYLE */) & 0x8 /* WS_EX_TOPMOST */) != 0;
            if (menu || owned || samePopup) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    static readonly string[] Browsers = { "chrome", "msedge", "firefox", "zen", "brave", "opera", "vivaldi" };

    static bool IsBrowser(IntPtr h) => Browsers.Contains(Win32.ProcessName(h));

    static readonly string[] MailApps = { "outlook", "olk", "hxoutlook", "thunderbird", "mailspring" };

    /// A browser or mail app is in front: what's on screen was written by whoever made the page or sent the email.
    public static bool ShowsOutsideContent(IntPtr h) => IsBrowser(h) || MailApps.Contains(Win32.ProcessName(h));

    /// Titles of the other open windows, so the model knows what it could switch to.
    static IEnumerable<string> OtherWindows(IntPtr front) =>
        Win32.AppWindows().Where(w => w.h != front).Select(w => w.title.Clip(50)).Distinct();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
}
