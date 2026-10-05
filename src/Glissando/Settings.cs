using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Glissando;

public enum BleMode { Auto, Advertise, Connect }

public sealed class SavedPlacement
{
    public int PeerX { get; set; }
    public int PeerY { get; set; }
    /// <summary>Unix ms of the edit this placement came from (see Settings.PlacementUpdated).</summary>
    public long Updated { get; set; }
    /// <summary>The user's word for the desk ("Home", "Office A"); optional. Shared with the other PC through the
    /// handshake: whoever named it last wins, so both PCs call the desk the same.</summary>
    public string Name { get; set; } = "";
    /// <summary>Unix ms when the name was last set, for the newest-wins sync.</summary>
    public long NameUpdated { get; set; }
}

public sealed class Settings
{
    /// <summary>Shared secret; must be identical on both PCs. Frames are AES-GCM encrypted with a key derived from it.</summary>
    public string Passphrase { get; set; } = "";

    /// <summary>Which PC is the BLE peripheral. Auto: advertise if possible, and let the PC with the lower address connect.</summary>
    public BleMode Ble { get; set; } = BleMode.Auto;

    /// <summary>What this PC did the last time the two linked in Auto mode: "connect" (it was the central) or
    /// "wait" (the other PC connected to it). Both PCs remember their own side, so the next link starts at once
    /// with the same roles. Nothing else is symmetric enough to decide: the address an advertisement is seen from
    /// is a random private one Windows rotates, so comparing addresses gave the two PCs different answers.</summary>
    public string BleLastRole { get; set; } = "";

    /// <summary>Where the other PC's virtual-screen origin sits, in this PC's pixels. Null = to the right of this desktop.</summary>
    public int? PeerX { get; set; }
    public int? PeerY { get; set; }

    /// <summary>Last known size of the other PC's virtual screen.</summary>
    public int OtherScreenWidth { get; set; }
    public int OtherScreenHeight { get; set; }

    /// <summary>Last known monitors of the other PC: "x,y,w,h,number;..." in its own coordinates.</summary>
    public string OtherMonitors { get; set; } = "";

    /// <summary>Unix ms when the placement was last edited; the newer placement wins when the PCs compare notes.</summary>
    public long PlacementUpdated { get; set; }

    /// <summary>This PC's own mouse and keyboard may take control of the other PC.</summary>
    public bool CaptureEnabled { get; set; } = true;

    /// <summary>Copy clipboard text across when the cursor crosses over.</summary>
    public bool SyncClipboard { get; set; } = true;

    /// <summary>Swap the system cursors for blank ones while this PC controls the other; restored on return.</summary>
    public bool HideCursorWhileControlling { get; set; }

    /// <summary>This PC's keyboard types where the cursor is. For a mouse that belongs to the other PC: keys typed
    /// here go to the other PC whenever its cursor is at home there, and type here while its cursor is on this PC
    /// (a key then no longer takes this PC back; its mouse still does). Ctrl+Alt+L keeps the keyboard here.</summary>
    public bool KeyboardFollowsCursor { get; set; }

    /// <summary>Make a window invisible on this PC while it is mirrored to the other one; it shows again when the
    /// mirror stops, or as soon as someone clicks or types on this PC.</summary>
    public bool HideMirroredWindow { get; set; } = true;

    /// <summary>Carry the mirrored window's pixels over UDP on a shared IP network when the two PCs can reach
    /// each other that way (addresses exchanged over BLE; no configuration, no firewall rule). BLE remains the
    /// control path and carries the pixels itself when this is off or the network does not connect.</summary>
    public bool UdpMirror { get; set; } = true;

    /// <summary>The colour of the frame drawn round a window mirrored to this PC, as #RRGGBB. Green like the
    /// tray icon by default.</summary>
    public string MirrorFrameColor { get; set; } = "#4CAF50";

    [JsonIgnore]
    public Color MirrorFrame
    {
        get
        {
            try { return ColorTranslator.FromHtml(MirrorFrameColor); }
            catch (Exception) { return Color.FromArgb(76, 175, 80); }
        }
        set => MirrorFrameColor = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
    }

    /// <summary>Placements remembered per screen setup - this PC's monitors, the other PC's name and its monitors -
    /// so every desk (home, each office desk, the laptop on its own) comes back as it was left. Keyed by
    /// Layout.SetupKey; PeerX/PeerY hold the placement in use, which is the entry for the current key.</summary>
    public Dictionary<string, SavedPlacement> Placements { get; set; } = new();

