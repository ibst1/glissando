using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Glissando.Mirror;

/// <summary>
/// Window capture through Windows.Graphics.Capture: the compositor hands us the window's own surface on the
/// GPU, and we copy it down. Measured on the source PC (2026-09-12, 1920x1140 Chromium window): 4-5 ms a frame
/// against 28 ms for PrintWindow, 42 ms with the window hidden -- and the hidden window (layered alpha 1, see
/// WindowHider) comes through in full, because it is the window's surface, not the screen, that is captured.
/// Frames arrive only when the window's content changes, so a still window costs nothing at all. Other
/// top-level windows of the app (menus, drop-downs) are not part of a window's surface; the sender overlays
/// them from the screen as before. The yellow capture border is turned off where Windows allows it.
/// </summary>
internal sealed class GpuCapture : IDisposable
{
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess { IntPtr GetInterface([In] ref Guid iid); }

    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr inspectable);

    private static readonly Guid ItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly GraphicsCaptureItem _item;
    private Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly AutoResetEvent _arrived = new(false);
    private ID3D11Texture2D? _staging;
    private int _stagingW, _stagingH;
    private Windows.Graphics.SizeInt32 _poolSize;

    /// <summary>The window went away.</summary>
    public volatile bool Closed;

    public static bool Supported
    {
        get { try { return GraphicsCaptureSession.IsSupported(); } catch { return false; } }
    }

    /// <summary>Null, with a log line, when GPU capture cannot be set up for this window.</summary>
    public static GpuCapture? TryCreate(IntPtr hwnd)
    {
        try
        {
            if (!Supported) { Log.Info("mirror: Windows.Graphics.Capture not supported here"); return null; }
            return new GpuCapture(hwnd);
        }
        catch (Exception ex) { Log.Warn("mirror: GPU capture unavailable: " + ex.Message); return null; }
    }

    private GpuCapture(IntPtr hwnd)
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, null, out _device!).CheckError();
        _context = _device.ImmediateContext;
        using (var dxgi = _device.QueryInterface<IDXGIDevice>())
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out IntPtr insp));
            _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(insp);
            Marshal.Release(insp);
        }
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = ItemIid;
        IntPtr itemPtr = interop.CreateForWindow(hwnd, ref iid);
        _item = GraphicsCaptureItem.FromAbi(itemPtr);
        Marshal.Release(itemPtr);
        _item.Closed += (_, _) => { Closed = true; _arrived.Set(); };

        _poolSize = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
        _pool.FrameArrived += (_, _) => _arrived.Set();
        _session = _pool.CreateCaptureSession(_item);
        try { _session.IsCursorCaptureEnabled = false; } catch { }
        try { _session.IsBorderRequired = false; } catch (Exception ex) { Log.Info("mirror: capture border stays on (" + ex.GetType().Name + ")"); }
        _session.StartCapture();
    }

    /// <summary>Wait up to <paramref name="waitMs"/> for a new frame and copy it, tightly packed 32bpp BGRA, into
    /// <paramref name="buffer"/> (replaced when too small). False when nothing changed in time. Only the newest
    /// queued frame is used; older ones are dropped. Called from one thread.</summary>
    public bool TryGetFrame(ref byte[]? buffer, int waitMs, out int width, out int height, out int stride)
    {
        width = height = stride = 0;
        if (Closed) return false;
        Direct3D11CaptureFrame? frame = Drain();
        if (frame == null)
        {
            _arrived.WaitOne(Math.Max(0, waitMs));
            frame = Drain();
            if (frame == null) return false;
        }
        using (frame)
        {
            var size = frame.ContentSize;
            width = size.Width; height = size.Height;
            if (width <= 0 || height <= 0) return false;
            var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
            var texIid = typeof(ID3D11Texture2D).GUID;
            IntPtr texPtr = access.GetInterface(ref texIid);
            using var tex = new ID3D11Texture2D(texPtr);
            var d = tex.Description;
            if (_staging == null || _stagingW != (int)d.Width || _stagingH != (int)d.Height)
            {
                _staging?.Dispose();
                _stagingW = (int)d.Width; _stagingH = (int)d.Height;
                _staging = _device.CreateTexture2D(new Texture2DDescription(d.Format, d.Width, d.Height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
            }
            _context.CopyResource(_staging, tex);
            var map = _context.Map(_staging, 0, MapMode.Read);
            try
            {
                width = Math.Min(width, _stagingW); height = Math.Min(height, _stagingH);
                stride = width * 4;
                if (buffer == null || buffer.Length < stride * height) buffer = new byte[stride * height];
                unsafe
                {
                    byte* src = (byte*)map.DataPointer;
                    fixed (byte* dst = buffer)
                        for (int y = 0; y < height; y++)
                            Buffer.MemoryCopy(src + (long)y * map.RowPitch, dst + (long)y * stride, stride, stride);
                }
            }
            finally { _context.Unmap(_staging, 0); }

            // The window was resized: give the pool the new size so the next frames are not clipped or padded.
            if (size.Width != _poolSize.Width || size.Height != _poolSize.Height)
            {
                _poolSize = size;
                try { _pool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size); } catch (Exception ex) { Log.Warn("mirror: capture pool recreate: " + ex.Message); }
            }
        }
        return true;
    }

    private Direct3D11CaptureFrame? Drain()
    {
        Direct3D11CaptureFrame? latest = null;
        while (true)
        {
            Direct3D11CaptureFrame? f;
            try { f = _pool.TryGetNextFrame(); } catch { f = null; }
            if (f == null) break;
            latest?.Dispose();
            latest = f;
        }
        return latest;
    }

    public void Dispose()
    {
        try { _session.Dispose(); } catch { }
        try { _pool.Dispose(); } catch { }
        _staging?.Dispose();
        _context.Dispose();
        _device.Dispose();
        _arrived.Dispose();
    }
}
