using System.Windows.Automation;

namespace Otto;

/// Ctrl+Alt+A, "ask about this": whatever is selected in the app you're in (or, with nothing selected, a
/// picture of that window) goes into the panel, ready for your question.
static class AskAbout
{
    public sealed record Context(string? Text, Attachment? Picture, string Window);

    /// Call before the panel opens: it reads the window that's in front now.
    public static Context Grab()
    {
        var h = Win32.Foreground();
        var title = Win32.Title(h);
        if (Blocklist.Match(title, Win32.ProcessName(h)) != null) return new(null, null, title); // never take from a blocked place
        // never press Ctrl+C in a console: there it stops whatever is running instead of copying
        var text = SelectedText() ?? (Desktop.IsCommandWindow(h) ? null : CopiedSelection());
        if (!string.IsNullOrWhiteSpace(text)) return new(text.Trim().Clip(3000), null, title);
        return new(null, WindowPicture(h, title), title);
    }

    /// The selection via accessibility (browsers, Word, Notepad...), which leaves the clipboard alone.
    static string? SelectedText() => UiTree.Quick(() =>
    {
        var focused = AutomationElement.FocusedElement;
        if (focused?.TryGetCurrentPattern(TextPattern.Pattern, out var p) != true) return null;
        var s = string.Join("\n", ((TextPattern)p).GetSelection().Select(r => r.GetText(5000)));
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }, (string?)null, 1000);

    /// Fallback for apps that don't expose their selection: press Ctrl+C, read it, then put back what was on
    /// the clipboard before (text or a picture; anything else is left as the copy).
    static string? CopiedSelection()
    {
        string? oldText = null;
        Image? oldImage = null;
        try
        {
            if (Clipboard.ContainsText()) oldText = Clipboard.GetText();
            else if (Clipboard.ContainsImage()) oldImage = Clipboard.GetImage();
            Clipboard.Clear();
        }
        catch { return null; } // clipboard busy in another app
        Desktop.PressCombo("ctrl+c");
        string? copied = null;
        for (int i = 0; i < 10 && copied == null; i++)
        {
            Thread.Sleep(40);
            try { if (Clipboard.ContainsText()) copied = Clipboard.GetText(); } catch { }
        }
        try
        {
            if (oldText != null) Clipboard.SetText(oldText);
            else if (oldImage != null) Clipboard.SetImage(oldImage);
        }
        catch { }
        return copied;
    }

    static Attachment? WindowPicture(IntPtr h, string title)
    {
        try
        {
            if (!Win32.GetWindowRect(h, out var r)) return null;
            var rect = Rectangle.Intersect(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom), Screen.FromHandle(h).Bounds);
            if (rect.Width < 20 || rect.Height < 20) return null;
            using var bmp = new Bitmap(rect.Width, rect.Height);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(rect.Location, Point.Empty, rect.Size);
            return Attachment.FromImage(bmp, (title.Length > 0 ? title.Clip(40) : "window") + ".png");
        }
        catch { return null; }
    }
}
