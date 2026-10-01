using System.Runtime.InteropServices;

namespace Otto;

/// Mouse and keyboard through SendInput.
static partial class Desktop
{
    // ---- mouse ----

    enum Btn { Left, Right, Middle }

    const uint MOUSEEVENTF_MOVE = 0x1, MOUSEEVENTF_ABSOLUTE = 0x8000, MOUSEEVENTF_VIRTUALDESK = 0x4000,
        LEFTDOWN = 0x2, LEFTUP = 0x4, RIGHTDOWN = 0x8, RIGHTUP = 0x10, MIDDLEDOWN = 0x20, MIDDLEUP = 0x40,
        MOUSEEVENTF_WHEEL = 0x800, MOUSEEVENTF_HWHEEL = 0x1000;

    static void MoveTo(Point p)
    {
        // absolute coordinates are 0..65535 across the whole virtual desktop
        var v = SystemInformation.VirtualScreen;
        int ax = (int)Math.Round((p.X - v.X) * 65535.0 / (v.Width - 1));
        int ay = (int)Math.Round((p.Y - v.Y) * 65535.0 / (v.Height - 1));
        Send(Mouse(ax, ay, 0, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK));
        ControlOverlay.Pulse(p);
    }

    static void Click(Point p, Btn b, int times)
    {
        MoveTo(p);
        Thread.Sleep(40);
        var (down, up) = b switch { Btn.Right => (RIGHTDOWN, RIGHTUP), Btn.Middle => (MIDDLEDOWN, MIDDLEUP), _ => (LEFTDOWN, LEFTUP) };
        for (int i = 0; i < times; i++)
        {
            Send(Mouse(0, 0, 0, down), Mouse(0, 0, 0, up));
            if (i + 1 < times) Thread.Sleep(60);
        }
    }

    static async Task Drag(Point a, Point b, CancellationToken ct)
    {
        MoveTo(a);
        await Task.Delay(50, ct);
        Send(Mouse(0, 0, 0, LEFTDOWN));
        try
        {
            for (int i = 1; i <= 15; i++) // in steps, so apps see a real drag
            {
                MoveTo(new Point(a.X + (b.X - a.X) * i / 15, a.Y + (b.Y - a.Y) * i / 15));
                await Task.Delay(15, ct);
            }
        }
        finally { Send(Mouse(0, 0, 0, LEFTUP)); }
    }

    static void Scroll(string dir, int notches)
    {
        notches = Math.Clamp(notches, 1, 30);
        int delta = 120 * notches;
        var input = dir switch
        {
            "up" => Mouse(0, 0, delta, MOUSEEVENTF_WHEEL),
            "down" => Mouse(0, 0, -delta, MOUSEEVENTF_WHEEL),
            "left" => Mouse(0, 0, -delta, MOUSEEVENTF_HWHEEL),
            "right" => Mouse(0, 0, delta, MOUSEEVENTF_HWHEEL),
            _ => throw new ArgumentException($"bad direction {dir}"),
        };
        Send(input);
    }

    // ---- keyboard ----

    const uint KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4, KEYEVENTF_EXTENDED = 0x1;

