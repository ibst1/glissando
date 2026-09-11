using System.Runtime.InteropServices;

namespace Glissando.Mirror;

/// <summary>
/// Source side of an interactive mirror: applies the viewer's pointer, clicks, wheel and keys to the mirrored
/// window. Everything goes through SendInput, the one path every application honours -- posting WM_* messages
/// to a background window loses the modifier state and is ignored by half the programs out there -- which
/// means the window must be on screen and in front when the input lands. So it is restored and brought forward
/// before a click or key, which Glissando may do because its own SendInput counts as the last input on this
/// PC. The source screen shows the window while that happens; nobody is sitting at it, the user is at the
/// viewer. Pointer moves alone never raise the window, they only place the cursor, so hover works once it is up.
/// </summary>
internal sealed class MirrorTarget
{
    private readonly IntPtr _hwnd;
    private readonly HashSet<(ushort vk, ushort scan, bool ext)> _keysDown = [];
    private readonly HashSet<byte> _buttonsDown = [];
    private readonly object _gate = new();

    public MirrorTarget(IntPtr hwnd) => _hwnd = hwnd;

    public void Apply(Frame f)
    {
        lock (_gate)
        {
            switch (f.Type)
            {
                case FrameType.MirrorPointer:
                    Place(f.U16(0), f.U16(2), raise: false);
                    break;
                case FrameType.MirrorMouse:
                    if (f.Payload.Length >= 8) Mouse(f.Payload[0], f.U16(1), f.U16(3), f.Payload[5], f.I16(6));
                    break;
                case FrameType.MirrorKey:
                    if (f.Payload.Length >= 5) Key(f.U16(0), f.U16(2), (f.Payload[4] & 1) != 0, (f.Payload[4] & 2) != 0);
                    break;
            }
        }
    }

    /// <summary>Put the cursor on a source-window pixel (the capture is the window rectangle, so the offset is
    /// from its top-left corner); false if the window is gone.</summary>
    private bool Place(int x, int y, bool raise)
    {
        if (!IsWindow(_hwnd)) return false;
        if (raise) Raise();
        if (!GetWindowRect(_hwnd, out var r)) return false;
        Native.MoveCursorAbsolute(r.Left + x, r.Top + y);
        return true;
    }

    private void Raise()
    {
        if (IsIconic(_hwnd)) ShowWindow(_hwnd, SW_RESTORE);
        if (GetForegroundWindow() == _hwnd) return;
        // A process may set the foreground window only if it produced the last input; a no-op SendInput
        // makes that true, the same fact the injector relies on for its cursor placement.
        Native.Send(Native.Mouse(Native.MOUSEEVENTF_MOVE, 0, 0));
        SetForegroundWindow(_hwnd);
    }

    private void Mouse(byte kind, int x, int y, byte button, int delta)
    {
        if (!Place(x, y, raise: kind is 1 or 2)) return;
        switch (kind)
        {
            case 1 or 2:
            {
                bool down = kind == 1;
                if (!ButtonFlag(button, down, out uint flag, out uint data)) return;
                if (down) _buttonsDown.Add(button); else _buttonsDown.Remove(button);
                Native.Send(Native.Mouse(flag, data: data));
                break;
            }
            case 3: Native.Send(Native.Mouse(Native.MOUSEEVENTF_WHEEL, data: unchecked((uint)delta))); break;
            case 4: Native.Send(Native.Mouse(Native.MOUSEEVENTF_HWHEEL, data: unchecked((uint)delta))); break;
        }
    }

    private static bool ButtonFlag(byte button, bool down, out uint flag, out uint data)
    {
        data = 0;
        switch (button)
        {
            case 1: flag = down ? Native.MOUSEEVENTF_LEFTDOWN : Native.MOUSEEVENTF_LEFTUP; return true;
            case 2: flag = down ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_RIGHTUP; return true;
            case 3: flag = down ? Native.MOUSEEVENTF_MIDDLEDOWN : Native.MOUSEEVENTF_MIDDLEUP; return true;
            case 4: flag = down ? Native.MOUSEEVENTF_XDOWN : Native.MOUSEEVENTF_XUP; data = 1; return true;
            case 5: flag = down ? Native.MOUSEEVENTF_XDOWN : Native.MOUSEEVENTF_XUP; data = 2; return true;
            default: flag = 0; return false;
        }
    }

    private void Key(ushort vk, ushort scan, bool up, bool ext)
    {
        if (!IsWindow(_hwnd)) return;
        if (!up) Raise();
        var key = (vk, scan, ext);
        if (up) _keysDown.Remove(key); else _keysDown.Add(key);
        // The injector's rule: an injected held modifier sticks only to the very next key, so re-assert every
        // held modifier before each non-modifier key-down.
        if (!up && vk != 0 && !Native.IsModifierVk(vk))
            foreach (var m in _keysDown)
                if (m.vk != 0 && Native.IsModifierVk(m.vk))
                    Client.InputInjector.SendKey(m.vk, m.scan, up: false, m.ext);
        Client.InputInjector.SendKey(vk, scan, up, ext);
    }

    /// <summary>Release whatever the viewer still holds, so nothing sticks when the mirror stops.</summary>
    public void ReleaseAll()
    {
        lock (_gate)
        {
            foreach (var k in _keysDown.ToArray()) Client.InputInjector.SendKey(k.vk, k.scan, up: true, k.ext);
            _keysDown.Clear();
            foreach (var b in _buttonsDown.ToArray())
                if (ButtonFlag(b, false, out uint flag, out uint data)) Native.Send(Native.Mouse(flag, data: data));
            _buttonsDown.Clear();
        }
    }

    private const int SW_RESTORE = 9;
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
}
