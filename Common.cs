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

static class StringExtensions
{
    /// At most n characters, with "…" when something was cut.
    public static string Clip(this string s, int n) => s.Length <= n ? s : s[..n] + "…";
}

static class Html
{
    /// Readable text from HTML. paragraphs: keep blank lines between blocks (emails); otherwise every
    /// run of blank lines becomes one line break (web pages, where 'find' works line by line).
    public static string ToText(string html, bool paragraphs = false)
    {
        html = Regex.Replace(html, "<(script|style|noscript|svg|head)[^>]*>.*?</\\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, "<(br|/p|/div|/li|/h[1-6]|/tr)[^>]*>", "\n", RegexOptions.IgnoreCase);
        var text = StripTags(html);
        text = Regex.Replace(text, "[ \t\u00a0]+", " "); // &nbsp; decodes to \u00a0
        return paragraphs
            ? Regex.Replace(text, "\\s*\n\\s*(\n\\s*)+", "\n\n").Trim()
            : Regex.Replace(text, "\\s*\n\\s*", "\n").Trim();
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
