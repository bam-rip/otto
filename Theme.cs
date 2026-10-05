using Microsoft.Win32;

namespace Otto;

/// Look and feel: dark or light, which side the panel slides in from, and how big the chat text is.
/// The panel reads these colours every time it paints, so a change shows straight away.
static class Theme
{
    const string Key = @"Software\Otto";

    public static bool Light
    {
        get => Read("Theme", "dark") == "light";
        set => Write("Theme", value ? "light" : "dark");
    }

    public static bool PanelLeft
    {
        get => Read("PanelSide", "right") == "left";
        set => Write("PanelSide", value ? "left" : "right");
    }

    /// 0 small, 1 normal, 2 large, 3 extra large.
    public static int TextSize
    {
        get => int.TryParse(Read("TextSize", "1"), out var n) ? Math.Clamp(n, 0, 3) : 1;
        set => Write("TextSize", Math.Clamp(value, 0, 3).ToString());
    }

    public static float TextScale => TextSize switch { 0 => 0.9f, 2 => 1.15f, 3 => 1.3f, _ => 1f };

    public static Color Fg => Light ? Color.FromArgb(24, 24, 24) : Color.White;
    public static Color Dim => Light ? Color.FromArgb(96, 96, 96) : Color.FromArgb(170, 170, 170);
    public static Color Field => Light ? Color.White : Color.FromArgb(46, 46, 46);
    public static Color Back => Light ? Color.FromArgb(243, 243, 243) : Color.FromArgb(26, 26, 26);

    /// A see-through layer on top of the background: lighter in dark mode, darker in light mode.
    /// 'alpha' is how strong it is on dark; light mode uses a bit less so greys don't get muddy.
    public static Color Over(int alpha) => Light ? Color.FromArgb(Math.Min(255, alpha * 3 / 4), 0, 0, 0) : Color.FromArgb(alpha, 255, 255, 255);

    public static event Action? Changed;
    public static void Apply() => Changed?.Invoke();

    static string Read(string name, string fallback)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key);
        return k?.GetValue(name) as string ?? fallback;
    }

    static void Write(string name, string value)
    {
        using var k = Registry.CurrentUser.CreateSubKey(Key);
        k.SetValue(name, value);
    }
}
