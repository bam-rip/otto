using System.IO.Compression;
using System.Security;
using System.Text;

namespace Otto;

/// Saving a chat as a Word document, plain text or Markdown (right-click in the chat → Export chat…).
static class Export
{
    public sealed record Line(bool User, string Text);

    public static string ToText(IEnumerable<Line> chat, DateTime when) =>
        $"Otto chat, {when:d MMMM yyyy h:mm tt}\r\n\r\n" +
        string.Join("\r\n\r\n", chat.Select(l => (l.User ? "You: " : "Otto: ") + l.Text.Replace("\n", "\r\n")));

    public static string ToMarkdown(IEnumerable<Line> chat, DateTime when) =>
        $"# Otto chat, {when:d MMMM yyyy h:mm tt}\n\n" +
        string.Join("\n\n", chat.Select(l => $"**{(l.User ? "You" : "Otto")}:** " + l.Text.Replace("\n", "  \n")));

    /// A small, valid .docx built by hand (a zip of three XML parts), so no Office or library is needed.
    public static byte[] ToDocx(IEnumerable<Line> chat, DateTime when)
    {
        static string Run(string text, bool bold = false, int halfPoints = 0) =>
            "<w:r>" + (bold || halfPoints > 0 ? "<w:rPr>" + (bold ? "<w:b/>" : "") + (halfPoints > 0 ? $"<w:sz w:val=\"{halfPoints}\"/>" : "") + "</w:rPr>" : "") +
            string.Join("<w:br/>", text.Split('\n').Select(t => $"<w:t xml:space=\"preserve\">{SecurityElement.Escape(XmlSafe(t))}</w:t>")) + "</w:r>";

        var body = new StringBuilder();
        body.Append("<w:p>").Append(Run($"Otto chat, {when:d MMMM yyyy h:mm tt}", bold: true, halfPoints: 32)).Append("</w:p>");
        foreach (var l in chat)
            body.Append("<w:p><w:pPr><w:spacing w:after=\"160\"/></w:pPr>")
                .Append(Run(l.User ? "You: " : "Otto: ", bold: true)).Append(Run(l.Text)).Append("</w:p>");

        var document = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" + body +
            "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\"/></w:sectPr></w:body></w:document>";
        const string types = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>";
        const string rels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>";

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string xml)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                w.Write(xml);
            }
            Add("[Content_Types].xml", types);
            Add("_rels/.rels", rels);
            Add("word/document.xml", document);
        }
        return ms.ToArray();
    }

    /// Text with the characters XML can't hold removed (control codes from pasted pages and emails, half an
    /// emoji): a single one makes Word call the whole document damaged.
    internal static string XmlSafe(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) sb.Append(c).Append(s[++i]);
            else if (!char.IsSurrogate(c) && System.Xml.XmlConvert.IsXmlChar(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    /// Asks where to save, then writes the chosen format. Returns the path, or null if cancelled.
    public static string? SaveWithDialog(IWin32Window owner, IReadOnlyList<Line> chat)
    {
        using var dlg = new SaveFileDialog
        {
            Title = "Export chat",
            Filter = "Word document (*.docx)|*.docx|Text file (*.txt)|*.txt|Markdown (*.md)|*.md",
            FileName = $"Otto chat {DateTime.Now:yyyy-MM-dd HHmm}",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dlg.ShowDialog(owner) != DialogResult.OK) return null;
        var now = DateTime.Now;
        switch (Path.GetExtension(dlg.FileName).ToLowerInvariant())
        {
            case ".txt": File.WriteAllText(dlg.FileName, ToText(chat, now), new UTF8Encoding(true)); break;
            case ".md": File.WriteAllText(dlg.FileName, ToMarkdown(chat, now)); break;
            default: File.WriteAllBytes(dlg.FileName, ToDocx(chat, now)); break;
        }
        return dlg.FileName;
    }
}
