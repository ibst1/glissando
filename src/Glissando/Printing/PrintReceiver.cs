using System.Diagnostics;
using System.Security.Cryptography;

namespace Glissando.Printing;

/// <summary>
/// The printing half of printing from the other PC: collects a job's chunks, answers PrintCheck with what is
/// still missing, and once the file is whole and its checksum matches, saves it under
/// %LOCALAPPDATA%\Glissando\Prints and prints it -- one job at a time.
/// </summary>
internal sealed class PrintReceiver
{
    private sealed class Incoming(PrintBeginInfo info)
    {
        public readonly PrintBeginInfo Info = info;
        public readonly byte[] Data = new byte[info.Size];
        public readonly bool[] Got = new bool[(info.Size + info.Chunk - 1) / info.Chunk];
        public int GotCount;
        public PrintState State = PrintState.Missing;
        public string Message = "";
        public long Touched = Stopwatch.GetTimestamp();
    }

    private readonly Dictionary<uint, Incoming> _jobs = [];
    private readonly Func<bool> _accept;
    private readonly Action<Frame> _send;
    private readonly SemaphoreSlim _printer = new(1, 1);
    private readonly Func<string, string, string, bool, (int Pages, string Note)> _print;
    private readonly Func<(string Default, string[] All)> _printers;

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glissando", "Prints");

    /// <summary>Something to tell the user here: (text, is an error). Raised on a worker thread.</summary>
    public event Action<string, bool>? Notice;

    /// <param name="print">(file, printer, document name, two-sided) -> pages and a note; DocumentPrinter.Print unless a test says otherwise.</param>
    public PrintReceiver(Func<bool> accept, Action<Frame> send, Func<string, string, string, bool, (int, string)>? print = null, Func<(string, string[])>? printers = null)
    {
        _accept = accept;
        _send = send;
        _print = print ?? ((file, printer, name, twoSided) => DocumentPrinter.Print(file, printer, name, twoSided));
        _printers = printers ?? DocumentPrinter.Printers;
    }

    public void OnBegin(Frame f)
    {
        if (PrintFrames.ParseBegin(f) is not { } info) return;
        lock (_jobs)
        {
            if (_jobs.ContainsKey(info.Job)) return;
            Expire();
            Incoming job;
            if (!_accept() || info.Size > PrintFrames.MaxBytes || info.Chunk > 2048 || !PrintFrames.Printable(info.Name))
            {
                // Keep a stub that says why, so the sender's PrintCheck gets an answer rather than Unknown.
                job = new Incoming(info with { Size = 1 })
                {
                    State = PrintState.Failed,
                    Message = !_accept() ? $"printing from the other PC is turned off on {Environment.MachineName}"
                        : info.Size > PrintFrames.MaxBytes ? "the file is too big"
                        : "only PDF files and pictures can be printed",
                };
            }
            else job = new Incoming(info);
            _jobs[info.Job] = job;
            if (job.State == PrintState.Missing) Log.Info($"print job {info.Job:x8} from the other PC: {info.Name}, {info.Size / 1024} kB, printer \"{info.Printer}\"{(info.TwoSided ? ", two-sided" : "")}");
        }
    }

    public void OnChunk(Frame f)
    {
        if (f.Payload.Length < 8) return;
        uint id = f.U32(0);
        int index = f.I32(4);
        lock (_jobs)
        {
            if (!_jobs.TryGetValue(id, out var job) || job.State != PrintState.Missing) return;
            if (index < 0 || index >= job.Got.Length || job.Got[index]) return;
            int offset = index * job.Info.Chunk, len = f.Payload.Length - 8;
            if (len != Math.Min(job.Info.Chunk, job.Info.Size - offset)) return;
            f.Payload.AsSpan(8).CopyTo(job.Data.AsSpan(offset));
            job.Got[index] = true;
            job.GotCount++;
            job.Touched = Stopwatch.GetTimestamp();
        }
    }

