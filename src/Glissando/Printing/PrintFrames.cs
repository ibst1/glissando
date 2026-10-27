using System.Buffers.Binary;
using System.Text;

namespace Glissando.Printing;

/// <summary>Where a print job stands on the PC that prints it, as told in a PrintStatus.</summary>
public enum PrintState : byte
{
    Unknown = 0,  // no such job here (its PrintBegin was lost, or this PC restarted): send the PrintBegin again
    Missing = 1,  // these chunk ranges have not arrived yet
    Printing = 2, // every byte is here and checks out; the printer is being fed
    Printed = 3,
    Failed = 4,   // with a message
}

public sealed record PrintBeginInfo(uint Job, int Size, int Chunk, byte[] Sha256, string Name, string Printer, bool TwoSided);

/// <summary>
/// Printing on the other PC's printers: the file itself travels (a PDF or a picture) and the other PC prints
/// it. Unlike the clipboard, a print job must arrive whole, so the sender streams the chunks and then asks
/// what is missing (PrintCheck); the printer answers with the missing ranges (PrintStatus), until nothing is.
/// </summary>
public static class PrintFrames
{
    /// <summary>File bytes per chunk: with the 9-byte header and the seal this fits a BLE packet (~500 bytes).</summary>
    public const int ChunkSize = 400;
    public const int MaxBytes = 64 * 1024 * 1024;
    public const int MaxRanges = 40;
    public const int BeginFixed = 4 + 4 + 2 + 32 + 1;

    /// <summary>Files the printing PC knows how to print.</summary>
    public static readonly string[] Extensions = [".pdf", ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"];

    public static bool Printable(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    // PrintBegin: job(u32) size(u32) chunk(u16) sha256(32) nameLen(1) name(UTF-8) printer(UTF-8, the rest; "" = default)
    // Two-sided: the printer is followed by "\n2" (a printer name has no line breaks). Nothing for one-sided, so a
    // peer older than the choice still finds the printer: to it "NAME\n1" was no printer, and it took its default.
    public static Frame Begin(uint job, int size, int chunk, byte[] sha, string name, string printer, bool twoSided)
    {
        var pr = Encoding.UTF8.GetBytes(twoSided ? printer + "\n2" : printer);
        var nm = Utf8Prefix(name, Math.Min(255, 400 - BeginFixed - pr.Length));
        var p = new byte[BeginFixed + nm.Length + pr.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(p, job);
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(4), size);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(8), (ushort)chunk);
        sha.AsSpan(0, 32).CopyTo(p.AsSpan(10));
        p[42] = (byte)nm.Length;
        nm.CopyTo(p, BeginFixed);
        pr.CopyTo(p, BeginFixed + nm.Length);
        return new(FrameType.PrintBegin, p);
    }

    public static PrintBeginInfo? ParseBegin(Frame f)
    {
        var p = f.Payload;
        if (p.Length < BeginFixed || BeginFixed + p[42] > p.Length) return null;
        int size = f.I32(4), chunk = f.U16(8);
        if (size <= 0 || chunk == 0) return null;
        string name = Encoding.UTF8.GetString(p, BeginFixed, p[42]);
        string printer = Encoding.UTF8.GetString(p, BeginFixed + p[42], p.Length - BeginFixed - p[42]);
        bool twoSided = false;
        int nl = printer.LastIndexOf('\n');
        if (nl >= 0) { twoSided = printer[(nl + 1)..] == "2"; printer = printer[..nl]; }
        return new(f.U32(0), size, chunk, p[10..42], name, printer, twoSided);
    }

    // PrintChunk: job(u32) index(u32) + the bytes at index * chunk
    public static Frame Chunk(uint job, int index, ReadOnlySpan<byte> data)
    {
        var p = new byte[8 + data.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(p, job);
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(4), index);
        data.CopyTo(p.AsSpan(8));
        return new(FrameType.PrintChunk, p);
    }

    // PrintCheck: job(u32)
    public static Frame Check(uint job)
    {
        var p = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(p, job);
        return new(FrameType.PrintCheck, p);
    }

    // PrintStatus: job(u32) state(1) + Missing: { start(u32) count(u32) }... | Printed, Failed: message (UTF-8)
    public static Frame Status(uint job, PrintState state, IReadOnlyList<(int Start, int Count)>? ranges = null, string message = "")
    {
        byte[] tail = state == PrintState.Missing && ranges != null ? new byte[8 * Math.Min(ranges.Count, MaxRanges)]
            : state is PrintState.Failed or PrintState.Printed ? Utf8Prefix(message, 300) : [];
        if (state == PrintState.Missing && ranges != null)
            for (int i = 0; i < tail.Length / 8; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(tail.AsSpan(8 * i), ranges[i].Start);
                BinaryPrimitives.WriteInt32LittleEndian(tail.AsSpan(8 * i + 4), ranges[i].Count);
            }
        var p = new byte[5 + tail.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(p, job);
        p[4] = (byte)state;
        tail.CopyTo(p, 5);
        return new(FrameType.PrintStatus, p);
    }

    public static (uint Job, PrintState State, List<(int Start, int Count)> Ranges, string Message)? ParseStatus(Frame f)
    {
        var p = f.Payload;
        if (p.Length < 5 || p[4] > (byte)PrintState.Failed) return null;
        var state = (PrintState)p[4];
        var ranges = new List<(int, int)>();
        string message = "";
        if (state == PrintState.Missing)
            for (int o = 5; o + 8 <= p.Length; o += 8) ranges.Add((f.I32(o), f.I32(o + 4)));
        else if (state is PrintState.Failed or PrintState.Printed)
            message = Encoding.UTF8.GetString(p, 5, p.Length - 5);
        return (f.U32(0), state, ranges, message);
    }

    // Printers: id(1) index(1) total(1) + a chunk of UTF-8 lines; the first line is the default printer, then
    // every printer. No lines at all: printing from the other PC is turned off there.
    public static IEnumerable<Frame> Printers(byte id, string defaultPrinter, IEnumerable<string> printers)
    {
        var all = printers.ToList();
        var bytes = all.Count == 0 ? [] : Encoding.UTF8.GetBytes(string.Join('\n', all.Prepend(defaultPrinter)));
        const int chunk = 380;
        int total = Math.Min(255, Math.Max(1, (bytes.Length + chunk - 1) / chunk));
        for (int i = 0; i < total; i++)
        {
            int start = i * chunk, len = Math.Max(0, Math.Min(chunk, bytes.Length - start));
            var p = new byte[3 + len];
            p[0] = id; p[1] = (byte)i; p[2] = (byte)total;
            if (len > 0) bytes.AsSpan(start, len).CopyTo(p.AsSpan(3));
            yield return new(FrameType.Printers, p);
        }
    }

    /// <summary>The longest whole-character prefix of <paramref name="s"/> that is at most <paramref name="max"/> bytes in UTF-8.</summary>
    public static byte[] Utf8Prefix(string s, int max)
    {
        max = Math.Max(0, max);
        var b = Encoding.UTF8.GetBytes(s);
        if (b.Length <= max) return b;
        int n = max;
        while (n > 0 && (b[n] & 0xC0) == 0x80) n--; // don't cut a character in two
        return b[..n];
    }
}
