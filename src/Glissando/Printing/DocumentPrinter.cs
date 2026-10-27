using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Glissando.Printing;

/// <summary>
/// Prints a PDF or a picture on one of this PC's printers without any other program: PDFium (or, failing that,
/// Windows.Data.Pdf) renders the PDF pages and WinForms printing sends them to the printer with its default
/// settings, one- or two-sided as asked. A PDF page prints at its true size, placed on the paper the way the PDF has it,
/// shrunk only when it is bigger than the paper; a picture is fitted to the printable area.
/// </summary>
internal static class DocumentPrinter
{
    private const int RenderDpi = 300;

    /// <summary>This PC's default printer ("" if none) and all its printers.</summary>
    public static (string Default, string[] All) Printers()
    {
        var all = PrinterSettings.InstalledPrinters.Cast<string>().ToArray();
        string def = "";
        try { var ps = new PrinterSettings(); if (ps.IsDefaultPrinter) def = ps.PrinterName; } catch { }
        return (def, all);
    }

    /// <summary>Prints the file, one- or two-sided; returns the number of pages, and a note when the printer could
    /// not do as asked. <paramref name="toFile"/>: the printer's output goes to that file instead (tests, with
    /// "Microsoft Print to PDF").</summary>
    public static (int Pages, string Note) Print(string path, string printer, string documentName, bool twoSided, string? toFile = null)
    {
        string note = "";
        using var doc = NewDocument(printer, documentName, toFile);
        // Always set, both ways: a printer whose own default is two-sided must still print one-sided when asked.
        if (twoSided && !doc.PrinterSettings.CanDuplex) note = "one-sided: the printer cannot print two-sided";
        else doc.PrinterSettings.Duplex = twoSided ? Duplex.Vertical : Duplex.Simplex; // Vertical = long-edge binding
        int pages = Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            ? PrintPdf(doc, path)
            : PrintPicture(doc, path);
        return (pages, note);
    }

    private static PrintDocument NewDocument(string printer, string name, string? toFile)
    {
        var doc = new PrintDocument { DocumentName = name, PrintController = new StandardPrintController() };
        doc.PrinterSettings.PrinterName = printer;
        if (toFile != null) { doc.PrinterSettings.PrintToFile = true; doc.PrinterSettings.PrintFileName = toFile; }
        if (!doc.PrinterSettings.IsValid) { doc.Dispose(); throw new InvalidOperationException($"there is no printer \"{printer}\""); }
        return doc;
    }

    /// <summary>A PDF's pages: how many, each one's size in hundredths of an inch, and each one drawn at a pixel size.</summary>
    internal interface IPdfPages : IDisposable
    {
        int Count { get; }
        SizeF Size(int index);
        Bitmap Render(int index, int width, int height);
    }

    /// <summary>PDFium when it loads here, Windows.Data.Pdf otherwise (or when asked, for tests).</summary>
    internal static IPdfPages OpenPdf(string path, bool windowsEngine = false) =>
        !windowsEngine && PdfiumDocument.Available() ? new PdfiumPages(File.ReadAllBytes(path)) : new WindowsPages(path);

    private sealed class PdfiumPages(byte[] data) : IPdfPages
    {
        private readonly PdfiumDocument _pdf = new(data);
        public int Count => _pdf.PageCount;
        public SizeF Size(int index) { var s = _pdf.PageSize(index); return new SizeF(s.Width * 100f / 72f, s.Height * 100f / 72f); }
        public Bitmap Render(int index, int width, int height) => _pdf.Render(index, width, height);
        public void Dispose() => _pdf.Dispose();
    }

