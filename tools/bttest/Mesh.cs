using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace BtTest;

// BLE mesh probe: can every PC be linked to every other PC at once, and what does that do to latency?
// Run "bttest blemesh" on every PC. Each one serves a GATT echo service (peripheral) and connects to every
// other PC it sees (central) - both roles at once, on one radio. Of each pair, the PC with the smaller name
// keeps the link it made; the other drops its own, so every pair has exactly one link.
// Every round, each PC pings its links at 125 Hz (mouse-move rate): all of them together, then each alone.
internal static class Mesh
{
    private static readonly Guid ServiceUuid = new("9c2a1d50-5b7e-4e6a-8f1c-3d4b2a1e0f57");
    private static readonly Guid CharUuid = new("9c2a1d51-5b7e-4e6a-8f1c-3d4b2a1e0f57");
    private const byte Hello = (byte)'H', HelloReply = (byte)'h', Ping = (byte)'P';

    private static readonly string Me = Environment.MachineName;

    private sealed class Link
    {
        public required string Name;
        public required ulong Address;
        public required BluetoothLEDevice Device;
        public required GattSession Session;
        public required GattCharacteristic Char;
        public readonly ConcurrentDictionary<long, double> Rtt = new();
        public long Seq;
        public int WriteFailures;
    }

    // Links this PC made (it is the central); who is linked to it (it is the peripheral), by client id.
    private static readonly ConcurrentDictionary<string, Link> Out = new();
    private static readonly ConcurrentDictionary<string, string> In = new();
    private static readonly ConcurrentDictionary<string, long> EchoesFrom = new();
    // Addresses being tried, and ones that turned out to be a PC that links to us instead (until the time given).
    private static readonly ConcurrentDictionary<ulong, byte> Trying = new();
    private static readonly ConcurrentDictionary<ulong, DateTime> Skip = new();
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    private static string T => DateTime.Now.ToString("HH:mm:ss.fff");

    public static async Task<int> Run(int roundSeconds)
    {
        Program.OpenLog("blemesh");
        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter == null) { Program.Log("no Bluetooth adapter"); return 2; }
        Program.Log($"adapter {adapter.BluetoothAddress:X12}  central={adapter.IsCentralRoleSupported}  peripheral={adapter.IsPeripheralRoleSupported}");
        Program.Log($"this PC: {Me}. Run \"bttest blemesh\" on every PC; leave them running for a few rounds, then Ctrl+C.");

        var provider = await StartPeripheral();
        if (provider == null) Program.Log("NOTE: no peripheral role - this PC can only link to PCs that serve");
        var watcher = StartCentral();

