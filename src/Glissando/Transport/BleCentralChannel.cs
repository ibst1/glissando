using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace Glissando.Transport;

/// <summary>This PC scans for a Glissando peripheral, connects, and reconnects whenever the link drops.</summary>
public sealed class BleCentralChannel : IChannel
{
    public event Action<byte[]>? Received;
    public event Action<bool>? LinkChanged;
    public event Action<string>? StatusChanged;

    public bool IsLinked { get; private set; }
    public bool IsCentral => true;

    private BluetoothLEDevice? _device;
    private GattSession? _session;
    private GattDeviceService? _service;
    private GattCharacteristic? _rx, _tx;
    private TaskCompletionSource? _dropped;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public async Task RunAsync(CancellationToken ct)
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter == null) { StatusChanged?.Invoke("BLE error: no adapter"); Log.Error("no Bluetooth adapter"); return; }
        Log.Info($"BLE adapter {adapter.BluetoothAddress:X12} central={adapter.IsCentralRoleSupported}");

        int emptyScans = 0, failedConnects = 0;
        while (!ct.IsCancellationRequested)
        {
            bool attempted = false;
            _linkedThisAttempt = false;
            try
            {
                bool found = await ConnectOnceAsync(ct);
                attempted = found;
                if (!found)
                {
                    emptyScans++;
                    if (GiveUpAfterEmptyScans > 0 && emptyScans >= GiveUpAfterEmptyScans)
                    {
                        Log.Warn($"BLE central: no advertisement from the peer in {emptyScans} scans ({emptyScans * ScanSeconds} s); giving the central role up so this PC advertises again and the peer can connect instead");
                        return;
                    }
                    Log.Info($"BLE central: no advertisement from the peer in {ScanSeconds} s; scanning again");
                    continue;
                }
                emptyScans = 0;
            }
            catch (OperationCanceledException) { break; }
            catch (TimeoutException ex)
            {
                // A GATT step that never answers: seen on a fresh connection right after the peer's own restart
                // (52 s of silence until Windows dropped the link, 2026-09-15). Treat the link as stale below.
                Log.Warn("BLE central: " + ex.Message + " - releasing the link so Windows drops it");
                BleHealth.Report("a new link does not answer (" + ex.Message + ")");
                _hung = true;
                attempted = true;
            }
            catch (Exception ex)
            {
                Log.Warn("BLE central: " + ex.Message);
                attempted = true;
            }
            finally
            {
                TearDown();
            }
            // The peer is in plain view and yet no attempt gets as far as a link: after sleep this PC's radio can
            // hold a connection to the peer whose far end is gone, and every GATT step on it fails or hangs
            // (2026-10-04: "connecting" for as long as anyone watched, the peer "advertising"). Stop being the
            // one who connects; BleAutoChannel then lets the peer connect to us instead.
            if (attempted)
            {
                if (_linkedThisAttempt) failedConnects = 0;
                else if (GiveUpAfterFailedConnects > 0 && ++failedConnects >= GiveUpAfterFailedConnects)
                {
                    Log.Warn($"BLE central: {failedConnects} attempts in a row reached the peer but no link; giving the central role up so the peer can connect to this PC instead");
                    GaveUpOnFailedConnects = true;
                    return;
                }
            }
            // A connection that pre-dated us and never carried a byte from the peer is a leftover from its
            // previous process or from before sleep: its GATT server is gone, and Windows keeps the LE link up
            // for as long as anyone holds it. Releasing it (TearDown) and re-acquiring 1.5 s later kept it
            // alive for minutes (2-4 min per restart, 2026-09-15): the link went away only when a retry
            // happened to leave it alone long enough. So wait until the radio really reports it gone.
            if ((_hung || (_preConnected && !_gotRx)) && _staleDeviceId != null)
            {
                _hung = false;
                try { await WaitUntilRadioDropsAsync(_staleDeviceId, ct); } catch (OperationCanceledException) { break; }
            }
            try { await Task.Delay(1500, ct); } catch (OperationCanceledException) { break; }
        }
        TearDown();
    }

    private bool _preConnected, _gotRx, _hung;
    private string? _staleDeviceId;
    private const int ScanSeconds = 20;

    /// <summary>After this many consecutive scans without the peer's advertisement, RunAsync returns instead of
    /// scanning forever. BleAutoChannel sets it: the peer's advertising may have died (its adapter reported
    /// "started" while nothing was on the air, 2026-09-24) while it can still connect, and this PC stopped
    /// advertising the moment it took the central role -- so both waited for an advertisement that never came.
    /// Going back to the auto loop puts this PC's advertisement on the air again. 0 = scan forever (pinned mode).</summary>
    public int GiveUpAfterEmptyScans { get; set; }

    /// <summary>After this many consecutive attempts that found the peer's advertisement but did not end in a
    /// link, RunAsync returns with <see cref="GaveUpOnFailedConnects"/> set. 0 = keep trying (pinned mode).</summary>
    public int GiveUpAfterFailedConnects { get; set; }

    /// <summary>RunAsync ended because connecting kept failing with the peer in view.</summary>
    public bool GaveUpOnFailedConnects { get; private set; }

    private bool _linkedThisAttempt;

    /// <summary>A GATT step failed. If the LE connection was already there when we looked the device up, it is
    /// most likely a leftover whose GATT server is gone; the retry loop waits for Windows to drop it.</summary>
    private void FailedOnPossiblyStaleLink(string what, bool preConnected)
    {
        if (preConnected) Log.Warn($"BLE central: {what} on a pre-existing connection - releasing it so Windows drops the old link");
        else Log.Warn($"BLE central: {what}");
    }

    /// <summary>Poll the device's connected state - through the enumeration property, which does not touch the
    /// link - until Windows has dropped it, for at most 30 s. Holding a BluetoothLEDevice for the check would
    /// keep the very link alive that we are waiting to see go.</summary>
    private static async Task WaitUntilRadioDropsAsync(string deviceId, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            bool? connected = await IsRadioConnectedAsync(deviceId);
            if (connected != true)
            {
                Log.Info(connected == false
                    ? $"BLE central: Windows dropped the old link after {sw.Elapsed.TotalSeconds:F1} s; reconnecting"
                    : "BLE central: cannot read the link state; reconnecting");
                return;
            }
            await Task.Delay(500, ct);
        }
        Log.Warn("BLE central: the old link is still up after 30 s (something else on this PC holds it?); reconnecting anyway");
    }

    private static async Task<bool?> IsRadioConnectedAsync(string deviceId)
    {
        try
        {
            var info = await DeviceInformation.CreateFromIdAsync(deviceId, new[] { "System.Devices.Aep.IsConnected" });
            return info.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var v) && v is bool b ? b : null;
        }
        catch (Exception ex)
        {
            Log.Warn("BLE central: link state lookup failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>Await a GATT step with a 10 s limit. A step that hangs is a link that is not really there.</summary>
    private static async Task<T> Step<T>(IAsyncOperation<T> op, string what, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try { return await op.AsTask(linked.Token); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException($"{what} did not answer within 10 s");
        }
    }

    /// <returns>False when the scan saw no advertisement from the peer; true after any attempt to connect.</returns>
    private async Task<bool> ConnectOnceAsync(CancellationToken ct)
    {
        StatusChanged?.Invoke("scanning");
        ulong address = await ScanAsync(ct);
        if (address == 0) return false;

        StatusChanged?.Invoke("connecting");
        _dropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _device = await Step(BluetoothLEDevice.FromBluetoothAddressAsync(address), "device lookup", ct);
        if (_device == null) { Log.Warn("FromBluetoothAddressAsync returned null"); return true; }
        bool preConnected = _device.ConnectionStatus == BluetoothConnectionStatus.Connected;
        _preConnected = preConnected; _gotRx = false; _staleDeviceId = _device.DeviceId;
        _firstRx = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (preConnected) Log.Info("BLE central: the peer is already connected at the radio level (a link left over from before)");
        _device.ConnectionStatusChanged += (s, e) =>
        {
            Log.Info($"BLE connection {s.ConnectionStatus}");
            if (s.ConnectionStatus == BluetoothConnectionStatus.Disconnected) _dropped?.TrySetResult();
        };

        // MaintainConnection is set only once the GATT server has answered (below): set before discovery it
        // pinned a stale link whose server was gone, and the retry loop then re-acquired that same link.
        _session = await Step(GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId), "GATT session", ct);

        var services = await Step(_device.GetGattServicesForUuidAsync(BleIds.Service, BluetoothCacheMode.Uncached), "service lookup", ct);
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
        {
            FailedOnPossiblyStaleLink($"service lookup {services.Status}" + (services.Services.Count == 0 && services.Status == GattCommunicationStatus.Success ? " (service not found)" : ""), preConnected);
            return true;
        }
        _service = services.Services[0];
        var chars = await Step(_service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached), "characteristics lookup", ct);
        if (chars.Status != GattCommunicationStatus.Success) { FailedOnPossiblyStaleLink($"characteristics {chars.Status}", preConnected); return true; }
        foreach (var c in chars.Characteristics)
        {
            if (c.Uuid == BleIds.Rx) _rx = c;
            else if (c.Uuid == BleIds.Tx) _tx = c;
        }
        if (_rx == null || _tx == null) { FailedOnPossiblyStaleLink("rx/tx characteristics missing", preConnected); return true; }

        _tx.ValueChanged += OnValueChanged;
        // Windows keeps a bonded client's notify subscription (CCCD) across connections. When the peripheral's
        // process restarts on a link that stayed up, the new GattLocalCharacteristic sees our "Notify" write as
        // no change - the value was already Notify - and never raises SubscribedClientsChanged, so the peer
        // believes nobody listens and drops every reply; from here the subscribe still reported Success. Writing
        // None first makes the second write a real change (2026-09-15: six 15 s cycles before it took by chance).
        if (preConnected)
        {
            try { await Step(_tx.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None), "unsubscribe", ct); } catch (TimeoutException) { throw; } catch { }
            await Task.Delay(150, ct);
        }
        var sub = await Step(_tx.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify), "subscribe", ct);
        if (sub != GattCommunicationStatus.Success) { FailedOnPossiblyStaleLink($"subscribe {sub}", preConnected); return true; }
        _session.MaintainConnection = true;

        // Connection parameters are left at Windows' default. The 2026-09-08 transport tests measured default
        // as the fastest here (~13 ms median); RequestPreferredConnectionParameters(ThroughputOptimized) was
        // slower (p50 27-56 ms). The lag that looked like interval drift was a Bluetooth mouse on the
        // controlling PC sharing this one radio, not the interval, so there is nothing to tune here.
        Log.Info($"BLE linked to {_device.Name} ({address:X12}), maxPdu {_session.MaxPduSize}");
        IsLinked = true;
        _linkedThisAttempt = true;
        StatusChanged?.Invoke("linked");
        LinkChanged?.Invoke(true);

        using (ct.Register(() => _dropped?.TrySetResult()))
        {
            if (preConnected)
            {
                // The session sends Hello as soon as the link is up and a live peer answers within ~100 ms.
                // Every GATT step above succeeds on a leftover link too - Windows' GATT server on the other
                // PC answers discovery and takes the CCCD write - but the new GattServiceProvider there never
                // hears of a client that connected before it existed, so nothing ever comes back. Give it 5 s.
                await Task.WhenAny(_firstRx.Task, _dropped.Task, Task.Delay(5000, ct));
                if (!_gotRx && !_dropped.Task.IsCompleted)
                {
                    Log.Warn("BLE central: no answer within 5 s on the pre-existing connection - it is stale; releasing it so Windows drops it");
                    return true;
                }
            }
            await _dropped.Task;
        }
        return true;
    }

    private TaskCompletionSource _firstRx = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task<ulong> ScanAsync(CancellationToken ct)
    {
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(BleIds.Service);
        var found = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Received += (s, e) => found.TrySetResult(e.BluetoothAddress);
        watcher.Stopped += (s, e) => { if (e.Error != BluetoothError.Success) Log.Warn($"scan stopped: {e.Error}"); found.TrySetResult(0); };
        watcher.Start();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(ScanSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            using (linked.Token.Register(() => found.TrySetResult(0)))
            {
                ulong addr = await found.Task;
                if (addr != 0) Log.Info($"BLE found peripheral {addr:X12}");
                return addr;
            }
        }
        finally
        {
            try { watcher.Stop(); } catch { }
        }
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var data = new byte[args.CharacteristicValue.Length];
        DataReader.FromBuffer(args.CharacteristicValue).ReadBytes(data);
        if (!_gotRx) { _gotRx = true; _firstRx.TrySetResult(); }
        Received?.Invoke(data);
    }

    private void TearDown()
    {
        bool wasLinked = IsLinked;
        IsLinked = false;
        if (_tx != null) _tx.ValueChanged -= OnValueChanged;
        _rx = null; _tx = null;
        try { _service?.Dispose(); } catch { }
        try { _session?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
        _service = null; _session = null; _device = null;
        if (wasLinked)
        {
            StatusChanged?.Invoke("disconnected");
            LinkChanged?.Invoke(false);
        }
    }

    public async Task<bool> SendAsync(byte[] packet)
    {
        var rx = _rx;
        if (rx == null || !IsLinked) return false;
        await _sendLock.WaitAsync();
        try
        {
            var w = new DataWriter();
            w.WriteBytes(packet);
            var st = await rx.WriteValueAsync(w.DetachBuffer(), GattWriteOption.WriteWithoutResponse);
            if (st == GattCommunicationStatus.Success) { _writeFails = 0; return true; }
            // The LE link can outlive the peer's GATT server (its process restarted, or it slept): Windows keeps
            // reporting Connected while every write comes back Unreachable/ProtocolError, and we would sit in
            // "linked" forever. A streak of failures is a dropped link: reconnect and find the fresh server.
            if (++_writeFails >= 5)
            {
                Log.Warn($"BLE central: {_writeFails} writes failed in a row ({st}); treating the link as lost");
                _writeFails = 0;
                _dropped?.TrySetResult();
            }
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("BLE write failed: " + ex.Message);
            _dropped?.TrySetResult();
            return false;
        }
        finally { _sendLock.Release(); }
    }

    private int _writeFails;

    public void Reconnect() => _dropped?.TrySetResult();

    public void Dispose()
    {
        TearDown();
        _sendLock.Dispose();
    }
}