    private sealed class WindowsPages : IPdfPages
    {
        private readonly PdfDocument _pdf;
        public WindowsPages(string path)
        {
            var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
            try { _pdf = PdfDocument.LoadFromFileAsync(file).AsTask().GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x8007052B)) { throw new InvalidOperationException("the PDF is password protected"); }
        }
        public int Count => (int)_pdf.PageCount;
        // DIPs (1/96 inch) -> hundredths of an inch.
        public SizeF Size(int index) { using var p = _pdf.GetPage((uint)index); return new SizeF((float)p.Size.Width * 100f / 96f, (float)p.Size.Height * 100f / 96f); }
        public Bitmap Render(int index, int width, int height) { using var p = _pdf.GetPage((uint)index); return WindowsRender(p, (uint)width, (uint)height); }
        public void Dispose() { }
    }

    private static int PrintPdf(PrintDocument doc, string path)
    {
        using var pdf = OpenPdf(path);
        int count = pdf.Count, next = 0;
        if (count == 0) throw new InvalidOperationException("the PDF has no pages");
        // Each page in its own orientation, so a landscape page is not shrunk onto portrait paper.
        doc.QueryPageSettings += (_, e) =>
        {
            var size = pdf.Size(next);
            e.PageSettings.Landscape = size.Width > size.Height;
        };
        doc.PrintPage += (_, e) =>
        {
            // In hundredths of an inch, the printer Graphics' unit.
            var size = pdf.Size(next);
            float w = size.Width, h = size.Height;
            using var bmp = pdf.Render(next, (int)Math.Max(1, w * RenderDpi / 100), (int)Math.Max(1, h * RenderDpi / 100));
            var paper = Paper(e.Graphics!);
            float scale = Math.Min(1f, Math.Min(paper.Width / w, paper.Height / h));
            float dw = w * scale, dh = h * scale;
            // Centred on the paper (a same-size page lands exactly on it). The Graphics' origin is the corner of
            // the printable area, the hard margin in from the paper's.
            float x = (paper.Width - dw) / 2 - paper.X, y = (paper.Height - dh) / 2 - paper.Y;
            Draw(e.Graphics!, bmp, x, y, dw, dh);
            e.HasMorePages = ++next < count;
        };
        doc.Print();
        return count;
    }

    private static int PrintPicture(PrintDocument doc, string path)
    {
        using var img = Image.FromFile(path);
        doc.DefaultPageSettings.Landscape = img.Width > img.Height;
        doc.PrintPage += (_, e) =>
        {
            var area = e.Graphics!.VisibleClipBounds; // the printable area, from the Graphics' origin
            // At the picture's own size if it has one that fits (a scan), else fitted to the printable area.
            float w = img.Width * 100f / Math.Max(1f, img.HorizontalResolution), h = img.Height * 100f / Math.Max(1f, img.VerticalResolution);
            float scale = Math.Min(area.Width / w, area.Height / h);
            if (img.HorizontalResolution > 100 && scale >= 1) scale = 1;
            float dw = w * scale, dh = h * scale;
            Draw(e.Graphics!, img, area.X + (area.Width - dw) / 2, area.Y + (area.Height - dh) / 2, dw, dh);
            e.HasMorePages = false;
        };
        doc.Print();
        return 1;
    }

    private static Bitmap WindowsRender(PdfPage page, uint width, uint height)
    {
        using var stream = new InMemoryRandomAccessStream();
        page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = width, DestinationHeight = height }).AsTask().GetAwaiter().GetResult();
        stream.Seek(0);
        using var s = stream.AsStreamForRead();
        using var decoded = new Bitmap(s);
        return new Bitmap(decoded); // a copy that does not need the stream any more
    }

    private static void Draw(Graphics g, Image img, float x, float y, float w, float h)
    {
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(img, x, y, w, h);
    }

    /// <summary>The sheet in the current orientation: size, and in X/Y where the printable area starts on it,
    /// all in hundredths of an inch. From the device itself: PageSettings' hard margins are wrong in landscape.</summary>
    private static RectangleF Paper(Graphics g)
    {
        IntPtr hdc = g.GetHdc();
        try
        {
            float dx = GetDeviceCaps(hdc, LOGPIXELSX), dy = GetDeviceCaps(hdc, LOGPIXELSY);
            return new RectangleF(GetDeviceCaps(hdc, PHYSICALOFFSETX) * 100f / dx, GetDeviceCaps(hdc, PHYSICALOFFSETY) * 100f / dy,
                GetDeviceCaps(hdc, PHYSICALWIDTH) * 100f / dx, GetDeviceCaps(hdc, PHYSICALHEIGHT) * 100f / dy);
        }
        finally { g.ReleaseHdc(hdc); }
    }

    private const int LOGPIXELSX = 88, LOGPIXELSY = 90, PHYSICALWIDTH = 110, PHYSICALHEIGHT = 111, PHYSICALOFFSETX = 112, PHYSICALOFFSETY = 113;
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hdc, int index);
}
