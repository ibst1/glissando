using System.Collections.Concurrent;
using System.Diagnostics;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace BtTest;

// BLE GATT latency probe: the server (peripheral) echoes every written value back as a
// notification; the client (central) writes 16-byte frames and measures RTT.
internal static class Ble
{
    private static readonly Guid ServiceUuid = new("9c2a1d40-5b7e-4e6a-8f1c-3d4b2a1e0f57");
    private static readonly Guid CharUuid = new("9c2a1d41-5b7e-4e6a-8f1c-3d4b2a1e0f57");
    private const int FrameSize = 16;

    public static async Task<int> Serve()
    {
        Program.OpenLog("bleserve");
        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter == null) { Program.Log("no Bluetooth adapter"); return 2; }
        Program.Log($"adapter: address {adapter.BluetoothAddress:X12}  LE={adapter.IsLowEnergySupported}  central={adapter.IsCentralRoleSupported}  peripheral={adapter.IsPeripheralRoleSupported}  ext-adv={adapter.IsExtendedAdvertisingSupported}");

        var res = await GattServiceProvider.CreateAsync(ServiceUuid);
        if (res.Error != BluetoothError.Success) { Program.Log($"GattServiceProvider.CreateAsync failed: {res.Error}"); return 2; }
        var provider = res.ServiceProvider;

