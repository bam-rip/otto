using System.IO.Compression;
using System.Text;

namespace Otto.Tests;

public class DocumentTests
{
    static string Zip(string ext, params (string name, string xml)[] parts)
    {
        var path = Path.Combine(Path.GetTempPath(), $"otto-doc-{Guid.NewGuid():N}{ext}");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, xml) in parts)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            w.Write(xml);
        }
        return path;
    }

    const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    [Fact]
    public void Spreadsheet_cells_come_out_in_their_columns()
    {
        var path = Zip(".xlsx",
            ("xl/workbook.xml", $"<workbook xmlns=\"{Main}\" xmlns:r=\"{R}\"><sheets><sheet name=\"Budget\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>"),
            ("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Target=\"worksheets/sheet1.xml\"/></Relationships>"),
            ("xl/sharedStrings.xml", $"<sst xmlns=\"{Main}\"><si><t>Rent</t></si><si><r><t>Fo</t></r><r><t>od</t></r></si></sst>"),
            ("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{Main}\"><sheetData>" +
                "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"C1\"><v>1200</v></c></row>" +
                "<row r=\"2\"><c r=\"A2\" t=\"s\"><v>1</v></c><c r=\"B2\" t=\"inlineStr\"><is><t>weekly</t></is></c><c r=\"C2\"><f>B1*4</f><v>300</v></c></row>" +
                "<row r=\"3\"><c t=\"b\"><v>1</v></c><c><v>7</v></c></row>" +
                "</sheetData></worksheet>"));
        try
        {
            var text = Documents.Read(path).Replace("\r", "");
            Assert.Equal("--- sheet \"Budget\" ---\nRent\t\t1200\nFood\tweekly\t300\nTRUE\t7", text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Slides_come_out_in_number_order()
    {
        const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
        string Slide(string text) => $"<p:sld xmlns:p=\"p\" xmlns:a=\"{A}\"><a:p><a:r><a:t>{text}</a:t></a:r></a:p></p:sld>";
        var path = Zip(".pptx", ("ppt/slides/slide10.xml", Slide("Ten")), ("ppt/slides/slide2.xml", Slide("Two")), ("ppt/slides/slide1.xml", Slide("One &amp; only")));
        try
        {
            var text = Documents.Read(path).Replace("\r", "");
            Assert.Equal("--- slide 1 ---\nOne & only\n\n--- slide 2 ---\nTwo\n\n--- slide 10 ---\nTen", text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Word_keeps_paragraphs_tabs_and_breaks()
    {
        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var path = Zip(".docx", ("word/document.xml",
            $"<w:document xmlns:w=\"{W}\"><w:body><w:p><w:r><w:t>Hi</w:t><w:tab/><w:t>Sam</w:t></w:r></w:p><w:p><w:r><w:t>a</w:t><w:br/><w:t>b</w:t></w:r></w:p></w:body></w:document>"));
        try { Assert.Equal("Hi\tSam\na\nb", Documents.Read(path).Replace("\r", "")); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_document_with_a_DTD_is_refused_rather_than_expanded()
    {
        // the "billion laughs": a few bytes of entity definitions that expand to gigabytes
        var path = Zip(".docx", ("word/document.xml", "<!DOCTYPE d [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">]><d>&b;</d>"));
        try { Assert.Throws<System.Xml.XmlException>(() => Documents.Read(path)); }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("A1", 0)]
    [InlineData("Z9", 25)]
    [InlineData("AA3", 26)]
    [InlineData("XFD1", 16_383)]
    [InlineData("ZZZZZZ1", 16_383)]
    public void Column_letters(string cell, int col) => Assert.Equal(col, Documents.Column(cell));

    [Fact]
    public void OCR_reads_text_drawn_in_a_picture()
    {
        if (!Ocr.Available) return; // no OCR language on this PC
        using var bmp = new Bitmap(600, 120);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            using var font = new Font("Arial", 28);
            g.DrawString("Invoice total 42", font, Brushes.Black, 20, 30);
        }
        var lines = Ocr.ReadAsync(bmp, CancellationToken.None).GetAwaiter().GetResult();
        var line = Assert.Single(lines);
        Assert.Equal("Invoice total 42", line.Text);
        Assert.InRange(line.Box.X, 10, 40);
        Assert.InRange(line.Box.Y, 15, 60);
    }

    [Fact]
    public void PDFs_are_read_page_by_page()
    {
        if (!Ocr.Available) return;
        var path = Path.Combine(Path.GetTempPath(), $"otto-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, MinimalPdf("Quarterly report", "Second page here"));
        try
        {
            var text = Ocr.ReadPdfAsync(path, 1, 20_000, CancellationToken.None).GetAwaiter().GetResult();
            Assert.Contains("--- page 1 of 2 ---\nQuarterly report", text);
            Assert.Contains("--- page 2 of 2 ---\nSecond page here", text);
            var second = Ocr.ReadPdfAsync(path, 2, 20_000, CancellationToken.None).GetAwaiter().GetResult();
            Assert.DoesNotContain("Quarterly", second);
            var tiny = Ocr.ReadPdfAsync(path, 1, 5, CancellationToken.None).GetAwaiter().GetResult();
            Assert.EndsWith("(Pages 2-2 not read yet: read_file again with \"page\": 2.)", tiny);
        }
        finally { File.Delete(path); }
    }

    /// A small valid PDF with one line of text per page.
    static byte[] MinimalPdf(params string[] pages)
    {
        var objs = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "" };
        var kids = new List<string>();
        foreach (var text in pages)
        {
            int pageNo = objs.Count + 1;
            kids.Add($"{pageNo} 0 R");
            objs.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 {pageNo + 2} 0 R >> >> /Contents {pageNo + 1} 0 R >>");
            var stream = $"BT /F1 36 Tf 60 700 Td ({text}) Tj ET";
            objs.Add($"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream");
            objs.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        }
        objs[1] = $"<< /Type /Pages /Kids [{string.Join(" ", kids)}] /Count {pages.Length} >>";
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objs.Count; i++) { offsets.Add(sb.Length); sb.Append($"{i + 1} 0 obj\n{objs[i]}\nendobj\n"); }
        int xref = sb.Length;
        sb.Append($"xref\n0 {objs.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objs.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
