using System.Runtime.InteropServices;

namespace Glissando;

/// <summary>
/// Moves this PC's monitors in Windows, as dragging them in Display settings does (no administrator rights
/// needed). Positions are given in Glissando's monitor coordinates (relative to the virtual screen's top-left);
/// the primary monitor stays at the desktop origin, as Windows requires, by moving everything else instead.
/// Windows may still nudge a monitor that ends up detached; the display-change notice brings the result back.
/// </summary>
internal static class DisplayArrangement
{
    /// <summary>Moves each monitor (by its Windows number) to a new top-left. Null on success, else why not.</summary>
    public static string? Apply(IReadOnlyList<(int Number, Point TopLeft)> moves)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GLISSANDO_FAKE_MONITORS")))
            return "fake monitors (GLISSANDO_FAKE_MONITORS) cannot be rearranged";
        var origin = Native.VirtualScreen().Location;
        var current = Layout.MyMonitors();
        // The primary monitor is the one at the desktop origin; its new position is what everything shifts by.
        var primary = current.FirstOrDefault(m => m.Bounds.X + origin.X == 0 && m.Bounds.Y + origin.Y == 0);
        var primaryMove = moves.FirstOrDefault(m => m.Number == primary.Number);
        var shift = primary.Number > 0 && primaryMove.Number == primary.Number
            ? new Point(-(primaryMove.TopLeft.X + origin.X), -(primaryMove.TopLeft.Y + origin.Y))
            : Point.Empty;
        foreach (var (number, topLeft) in moves)
        {
            string device = $@"\\.\DISPLAY{number}";
            var dm = new DEVMODE { dmDeviceName = "", dmFormName = "", dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettingsW(device, ENUM_CURRENT_SETTINGS, ref dm)) return $"cannot read {device}";
            dm.dmPositionX = topLeft.X + origin.X + shift.X;
            dm.dmPositionY = topLeft.Y + origin.Y + shift.Y;
            dm.dmFields = DM_POSITION;
            int r = ChangeDisplaySettingsExW(device, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
            if (r != 0) return $"Windows refused to move screen {number} (code {r})";
            Log.Info($"display arrangement: screen {number} to ({dm.dmPositionX},{dm.dmPositionY}) desktop pixels");
        }
        int done = ChangeDisplaySettingsExW(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        return done == 0 ? null : $"Windows could not apply the arrangement (code {done})";
    }

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const uint DM_POSITION = 0x20;
    private const uint CDS_UPDATEREGISTRY = 0x01, CDS_NORESET = 0x10000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(string deviceName, ref DEVMODE devMode, IntPtr hwnd, uint flags, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(string? deviceName, IntPtr devMode, IntPtr hwnd, uint flags, IntPtr lParam);
}
