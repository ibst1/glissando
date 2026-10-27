using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Glissando.Printing;

/// <summary>
/// A PDF opened with PDFium, Chrome's PDF engine. Windows.Data.Pdf draws filled-in form fields in the wrong font
/// (a form filled in by a script, with NeedAppearances set, has no ready-made look for its fields); PDFium builds
/// that look itself, as Chrome and Acrobat do. pdfium.dll travels inside Glissando.exe and is unpacked under
/// %LOCALAPPDATA%\Glissando the first time something is printed. PDFium is not thread-safe: one call at a time.
/// </summary>
internal sealed class PdfiumDocument : IDisposable
{
    private const string Lib = "pdfium";
    private const string LibVersion = "158.0.8125"; // the bblanchon.PDFium.Win32 package in Glissando.csproj
    private const int FPDF_ANNOT = 0x01, FPDF_PRINTING = 0x800, FPDFBitmap_BGRx = 3;
    private static readonly object Gate = new();
    private static bool _initialised;

    private IntPtr _doc, _form, _formInfo, _data;

    public int PageCount { get; }

    static PdfiumDocument() => NativeLibrary.SetDllImportResolver(typeof(PdfiumDocument).Assembly,
        (name, _, _) => name == Lib ? NativeLibrary.Load(Unpack()) : IntPtr.Zero);

    /// <summary>Whether pdfium.dll can be loaded here (false: print with Windows.Data.Pdf instead).</summary>
    public static bool Available()
    {
        try { lock (Gate) Init(); return true; }
        catch (Exception ex) { Log.Warn("PDFium not available, printing PDFs with Windows' own engine: " + ex.Message); return false; }
    }

    public PdfiumDocument(byte[] pdf)
    {
        lock (Gate)
        {
            Init();
            _data = Marshal.AllocHGlobal(pdf.Length); // PDFium reads the bytes for as long as the document is open
            Marshal.Copy(pdf, 0, _data, pdf.Length);
            _doc = FPDF_LoadMemDocument64(_data, (nuint)pdf.Length, null);
            if (_doc == IntPtr.Zero)
            {
                uint err = FPDF_GetLastError();
                Marshal.FreeHGlobal(_data);
                _data = IntPtr.Zero;
                throw new InvalidOperationException(err switch
                {
                    4 => "the PDF is password protected",
                    3 => "the file is not a PDF, or it is damaged",
                    _ => $"PDFium could not open the PDF (error {err})",
                });
            }
            PageCount = FPDF_GetPageCount(_doc);
            // A form-fill environment, so form fields are drawn the way a viewer shows them. Version 1 with every
            // callback left out: PDFium checks each before it calls it.
            _formInfo = Marshal.AllocHGlobal(1024);
            unsafe { new Span<byte>((void*)_formInfo, 1024).Clear(); }
            Marshal.WriteInt32(_formInfo, 1);
            _form = FPDFDOC_InitFormFillEnvironment(_doc, _formInfo);
            if (_form != IntPtr.Zero) FPDF_SetFormFieldHighlightAlpha(_form, 0);
        }
    }

    /// <summary>The page's size in points (1/72 inch), as it is shown (its rotation applied).</summary>
    public SizeF PageSize(int index)
    {
        lock (Gate)
        {
            var page = FPDF_LoadPage(_doc, index);
            if (page == IntPtr.Zero) throw new InvalidOperationException($"page {index + 1} of the PDF cannot be read");
            try { return new SizeF(FPDF_GetPageWidthF(page), FPDF_GetPageHeightF(page)); }
            finally { FPDF_ClosePage(page); }
        }
    }

