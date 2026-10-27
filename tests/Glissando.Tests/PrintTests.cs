using System.Drawing;
using System.Drawing.Printing;
using Glissando.Printing;
using Xunit;

namespace Glissando.Tests;

public class PrintTests
{
    [Fact]
    public void Begin_RoundTrips_AndFitsABlePacket()
    {
        var sha = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        string name = string.Concat(Enumerable.Repeat("Årsredovisning för föreningen ", 20)) + ".pdf";
        var f = PrintFrames.Begin(7, 123456, PrintFrames.ChunkSize, sha, name, @"\\print.example\Följ-mig", true);
        Assert.True(f.Encode().Length <= 401);
        var info = PrintFrames.ParseBegin(f)!;
        Assert.Equal(7u, info.Job);
        Assert.Equal(123456, info.Size);
        Assert.Equal(sha, info.Sha256);
        Assert.Equal(@"\\print.example\Följ-mig", info.Printer);
        Assert.StartsWith("Årsredovisning", info.Name);
    }

    [Fact]
    public void Status_RoundTripsRangesAndMessages()
    {
        var st = PrintFrames.ParseStatus(PrintFrames.Status(9, PrintState.Missing, [(0, 3), (10, 1)]))!.Value;
        Assert.Equal(PrintState.Missing, st.State);
        Assert.Equal([(0, 3), (10, 1)], st.Ranges);
        var failed = PrintFrames.ParseStatus(PrintFrames.Status(9, PrintState.Failed, message: "papperet är slut"))!.Value;
        Assert.Equal("papperet är slut", failed.Message);
    }

    [Fact]
    public void MissingRanges_AreRunsOfMissingChunks()
    {
        bool[] got = [true, false, false, true, false, true];
        Assert.Equal([(1, 2), (4, 1)], PrintReceiver.MissingRanges(got));
        Assert.Empty(PrintReceiver.MissingRanges([true, true]));
    }

    /// <summary>A whole job over a link that loses a fifth of the chunks: the file arrives intact and printed.</summary>
    [Fact]
    public async Task Job_ArrivesWhole_OverALossyLink()
    {
        var file = Path.Combine(Path.GetTempPath(), $"glissando-test-{Guid.NewGuid():N}.pdf");
        var data = new byte[50_000];
        new Random(1).NextBytes(data);
        File.WriteAllBytes(file, data);
        byte[]? printed = null;
        string? printedOn = null;
        PrintSender? sender = null;
        var rnd = new Random(2);
        var receiver = new PrintReceiver(() => true, f => sender!.OnStatus(f),
            print: (path, printer, _, twoSided) => { printed = File.ReadAllBytes(path); printedOn = printer; return (2, twoSided ? "" : "unexpected"); },
            printers: () => ("Kontor", new[] { "Kontor", "Färg" }));
        void Deliver(Frame f)
        {
            switch (f.Type)
            {
                case FrameType.PrintBegin: receiver.OnBegin(f); break;
                case FrameType.PrintChunk: if (rnd.NextDouble() > 0.2) receiver.OnChunk(f); break;
                case FrameType.PrintCheck: receiver.OnCheck(f); break;
            }
        }
        sender = new PrintSender(Deliver, _ => false, () => 0, () => true, () => 20);
        var done = new TaskCompletionSource<(bool, string)>();
        sender.Finished += (_, ok, msg) => done.TrySetResult((ok, msg));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        _ = sender.RunAsync(cts.Token);
        sender.Add(new PrintJob(file, "Färg", false, TwoSided: true));
        var (ok, message) = await done.Task.WaitAsync(cts.Token);
        File.Delete(file);
        Assert.True(ok, message);
        Assert.Equal(data, printed);
        Assert.Equal("Färg", printedOn);
        Assert.Contains("2 pages two-sided on Färg", message);
    }

    [Fact]
    public void Receiver_SaysWhy_WhenPrintingIsOff()
    {
        Frame? answer = null;
        var receiver = new PrintReceiver(() => false, f => answer = f);
        receiver.OnBegin(PrintFrames.Begin(3, 1000, PrintFrames.ChunkSize, new byte[32], "a.pdf", "", false));
        receiver.OnCheck(PrintFrames.Check(3));
        var st = PrintFrames.ParseStatus(answer!.Value)!.Value;
        Assert.Equal(PrintState.Failed, st.State);
        Assert.Contains("turned off", st.Message);
    }

    /// <summary>The real printing path, into "Microsoft Print to PDF": a picture becomes a one-page PDF, and that
    /// PDF printed again is still one page.</summary>
    [Fact]
    public void Pdf_And_Picture_PrintThroughWindows()
    {
        const string pdfPrinter = "Microsoft Print to PDF";
        if (!PrinterSettings.InstalledPrinters.Cast<string>().Contains(pdfPrinter)) return;
        string dir = Path.Combine(Path.GetTempPath(), $"glissando-print-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string png = Path.Combine(dir, "test.png"), first = Path.Combine(dir, "first.pdf"), second = Path.Combine(dir, "second.pdf");
            using (var bmp = new Bitmap(800, 600))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                g.DrawRectangle(Pens.Black, 10, 10, 780, 580);
                g.DrawString("Glissando print test", new Font("Arial", 40), Brushes.Black, 60, 250);
                bmp.Save(png);
            }
            Assert.Equal(1, DocumentPrinter.Print(png, pdfPrinter, "test.png", false, first).Pages);
            Assert.True(new FileInfo(first).Length > 1000);
            Assert.Equal(1, DocumentPrinter.Print(first, pdfPrinter, "first.pdf", true, second).Pages);
            Assert.True(new FileInfo(second).Length > 1000);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
