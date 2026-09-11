using System.Diagnostics;
using System.Drawing;

namespace Glissando.Mirror;

/// <summary>
/// Destination side: reassembles mirror chunks into frames and paints them into a <see cref="MirrorWindow"/>.
/// All methods run on the UI thread; the session marshals incoming frames here. Frames are best-effort -- if a
/// chunk is lost the partial frame is dropped when the next frame's chunks start arriving, and the periodic
/// keyframe repaints the whole window to heal any accumulated gaps.
/// </summary>
internal sealed class MirrorReceiver
{
    private MirrorWindow? _window;
    private int _srcW, _srcH;

    private ushort _frameId;
    private int _count, _have;
    private Rectangle _region;
    private bool _keyframe;
    private byte[]?[] _chunks = [];

    private readonly Stopwatch _log = Stopwatch.StartNew();
    private int _rxChunks, _rxDone, _rxDropped, _rxDecodeErr;
    private bool _painted;

    public void Handle(Frame f)
    {
        switch (f.Type)
        {
            case FrameType.MirrorBegin:
                _srcW = f.U16(0); _srcH = f.U16(2);
                Log.Info($"mirror rx: begin {_srcW}x{_srcH}");
                Ensure().Prepare(_srcW, _srcH);
                break;
            case FrameType.MirrorChunk:
                HandleChunk(f);
                break;
            case FrameType.MirrorEnd:
                _window?.Hide();
                Reset();
                break;
        }
    }

    private void MaybeLog()
    {
        if (_log.ElapsedMilliseconds < 2000) return;
        Log.Info($"mirror rx: {_rxChunks} chunks, {_rxDone} tiles painted, {_rxDropped} incomplete, {_rxDecodeErr} decode errors in {_log.ElapsedMilliseconds} ms");
        _rxChunks = _rxDone = _rxDropped = _rxDecodeErr = 0; _log.Restart();
    }

    private void HandleChunk(Frame f)
    {
        if (f.Payload.Length < Frame.MirrorChunkHeader) return;
        ushort frameId = f.U16(0), index = f.U16(2), count = f.U16(4);
        int x = f.U16(6), y = f.U16(8), w = f.U16(10), h = f.U16(12);
        bool keyframe = f.Payload[14] != 0;
        if (count == 0) return;
        _rxChunks++;
        MaybeLog();

        // A keyframe covers the whole window, so it also tells us the source size if MirrorBegin was lost.
        if (keyframe) { _srcW = Math.Max(_srcW, x + w); _srcH = Math.Max(_srcH, y + h); }

        if (frameId != _frameId || _chunks.Length != count)
        {
            if (_count > 0 && _have < _count) _rxDropped++; // abandoned an incomplete tile
            _frameId = frameId; _count = count; _have = 0;
            _region = new Rectangle(x, y, w, h); _keyframe = keyframe;
            _chunks = new byte[count][];
        }
        if (index >= _chunks.Length || _chunks[index] != null) return;
        _chunks[index] = f.Payload.AsSpan(Frame.MirrorChunkHeader).ToArray();
        if (++_have == _count) Complete();
    }

    private void Complete()
    {
        try
        {
            int total = 0;
            foreach (var c in _chunks) total += c?.Length ?? 0;
            var jpeg = new byte[total];
            int o = 0;
            foreach (var c in _chunks) { c!.CopyTo(jpeg, o); o += c.Length; }

            using var ms = new MemoryStream(jpeg);
            using var img = new Bitmap(ms);
            var win = Ensure();
            win.Prepare(_srcW, _srcH);
            win.Blit(_region.Location, img, _keyframe);
            _rxDone++;
            if (!_painted) { _painted = true; Log.Info($"mirror rx: first tile painted, region {_region}, backing {_srcW}x{_srcH}"); }
        }
        catch (Exception ex) { _rxDecodeErr++; Log.Warn("mirror decode: " + ex.Message); }
        finally { _chunks = []; _have = 0; _count = 0; } // _count=0 so the next tile is not miscounted as a drop
    }

    private MirrorWindow Ensure()
    {
        if (_window == null || _window.IsDisposed)
        {
            _window = new MirrorWindow();
            _window.FormClosed += (_, _) => { _window = null; Reset(); };
        }
        return _window;
    }

    private void Reset() { _chunks = []; _have = 0; _frameId = 0; _count = 0; _painted = false; }

    public void Close()
    {
        _window?.Close();
        _window = null;
        Reset();
    }
}
