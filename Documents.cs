using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Otto;

/// Text out of Office files. .docx, .xlsx and .pptx are zips of XML, so no Office or extra library is needed.
/// Every part is size-checked before it's read (a tiny zip can hold gigabytes), and XML is read with DTDs off.
static class Documents
{
    const long MaxPart = 50_000_000;
    const int MaxRowsPerSheet = 2_000;
    const int MaxColumns = 200; // a stray value in column XFD would otherwise make every row 16,000 tabs wide

    public static bool Handles(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".docx" or ".xlsx" or ".xlsm" or ".pptx";

    public static string Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".docx" => Word(zip, path),
            ".pptx" => Slides(zip, path),
            _ => Sheets(zip, path),
        };
    }

    static XmlReader Xml(ZipArchive zip, string part, string path)
    {
        var entry = zip.GetEntry(part) ?? throw new InvalidDataException($"{path} is damaged or isn't an Office file (no {part}).");
        if (entry.Length > MaxPart) throw new InvalidDataException($"{path} is too big to read whole ({entry.Length / 1_000_000} MB of text).");
        return XmlReader.Create(entry.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = false });
    }

    /// Paragraph text from a WordprocessingML or DrawingML part: w:t / a:t runs, a line per paragraph.
    static void Paragraphs(XmlReader x, StringBuilder sb, string para)
    {
        // ReadElementContentAsString already moves past the element, so the loop only calls Read() otherwise
        x.Read();
        while (!x.EOF)
        {
            if (x.NodeType == XmlNodeType.Element && x.LocalName == "t") { sb.Append(x.ReadElementContentAsString()); continue; }
            if (x.NodeType == XmlNodeType.Element && x.LocalName is "tab") sb.Append('\t');
            if (x.NodeType == XmlNodeType.Element && x.LocalName is "br") sb.Append('\n');
            if (x.NodeType == XmlNodeType.EndElement && x.LocalName == para) sb.Append('\n');
            x.Read();
        }
    }

    static string Word(ZipArchive zip, string path)
    {
        var sb = new StringBuilder();
        using (var x = Xml(zip, "word/document.xml", path)) Paragraphs(x, sb, "p");
        return sb.ToString().Trim();
    }

    static string Slides(ZipArchive zip, string path)
    {
        // slide1.xml, slide2.xml... in number order (as text, slide10 would come before slide2)
        var slides = zip.Entries.Where(e => e.FullName.StartsWith("ppt/slides/slide") && e.FullName.EndsWith(".xml"))
            .Select(e => (e.FullName, n: int.TryParse(e.FullName["ppt/slides/slide".Length..^4], out var n) ? n : int.MaxValue))
            .OrderBy(s => s.n).ToList();
        if (slides.Count == 0) throw new InvalidDataException($"{path} has no slides, or isn't a PowerPoint file.");
        var sb = new StringBuilder();
        foreach (var (part, n) in slides)
        {
            sb.Append($"--- slide {n} ---\n");
            using var x = Xml(zip, part, path);
            Paragraphs(x, sb, "p");
            sb.Append('\n');
        }
        return sb.ToString().Trim();
    }

    static string Sheets(ZipArchive zip, string path)
    {
        // shared strings: most text cells hold an index into this list
        var shared = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") != null)
        {
            using var x = Xml(zip, "xl/sharedStrings.xml", path);
            var cur = new StringBuilder();
            x.Read();
            while (!x.EOF)
            {
                if (x.NodeType == XmlNodeType.Element && x.LocalName == "t") { cur.Append(x.ReadElementContentAsString()); continue; }
                if (x.NodeType == XmlNodeType.Element && x.LocalName == "si") cur.Clear();
                if (x.NodeType == XmlNodeType.EndElement && x.LocalName == "si") shared.Add(cur.ToString());
                x.Read();
            }
        }

        // sheet names in workbook order, and the part each lives in
        var rels = new Dictionary<string, string>();
        using (var x = Xml(zip, "xl/_rels/workbook.xml.rels", path))
            while (x.Read())
                if (x.NodeType == XmlNodeType.Element && x.LocalName == "Relationship" && x.GetAttribute("Id") is string id && x.GetAttribute("Target") is string target)
                    rels[id] = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
        var sheets = new List<(string name, string part)>();
        using (var x = Xml(zip, "xl/workbook.xml", path))
            while (x.Read())
                if (x.NodeType == XmlNodeType.Element && x.LocalName == "sheet"
                    && x.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships") is string rid && rels.TryGetValue(rid, out var part))
                    sheets.Add((x.GetAttribute("name") ?? "Sheet", part));

        var sb = new StringBuilder();
        foreach (var (name, part) in sheets)
        {
            if (zip.GetEntry(part) == null) continue; // a chart sheet or a broken link
            sb.Append($"--- sheet \"{name}\" ---\n");
            int rows = 0;
            using var x = Xml(zip, part, path);
            var row = new List<(int col, string value)>();
            string? type = null;
            int col = -1; // the cell being read; -1 between cells
            x.Read();
            while (!x.EOF)
            {
                if (x.NodeType == XmlNodeType.Element && x.LocalName is "v" or "t" && col >= 0)
                {
                    var v = x.ReadElementContentAsString();
                    if (type == "s" && int.TryParse(v, out var i)) v = i >= 0 && i < shared.Count ? shared[i] : "";
                    else if (type == "b") v = v == "1" ? "TRUE" : "FALSE";
                    if (row.Count > 0 && row[^1].col == col) row[^1] = (col, row[^1].value + v); // rich text: several runs in one cell
                    else if (col < MaxColumns) row.Add((col, v));
                    continue;
                }
                if (x.NodeType == XmlNodeType.Element && x.LocalName == "c")
                {
                    type = x.GetAttribute("t");
                    // the cell's address is optional: without one it's the next column along
                    col = x.GetAttribute("r") is string r ? Column(r) : row.Count > 0 ? row[^1].col + 1 : 0;
                    if (x.IsEmptyElement) col = -1;
                }
                if (x.NodeType == XmlNodeType.EndElement && x.LocalName == "c") col = -1;
                if (x.NodeType == XmlNodeType.EndElement && x.LocalName == "row")
                {
                    if (row.Count > 0)
                    {
                        if (++rows > MaxRowsPerSheet) { sb.Append($"…(more rows; sheet has over {MaxRowsPerSheet})\n"); break; }
                        // cells by their column (A, B, ... AA), with blanks kept so columns line up
                        var cells = new string[row.Max(c => c.col) + 1];
                        foreach (var (c, value) in row) cells[c] = value.Replace('\t', ' ').Replace('\n', ' ');
                        sb.AppendJoin('\t', cells.Select(c => c ?? "")).Append('\n');
                    }
                    row.Clear();
                }
                x.Read();
            }
            sb.Append('\n');
        }
        return sb.Length > 0 ? sb.ToString().Trim() : $"{path} has no sheets with data.";
    }

    /// "C12" → 2 (A = 0).
    internal static int Column(string cellRef)
    {
        int col = 0;
        foreach (var ch in cellRef)
        {
            if (!char.IsAsciiLetter(ch)) break;
            col = col * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
            if (col > 16_384) break; // Excel's last column is XFD
        }
        return Math.Clamp(col - 1, 0, 16_383);
    }
}