        long round = 0;
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(roundSeconds));
            round++;
            Program.Log("");
            Program.Log($"===== round {round}  {T}");
            Program.Log($"advertising: {provider?.AdvertisementStatus.ToString() ?? "-"}   scanning: {watcher.Status}");
            Program.Log($"links this PC made ({Out.Count}): {string.Join(", ", Out.Keys.OrderBy(k => k))}");
            Program.Log($"links made to this PC ({In.Count}): {string.Join(", ", In.Values.OrderBy(v => v))}" +
                        (EchoesFrom.IsEmpty ? "" : "   echoed since last round: " + string.Join(", ", EchoesFrom.Select(kv => $"{kv.Key} {kv.Value}"))));
            EchoesFrom.Clear();
            var links = Out.Values.ToArray();
            if (links.Length == 0) continue;

            await Task.WhenAll(links.Select(l => Measure(l, $"all {links.Length + In.Count} links busy", 1250)));
            foreach (var l in links) await Measure(l, "this PC's links one at a time", 625);
        }
    }

    // ---- peripheral: echo every ping back to the client that sent it ----

    private static async Task<GattServiceProvider?> StartPeripheral()
    {
        var res = await GattServiceProvider.CreateAsync(ServiceUuid);
        if (res.Error != BluetoothError.Success) { Program.Log($"GattServiceProvider.CreateAsync: {res.Error}"); return null; }
        var provider = res.ServiceProvider;
        var cres = await provider.Service.CreateCharacteristicAsync(CharUuid, new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.WriteWithoutResponse | GattCharacteristicProperties.Write | GattCharacteristicProperties.Notify,
            WriteProtectionLevel = GattProtectionLevel.Plain,
            ReadProtectionLevel = GattProtectionLevel.Plain,
        });
        if (cres.Error != BluetoothError.Success) { Program.Log($"CreateCharacteristicAsync: {cres.Error}"); return null; }
        var ch = cres.Characteristic;
        ch.SubscribedClientsChanged += (s, _) =>
        {
            var ids = s.SubscribedClients.Select(c => c.Session.DeviceId.Id).ToHashSet();
            foreach (var gone in In.Keys.Where(k => !ids.Contains(k)).ToArray())
                if (In.TryRemove(gone, out var name)) Program.Log($"{T} {name} unlinked from this PC");
            Program.Log($"{T} subscribed clients: {s.SubscribedClients.Count}");
        };
        ch.WriteRequested += async (s, e) =>
        {
            var deferral = e.GetDeferral();
            try
            {
                var req = await e.GetRequestAsync();
                if (req == null) return;
                if (req.Option == GattWriteOption.WriteWithResponse) req.Respond();
                var b = new byte[req.Value.Length];
                DataReader.FromBuffer(req.Value).ReadBytes(b);
                if (b.Length == 0) return;
                string id = e.Session.DeviceId.Id;
                var client = s.SubscribedClients.FirstOrDefault(c => c.Session.DeviceId.Id == id);
                if (client == null) return;
                if (b[0] == Hello)
                {
                    string name = Encoding.UTF8.GetString(b, 1, b.Length - 1);
                    if (In.TryAdd(id, name)) Program.Log($"{T} {name} linked to this PC (maxPdu {client.MaxNotificationSize})");
                    await s.NotifyValueAsync(Frame(HelloReply, Encoding.UTF8.GetBytes(Me)), client);
                }
                else if (b[0] == Ping)
                {
                    EchoesFrom.AddOrUpdate(In.GetValueOrDefault(id, "?"), 1, (_, n) => n + 1);
                    await s.NotifyValueAsync(req.Value, client);
                }
            }
            catch (Exception ex) { Program.Log("echo: " + ex.Message); }
            finally { deferral.Complete(); }
        };
        var adv = new GattServiceProviderAdvertisingParameters { IsConnectable = true, IsDiscoverable = true };
        provider.AdvertisementStatusChanged += (s, e) =>
        {
            Program.Log($"{T} advertising: {e.Status} {e.Error}");
            if (e.Status == GattServiceProviderAdvertisementStatus.Aborted)
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2000);
                    try { if (s.AdvertisementStatus != GattServiceProviderAdvertisementStatus.Started) s.StartAdvertising(adv); }
                    catch (Exception ex) { Program.Log("StartAdvertising: " + ex.Message); }
                });
        };
        try { provider.StartAdvertising(adv); }
        catch (Exception ex) { Program.Log("StartAdvertising: " + ex.Message); }
        return provider;
    }

    private static IBuffer Frame(byte type, byte[] body)
    {
        var w = new DataWriter();
        w.WriteByte(type);
        w.WriteBytes(body);
        return w.DetachBuffer();
    }

    // ---- central: connect to every PC that serves, one at a time ----

    private static BluetoothLEAdvertisementWatcher StartCentral()
    {
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(ServiceUuid);
        watcher.Received += (sender, e) =>
        {
            ulong a = e.BluetoothAddress;
            if (Out.Values.Any(l => l.Address == a)) return;
            if (Skip.TryGetValue(a, out var until) && DateTime.Now < until) return;
            if (!Trying.TryAdd(a, 0)) return;
            _ = Task.Run(async () =>
            {
                try { await Connect(a, e.BluetoothAddressType, e.RawSignalStrengthInDBm); }
                catch (Exception ex) { Program.Log($"{T} {a:X12}: {ex.GetType().Name}: {ex.Message}"); Skip[a] = DateTime.Now.AddSeconds(15); }
                finally { Trying.TryRemove(a, out byte _); }
            });
        };
        watcher.Stopped += (_, e) => Program.Log($"{T} scanning stopped: {e.Error}");
        watcher.Start();
        return watcher;
    }

    private static async Task Connect(ulong addr, BluetoothAddressType type, short rssi)
    {
        await OneAtATime.WaitAsync();
        try
        {
            var sw = Stopwatch.StartNew();
            var device = await BluetoothLEDevice.FromBluetoothAddressAsync(addr, type);
            if (device == null) { Skip[addr] = DateTime.Now.AddSeconds(15); return; }
            var session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId);
            session.MaintainConnection = true;
            var sres = await device.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached);
            if (sres.Status != GattCommunicationStatus.Success || sres.Services.Count == 0)
            {
                Program.Log($"{T} {addr:X12}: service lookup {sres.Status}");
                Drop(device, session); Skip[addr] = DateTime.Now.AddSeconds(15); return;
            }
            var cres = await sres.Services[0].GetCharacteristicsForUuidAsync(CharUuid, BluetoothCacheMode.Uncached);
            if (cres.Status != GattCommunicationStatus.Success || cres.Characteristics.Count == 0)
            {
                Program.Log($"{T} {addr:X12}: characteristic lookup {cres.Status}");
                Drop(device, session); Skip[addr] = DateTime.Now.AddSeconds(15); return;
            }
            var ch = cres.Characteristics[0];
            var named = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Link? link = null;
            ch.ValueChanged += (_, e) =>
            {
                var b = new byte[e.CharacteristicValue.Length];
                DataReader.FromBuffer(e.CharacteristicValue).ReadBytes(b);
                if (b.Length == 0) return;
                if (b[0] == HelloReply) named.TrySetResult(Encoding.UTF8.GetString(b, 1, b.Length - 1));
                else if (b[0] == Ping && b.Length >= 17 && link != null)
                    link.Rtt[BitConverter.ToInt64(b, 1)] = (Stopwatch.GetTimestamp() - BitConverter.ToInt64(b, 9)) * 1000.0 / Stopwatch.Frequency;
            };
            if (await ch.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify) != GattCommunicationStatus.Success)
            {
                Program.Log($"{T} {addr:X12}: could not subscribe");
                Drop(device, session); Skip[addr] = DateTime.Now.AddSeconds(15); return;
            }
            await ch.WriteValueAsync(Frame(Hello, Encoding.UTF8.GetBytes(Me)), GattWriteOption.WriteWithoutResponse);
            if (await Task.WhenAny(named.Task, Task.Delay(5000)) != named.Task)
            {
                Program.Log($"{T} {addr:X12}: no hello reply");
                Drop(device, session); Skip[addr] = DateTime.Now.AddSeconds(15); return;
            }
            string name = named.Task.Result;
            // One link per pair: the PC with the smaller name keeps the one it made.
            if (string.CompareOrdinal(Me, name) > 0 || Out.ContainsKey(name))
            {
                Drop(device, session);
                Skip[addr] = DateTime.Now.AddMinutes(2);
                return;
            }
            link = new Link { Name = name, Address = addr, Device = device, Session = session, Char = ch };
            Out[name] = link;
            Program.Log($"{T} linked to {name} ({addr:X12}, rssi {rssi} dBm) in {sw.ElapsedMilliseconds} ms, maxPdu {session.MaxPduSize}; now {Out.Count} out, {In.Count} in");
            device.ConnectionStatusChanged += (d, _) =>
            {
                if (d.ConnectionStatus != BluetoothConnectionStatus.Disconnected) return;
                if (Out.TryRemove(name, out Link? _)) Program.Log($"{T} link to {name} dropped");
                Drop(device, session);
            };
        }
        finally { OneAtATime.Release(); }
    }

    private static void Drop(BluetoothLEDevice device, GattSession session)
    {
        try { session.MaintainConnection = false; session.Dispose(); } catch { }
        try { device.Dispose(); } catch { }
    }

    // ---- measurement ----

    private static async Task Measure(Link l, string what, int count)
    {
        long first = l.Seq;
        var frame = new byte[17];
        frame[0] = Ping;
        var t = Stopwatch.StartNew();
        for (int i = 0; i < count && Out.ContainsKey(l.Name); i++)
        {
            BitConverter.TryWriteBytes(frame.AsSpan(1, 8), l.Seq++);
            BitConverter.TryWriteBytes(frame.AsSpan(9, 8), Stopwatch.GetTimestamp());
            var w = new DataWriter();
            w.WriteBytes(frame);
            try
            {
                var r = await l.Char.WriteValueAsync(w.DetachBuffer(), GattWriteOption.WriteWithoutResponse);
                if (r != GattCommunicationStatus.Success && l.WriteFailures++ < 5) Program.Log($"{T} write to {l.Name}: {r}");
            }
            catch (Exception ex) { if (l.WriteFailures++ < 5) Program.Log($"{T} write to {l.Name}: {ex.Message}"); }
            double target = (i + 1) * 8.0;
            while (t.Elapsed.TotalMilliseconds < target)
            {
                if (target - t.Elapsed.TotalMilliseconds > 2) await Task.Delay(1); else Thread.SpinWait(200);
            }
        }
        await Task.Delay(1500);
        var rtts = new List<double>();
        int lost = 0;
        for (long q = first; q < l.Seq; q++)
            if (l.Rtt.TryRemove(q, out var r)) rtts.Add(r); else lost++;
        Program.Report($"{l.Name,-14} {what}", rtts, lost, (int)(l.Seq - first));
    }
}