    /// <summary>Load the placement remembered for this setup into PeerX/PeerY. False if none is remembered.
    /// <paramref name="changed"/> says whether the placement in use actually moved.</summary>
    public bool RecallPlacement(string key, out bool changed)
    {
        changed = false;
        if (!Layout.IsCompleteSetupKey(key) || !Placements.TryGetValue(key, out var p)) return false;
        changed = PeerX != p.PeerX || PeerY != p.PeerY;
        PeerX = p.PeerX;
        PeerY = p.PeerY;
        PlacementUpdated = changed ? p.Updated : Math.Max(PlacementUpdated, p.Updated);
        return true;
    }

    /// <summary>Store the placement in use under this setup's key.</summary>
    public void RememberPlacement(string key)
    {
        if (PeerX is not int x || PeerY is not int y || !Layout.IsCompleteSetupKey(key)) return;
        Placements.TryGetValue(key, out var old);
        Placements[key] = new SavedPlacement { PeerX = x, PeerY = y, Updated = PlacementUpdated, Name = old?.Name ?? "", NameUpdated = old?.NameUpdated ?? 0 };
    }

    /// <summary>After a few minutes without input, ping only every 10 s so the BLE link can idle (first move after that is slower).</summary>
    public bool PowerSaveWhenIdle { get; set; }

    /// <summary>Idle time before power saving kicks in.</summary>
    public int PowerSaveAfterMs { get; set; } = 5 * 60_000;

    /// <summary>Ping interval while power saving.</summary>
    public int IdleKeepaliveMs { get; set; } = 10_000;

    /// <summary>Ping interval while linked. Keeps the BLE connection interval short.</summary>
    public int KeepaliveMs { get; set; } = 1000;

    /// <summary>Silence from the peer after which control is released.</summary>
    public int PeerTimeoutMs { get; set; } = 4000;

    [JsonIgnore]
    public Size OtherScreen
    {
        get => new(OtherScreenWidth, OtherScreenHeight);
        set { OtherScreenWidth = value.Width; OtherScreenHeight = value.Height; }
    }

    [JsonIgnore]
    public MonitorRect[] OtherMonitorList
    {
        get
        {
            try
            {
                return OtherMonitors.Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Select(m => m.Split(','))
                    .Where(f => f.Length >= 4)
                    .Select(f => new MonitorRect(
                        new Rectangle(int.Parse(f[0]), int.Parse(f[1]), int.Parse(f[2]), int.Parse(f[3])),
                        f.Length > 4 && int.TryParse(f[4], out var n) ? n : 0,
                        f.Length > 5 ? f[5] : ""))
                    .ToArray();
            }
            catch (FormatException) { return []; }
        }
        set => OtherMonitors = string.Join(";", value.Select(m => $"{m.Bounds.X},{m.Bounds.Y},{m.Bounds.Width},{m.Bounds.Height},{m.Number}" + (m.Id != "" ? "," + m.Id : "")));
    }

    [JsonIgnore]
    public static string FilePath { get; private set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Glissando", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static Settings Load(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path)) FilePath = Path.GetFullPath(path);
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions) ?? new Settings();
                foreach (var k in s.Placements.Keys.Where(k => !Layout.IsCompleteSetupKey(k)).ToArray()) s.Placements.Remove(k);
                s.KeepaliveMs = Math.Clamp(s.KeepaliveMs, 200, 10_000);
                s.PeerTimeoutMs = Math.Clamp(s.PeerTimeoutMs, 1000, 60_000);
                return s;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Error($"settings unreadable, using defaults: {ex.Message}");
        }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }

    public Settings Clone() => (Settings)MemberwiseClone();

    // ---- start with Windows (HKCU Run key; no admin needed) ----

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool StartWithWindows
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                return k?.GetValue("Glissando") is string;
            }
            catch { return false; }
        }
        set
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(RunKey);
                if (value) k.SetValue("Glissando", $"\"{Environment.ProcessPath}\"");
                else k.DeleteValue("Glissando", throwOnMissingValue: false);
            }
            catch (Exception ex) { Log.Warn("start with Windows: " + ex.Message); }
        }
    }
}
