using System.Windows.Automation;

namespace Otto;

/// Places Otto must never touch: websites and apps the user lists in settings (a banking site, a work app).
/// Otto won't open them, fetch them, read them on screen or click in them. One entry per line in
/// %LOCALAPPDATA%\Otto\blocked.txt; Otto's own file tools can't change that folder without asking.
static class Blocklist
{
    static string FilePath => Path.Combine(Paths.Data, "blocked.txt");

    static (string path, DateTime written, List<string> entries) cache = ("", DateTime.MinValue, new());

    public static List<string> Entries
    {
        get
        {
            try
            {
                var path = FilePath;
                if (!File.Exists(path)) return new();
                var written = File.GetLastWriteTimeUtc(path);
                // read on every screen action, so it's kept in memory until the file changes
                if (cache.path != path || cache.written != written)
                    cache = (path, written, File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length >= 2 && !l.StartsWith('#')).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
                return cache.entries.ToList();
            }
            catch (IOException) { return new(); }
        }
        set
        {
            Directory.CreateDirectory(Paths.Data);
            SafeFile.WriteAllLines(FilePath, value.Select(v => v.Trim()).Where(v => v.Length >= 2));
        }
    }

    /// The entry a piece of text (an address, an app name, a window title) falls under, or null.
    public static string? Match(params string?[] texts)
    {
        var list = Entries;
        if (list.Count == 0) return null;
        foreach (var t in texts)
        {
            if (string.IsNullOrWhiteSpace(t)) continue;
            var host = Uri.TryCreate(t.Contains("://") ? t : "https://" + t, UriKind.Absolute, out var u) ? u.Host : "";
            foreach (var e in list)
            {
                var entry = e.Replace("https://", "", StringComparison.OrdinalIgnoreCase).Replace("http://", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
                if (entry.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) entry = entry[4..]; // www.x.com covers x.com too
                if (t.Contains(entry, StringComparison.OrdinalIgnoreCase)) return e;
                if (host.Length > 0 && (host.Equals(entry, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + entry, StringComparison.OrdinalIgnoreCase))) return e;
            }
        }
        return null;
    }

    public static string Refusal(string entry) =>
        $"Blocked: you've told Otto never to touch \"{entry}\" (Settings → Privacy and safety). Don't try another way; tell the user.";

    /// Throws if the front window is a blocked app or shows a blocked site. Checks the title, the program,
    /// and, in a browser, the address bar (page titles don't always name the site).
    public static void CheckFront()
    {
        if (Entries.Count == 0) return;
        var h = Win32.Foreground();
        if (Match(Win32.Title(h), Win32.ProcessName(h), UiTree.ShowsOutsideContent(h) ? BrowserAddress(h) : null) is string hit)
            throw new UnauthorizedAccessException(Refusal(hit));
    }

    /// The address in a browser's address bar, or null if it can't be read in time.
    static string? BrowserAddress(IntPtr h) => UiTree.Quick(() =>
    {
        var edits = AutomationElement.FromHandle(h).FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        foreach (AutomationElement e in edits)
        {
            var name = e.Current.Name ?? "";
            var id = e.Current.AutomationId ?? "";
            if (!(name.Contains("address", StringComparison.OrdinalIgnoreCase) || id is "urlbar-input" or "addressEditBox")) continue;
            if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var p)) return ((ValuePattern)p).Current.Value;
        }
        return null;
    }, (string?)null);
}
