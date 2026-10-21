using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using InTheHand.Net;
using InTheHand.Net.Bluetooth;
using InTheHand.Net.Sockets;

namespace BtTest;

// RFCOMM latency probe: one side echoes 32-byte frames, the other measures RTT
// under several traffic patterns (steady 125 Hz, idle wake-up, keepalive, burst).
internal static class Program
{
    private const int FrameSize = 32;
    private static readonly Guid ServiceId = new("8b6c4b3e-2f0d-4f4c-9a1b-6e2f3c5d7a90");

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);

    private static StreamWriter? _log;

    private static int Main(string[] args)
    {
        timeBeginPeriod(1);
        if (args.Length == 0)
        {
            Console.WriteLine("usage:");
            Console.WriteLine("  bttest list                 show local radio and paired devices");
            Console.WriteLine("  bttest listen               echo server (run on one PC)");
            Console.WriteLine("  bttest connect <address>    run latency tests against the listener");
            Console.WriteLine("  bttest blemesh [seconds]    run on every PC: link each to each over BLE, ping all links every round");
            return 1;
        }

        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "list": return List();
                case "listen": return Listen();
                case "connect":
                    if (args.Length < 2) { Console.WriteLine("connect needs an address like 00:11:22:33:44:55"); return 1; }
                    return Connect(args[1], null);
                case "rate":
                    if (args.Length < 4) { Console.WriteLine("rate <address> <hz> <seconds> [hz seconds ...]"); return 1; }
                    var runs = new List<(double hz, int seconds)>();
                    for (int i = 2; i + 1 < args.Length; i += 2)
                        runs.Add((double.Parse(args[i]), int.Parse(args[i + 1])));
                    return Connect(args[1], runs);
                case "blescan":
                    return Ble.Scan(args.Length > 1 ? int.Parse(args[1]) : 15).GetAwaiter().GetResult();
                case "blemesh":
                    return Mesh.Run(args.Length > 1 ? int.Parse(args[1]) : 30).GetAwaiter().GetResult();
                case "bleserve":
                    return Ble.Serve().GetAwaiter().GetResult();
                case "bleconnect":
                {
                    // bleconnect [none|balanced|throughput|power] [hz seconds ...]
                    string prefer = "none";
                    int i = 1;
                    if (args.Length > 1 && !double.TryParse(args[1], out _)) { prefer = args[1].ToLowerInvariant(); i = 2; }
                    List<(double hz, int seconds)>? bleRuns = null;
                    if (i + 1 < args.Length)
                    {
                        bleRuns = new();
                        for (; i + 1 < args.Length; i += 2) bleRuns.Add((double.Parse(args[i]), int.Parse(args[i + 1])));
                    }
                    return Ble.Connect(prefer, bleRuns).GetAwaiter().GetResult();
                }
                default: Console.WriteLine("unknown command"); return 1;
            }
        }
        catch (Exception e)
        {
            Log($"ERROR: {e.GetType().Name}: {e.Message}");
            Log(e.ToString());
            return 2;
        }
        finally { _log?.Flush(); _log?.Dispose(); }
    }

    internal static void Log(string s)
    {
        Console.WriteLine(s);
        _log?.WriteLine(s);
        _log?.Flush();
    }

    internal static void OpenLog(string role)
    {
        var dir = AppContext.BaseDirectory;
        var path = Path.Combine(dir, $"bttest-{role}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        _log = new StreamWriter(path, false);
        Log($"log: {path}");
        Log($"machine: {Environment.MachineName}, {Environment.OSVersion}");
    }

    private static int List()
    {
        var radio = BluetoothRadio.Default;
        Console.WriteLine(radio == null
            ? "No Bluetooth radio found."
            : $"Local radio: {radio.Name}  address {radio.LocalAddress}  mode {radio.Mode}");
        using var c = new BluetoothClient();
        Console.WriteLine("Paired devices:");
        foreach (var d in c.PairedDevices)
            Console.WriteLine($"  {d.DeviceAddress}  {d.DeviceName}  connected={d.Connected}");
        return 0;
    }

    private static int Listen()
    {
        OpenLog("listen");
        var radio = BluetoothRadio.Default;
        Log(radio == null ? "No Bluetooth radio found." : $"Local radio: {radio.Name}  address {radio.LocalAddress}");
        var listener = new BluetoothListener(ServiceId) { ServiceName = "GlissandoBtTest" };
        listener.Start();
        Log("Listening for RFCOMM connections. On the other PC run:  bttest connect " + radio?.LocalAddress);
        while (true)
        {
            using var client = listener.AcceptBluetoothClient();
            Log($"{DateTime.Now:HH:mm:ss.fff} client connected");
            using var s = client.GetStream();
            var buf = new byte[FrameSize];
            long n = 0;
            try
            {
                while (ReadExact(s, buf))
                {
                    s.Write(buf, 0, buf.Length);
                    s.Flush();
                    n++;
                }
            }
            catch (Exception e) { Log($"stream ended: {e.GetType().Name}: {e.Message}"); }
            Log($"{DateTime.Now:HH:mm:ss.fff} client disconnected after {n} frames");
        }
    }

    private static int Connect(string address, List<(double hz, int seconds)>? rateRuns)
    {
        OpenLog(rateRuns == null ? "connect" : "rate");
        var addr = BluetoothAddress.Parse(address);
        using var client = new BluetoothClient();
        var sw = Stopwatch.StartNew();
        Log($"Connecting to {addr} ...");
        client.Connect(addr, ServiceId);
        Log($"Connected in {sw.ElapsedMilliseconds} ms");
        using var s = client.GetStream();

        var results = new ConcurrentDictionary<long, double>();
        var stop = false;
        var reader = new Thread(() =>
        {
            var buf = new byte[FrameSize];
            try
            {
                while (!stop && ReadExact(s, buf))
                {
                    long seq = BitConverter.ToInt64(buf, 0);
                    long sent = BitConverter.ToInt64(buf, 8);
                    double rtt = (Stopwatch.GetTimestamp() - sent) * 1000.0 / Stopwatch.Frequency;
                    results[seq] = rtt;
                }
            }
            catch (Exception e) { if (!stop) Log($"reader ended: {e.GetType().Name}: {e.Message}"); }
        }) { IsBackground = true };
        reader.Start();

        long seq = 0;
        var frame = new byte[FrameSize];

        void Send()
        {
            BitConverter.TryWriteBytes(frame.AsSpan(0, 8), seq);
            BitConverter.TryWriteBytes(frame.AsSpan(8, 8), Stopwatch.GetTimestamp());
            s.Write(frame, 0, frame.Length);
            s.Flush();
            seq++;
        }

        // Sends `count` frames spaced `intervalMs` apart, waits for replies, reports stats.
        void Run(string name, int count, double intervalMs, double waitMs)
        {
            long first = seq;
            var t = Stopwatch.StartNew();
            for (int i = 0; i < count; i++)
            {
                Send();
                double target = (i + 1) * intervalMs;
                while (t.Elapsed.TotalMilliseconds < target)
                {
                    double left = target - t.Elapsed.TotalMilliseconds;
                    if (left > 2) Thread.Sleep(1); else Thread.SpinWait(200);
                }
            }
            long last = seq - 1;
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed.TotalMilliseconds < waitMs)
            {
                bool all = true;
                for (long q = first; q <= last; q++) if (!results.ContainsKey(q)) { all = false; break; }
                if (all) break;
                Thread.Sleep(1);
            }
            var rtts = new List<double>();
            int lost = 0;
            for (long q = first; q <= last; q++)
                if (results.TryGetValue(q, out var r)) rtts.Add(r); else lost++;
            Report(name, rtts, lost, count);
        }

        Log("");
        Log("A. warm-up");
        Run("warm-up 100 x 8 ms", 100, 8, 2000);

        if (rateRuns != null)
        {
            foreach (var (hz, seconds) in rateRuns)
            {
                double interval = 1000.0 / hz;
                Run($"{hz} Hz x {seconds} s ({interval:F1} ms gaps)", (int)(hz * seconds), interval, 3000);
            }
            Log("done");
            stop = true;
            return 0;
        }

        Log("");
        Log("A. steady 125 Hz for 10 s (mouse-move rate)");
        Run("125 Hz x 10 s", 1250, 8, 2000);

        Log("");
        Log("B. idle wake-up: 10 s silence, then one frame, then 20 frames at 8 ms");
        for (int round = 1; round <= 3; round++)
        {
            Thread.Sleep(10_000);
            Run($"round {round} first frame after 10 s idle", 1, 0, 5000);
            Run($"round {round} next 20 frames", 20, 8, 2000);
        }

        Log("");
        Log("C. keepalive: does periodic traffic keep the link awake?");
        Run("keepalive 2 Hz x 10 s (500 ms gaps)", 20, 500, 3000);
        Run("keepalive 1 Hz x 10 s (1000 ms gaps)", 10, 1000, 3000);
        Run("keepalive 4 Hz x 10 s (250 ms gaps)", 40, 250, 3000);

        Log("");
        Log("D. burst: 200 frames as fast as possible (buffering / coalescing)");
        {
            long first = seq;
            var t = Stopwatch.StartNew();
            for (int i = 0; i < 200; i++) Send();
            double sendMs = t.Elapsed.TotalMilliseconds;
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed.TotalMilliseconds < 5000 && !results.ContainsKey(seq - 1)) Thread.Sleep(1);
            double totalMs = t.Elapsed.TotalMilliseconds;
            var rtts = new List<double>();
            int lost = 0;
            for (long q = first; q < seq; q++) if (results.TryGetValue(q, out var r)) rtts.Add(r); else lost++;
            Log($"burst: sent 200 frames in {sendMs:F1} ms, last reply after {totalMs:F1} ms ({200 * FrameSize * 8 / totalMs:F0} kbit/s round trip)");
            Report("burst per-frame RTT", rtts, lost, 200);
        }

        Log("");
        Log("E. steady 125 Hz again for 5 s (after everything)");
        Run("125 Hz x 5 s", 625, 8, 2000);

        Log("");
        Log("done");
        stop = true;
        return 0;
    }

    internal static void Report(string name, List<double> rtts, int lost, int count)
    {
        if (rtts.Count == 0) { Log($"{name}: no replies ({lost}/{count} lost)"); return; }
        rtts.Sort();
        double P(double p) => rtts[Math.Min(rtts.Count - 1, (int)Math.Floor(p * (rtts.Count - 1)))];
        Log($"{name}: n={rtts.Count} lost={lost}  min={rtts[0]:F1}  p50={P(0.5):F1}  p95={P(0.95):F1}  max={rtts[^1]:F1} ms");
    }

    private static bool ReadExact(Stream s, byte[] buf)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int n = s.Read(buf, off, buf.Length - off);
            if (n <= 0) return false;
            off += n;
        }
        return true;
    }
}
