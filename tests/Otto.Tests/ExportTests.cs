using System.IO.Compression;
using System.Xml.Linq;

namespace Otto.Tests;

public class ExportTests
{
    static readonly List<Export.Line> Chat = new()
    {
        new(true, "Is <this> & that \"ok\"?"),
        new(false, "Yes.\nTwo lines."),
    };
    static readonly DateTime When = new(2026, 10, 5, 14, 30, 0);

    [Fact]
    public void Text_and_markdown_say_who_said_what()
    {
        var text = Export.ToText(Chat, When);
        Assert.Contains("You: Is <this> & that \"ok\"?", text);
        Assert.Contains("Otto: Yes.\r\nTwo lines.", text);
        Assert.Contains("**Otto:** Yes.", Export.ToMarkdown(Chat, When));
    }

    [Fact]
    public void Word_file_is_a_valid_document_with_the_text_escaped()
    {
        var bytes = Export.ToDocx(Chat, When);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
        Assert.NotNull(zip.GetEntry("_rels/.rels"));
        using var s = zip.GetEntry("word/document.xml")!.Open();
        var doc = XDocument.Load(s); // throws if the XML isn't well formed
        var all = string.Concat(doc.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value));
        Assert.Contains("Is <this> & that \"ok\"?", all);
        Assert.Contains("Otto chat, 5 October 2026", all);
    }
}
