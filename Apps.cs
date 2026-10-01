using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Otto;

/// Opening apps by name and managing windows: one cheap call instead of clicking through
/// the Start menu or the taskbar with the computer tool.
static class Apps
{
    public static async Task<string> Open(string target, CancellationToken ct)
    {
        var before = Win32.Foreground();
        var expanded = Environment.ExpandEnvironmentVariables(target.Trim());
        string how;

        if (expanded.Contains("://") || expanded.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ||
            expanded.StartsWith("ms-", StringComparison.OrdinalIgnoreCase) || File.Exists(expanded) || Directory.Exists(expanded))
        {
            Shell(expanded.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + expanded : expanded);
            how = expanded;
        }
        else if (Aliases.TryGetValue(expanded, out var alias)) { Shell(alias); how = alias; }
        else if (TryShell(expanded)) how = expanded; // notepad, calc, winword, chrome... (PATH and App Paths)
        else if (FindShortcut(expanded) is string lnk) { Shell(lnk); how = Path.GetFileNameWithoutExtension(lnk); }
        else if (await FindStoreApp(expanded, ct) is (string id, string name)) { Shell(@"shell:AppsFolder\" + id); how = name; }
        else return $"Couldn't find an app or file called '{target}': it doesn't seem to be installed (checked PATH, Start menu and Store apps).";

        // wait for its window, so the next step doesn't act on the old one
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 6_000)
        {
            await Task.Delay(150, ct);
            var now = Win32.Foreground();
            if (now != before && Win32.Title(now).Length > 0 && Win32.ProcessName(now) == "openwith")
            {
                // Windows' "How do you want to open this?" box: the app isn't installed. Close it instead of
                // leaving the model to click around in it.
                Win32.PostMessage(now, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                return $"'{target}' isn't installed (Windows asked which app to use, so I closed that box). Install it first, e.g. with winget.";
            }
            if (now != before && Win32.Title(now).Length > 0)
            {
                await Task.Delay(250, ct); // let it finish drawing
                return $"Opened {how}. Front window: {Win32.Title(Win32.Foreground())}";
            }
        }
        return $"Opened {how} (no new window came to the front yet; it may still be loading or already open in the background).";
    }

    static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["settings"] = "ms-settings:", ["windows settings"] = "ms-settings:",
        ["file explorer"] = "explorer.exe", ["explorer"] = "explorer.exe", ["files"] = "explorer.exe",
        ["task manager"] = "taskmgr.exe", ["control panel"] = "control.exe", ["terminal"] = "wt.exe",
        ["command prompt"] = "cmd.exe", ["cmd"] = "cmd.exe", ["powershell"] = "powershell.exe",
        ["snipping tool"] = "ms-screenclip:", ["store"] = "ms-windows-store:",
    };

    static void Shell(string what) => Process.Start(new ProcessStartInfo(what) { UseShellExecute = true });

    static bool TryShell(string what)
    {
        try { Shell(what); return true; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    /// Start menu shortcuts: exact name first, then the shortest name containing it ("word" → "Word").
    static string? FindShortcut(string name)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        var links = roots.Where(Directory.Exists)
            .SelectMany(r => { try { return Directory.EnumerateFiles(r, "*.lnk", SearchOption.AllDirectories); } catch { return Enumerable.Empty<string>(); } })
            .Where(l => !Path.GetFileName(l).Contains("uninstall", StringComparison.OrdinalIgnoreCase))
            .ToList();
        string N(string l) => Path.GetFileNameWithoutExtension(l);
        return links.FirstOrDefault(l => N(l).Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? links.Where(l => N(l).Contains(name, StringComparison.OrdinalIgnoreCase)).OrderBy(l => N(l).Length).FirstOrDefault();
    }

    /// Store apps (Calculator, Spotify from the Store, Photos...) aren't .lnk files; ask Windows for them.
    static async Task<(string id, string name)?> FindStoreApp(string name, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoProfile", "-Command", "Get-StartApps | ForEach-Object { $_.Name + '|' + $_.AppID }" },
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        var lines = (await p.StandardOutput.ReadToEndAsync(ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim().Split('|')).Where(a => a.Length == 2).Select(a => (name: a[0], id: a[1])).ToList();
        var hit = lines.FirstOrDefault(a => a.name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (hit.id == null) hit = lines.Where(a => a.name.Contains(name, StringComparison.OrdinalIgnoreCase)).OrderBy(a => a.name.Length).FirstOrDefault();
        return hit.id == null ? null : (hit.id, hit.name);
    }

    // ---- windows ----

    public static string Window(string action, string? title)
    {
        var all = Win32.AppWindows();
        if (action == "list")
            return all.Count == 0 ? "No windows open." : string.Join("\n", all.Select(w => (IsIconic(w.h) ? "[minimized] " : "") + w.title));
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException($"'{action}' needs a title");

        var match = all.FirstOrDefault(w => w.title.Equals(title, StringComparison.OrdinalIgnoreCase));
        if (match.h == IntPtr.Zero) match = all.FirstOrDefault(w => w.title.Contains(title, StringComparison.OrdinalIgnoreCase));
        if (match.h == IntPtr.Zero) return $"No window with '{title}' in its title. Open windows:\n" + string.Join("\n", all.Select(w => w.title));

        switch (action)
        {
            case "focus":
                if (IsIconic(match.h)) ShowWindow(match.h, 9);
                // Windows only lets the active app change focus; a tap of Alt counts as user input and unlocks it
                keybd_event(0x12, 0, 0, 0);
                keybd_event(0x12, 0, 2, 0);
                Win32.SetForegroundWindow(match.h);
                break;
            case "minimize": ShowWindow(match.h, 6); break;
            case "maximize": ShowWindow(match.h, 3); Win32.SetForegroundWindow(match.h); break;
            case "restore": ShowWindow(match.h, 9); break;
            case "close": Win32.PostMessage(match.h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); break;
            default: throw new ArgumentException($"Unknown window action {action}");
        }
        Thread.Sleep(300);
        return $"{action}: {match.title}";
    }

    const uint WM_CLOSE = 0x0010;
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, int extra);
}
