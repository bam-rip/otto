using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Otto;

/// Reading text out of pictures with Windows' own OCR engine (built into Windows 10, so it costs nothing to ship).
/// Used where UI Automation sees nothing (games, canvases, image-only apps) and for PDFs. Runs on this PC: nothing
/// leaves it, and a few hundred words of text cost far fewer tokens than a screenshot.
static class Ocr
{
    public sealed record Line(string Text, Rectangle Box);

    /// The engine for the user's Windows languages, falling back to English; null if Windows has no OCR language.
    static readonly Lazy<OcrEngine?> Engine = new(() =>
    {
        try { return OcrEngine.TryCreateFromUserProfileLanguages() ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US")); }
        catch (Exception e) when (e is COMException or TypeLoadException or PlatformNotSupportedException) { return null; }
    });

    public static bool Available => Engine.Value != null;

    /// The lines of text in the picture, with where each sits (in the picture's pixels). Pictures bigger than the
    /// engine accepts are shrunk first, and the boxes scaled back.
    public static async Task<List<Line>> ReadAsync(Bitmap picture, CancellationToken ct)
    {
        var engine = Engine.Value ?? throw new InvalidOperationException("Windows' text recognition (OCR) isn't installed for any of your languages.");
        double k = Math.Min(1.0, OcrEngine.MaxImageDimension / (double)Math.Max(picture.Width, picture.Height));
        using var scaled = k < 1 ? new Bitmap(picture, Math.Max(1, (int)(picture.Width * k)), Math.Max(1, (int)(picture.Height * k))) : null;
        using var soft = ToSoftwareBitmap(scaled ?? picture);
        var result = await engine.RecognizeAsync(soft).AsTask(ct);
        var lines = new List<Line>();
        foreach (var l in result.Lines)
        {
            if (l.Words.Count == 0) continue;
            double x1 = l.Words.Min(w => w.BoundingRect.X), y1 = l.Words.Min(w => w.BoundingRect.Y);
            double x2 = l.Words.Max(w => w.BoundingRect.X + w.BoundingRect.Width), y2 = l.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
            lines.Add(new Line(l.Text, Rectangle.FromLTRB((int)(x1 / k), (int)(y1 / k), (int)Math.Ceiling(x2 / k), (int)Math.Ceiling(y2 / k))));
        }
        return lines;
    }

    static SoftwareBitmap ToSoftwareBitmap(Bitmap b)
    {
        var data = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb); // BGRA in memory
        try
        {
            var bytes = new byte[b.Width * 4 * b.Height];
            for (int y = 0; y < b.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * b.Width * 4, b.Width * 4);
            return SoftwareBitmap.CreateCopyFromBuffer(bytes.AsBuffer(), BitmapPixelFormat.Bgra8, b.Width, b.Height, BitmapAlphaMode.Ignore);
        }
        finally { b.UnlockBits(data); }
    }

    // ---- PDFs ----

    public const int PdfPageLimit = 20;

    /// The text of a PDF, page by page: each page is drawn by Windows' own PDF reader and then read by OCR, so it
    /// works for scanned documents too. Reads from 'firstPage' (1-based) until about maxChars of text or PdfPageLimit pages.
    public static async Task<string> ReadPdfAsync(string path, int firstPage, int maxChars, CancellationToken ct)
    {
        // read through a stream Otto closes when done: loaded as a StorageFile, Windows kept the PDF locked (the user
        // couldn't delete or replace it) until Otto happened to clean up
        using var file = File.OpenRead(path);
        using var source = file.AsRandomAccessStream();
        PdfDocument doc;
        try { doc = await PdfDocument.LoadFromStreamAsync(source).AsTask(ct); }
        catch (Exception e) when (e.HResult == unchecked((int)0x8007052B)) // ERROR_WRONG_PASSWORD
        {
            return $"{path} is password-protected. Ask the user to open it themselves.";
        }
        int count = (int)doc.PageCount;
        if (firstPage < 1 || firstPage > Math.Max(1, count)) return $"{path} has {count} page(s); there's no page {firstPage}.";
        int last = Math.Min(count, firstPage + PdfPageLimit - 1);
        var sb = new StringBuilder();
        for (int n = firstPage; n <= last; n++)
        {
            ct.ThrowIfCancellationRequested();
            using var page = doc.GetPage((uint)(n - 1));
            // about 200 dpi for an A4 page: sharp enough for small print. A very long page (a receipt, a web page saved
            // as PDF) is drawn narrower, so its height stays inside what the engine accepts.
            double width = Math.Min(1700, OcrEngine.MaxImageDimension);
            if (page.Size.Width > 0 && page.Size.Height * width / page.Size.Width > OcrEngine.MaxImageDimension)
                width = OcrEngine.MaxImageDimension * page.Size.Width / page.Size.Height;
            using var stream = new InMemoryRandomAccessStream();
            var options = new PdfPageRenderOptions { DestinationWidth = (uint)Math.Max(1, width), BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255) };
            await page.RenderToStreamAsync(stream, options).AsTask(ct);
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct);
            using var soft = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore).AsTask(ct);
            var result = await (Engine.Value ?? throw new InvalidOperationException("Windows' text recognition (OCR) isn't installed for any of your languages.")).RecognizeAsync(soft).AsTask(ct);
            sb.Append($"--- page {n} of {count} ---\n");
            sb.AppendJoin('\n', result.Lines.Select(l => l.Text)).Append("\n\n");
            if (sb.Length >= maxChars) { last = n; break; } // enough for one reply; the rest on request
        }
        var text = sb.Length > 0 ? sb.ToString().TrimEnd().Head(maxChars + 2_000) : $"{path} has no pages.";
        return last < count ? text + $"\n\n(Pages {last + 1}-{count} not read yet: read_file again with \"page\": {last + 1}.)" : text;
    }
}
