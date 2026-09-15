using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
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

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectOnceAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Warn("BLE central: " + ex.Message);
            }
            finally
            {
                TearDown();
            }
            // After a failure on a connection that pre-dated us, give Windows time to drop it before trying
            // again; re-acquiring it at once kept the stale link alive for minutes (2026-09-15).
            int pause = _backoffMs > 0 ? _backoffMs : 1500;
            _backoffMs = 0;
            try { await Task.Delay(pause, ct); } catch (OperationCanceledException) { break; }
        }
        TearDown();
    }

    private int _backoffMs;

    /// <summary>A GATT step failed. If the LE connection was already there when we looked the device up, it is
    /// most likely a leftover from the peer's previous process or from before sleep, whose GATT server is gone:
    /// release every reference (TearDown does) and wait so Windows actually drops it, then rediscover.</summary>
    private void FailedOnPossiblyStaleLink(string what, bool preConnected)
    {
        if (preConnected)
        {
            Log.Warn($"BLE central: {what} on a pre-existing connection - releasing it so Windows drops the old link, retrying in 6 s");
            _backoffMs = 6000;
        }
        else Log.Warn($"BLE central: {what}");
    }

    private async Task ConnectOnceAsync(CancellationToken ct)
    {
        StatusChanged?.Invoke("scanning");
        ulong address = await ScanAsync(ct);
        if (address == 0) return;

        StatusChanged?.Invoke("connecting");
        _dropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
        if (_device == null) { Log.Warn("FromBluetoothAddressAsync returned null"); return; }
        bool preConnected = _device.ConnectionStatus == BluetoothConnectionStatus.Connected;
        if (preConnected) Log.Info("BLE central: the peer is already connected at the radio level (a link left over from before)");
        _device.ConnectionStatusChanged += (s, e) =>
        {
            Log.Info($"BLE connection {s.ConnectionStatus}");
            if (s.ConnectionStatus == BluetoothConnectionStatus.Disconnected) _dropped?.TrySetResult();
        };

        // MaintainConnection is set only once the GATT server has answered (below): set before discovery it
        // pinned a stale link whose server was gone, and the retry loop then re-acquired that same link.
        _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId);

        var services = await _device.GetGattServicesForUuidAsync(BleIds.Service, BluetoothCacheMode.Uncached);
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
        {
            FailedOnPossiblyStaleLink($"service lookup {services.Status}" + (services.Services.Count == 0 && services.Status == GattCommunicationStatus.Success ? " (service not found)" : ""), preConnected);
            return;
        }
        _service = services.Services[0];
        var chars = await _service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
        if (chars.Status != GattCommunicationStatus.Success) { FailedOnPossiblyStaleLink($"characteristics {chars.Status}", preConnected); return; }
        foreach (var c in chars.Characteristics)
        {
            if (c.Uuid == BleIds.Rx) _rx = c;
            else if (c.Uuid == BleIds.Tx) _tx = c;
        }
        if (_rx == null || _tx == null) { FailedOnPossiblyStaleLink("rx/tx characteristics missing", preConnected); return; }

        _tx.ValueChanged += OnValueChanged;
        var sub = await _tx.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (sub != GattCommunicationStatus.Success) { FailedOnPossiblyStaleLink($"subscribe {sub}", preConnected); return; }
        _session.MaintainConnection = true;

        // Connection parameters are left at Windows' default. The 2026-09-08 transport tests measured default
        // as the fastest here (~13 ms median); RequestPreferredConnectionParameters(ThroughputOptimized) was
        // slower (p50 27-56 ms). The lag that looked like interval drift was a Bluetooth mouse on the
        // controlling PC sharing this one radio, not the interval, so there is nothing to tune here.
        Log.Info($"BLE linked to {_device.Name} ({address:X12}), maxPdu {_session.MaxPduSize}");
        IsLinked = true;
        StatusChanged?.Invoke("linked");
        LinkChanged?.Invoke(true);

        using (ct.Register(() => _dropped?.TrySetResult()))
            await _dropped.Task;
    }

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
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
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
