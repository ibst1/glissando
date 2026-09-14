using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Glissando;

/// <summary>
/// Is a NotifyIcon really in the taskbar? Shell_NotifyIcon(NIM_ADD) times out after 4 s when Explorer is busy -
/// typical for an exe autostarted at logon - and WinForms' NotifyIcon ignores the failure: it marks the icon as
/// added and never retries, so a live, linked Glissando ran all day with no icon and no way to exit but Task
/// Manager (RS, 2026-09-14). Shell_NotifyIconGetRect answers S_OK only for an icon Explorer actually holds (in
/// the tray or its overflow), so it is the truth the watchdog in TrayContext checks against. The icon's window
/// handle and id are private to NotifyIcon; they are read by reflection, and if that ever breaks the caller
/// falls back to a blind re-add.
/// </summary>
internal static class TrayIconProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONIDENTIFIER
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

    private static readonly FieldInfo? WindowField = typeof(NotifyIcon).GetField("_window", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? IdField = typeof(NotifyIcon).GetField("_id", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>True: Explorer holds the icon. False: it does not (the add failed or was lost). Null: could not
    /// tell - the private fields were not found or the call threw.</summary>
    public static bool? IsRegistered(NotifyIcon icon)
    {
        try
        {
            if (WindowField?.GetValue(icon) is not NativeWindow window || IdField?.GetValue(icon) is not int id) return null;
            if (window.Handle == IntPtr.Zero) return false;
            var ident = new NOTIFYICONIDENTIFIER { cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(), hWnd = window.Handle, uID = (uint)id, guidItem = Guid.Empty };
            int hr = Shell_NotifyIconGetRect(ref ident, out _);
            return hr == 0;
        }
        catch (Exception ex)
        {
            Log.Warn("tray icon probe: " + ex.Message);
            return null;
        }
    }
}
