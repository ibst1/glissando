using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Glissando.Printing;

/// <summary>
/// Prints a PDF or a picture on one of this PC's printers without any other program: Windows renders the PDF
/// pages (Windows.Data.Pdf, the same engine as Edge's viewer) and WinForms printing sends them to the printer
/// with its default settings. A PDF page prints at its true size, placed on the paper the way the PDF has it,
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

    /// <summary>Prints the file; returns the number of pages. <paramref name="toFile"/>: the printer's output goes
    /// to that file instead (tests, with "Microsoft Print to PDF").</summary>
    public static int Print(string path, string printer, string documentName, string? toFile = null) =>
        Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            ? PrintPdf(path, printer, documentName, toFile)
            : PrintPicture(path, printer, documentName, toFile);

    private static PrintDocument NewDocument(string printer, string name, string? toFile)
    {
        var doc = new PrintDocument { DocumentName = name, PrintController = new StandardPrintController() };
        doc.PrinterSettings.PrinterName = printer;
        if (toFile != null) { doc.PrinterSettings.PrintToFile = true; doc.PrinterSettings.PrintFileName = toFile; }
        if (!doc.PrinterSettings.IsValid) { doc.Dispose(); throw new InvalidOperationException($"there is no printer \"{printer}\""); }
        return doc;
    }

    private static int PrintPdf(string path, string printer, string name, string? toFile)
    {
        var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
        PdfDocument pdf;
        try { pdf = PdfDocument.LoadFromFileAsync(file).AsTask().GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x8007052B)) { throw new InvalidOperationException("the PDF is password protected"); }
        int count = (int)pdf.PageCount, next = 0;
        if (count == 0) throw new InvalidOperationException("the PDF has no pages");
        using var doc = NewDocument(printer, name, toFile);
        // Each page in its own orientation, so a landscape page is not shrunk onto portrait paper.
        doc.QueryPageSettings += (_, e) =>
        {
            using var page = pdf.GetPage((uint)next);
            e.PageSettings.Landscape = page.Size.Width > page.Size.Height;
        };
        doc.PrintPage += (_, e) =>
        {
            using var page = pdf.GetPage((uint)next);
            // The page's size in DIPs (1/96 inch) -> hundredths of an inch, the printer Graphics' unit.
            float w = (float)page.Size.Width * 100f / 96f, h = (float)page.Size.Height * 100f / 96f;
            using var bmp = Render(page, (uint)Math.Max(1, w * RenderDpi / 100), (uint)Math.Max(1, h * RenderDpi / 100));
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

    private static int PrintPicture(string path, string printer, string name, string? toFile)
    {
        using var img = Image.FromFile(path);
        using var doc = NewDocument(printer, name, toFile);
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

    private static Bitmap Render(PdfPage page, uint width, uint height)
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
