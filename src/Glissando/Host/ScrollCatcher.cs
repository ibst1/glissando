using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Glissando.Host;

/// <summary>
/// A precision touchpad's two-finger scroll is not a wheel: Windows decides per window under the cursor what
/// to make of it. For a window that takes Direct Manipulation (Explorer, Edge, UWP) or for no window at all,
/// nothing reaches WH_MOUSE_LL or Raw Input (measured 2026-09-15: a mouse wheel showed in both, the touchpad
/// in neither, whether local or while remote), so it never reached the peer. For a plain Win32 window Windows
/// synthesizes small-delta WM_MOUSEWHEEL input (hDevice 0, not flagged injected) that travels the normal path.
/// While this PC controls the other one its cursor is frozen at the crossing point (the hook swallows every
/// move), so this near-invisible plain window is parked under it. That alone makes the gesture become wheel
/// input, which the hook swallows and Raw Input forwards like a mouse's wheel (deltas of 1-14 per event, 488
/// in the first test). Should a message ever reach the window itself, WndProc forwards it too.
/// Requires "Scroll inactive windows when I hover over them" (the Windows default); with it off, Windows
/// gives the gesture to the focused window instead.
/// </summary>
internal sealed class ScrollCatcher : Form
{
    /// <summary>delta (wheel units; a touchpad sends small ones), horizontal</summary>
    public event Action<int, bool>? Wheel;

    private const int Extent = 64;

    public ScrollCatcher()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.Black;
        Opacity = 0.01; // layered, alpha ~3: invisible in practice, still hit-tested
        Bounds = new Rectangle(-32000, -32000, Extent, Extent);
        _ = Handle;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    /// <summary>Park under the (frozen) cursor.</summary>
    public void ShowAt(Point cursor)
    {
        if (InvokeRequired) { BeginInvoke(() => ShowAt(cursor)); return; }
        SetWindowPos(Handle, HWND_TOPMOST, cursor.X - Extent / 2, cursor.Y - Extent / 2, Extent, Extent, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    public void HideAway()
    {
        if (InvokeRequired) { BeginInvoke(HideAway); return; }
        SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_HIDEWINDOW);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_MOUSEACTIVATE:
                m.Result = (IntPtr)MA_NOACTIVATE;
                return;
            case WM_MOUSEWHEEL:
            case WM_MOUSEHWHEEL:
                // Our own injected wheel (the mirror scrolling a window on this PC) must not go to the peer.
                if (GetMessageExtraInfo() != Native.InjectTag)
                    Wheel?.Invoke(unchecked((short)(((long)m.WParam >> 16) & 0xFFFF)), m.Msg == WM_MOUSEHWHEEL);
                m.Result = IntPtr.Zero;
                return;
        }
        base.WndProc(ref m);
    }

    private const int WM_MOUSEACTIVATE = 0x0021, WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E, MA_NOACTIVATE = 3;
    private const int WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040, SWP_HIDEWINDOW = 0x0080;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetMessageExtraInfo();
}