    public void OnCheck(Frame f)
    {
        if (f.Payload.Length < 4) return;
        uint id = f.U32(0);
        Frame answer;
        Incoming? toPrint = null;
        lock (_jobs)
        {
            if (!_jobs.TryGetValue(id, out var job)) answer = PrintFrames.Status(id, PrintState.Unknown);
            else
            {
                job.Touched = Stopwatch.GetTimestamp();
                if (job.State == PrintState.Missing && job.GotCount == job.Got.Length)
                {
                    if (SHA256.HashData(job.Data).AsSpan().SequenceEqual(job.Info.Sha256))
                    {
                        job.State = PrintState.Printing;
                        toPrint = job;
                    }
                    else
                    {
                        // Should not happen (every packet is sealed), but if it does: all of it once more.
                        Log.Warn($"print job {id:x8}: checksum mismatch, asking for the whole file again");
                        Array.Clear(job.Got);
                        job.GotCount = 0;
                    }
                }
                answer = job.State == PrintState.Missing ? PrintFrames.Status(id, PrintState.Missing, MissingRanges(job.Got))
                    : PrintFrames.Status(id, job.State, message: job.Message);
            }
        }
        _send(answer);
        if (toPrint != null) _ = Task.Run(() => Print(toPrint));
    }

    private async Task Print(Incoming job)
    {
        await _printer.WaitAsync();
        try
        {
            string printer = job.Info.Printer, note = "";
            var (def, all) = _printers();
            if (printer == "" || !all.Contains(printer, StringComparer.OrdinalIgnoreCase))
            {
                if (printer != "") note = $" (\"{printer}\" is not on this PC)";
                printer = def;
            }
            if (printer == "") throw new InvalidOperationException($"{Environment.MachineName} has no printer");
            string path = Save(job);
            var (pages, sides) = _print(path, printer, job.Info.Name, job.Info.TwoSided);
            if (sides != "") note += $" ({sides})";
            job.Message = $"{pages} page{(pages == 1 ? "" : "s")}{(job.Info.TwoSided && sides == "" ? " two-sided" : "")} on {printer}{note}";
            job.State = PrintState.Printed;
            Log.Info($"print job {job.Info.Job:x8}: {job.Info.Name} printed, {job.Message}");
            Notice?.Invoke($"Printed {job.Info.Name} from the other PC: {job.Message}", false);
        }
        catch (Exception ex)
        {
            job.Message = ex.Message;
            job.State = PrintState.Failed;
            Log.Warn($"print job {job.Info.Job:x8}: {job.Info.Name} failed: {ex}");
            Notice?.Invoke($"Could not print {job.Info.Name} from the other PC: {ex.Message}", true);
        }
        finally { _printer.Release(); }
        // Tell the sender now rather than at its next PrintCheck.
        _send(PrintFrames.Status(job.Info.Job, job.State, message: job.Message));
    }

    /// <summary>Keep the file, under a name of its own, for a week: handy to print again by hand.</summary>
    private static string Save(Incoming job)
    {
        Directory.CreateDirectory(Folder);
        foreach (var old in Directory.EnumerateFiles(Folder))
            try { if (File.GetLastWriteTime(old) < DateTime.Now.AddDays(-7)) File.Delete(old); } catch { }
        string name = string.Concat(Path.GetFileName(job.Info.Name).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (name.Trim('.', ' ', '_') == "") name = "print" + Path.GetExtension(job.Info.Name);
        string path = Path.Combine(Folder, $"{DateTime.Now:yyyyMMdd-HHmmss} {name}");
        File.WriteAllBytes(path, job.Data);
        return path;
    }

    /// <summary>The first few runs of chunks not yet here.</summary>
    internal static List<(int Start, int Count)> MissingRanges(bool[] got)
    {
        var ranges = new List<(int, int)>();
        for (int i = 0; i < got.Length && ranges.Count < PrintFrames.MaxRanges; i++)
        {
            if (got[i]) continue;
            int start = i;
            while (i + 1 < got.Length && !got[i + 1]) i++;
            ranges.Add((start, i - start + 1));
        }
        return ranges;
    }

    /// <summary>Forget jobs untouched for 15 minutes (a sender that gave up), so their memory goes.</summary>
    private void Expire()
    {
        long limit = Stopwatch.GetTimestamp() - 15 * 60 * Stopwatch.Frequency;
        foreach (var id in _jobs.Where(kv => kv.Value.Touched < limit && kv.Value.State != PrintState.Printing).Select(kv => kv.Key).ToList())
            _jobs.Remove(id);
    }
}
