using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace Glissando.Printing;

/// <summary>A file to print on the other PC. <see cref="FromFolder"/>: it lay in the print folder, which moves it
/// to Sent or Failed afterwards.</summary>
public sealed record PrintJob(string Path, string Printer, bool FromFolder);

/// <summary>
/// The sending half of printing on the other PC: one job at a time, in the order given. Chunks go over the UDP
/// path when it is up, else on the BLE queue a few at a time, so keys and moves never wait behind a whole file.
/// After each pass the sender asks what is missing and sends that, until the other PC has it all; then it
/// waits for the printer's verdict.
/// </summary>
internal sealed class PrintSender
{
    private readonly Channel<PrintJob> _jobs = Channel.CreateUnbounded<PrintJob>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Action<Frame> _enqueue;
    private readonly Func<Frame, bool> _sendFast;
    private readonly Func<int> _queued;
    private readonly Func<bool> _ready;
    private readonly Func<double> _rttMs;
    private readonly object _gate = new();
    private TaskCompletionSource<(PrintState State, List<(int Start, int Count)> Ranges, string Message)>? _answer;
    private uint _answerJob;
    private int _pending;

    /// <summary>A job ended: printed (true) or not, with the reason. Raised on a worker thread.</summary>
    public event Action<PrintJob, bool, string>? Finished;
    /// <summary>Progress text changed. Raised on a worker thread.</summary>
    public event Action? ProgressChanged;

    /// <summary>What is being sent now, for the tray ("" when nothing).</summary>
    public string Progress { get; private set; } = "";

    public int Pending => Volatile.Read(ref _pending);

    public PrintSender(Action<Frame> enqueue, Func<Frame, bool> sendFast, Func<int> queued, Func<bool> ready, Func<double> rttMs)
    {
        _enqueue = enqueue;
        _sendFast = sendFast;
        _queued = queued;
        _ready = ready;
        _rttMs = rttMs;
    }

    public void Add(PrintJob job)
    {
        Interlocked.Increment(ref _pending);
        _jobs.Writer.TryWrite(job);
    }

