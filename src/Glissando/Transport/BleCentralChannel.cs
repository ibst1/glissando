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
            try { await Task.Delay(1500, ct); } catch (OperationCanceledException) { break; }
        }
        TearDown();
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
        _device.ConnectionStatusChanged += (s, e) =>
        {
            Log.Info($"BLE connection {s.ConnectionStatus}");
            if (s.ConnectionStatus == BluetoothConnectionStatus.Disconnected) _dropped?.TrySetResult();
        };

        _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId);
        _session.MaintainConnection = true;

        var services = await _device.GetGattServicesForUuidAsync(BleIds.Service, BluetoothCacheMode.Uncached);
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
        {
            Log.Warn($"service lookup {services.Status}");
            return;
        }
        _service = services.Services[0];
        var chars = await _service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
        if (chars.Status != GattCommunicationStatus.Success) { Log.Warn($"characteristics {chars.Status}"); return; }
        foreach (var c in chars.Characteristics)
        {
            if (c.Uuid == BleIds.Rx) _rx = c;
            else if (c.Uuid == BleIds.Tx) _tx = c;
        }
        if (_rx == null || _tx == null) { Log.Warn("rx/tx characteristics missing"); return; }

        _tx.ValueChanged += OnValueChanged;
        var sub = await _tx.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (sub != GattCommunicationStatus.Success) { Log.Warn($"subscribe {sub}"); return; }

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
            return st == GattCommunicationStatus.Success;
        }
        catch (Exception ex)
        {
            Log.Warn("BLE write failed: " + ex.Message);
            _dropped?.TrySetResult();
            return false;
        }
        finally { _sendLock.Release(); }
    }

    public void Reconnect() => _dropped?.TrySetResult();

    public void Dispose()
    {
        TearDown();
        _sendLock.Dispose();
    }
}
