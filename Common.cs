using System.Drawing.Imaging;
using System.Net;
using System.Text.RegularExpressions;

namespace Otto;

/// Where Otto keeps its data: %LOCALAPPDATA%\Otto (notes, routines, chats, backups, sounds, scripts).
static class Paths
{
    /// Settable so tests can point Otto at a temp folder instead of the real one.
    internal static string Data { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Otto");
}

/// Reading and writing string values under HKCU, for settings kept in the registry.
static class Reg
{
    public static string? Get(string key, string name)
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key);
        return k?.GetValue(name) as string;
    }

    /// Writes a string; null removes the value.
    public static void Set(string key, string name, string? value)
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(key);
        if (value == null) k.DeleteValue(name, false);
        else k.SetValue(name, value);
    }

    /// A DWORD value (how on/off preferences have always been stored), or null if it isn't there.
    public static int? GetInt(string key, string name)
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key);
        return k?.GetValue(name) is int v ? v : null;
    }

    public static void SetInt(string key, string name, int value)
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(key);
        k.SetValue(name, value);
    }
}

/// Saving Otto's own files whole or not at all: written beside the real file, then swapped in, so a crash or
/// power cut mid-write leaves the previous version instead of a half-written file (which reads as "no
/// reminders", "no routines"...).
static class SafeFile
{
    public static void WriteAllBytes(string path, byte[] data)
    {
        File.WriteAllBytes(path + ".tmp", data);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public static void WriteAllText(string path, string text) => WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(text));

    public static void WriteAllLines(string path, IEnumerable<string> lines) =>
        WriteAllText(path, string.Concat(lines.Select(l => l + Environment.NewLine)));
}

static class StringExtensions
{
    /// At most n characters, with "…" when something was cut.
    public static string Clip(this string s, int n) => s.Length <= n ? s : s.Head(n) + "…";

    /// The first n characters (s if shorter), one fewer if n would cut an emoji or other two-part character in
    /// half: half of one isn't valid text, and an AI provider can refuse a whole request over it.
    public static string Head(this string s, int n) =>
        s.Length <= n ? s : s[..(n > 0 && char.IsHighSurrogate(s[n - 1]) ? n - 1 : n)];
}

static class Html
{
    /// Readable text from HTML. paragraphs: keep blank lines between blocks (emails); otherwise every
    /// run of blank lines becomes one line break (web pages, where 'find' works line by line).
    public static string ToText(string html, bool paragraphs = false)
    {
        html = DropHiddenBlocks(html);
        html = Regex.Replace(html, "<(br|/p|/div|/li|/h[1-6]|/tr)[^>]*>", "\n", RegexOptions.IgnoreCase);
        var text = StripTags(html);
        text = Regex.Replace(text, "[ \t\u00a0]+", " "); // &nbsp; decodes to \u00a0
        return paragraphs
            ? Regex.Replace(text, "\\s*\n\\s*(\n\\s*)+", "\n\n").Trim()
            : Regex.Replace(text, "\\s*\n\\s*", "\n").Trim();
    }

    static readonly string[] Hidden = { "script", "style", "noscript", "svg", "head" };

    /// Removes &lt;script&gt;...&lt;/script&gt; (and style, noscript, svg, head) blocks, replacing each with a space. Same
    /// result as the regex <c>&lt;(script|style|noscript|svg|head)[^&gt;]*&gt;.*?&lt;/\1&gt;</c> (case-insensitive), but in one
    /// pass: that regex re-scans to the end of the page for every opening tag that never closes, so a hostile page
    /// of unclosed tags took minutes. Here a tag type that never closes is noted once and skipped after that.
    internal static string DropHiddenBlocks(string html)
    {
        var lower = html.ToLowerInvariant(); // same length as html, for case-insensitive searching
        var neverCloses = new HashSet<string>();
        var sb = new System.Text.StringBuilder(html.Length);
        int pos = 0, scan = 0;
        while (true)
        {
            int open = lower.IndexOf('<', scan);
            if (open < 0) break;
            string? tag = Hidden.FirstOrDefault(t => string.CompareOrdinal(lower, open + 1, t, 0, t.Length) == 0 && !neverCloses.Contains(t));
            int openEnd = tag == null ? -1 : lower.IndexOf('>', open + 1 + tag.Length);
            if (tag == null || openEnd < 0) { scan = open + 1; continue; }
            int close = lower.IndexOf("</" + tag + ">", openEnd + 1, StringComparison.Ordinal);
            if (close < 0) { neverCloses.Add(tag); scan = open + 1; continue; } // no closing tag from here on, so never again
            sb.Append(html, pos, open - pos).Append(' ');
            pos = scan = close + tag.Length + 3;
        }
        return sb.Append(html, pos, html.Length - pos).ToString();
    }

    public static string StripTags(string s) => WebUtility.HtmlDecode(Regex.Replace(s, "<[^>]+>", "")).Trim();
}

static class Jpeg
{
    static readonly ImageCodecInfo Codec = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);

    /// quality: 0-100. Screenshots use 75, attached images 85 (people attach things to be read closely).
    public static byte[] Encode(Image img, long quality)
    {
        using var ms = new MemoryStream();
        using var ps = new EncoderParameters(1) { Param = { [0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality) } };
        img.Save(ms, Codec, ps);
        return ms.ToArray();
    }
}

/// An error response from an AI provider, with its HTTP status so callers can react to specific codes
/// (429 = over the rate limit or quota) without parsing the message.
sealed class ApiException(int status, string message) : Exception($"API {status}: {message}")
{
    public int Status { get; } = status;
}
