using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Glissando;

/// <summary>
/// Which physical monitor sits behind each of Windows' <c>\\.\DISPLAYn</c> devices. A desk is told apart by its
/// screens, and "the same screens" must mean the same panels, not the same count or size: QueryDisplayConfig
/// maps every active source (the GDI device Screen.DeviceName names) to its target, whose EDID manufacturer and
/// product code Windows reports directly; the serial number is read from the EDID block the PnP manager keeps in
/// the registry (readable without administrator rights). The id is "DELA0B5-SERIAL"; a monitor without a usable
/// serial (many laptop panels, some cheap monitors) falls back to manufacturer+product+connector, which is
/// stable as long as it stays on the same port. Anything that fails leaves the id empty and the desk falls back
/// to matching by size.
/// </summary>
internal static class MonitorIdentity
{
    public sealed record Info(string Id, string Friendly);

    /// <summary>GDI device name ("\\.\DISPLAY1") -> identity, for every active monitor.</summary>
    public static Dictionary<string, Info> Query()
    {
        var result = new Dictionary<string, Info>(StringComparer.OrdinalIgnoreCase);
        try
        {
            const uint QDC_ONLY_ACTIVE_PATHS = 2;
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint nPath, out uint nMode) != 0 || nPath == 0) return result;
            var paths = new DISPLAYCONFIG_PATH_INFO[nPath];
            var modes = new DISPLAYCONFIG_MODE_INFO[Math.Max(1, nMode)];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref nPath, paths, ref nMode, modes, IntPtr.Zero) != 0) return result;
            for (int i = 0; i < nPath; i++)
            {
                var p = paths[i];
                var src = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER { type = 1, size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(), adapterId = p.sourceInfo.adapterId, id = p.sourceInfo.id },
                };
                if (DisplayConfigGetDeviceInfo(ref src) != 0 || string.IsNullOrEmpty(src.viewGdiDeviceName)) continue;
                var tgt = new DISPLAYCONFIG_TARGET_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER { type = 2, size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(), adapterId = p.targetInfo.adapterId, id = p.targetInfo.id },
                };
                if (DisplayConfigGetDeviceInfo(ref tgt) != 0) continue;

                string model = Manufacturer(tgt.edidManufactureId) + tgt.edidProductCodeId.ToString("X4");
                string serial = EdidSerial(tgt.monitorDevicePath);
                string id = Sanitize(serial != "" ? $"{model}-{serial}" : $"{model}@{Connector(tgt.monitorDevicePath)}");
                result[src.viewGdiDeviceName] = new Info(id, tgt.monitorFriendlyDeviceName ?? "");
            }
        }
        catch (Exception ex) { Log.Warn("monitor identity: " + ex.Message); }
        return result;
    }

    /// <summary>EDID packs the three-letter manufacturer code into 16 bits, big-endian; the struct hands it over as
    /// the two bytes read little-endian, so swap first. Falls back to hex if it does not decode to letters.</summary>
    private static string Manufacturer(ushort raw)
    {
        int v = ((raw & 0xFF) << 8) | (raw >> 8);
        char a = (char)('A' - 1 + ((v >> 10) & 0x1F)), b = (char)('A' - 1 + ((v >> 5) & 0x1F)), c = (char)('A' - 1 + (v & 0x1F));
        bool ok = a is >= 'A' and <= 'Z' && b is >= 'A' and <= 'Z' && c is >= 'A' and <= 'Z';
        return ok ? new string([a, b, c]) : raw.ToString("X4");
    }

    /// <summary>"\\?\DISPLAY#DELA0B5#5&2f6b3c1&0&UID4353#{guid}" -> "5&2f6b3c1&0&UID4353".</summary>
    private static string Connector(string devicePath)
    {
        var parts = Parts(devicePath);
        return parts.Length >= 3 ? parts[2] : devicePath;
    }

    private static string[] Parts(string devicePath)
    {
        string s = devicePath ?? "";
        if (s.StartsWith(@"\\?\")) s = s[4..];
        return s.Split('#');
    }

    /// <summary>The serial from the monitor's EDID as the PnP manager stored it:
    /// HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\{model}\{instance}\Device Parameters\EDID. The serial-number
    /// descriptor (tag 0xFF) is preferred; else the 32-bit serial at bytes 12-15 when it is not zero.</summary>
    private static string EdidSerial(string devicePath)
    {
        try
        {
            var parts = Parts(devicePath);
            if (parts.Length < 3 || !parts[0].Equals("DISPLAY", StringComparison.OrdinalIgnoreCase)) return "";
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{parts[1]}\{parts[2]}\Device Parameters");
            if (key?.GetValue("EDID") is not byte[] edid || edid.Length < 128) return "";
            for (int o = 54; o + 18 <= 128; o += 18)
            {
                if (edid[o] == 0 && edid[o + 1] == 0 && edid[o + 2] == 0 && edid[o + 3] == 0xFF)
                {
                    string text = Encoding.ASCII.GetString(edid, o + 5, 13).Split('\n')[0].Trim();
                    if (text.Length > 0 && text.Any(char.IsLetterOrDigit)) return text;
                }
            }
            uint n = BitConverter.ToUInt32(edid, 12);
            return n != 0 && n != 1 ? n.ToString() : "";
        }
        catch { return ""; }
    }

    /// <summary>Ids travel in setup keys and settings strings whose separators they must not contain.</summary>
    private static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(c is ',' or ';' or '#' or '|' or '@' or ' ' ? '_' : c);
        return sb.ToString();
    }

    // ---- DisplayConfig interop ---------------------------------------------------

    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] private struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology; public uint rotation; public uint scaling;
        public uint refreshRateNumerator, refreshRateDenominator; public uint scanLineOrdering; public int targetAvailable; public uint statusFlags;
    }
    [StructLayout(LayoutKind.Sequential)] private struct DISPLAYCONFIG_PATH_INFO { public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo; public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo; public uint flags; }
    [StructLayout(LayoutKind.Sequential, Size = 64)] private struct DISPLAYCONFIG_MODE_INFO { public uint infoType; public uint id; public LUID adapterId; }
    [StructLayout(LayoutKind.Sequential)] private struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public uint type; public uint size; public LUID adapterId; public uint id; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags; public uint outputTechnology; public ushort edidManufactureId; public ushort edidProductCodeId; public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray, ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);
}