    /// Plain runs go 40 characters per SendInput call (one key at a time was ~8 ms per character).
    /// Newlines and tabs are real key presses so apps treat them as Enter/Tab.
    static async Task TypeText(string text, CancellationToken ct)
    {
        var chunk = new List<INPUT>();
        async Task Flush()
        {
            if (chunk.Count == 0) return;
            Send(chunk.ToArray());
            chunk.Clear();
            await Task.Delay(15, ct); // let the app's message queue drain
        }
        foreach (var ch in text.Replace("\r\n", "\n"))
        {
            ct.ThrowIfCancellationRequested();
            if (ch is '\n' or '\t')
            {
                await Flush();
                PressCombo(ch == '\n' ? "enter" : "tab");
                await Task.Delay(15, ct);
                continue;
            }
            chunk.Add(Key(0, ch, KEYEVENTF_UNICODE));
            chunk.Add(Key(0, ch, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
            if (chunk.Count >= 80) await Flush();
        }
        await Flush();
    }

    static readonly Dictionary<string, Keys> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = Keys.ControlKey, ["control"] = Keys.ControlKey, ["shift"] = Keys.ShiftKey, ["alt"] = Keys.Menu,
        ["win"] = Keys.LWin, ["windows"] = Keys.LWin, ["super"] = Keys.LWin, ["cmd"] = Keys.LWin, ["meta"] = Keys.LWin,
        ["enter"] = Keys.Enter, ["return"] = Keys.Enter, ["esc"] = Keys.Escape, ["escape"] = Keys.Escape,
        ["tab"] = Keys.Tab, ["space"] = Keys.Space, ["backspace"] = Keys.Back, ["delete"] = Keys.Delete, ["del"] = Keys.Delete,
        ["insert"] = Keys.Insert, ["home"] = Keys.Home, ["end"] = Keys.End, ["pageup"] = Keys.PageUp, ["pagedown"] = Keys.PageDown,
        ["up"] = Keys.Up, ["down"] = Keys.Down, ["left"] = Keys.Left, ["right"] = Keys.Right,
        ["printscreen"] = Keys.PrintScreen, ["capslock"] = Keys.CapsLock, ["menu"] = Keys.Apps,
        ["plus"] = Keys.Oemplus, ["minus"] = Keys.OemMinus, ["comma"] = Keys.Oemcomma, ["period"] = Keys.OemPeriod,
        ["volumeup"] = Keys.VolumeUp, ["volumedown"] = Keys.VolumeDown, ["mute"] = Keys.VolumeMute,
    };

    static readonly HashSet<Keys> Extended = new()
    {
        Keys.Insert, Keys.Delete, Keys.Home, Keys.End, Keys.PageUp, Keys.PageDown,
        Keys.Up, Keys.Down, Keys.Left, Keys.Right, Keys.LWin, Keys.Apps,
    };

    static Keys Parse(string k)
    {
        k = k.Trim();
        if (Named.TryGetValue(k, out var named)) return named;
        if (k.Length == 1)
        {
            char c = char.ToUpperInvariant(k[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return (Keys)c;
            short vk = VkKeyScan(k[0]);
            if (vk != -1) return (Keys)(vk & 0xFF);
        }
        if (k.Length >= 2 && (k[0] == 'f' || k[0] == 'F') && int.TryParse(k[1..], out int f) && f is >= 1 and <= 24)
            return Keys.F1 + (f - 1);
        throw new ArgumentException($"Unknown key '{k}'");
    }

    /// "ctrl+shift+s" → hold modifiers, tap the last key, release in reverse.
    static void PressCombo(string combo)
    {
        var keys = combo.Split('+', StringSplitOptions.RemoveEmptyEntries).Select(Parse).ToList();
        if (combo.EndsWith("++")) keys.Add(Keys.Oemplus);
        var down = keys.Select(k => Key((ushort)k, (char)0, Extended.Contains(k) ? KEYEVENTF_EXTENDED : 0));
        var up = Enumerable.Reverse(keys).Select(k => Key((ushort)k, (char)0, KEYEVENTF_KEYUP | (Extended.Contains(k) ? KEYEVENTF_EXTENDED : 0)));
        Send(down.Concat(up).ToArray());
    }

    // ---- SendInput ----

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public int mouseData; public uint dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit)]
    struct UNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public UNION u; }

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern short VkKeyScan(char ch);

    static INPUT Mouse(int dx, int dy, int data, uint flags) =>
        new() { type = 0, u = new UNION { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags } } };
    static INPUT Key(ushort vk, char scan, uint flags) =>
        new() { type = 1, u = new UNION { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } } };

    static void Send(params INPUT[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
            throw new InvalidOperationException("Windows blocked the input (an admin window may be in front).");
    }
}

