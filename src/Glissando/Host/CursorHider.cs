using System.Runtime.InteropServices;

namespace Glissando.Host;

/// <summary>
/// Hides the cursor on the controlling PC while the other PC is being driven. There is no per-process
/// way to hide the cursor over other apps' windows without administrator rights, so the whole system
/// cursor set is swapped for blank cursors and restored from the user's scheme afterwards.
/// </summary>
internal static class CursorHider
{
    // OCR_* ids: normal, ibeam, wait, cross, up, size nwse/nesw/we/ns/all, no, hand, appstarting
    private static readonly uint[] Ids = [32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650];
    private static bool _hidden;
    private static readonly object Gate = new();

    public static void Hide()
    {
        lock (Gate)
        {
            if (_hidden) return;
            foreach (var id in Ids)
            {
                var h = CreateBlankCursor();
                if (h == IntPtr.Zero) continue;
                if (!SetSystemCursor(h, id)) DestroyCursor(h); // on success the system owns the handle
            }
            _hidden = true;
            Log.Info("system cursors hidden");
        }
    }

    /// <summary>Reloads the user's cursor scheme. Safe to call any time, also when nothing was hidden.</summary>
    public static void Restore()
    {
        lock (Gate)
        {
            if (!_hidden) return;
            SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0);
            _hidden = false;
            Log.Info("system cursors restored");
        }
    }

    private static IntPtr CreateBlankCursor()
    {
        const int size = 32;
        var and = new byte[size * size / 8];
        var xor = new byte[size * size / 8];
        Array.Fill(and, (byte)0xFF); // AND=1, XOR=0 → screen unchanged → invisible
        return CreateCursor(IntPtr.Zero, 0, 0, size, size, and, xor);
    }

    private const uint SPI_SETCURSORS = 0x0057;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateCursor(IntPtr hInst, int xHotSpot, int yHotSpot, int nWidth, int nHeight, byte[] pvANDPlane, byte[] pvXORPlane);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSystemCursor(IntPtr hcur, uint id);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyCursor(IntPtr hCursor);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);
}