        var cres = await provider.Service.CreateCharacteristicAsync(CharUuid, new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.WriteWithoutResponse | GattCharacteristicProperties.Write | GattCharacteristicProperties.Notify,
            WriteProtectionLevel = GattProtectionLevel.Plain,
            ReadProtectionLevel = GattProtectionLevel.Plain,
            UserDescription = "echo",
        });
        if (cres.Error != BluetoothError.Success) { Program.Log($"CreateCharacteristicAsync failed: {cres.Error}"); return 2; }
        var ch = cres.Characteristic;

        long n = 0;
        ch.SubscribedClientsChanged += (s, e) =>
        {
            Program.Log($"{DateTime.Now:HH:mm:ss.fff} subscribed clients: {s.SubscribedClients.Count}");
            foreach (var c in s.SubscribedClients) Program.Log($"  client {c.Session.DeviceId.Id}  maxPdu {c.MaxNotificationSize}");
        };
        ch.WriteRequested += async (s, e) =>
        {
            var deferral = e.GetDeferral();
            try
            {
                var req = await e.GetRequestAsync();
                if (req == null) return;
                if (req.Option == GattWriteOption.WriteWithResponse) req.Respond();
                Interlocked.Increment(ref n);
                await s.NotifyValueAsync(req.Value);
            }
            catch (Exception ex) { Program.Log("write handler error: " + ex.Message); }
            finally { deferral.Complete(); }
        };
        var advParams = new GattServiceProviderAdvertisingParameters { IsConnectable = true, IsDiscoverable = true };
        int restarts = 0;
        provider.AdvertisementStatusChanged += (s, e) =>
        {
            Program.Log($"{DateTime.Now:HH:mm:ss.fff} advertising status: {e.Status}  error {e.Error}");
            if (e.Status == GattServiceProviderAdvertisementStatus.Aborted && restarts < 10)
            {
                restarts++;
                Task.Run(async () =>
                {
                    await Task.Delay(1000);
                    if (provider.AdvertisementStatus != GattServiceProviderAdvertisementStatus.Started)
                    {
                        Program.Log($"restarting advertising (attempt {restarts})");
                        try { provider.StartAdvertising(advParams); }
                        catch (Exception ex) { Program.Log("StartAdvertising threw: " + ex.Message); }
                    }
                });
            }
        };
        provider.StartAdvertising(advParams);
        Program.Log("advertising GATT echo service. On the other PC run:  bttest bleconnect [none|balanced|throughput|power]");
        while (true)
        {
            await Task.Delay(10_000);
            Program.Log($"{DateTime.Now:HH:mm:ss} echoed {Interlocked.Read(ref n)} frames, advertising {provider.AdvertisementStatus}");
        }
    }

    public static async Task<int> Scan(int seconds)
    {
        Program.OpenLog("blescan");
        var seen = new ConcurrentDictionary<string, int>();
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (s, e) =>
        {
            var uuids = string.Join(",", e.Advertisement.ServiceUuids.Select(u => u.ToString()));
            var sections = string.Join(",", e.Advertisement.DataSections.Select(d => $"0x{d.DataType:X2}[{d.Data.Length}]"));
            var key = $"{e.BluetoothAddress:X12} {e.BluetoothAddressType} {e.AdvertisementType} name='{e.Advertisement.LocalName}' uuids=[{uuids}] sections=[{sections}]";
            if (seen.AddOrUpdate(key, 1, (_, c) => c + 1) == 1)
                Program.Log($"{DateTime.Now:HH:mm:ss.fff} rssi {e.RawSignalStrengthInDBm,4}  {key}");
        };
        watcher.Stopped += (s, e) => Program.Log($"watcher stopped: {e.Error}");
        watcher.Start();
        Program.Log($"scanning all advertisements for {seconds} s (looking for {ServiceUuid}) ...");
        await Task.Delay(seconds * 1000);
        watcher.Stop();
        Program.Log($"distinct advertisement variants: {seen.Count}");
        return 0;
    }

    public static async Task<int> Connect(string prefer, List<(double hz, int seconds)>? rateRuns)
    {
        Program.OpenLog("bleconnect");
        var adapter = await BluetoothAdapter.GetDefaultAsync();
        Program.Log($"adapter: address {adapter?.BluetoothAddress:X12}  central={adapter?.IsCentralRoleSupported}");

        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(ServiceUuid);
        var found = new TaskCompletionSource<(ulong addr, short rssi, BluetoothAddressType type)>();
        watcher.Received += (s, e) => found.TrySetResult((e.BluetoothAddress, e.RawSignalStrengthInDBm, e.BluetoothAddressType));
        watcher.Stopped += (s, e) => { if (e.Error != BluetoothError.Success) Program.Log($"watcher stopped: {e.Error}"); };
        var sw = Stopwatch.StartNew();
        watcher.Start();
        Program.Log("scanning for the echo service ...");
        var winner = await Task.WhenAny(found.Task, Task.Delay(20_000));
        watcher.Stop();
        if (winner != found.Task) { Program.Log("no advertisement with the service UUID seen in 20 s"); return 2; }
        var (addr, rssi, addrType) = found.Task.Result;
        Program.Log($"found {addr:X12} ({addrType})  rssi {rssi} dBm  after {sw.ElapsedMilliseconds} ms");

        using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(addr, addrType);
        if (device == null) { Program.Log("FromBluetoothAddressAsync returned null"); return 2; }
        device.ConnectionStatusChanged += (s, e) => Program.Log($"{DateTime.Now:HH:mm:ss.fff} connection status: {s.ConnectionStatus}");
        Program.Log($"device: '{device.Name}'  status {device.ConnectionStatus}");

        BluetoothLEPreferredConnectionParametersRequest? pref = null;
        if (prefer != "none")
        {
            var p = prefer switch
            {
                "throughput" => BluetoothLEPreferredConnectionParameters.ThroughputOptimized,
                "power" => BluetoothLEPreferredConnectionParameters.PowerOptimized,
                _ => BluetoothLEPreferredConnectionParameters.Balanced,
            };
            pref = device.RequestPreferredConnectionParameters(p);
            Program.Log($"preferred connection parameters '{prefer}': {pref.Status}");
        }

        using var session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId);
        session.MaintainConnection = true;
        session.SessionStatusChanged += (s, e) => Program.Log($"{DateTime.Now:HH:mm:ss.fff} session status: {e.Status} {e.Error}");

        var sres = await device.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached);
        if (sres.Status != GattCommunicationStatus.Success || sres.Services.Count == 0) { Program.Log($"service lookup: {sres.Status} ({sres.Services.Count} found)"); return 2; }
        var svc = sres.Services[0];
        var cres = await svc.GetCharacteristicsForUuidAsync(CharUuid, BluetoothCacheMode.Uncached);
        if (cres.Status != GattCommunicationStatus.Success || cres.Characteristics.Count == 0) { Program.Log($"characteristic lookup: {cres.Status}"); return 2; }
        var ch = cres.Characteristics[0];
        Program.Log($"connected in {sw.ElapsedMilliseconds} ms  status {device.ConnectionStatus}  maxPdu {session.MaxPduSize}  props {ch.CharacteristicProperties}");
        if (pref != null) Program.Log($"preferred connection parameters after connect: {pref.Status}");

        var results = new ConcurrentDictionary<long, double>();
        ch.ValueChanged += (s, e) =>
        {
            var b = new byte[e.CharacteristicValue.Length];
            DataReader.FromBuffer(e.CharacteristicValue).ReadBytes(b);
            if (b.Length < 16) return;
            long seqIn = BitConverter.ToInt64(b, 0);
            long sent = BitConverter.ToInt64(b, 8);
            results[seqIn] = (Stopwatch.GetTimestamp() - sent) * 1000.0 / Stopwatch.Frequency;
        };
        var sub = await ch.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
        Program.Log($"subscribe to notifications: {sub}");
        if (sub != GattCommunicationStatus.Success) return 2;

        var writeOpt = ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
            ? GattWriteOption.WriteWithoutResponse : GattWriteOption.WriteWithResponse;
        Program.Log($"write option: {writeOpt}");

        long seq = 0;
        var frame = new byte[FrameSize];
        int writeFailures = 0;

        async Task Send()
        {
            BitConverter.TryWriteBytes(frame.AsSpan(0, 8), seq);
            BitConverter.TryWriteBytes(frame.AsSpan(8, 8), Stopwatch.GetTimestamp());
            seq++;
            var w = new DataWriter();
            w.WriteBytes(frame);
            var r = await ch.WriteValueAsync(w.DetachBuffer(), writeOpt);
            if (r != GattCommunicationStatus.Success && writeFailures++ < 5) Program.Log($"write failed: {r}");
        }

        async Task Run(string name, int count, double intervalMs, double waitMs)
        {
            long first = seq;
            var t = Stopwatch.StartNew();
            for (int i = 0; i < count; i++)
            {
                await Send();
                double target = (i + 1) * intervalMs;
                while (t.Elapsed.TotalMilliseconds < target)
                {
                    double left = target - t.Elapsed.TotalMilliseconds;
                    if (left > 2) await Task.Delay(1); else Thread.SpinWait(200);
                }
            }
            double sendMs = t.Elapsed.TotalMilliseconds;
            long last = seq - 1;
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed.TotalMilliseconds < waitMs)
            {
                bool all = true;
                for (long q = first; q <= last; q++) if (!results.ContainsKey(q)) { all = false; break; }
                if (all) break;
                await Task.Delay(1);
            }
            var rtts = new List<double>();
            int lost = 0;
            for (long q = first; q <= last; q++)
                if (results.TryGetValue(q, out var r)) rtts.Add(r); else lost++;
            double actualHz = count > 1 ? count * 1000.0 / sendMs : 0;
            Program.Report(name + (count > 1 ? $" [actual {actualHz:F0} Hz]" : ""), rtts, lost, count);
        }

        Program.Log("");
        Program.Log("A. warm-up");
        await Run("warm-up 100 x 8 ms", 100, 8, 2000);
        Program.Log($"maxPdu now {session.MaxPduSize}");

        if (rateRuns != null)
        {
            foreach (var (hz, seconds) in rateRuns)
                await Run($"{hz} Hz x {seconds} s ({1000.0 / hz:F1} ms gaps)", (int)(hz * seconds), 1000.0 / hz, 3000);
        }
        else
        {
            Program.Log("");
            Program.Log("A. steady 125 Hz for 10 s (mouse-move rate)");
            await Run("125 Hz x 10 s", 1250, 8, 2000);

            Program.Log("");
            Program.Log("B. idle wake-up: 10 s silence, then one frame, then 20 frames at 8 ms");
            for (int round = 1; round <= 3; round++)
            {
                await Task.Delay(10_000);
                await Run($"round {round} first frame after 10 s idle", 1, 0, 5000);
                await Run($"round {round} next 20 frames", 20, 8, 2000);
            }

            Program.Log("");
            Program.Log("C. keepalive");
            await Run("keepalive 2 Hz x 10 s (500 ms gaps)", 20, 500, 3000);
            await Run("keepalive 1 Hz x 10 s (1000 ms gaps)", 10, 1000, 3000);

            Program.Log("");
            Program.Log("D. burst: 200 frames as fast as the write API allows");
            await Run("burst 200", 200, 0, 5000);

            Program.Log("");
            Program.Log("E. 60 Hz x 5 s and 125 Hz x 5 s again");
            await Run("60 Hz x 5 s", 300, 1000.0 / 60, 2000);
            await Run("125 Hz x 5 s", 625, 8, 2000);
        }

        Program.Log("done");
        pref?.Dispose();
        return 0;
    }
}