    /// <summary>The page as a white-backed bitmap of the given size, form fields and printable annotations included.</summary>
    public Bitmap Render(int index, int width, int height)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        var bits = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadWrite, PixelFormat.Format32bppRgb);
        try
        {
            lock (Gate)
            {
                var page = FPDF_LoadPage(_doc, index);
                if (page == IntPtr.Zero) throw new InvalidOperationException($"page {index + 1} of the PDF cannot be read");
                var target = FPDFBitmap_CreateEx(width, height, FPDFBitmap_BGRx, bits.Scan0, bits.Stride);
                try
                {
                    if (_form != IntPtr.Zero) FORM_OnAfterLoadPage(page, _form);
                    FPDFBitmap_FillRect(target, 0, 0, width, height, 0xFFFFFFFF);
                    FPDF_RenderPageBitmap(target, page, 0, 0, width, height, 0, FPDF_ANNOT | FPDF_PRINTING);
                    if (_form != IntPtr.Zero) FPDF_FFLDraw(_form, target, page, 0, 0, width, height, 0, FPDF_ANNOT | FPDF_PRINTING);
                }
                finally
                {
                    FPDFBitmap_Destroy(target); // the pixels belong to bmp
                    if (_form != IntPtr.Zero) FORM_OnBeforeClosePage(page, _form);
                    FPDF_ClosePage(page);
                }
            }
        }
        catch { bmp.UnlockBits(bits); bmp.Dispose(); throw; }
        bmp.UnlockBits(bits);
        return bmp;
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (_form != IntPtr.Zero) { FPDFDOC_ExitFormFillEnvironment(_form); _form = IntPtr.Zero; }
            if (_doc != IntPtr.Zero) { FPDF_CloseDocument(_doc); _doc = IntPtr.Zero; }
            if (_formInfo != IntPtr.Zero) { Marshal.FreeHGlobal(_formInfo); _formInfo = IntPtr.Zero; }
            if (_data != IntPtr.Zero) { Marshal.FreeHGlobal(_data); _data = IntPtr.Zero; }
        }
    }

    private static void Init()
    {
        if (_initialised) return;
        FPDF_InitLibrary();
        _initialised = true;
    }

    /// <summary>pdfium.dll from the exe's resources to %LOCALAPPDATA%\Glissando\pdfium-VERSION, once.</summary>
    private static string Unpack()
    {
        using var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("pdfium.dll")
            ?? throw new FileNotFoundException("pdfium.dll is not in this build");
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glissando", "pdfium-" + LibVersion);
        string path = Path.Combine(dir, "pdfium.dll");
        if (File.Exists(path) && new FileInfo(path).Length == res.Length) return path;
        Directory.CreateDirectory(dir);
        string temp = path + $".{Environment.ProcessId}.tmp";
        using (var fs = File.Create(temp)) res.CopyTo(fs);
        File.Move(temp, path, overwrite: true);
        Log.Info("unpacked " + path);
        return path;
    }

    [DllImport(Lib)] private static extern void FPDF_InitLibrary();
    [DllImport(Lib)] private static extern IntPtr FPDF_LoadMemDocument64(IntPtr data, nuint size, [MarshalAs(UnmanagedType.LPStr)] string? password);
    [DllImport(Lib)] private static extern uint FPDF_GetLastError();
    [DllImport(Lib)] private static extern int FPDF_GetPageCount(IntPtr doc);
    [DllImport(Lib)] private static extern void FPDF_CloseDocument(IntPtr doc);
    [DllImport(Lib)] private static extern IntPtr FPDF_LoadPage(IntPtr doc, int index);
    [DllImport(Lib)] private static extern void FPDF_ClosePage(IntPtr page);
    [DllImport(Lib)] private static extern float FPDF_GetPageWidthF(IntPtr page);
    [DllImport(Lib)] private static extern float FPDF_GetPageHeightF(IntPtr page);
    [DllImport(Lib)] private static extern IntPtr FPDFBitmap_CreateEx(int width, int height, int format, IntPtr firstScan, int stride);
    [DllImport(Lib)] private static extern int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
    [DllImport(Lib)] private static extern void FPDFBitmap_Destroy(IntPtr bitmap);
    [DllImport(Lib)] private static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int x, int y, int width, int height, int rotate, int flags);
    [DllImport(Lib)] private static extern IntPtr FPDFDOC_InitFormFillEnvironment(IntPtr doc, IntPtr formInfo);
    [DllImport(Lib)] private static extern void FPDFDOC_ExitFormFillEnvironment(IntPtr form);
    [DllImport(Lib)] private static extern void FORM_OnAfterLoadPage(IntPtr page, IntPtr form);
    [DllImport(Lib)] private static extern void FORM_OnBeforeClosePage(IntPtr page, IntPtr form);
    [DllImport(Lib)] private static extern void FPDF_FFLDraw(IntPtr form, IntPtr bitmap, IntPtr page, int x, int y, int width, int height, int rotate, int flags);
    [DllImport(Lib)] private static extern void FPDF_SetFormFieldHighlightAlpha(IntPtr form, byte alpha);
}
