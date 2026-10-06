using System.Diagnostics;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;

namespace Glissando.Transport;

/// <summary>
/// Decides at runtime which PC connects to which. Advertise if the adapter can, and scan at the
/// same time. When the other PC's advertisement is seen: a PC that is not advertising connects at
/// once; so does a PC that connected the last time the two linked (Settings.BleLastRole, which both
/// PCs remember for their own side, so the roles are fixed from the second link on). Otherwise it
/// waits for the peer to connect, and if the peer has not within its patience -- 8 to 18 s,
/// staggered by adapter address so the two never give up together -- connects anyway.
///
/// It used to compare Bluetooth addresses, its own against the one the peer's advertisement was seen
/// from. That one is a random private address Windows rotates, so the two PCs compared different
/// pairs, both concluded "the other has the lower address and should connect to us", and stood
/// advertising at each other (2026-09-23, the first time the second adapter advertised at all).
/// Putting the real address in the advertisement's service data does not fit next to the 128-bit
/// service UUID (StartedWithoutAllAdvertisementData). Remembering the outcome needs nothing on the air.
/// </summary>
public sealed class BleAutoChannel : IChannel
{
    public event Action<byte[]>? Received;
    public event Action<bool>? LinkChanged;
    public event Action<string>? StatusChanged;

    private readonly Func<string> _lastRole;
    private readonly Action<string> _rememberRole;

    /// <param name="lastRole">Reads Settings.BleLastRole: "connect", "wait" or "".</param>
    /// <param name="rememberRole">Stores it after a link comes up.</param>
    public BleAutoChannel(Func<string> lastRole, Action<string> rememberRole)
    {
        _lastRole = lastRole;
        _rememberRole = rememberRole;
    }

    private IChannel? _active;
    private CancellationTokenSource? _runCts;   // cancels the current RunOnceAsync (sleep)
    private volatile bool _asleep;
    private readonly SemaphoreSlim _wake = new(0);
    public bool IsLinked => _active?.IsLinked ?? false;
    public bool IsCentral => _active is BleCentralChannel;

