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
