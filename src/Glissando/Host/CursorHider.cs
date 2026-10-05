using System.Runtime.InteropServices;

namespace Glissando.Host;

/// <summary>
/// Hides the cursor on the controlling PC while the other PC is being driven. There is no per-process
/// way to hide the cursor over other apps' windows without administrator rights, so the whole system
/// cursor set is swapped for blank cursors and restored from the user's scheme afterwards.
/// Two parts of Glissando may want it hidden at once -- controlling the other PC, and a mirror driving the
/// cursor over a window hidden here -- so it stays blank until every owner has let go.
/// </summary>
internal static class CursorHider
{
    // OCR_* ids: normal, ibeam, wait, cross, up, size nwse/nesw/we/ns/all, no, hand, appstarting
    private static readonly uint[] Ids = [32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650];
    public const string Control = "control", Mirror = "mirror";
    private static bool _hidden;
    private static readonly HashSet<string> Owners = [];
    private static readonly object Gate = new();

    public static void Hide(string owner = Control)
    {
        lock (Gate)
        {
            bool added = Owners.Add(owner);
            if (_hidden)
            {
                if (added) Log.Info($"system cursors also kept hidden for: {owner}");
                return;
            }
            foreach (var id in Ids)
            {
                var h = CreateBlankCursor();
                if (h == IntPtr.Zero) continue;
                if (!SetSystemCursor(h, id)) DestroyCursor(h); // on success the system owns the handle
            }
            _hidden = true;
            Log.Info($"system cursors hidden ({owner})");
        }
    }

    /// <summary>This owner no longer needs the cursor hidden; the user's scheme comes back once no owner does.
    /// Safe to call any time, also when nothing was hidden.</summary>
    public static void Restore(string owner = Control)
    {
        lock (Gate)
        {
            bool removed = Owners.Remove(owner);
            if (!_hidden) return;
            if (Owners.Count > 0)
            {
                if (removed) Log.Info($"system cursors: {owner} lets go, still hidden for: {string.Join(", ", Owners)}");
                return;
            }
            SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0);
            _hidden = false;
            Log.Info($"system cursors restored ({owner})");
        }
    }

    /// <summary>Reloads the user's cursor scheme whoever hid it: on exit, a crash, the session closing.</summary>
    public static void RestoreAll()
    {
        lock (Gate)
        {
            Owners.Clear();
            if (!_hidden) return;
            SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0);
            _hidden = false;
            Log.Info("system cursors restored");
        }
    }

    /// <summary>
    /// Reload the user's cursor scheme unconditionally. Called at startup so that if a previous instance
    /// was killed (Task Manager, a crash, a force-kill) while the cursors were hidden and left the whole
    /// system with blank cursors, launching Glissando again puts them back.
    /// </summary>
    public static void RestoreSchemeAtStartup()
    {
        lock (Gate)
        {
            SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0);
            _hidden = false;
            Owners.Clear();
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