    /// <summary>Runs the decision loop for the life of the session. Anything the Bluetooth stack throws --
    /// GATT setup or scanning refusing while the stack is not ready, or after a night's sleep -- is logged,
    /// shown in the tray and retried, instead of ending the loop in silence with the tray stuck on "starting"
    /// (2026-09-13: no advertising, no error, no link, until the app was restarted).</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_asleep)
            {
                // Everything is torn down; wait for Wake(). No radio work at all while the machine sleeps.
                try { await _wake.WaitAsync(ct); } catch (OperationCanceledException) { return; }
                continue;
            }
            var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _runCts = run;
            try
            {
                await RunOnceAsync(run.Token);
                if (ct.IsCancellationRequested) return;
                if (!_asleep) Log.Info("BLE auto: starting over in 5 s (advertising and scanning)");
            }
            catch (OperationCanceledException) { if (ct.IsCancellationRequested) return; }
            catch (Exception ex)
            {
                Log.Error("BLE auto: " + ex.Message + " - retrying in 5 s");
                StatusChanged?.Invoke("BLE error: " + Brief(ex.Message) + " (retrying)");
            }
            finally { _runCts = null; run.Dispose(); }
            if (_asleep) continue;
            try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { return; }
        }
    }

    public void Sleep()
    {
        if (_asleep) return;
        _asleep = true;
        Log.Info("BLE auto: sleeping - stopping advertising, releasing the GATT server and the link");
        StatusChanged?.Invoke("asleep");
        try { _runCts?.Cancel(); } catch { }
    }

    public void Wake()
    {
        if (!_asleep) return;
        _asleep = false;
        Log.Info("BLE auto: awake - starting over");
        _wake.Release();
    }

    private static string Brief(string s) => s.Length > 60 ? s[..60] + "..." : s;

    private async Task RunOnceAsync(CancellationToken ct)
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter == null) { StatusChanged?.Invoke("BLE error: no adapter"); Log.Error("no Bluetooth adapter"); return; }
        ulong myAddr = adapter.BluetoothAddress;

        BlePeripheralChannel? p = null;
        CancellationTokenSource? pCts = null;
        Task? pTask = null;
        Action? detachP = null;
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(BleIds.Service);
        Stopwatch? waitingSince = null; // the peer is in view and the rule says it should connect to us

        if (adapter.IsPeripheralRoleSupported)
        {
            pCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pToken = pCts.Token;
            void StartPeripheral()
            {
                detachP?.Invoke();
                p = new BlePeripheralChannel();
                detachP = Attach(p, up =>
                {
                    // While someone is connected to us there is nothing to decide; resume scanning if they leave.
                    if (up) { waitingSince = null; Remember("wait"); }
                    try { if (up) watcher.Stop(); else watcher.Start(); } catch { }
                });
                _active = p;
            }
            StartPeripheral();
            // The peripheral half, kept alive: a start that fails (Fail() returns, or the stack throws) is retried
            // after 5 s; a start that HANGS in one of its awaits (seen once with the stack out of sorts) is given
            // 45 s, warned about at 15, then abandoned for a fresh instance -- the stuck one is left to the GC.
            pTask = Task.Run(async () =>
            {
                while (!pToken.IsCancellationRequested)
                {
                    var cur = p!;
                    Task run;
                    // On a task of its own: the stack can hang INSIDE a WinRT call (GattServiceProvider.CreateAsync
                    // blocked synchronously on 2026-10-06), and called inline that took this watchdog down with it,
                    // so the stuck start was never reported.
                    run = Task.Run(() => cur.RunAsync(pToken));
                    var warned = false;
                    var startedAt = Stopwatch.StartNew();
                    while (true)
                    {
                        var done = await Task.WhenAny(run, Task.Delay(1000, pToken).ContinueWith(_ => { }));
                        if (pToken.IsCancellationRequested) return;
                        if (done == run) break;
                        if (cur.IsAdvertising || cur.IsLinked) { startedAt.Restart(); warned = false; continue; }
                        // An adapter that refuses to advertise (Aborted on every try) is not a stuck stack: this
                        // PC connects instead, and it was told so while the peer was merely away for 15 s.
                        if (!warned && startedAt.ElapsedMilliseconds > 15000 && !cur.AdvertisingRefused)
                        {
                            warned = true;
                            Log.Warn("BLE peripheral: still not advertising after 15 s (Bluetooth stack stuck? toggling Bluetooth off and on helps)");
                            StatusChanged?.Invoke("BLE error: advertising will not start (Bluetooth stuck?)");
                            BleHealth.Report("advertising does not start");
                        }
                        if (startedAt.ElapsedMilliseconds > 45000)
                        {
                            Log.Warn("BLE peripheral: start hung for 45 s; abandoning it and starting a fresh one");
                            run = Task.CompletedTask;
                            break;
                        }
                    }
                    if (run.IsFaulted)
                    {
                        var ex = run.Exception?.GetBaseException();
                        if (ex is not OperationCanceledException)
                        {
                            Log.Error("BLE peripheral: " + ex?.Message + " - retrying in 5 s");
                            StatusChanged?.Invoke("BLE error: " + Brief(ex?.Message ?? "?") + " (retrying)");
                        }
                    }
                    else if (!pToken.IsCancellationRequested) Log.Info("BLE peripheral: run ended (reconnect or failure); a fresh GATT server starts in 5 s");
                    try { await Task.Delay(5000, pToken); } catch (OperationCanceledException) { return; }
                    if (pToken.IsCancellationRequested) return;
                    StartPeripheral();
                }
            });
        }
        else
        {
            Log.Info("BLE auto: adapter cannot advertise; this PC will connect");
        }

        var decision = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = Stopwatch.StartNew();
        string why = "";
        long lastWaitLog = 0;
        int patienceMs = 8000 + (int)(myAddr % 11) * 1000; // 8-18 s, different on the two PCs
        string role = _lastRole() ?? "";
        // Our own attempts to connect have just failed several times with the peer in view: for this round,
        // be the one who waits, and wait long enough for the peer's own patience to run out so it connects
        // to us. The remembered roles then follow whichever way the link comes up.
        bool yielding = _yieldNextRound;
        _yieldNextRound = false;
        if (yielding)
        {
            role = "wait";
            patienceMs *= 3;
            Log.Info($"BLE auto: connecting kept failing; advertising and giving the other PC {patienceMs / 1000} s to connect to this one instead");
        }
        watcher.Received += (s, e) =>
        {
            if (e.BluetoothAddress == myAddr || e.BluetoothAddress == 0) return;
            if (p != null && p.IsLinked) return;
            if (p != null && started.ElapsedMilliseconds < 3000) return; // let our own advertising settle first
            bool advertising = p != null && p.IsAdvertising;
            if (!advertising) { why = "not advertising"; decision.TrySetResult(e.BluetoothAddress); return; }
            if (role == "connect") { why = "connected last time"; decision.TrySetResult(e.BluetoothAddress); return; }
            waitingSince ??= Stopwatch.StartNew();
            if (waitingSince.ElapsedMilliseconds > patienceMs)
            {
                why = $"the peer did not connect within {patienceMs / 1000} s";
                Log.Warn($"BLE auto: peer {e.BluetoothAddress:X12} has not connected to us in {patienceMs / 1000} s; connecting ourselves");
                decision.TrySetResult(e.BluetoothAddress);
                return;
            }
            long now = Environment.TickCount64;
            if (now - lastWaitLog > 10000)
            {
                lastWaitLog = now;
                Log.Info($"BLE auto: peer {e.BluetoothAddress:X12} seen; " + (role == "wait"
                    ? $"it connected to us last time, so it should again (giving it {patienceMs / 1000} s)"
                    : $"first link between these two: waiting {patienceMs / 1000} s for it to connect before connecting ourselves"));
            }
        };
        // Scanning can be refused while the stack is not ready ("device not ready"): log, say so, try again.
        while (true)
        {
            try { watcher.Start(); break; }
            catch (Exception ex)
            {
                Log.Error("BLE scan: " + ex.Message + " - retrying in 5 s");
                StatusChanged?.Invoke("BLE error: scanning refused (" + Brief(ex.Message) + "), retrying");
                await Task.Delay(5000, ct);
            }
        }
        StatusChanged?.Invoke(p != null ? "advertising, scanning" : "scanning");

        ulong peer;
        using (ct.Register(() => decision.TrySetCanceled()))
        {
            try { peer = await decision.Task; }
            catch (OperationCanceledException)
            {
                // Session end or Sleep(): stop scanning, stop advertising and release the GATT server for real.
                try { watcher.Stop(); } catch { }
                if (p != null) { pCts!.Cancel(); try { await pTask!; } catch { } detachP?.Invoke(); DisposeAside(p); }
                _active = null;
                return;
            }
        }
        try { watcher.Stop(); } catch { }

        Log.Info($"BLE auto: peer {peer:X12} seen; this PC ({myAddr:X12}) connects ({why})");
        if (p != null)
        {
            pCts!.Cancel();
            try { await pTask!; } catch { }
            detachP?.Invoke();
            DisposeAside(p);
        }

        // Three empty scans (60 s) and the central gives up, which ends this run: the loop above starts over
        // with this PC advertising again, so a peer whose advertising has died can connect to us instead.
        // Likewise three attempts in a row that reach the peer but end without a link: next round this PC waits.
        var c = new BleCentralChannel { GiveUpAfterEmptyScans = 3, GiveUpAfterFailedConnects = 3 };
        var detachC = Attach(c, up => { if (up) Remember("connect"); });
        _active = c;
        try { await c.RunAsync(ct); }
        finally { if (c.GaveUpOnFailedConnects) _yieldNextRound = true; detachC(); try { c.Dispose(); } catch { } _active = null; }
    }

    private bool _yieldNextRound;

    /// <summary>
    /// Release a peripheral without waiting for it. On a hung stack its StopAdvertising hangs too (LU,
    /// 2026-10-06): disposed inline, the peer had been seen and this PC was to connect, but the central never
    /// started, and exiting hung the same way. Off to the side, a hang costs a thread, not the link.
    /// </summary>
    private static void DisposeAside(BlePeripheralChannel p)
    {
        var t = Task.Run(() => { try { p.Dispose(); } catch { } });
        _ = Task.WhenAny(t, Task.Delay(10000)).ContinueWith(w =>
        {
            if (w.Result != t)
            {
                Log.Warn("BLE peripheral: releasing the old GATT server hangs (Bluetooth stack stuck); carrying on without it");
                BleHealth.Report("the old GATT server cannot be released");
            }
        });
    }

    private void Remember(string role)
    {
        try
        {
            if (_lastRole() == role) return;
            Log.Info($"BLE auto: remembering that this PC {(role == "connect" ? "connects" : "waits for the other PC to connect")}");
            _rememberRole(role);
        }
        catch (Exception ex) { Log.Warn("BLE auto: could not remember the role: " + ex.Message); }
    }

    private Action Attach(IChannel ch, Action<bool>? onLink)
    {
        Action<byte[]> r = b => Received?.Invoke(b);
        Action<bool> l = up => { LinkChanged?.Invoke(up); onLink?.Invoke(up); };
        Action<string> s = st => StatusChanged?.Invoke(st);
        ch.Received += r; ch.LinkChanged += l; ch.StatusChanged += s;
        return () => { ch.Received -= r; ch.LinkChanged -= l; ch.StatusChanged -= s; };
    }

    public Task<bool> SendAsync(byte[] packet) => _active?.SendAsync(packet) ?? Task.FromResult(false);

    public void Reconnect() => _active?.Reconnect();

    public void Dispose()
    {
        if (_active is BlePeripheralChannel p) DisposeAside(p);   // exiting must not hang on a stuck stack
        else _active?.Dispose();
    }
}