    /// <summary>A PrintStatus from the other PC.</summary>
    public void OnStatus(Frame f)
    {
        if (PrintFrames.ParseStatus(f) is not { } st) return;
        lock (_gate)
            if (_answer != null && st.Job == _answerJob) _answer.TrySetResult((st.State, st.Ranges, st.Message));
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var job in _jobs.Reader.ReadAllAsync(ct))
            {
                string result;
                bool ok = false;
                try { (ok, result) = await SendOne(job, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { result = ex.Message; }
                Interlocked.Decrement(ref _pending);
                SetProgress("");
                Log.Info($"print {Path.GetFileName(job.Path)}: {(ok ? "printed" : "failed")} {result}");
                Finished?.Invoke(job, ok, result);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<(bool, string)> SendOne(PrintJob job, CancellationToken ct)
    {
        string name = Path.GetFileName(job.Path);
        string where = job.Printer == "" ? "the default printer" : job.Printer;
        byte[] data = await ReadWhenWritten(job.Path, ct);
        if (data.Length == 0) return (false, "the file is empty");
        if (data.Length > PrintFrames.MaxBytes) return (false, $"the file is {data.Length / 1048576} MB; at most {PrintFrames.MaxBytes / 1048576} MB can be printed on the other PC");
        if (!PrintFrames.Printable(job.Path)) return (false, "only PDF files and pictures can be printed on the other PC");

        uint id = (uint)Random.Shared.Next(1, int.MaxValue);
        int chunks = (data.Length + PrintFrames.ChunkSize - 1) / PrintFrames.ChunkSize;
        var begin = PrintFrames.Begin(id, data.Length, PrintFrames.ChunkSize, SHA256.HashData(data), name, job.Printer);
        var check = PrintFrames.Check(id);
        Log.Info($"print {name}: {data.Length / 1024} kB in {chunks} chunks to {where}");
        SetProgress($"Sending {name} to {where}");

        bool begun = false;
        int unknowns = 0, rounds = 0;
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var st = await Ask(id, begun ? [check] : [begin, check], ct);
            if (st == null) return (false, "the other PC did not answer");
            switch (st.Value.State)
            {
                case PrintState.Unknown:
                    // The PrintBegin was lost, or the other Glissando restarted and lost what it had.
                    begun = false;
                    if (++unknowns > 5) return (false, "the other PC did not take the job");
                    break;
                case PrintState.Missing:
                    begun = true;
                    if (++rounds > 200) return (false, "too many pieces went missing on the way");
                    int missing = st.Value.Ranges.Sum(r => r.Count), sent = 0, burst = 0;
                    if (rounds > 1) Log.Info($"print {name}: round {rounds}, {missing} chunks missing");
                    foreach (var (start, count) in st.Value.Ranges)
                        for (int i = start; i < start + count && i < chunks; i++)
                        {
                            if (i < 0) continue;
                            int len = Math.Min(PrintFrames.ChunkSize, data.Length - i * PrintFrames.ChunkSize);
                            var chunk = PrintFrames.Chunk(id, i, data.AsSpan(i * PrintFrames.ChunkSize, len));
                            if (_sendFast(chunk))
                            {
                                // A few MB/s at most: a burst of datagrams beyond what the Wi-Fi takes is only lost.
                                if (++burst % 8 == 0) await Task.Delay(1, ct);
                            }
                            else
                            {
                                // On Bluetooth, keep only a few chunks in the queue so input never waits behind the file.
                                while (_queued() > 4 || !_ready())
                                {
                                    if (!await WaitReady(ct)) return (false, "the link to the other PC was lost");
                                    await Task.Delay(5, ct);
                                }
                                _enqueue(chunk);
                            }
                            if (++sent % 64 == 0)
                            {
                                int pct = (int)(100L * (chunks - missing + sent) / chunks);
                                SetProgress($"Sending {name} to {where}: {pct}%");
                            }
                        }
                    break;
                case PrintState.Printing:
                    if (rounds > 0) { Log.Info($"print {name}: delivered in {sw.Elapsed.TotalSeconds:0.0} s, {rounds} round(s)"); rounds = 0; }
                    SetProgress($"Printing {name} on {where}");
                    // A long document takes a while to print; ask again now and then.
                    if (sw.Elapsed > TimeSpan.FromMinutes(30)) return (false, "the other PC is still printing after 30 minutes");
                    await Task.Delay(2000, ct);
                    break;
                case PrintState.Printed:
                    return (true, st.Value.Message);
                case PrintState.Failed:
                    return (false, st.Value.Message == "" ? "printing failed on the other PC" : st.Value.Message);
            }
        }
    }

    /// <summary>Send the frames and wait for the job's PrintStatus, a few times over; null if none came.</summary>
    private async Task<(PrintState State, List<(int Start, int Count)> Ranges, string Message)?> Ask(uint id, Frame[] frames, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            if (!await WaitReady(ct)) return null;
            var tcs = new TaskCompletionSource<(PrintState, List<(int, int)>, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) { _answer = tcs; _answerJob = id; }
            foreach (var f in frames) _enqueue(f);
            // Behind the chunks still queued on Bluetooth, and the link's own round trip.
            int timeout = 3000 + (int)Math.Min(20_000, 3 * _rttMs()) + 100 * _queued();
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct));
            lock (_gate) if (_answer == tcs) _answer = null;
            if (done == tcs.Task) return await tcs.Task;
        }
        return null;
    }

    /// <summary>Wait, at most ten minutes, for the link to be authenticated.</summary>
    private async Task<bool> WaitReady(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (!_ready())
        {
            if (sw.Elapsed > TimeSpan.FromMinutes(10)) return false;
            await Task.Delay(500, ct);
        }
        return true;
    }

    /// <summary>The file's bytes once whoever writes it is done: "Microsoft Print to PDF" keeps the file open
    /// while it writes, so wait until it can be opened without sharing write access.</summary>
    private static async Task<byte[]> ReadWhenWritten(string path, CancellationToken ct)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var b = new byte[fs.Length];
                fs.ReadExactly(b);
                return b;
            }
            catch (IOException) when (i < 120) { await Task.Delay(500, ct); }
        }
    }

    private void SetProgress(string s)
    {
        if (s == Progress) return;
        Progress = s;
        ProgressChanged?.Invoke();
    }
}
