using System.Diagnostics;
using System.Drawing;
using System.Threading.Channels;

namespace Glissando.Mirror;

/// <summary>
/// Destination side of one mirror (one source window; the tray keeps one receiver per mirror id): reassembles
/// mirror chunks into tiles, decodes them and paints them into a <see cref="MirrorWindow"/>. Frames are best-effort -- if a chunk is lost the partial tile is dropped when the
/// next tile's chunks start arriving, and the sender's background heal repaints stale tiles.
///
/// Threading: <see cref="Handle"/> is called on the session's receive threads (it is cheap: bookkeeping under a
/// lock). Complete tiles go to a decoder task, which turns the JPEG into a bitmap off the UI thread and hands
/// the UI only the blit. Doing all of it on the UI thread with one marshal per chunk held the window to a few
/// hundred tiles a second and made the viewer sluggish under a full-window change.
/// </summary>
internal sealed class MirrorReceiver
{
    private MirrorWindow? _window;
    private int _srcW, _srcH;
    private readonly byte _id;
    private string _title = "";
    private bool _ended;

    /// <summary>Where the mirror window's interactions go (the session sends them to the source PC).</summary>
    public Action<Frame>? Send { get; set; }

    /// <summary>Runs an action on the UI thread (set by the tray, which owns the message loop).</summary>
    public Action<Action>? Ui { get; set; }

    private readonly object _gate = new();
    private ushort _frameId;
    private int _count, _have;
    private Rectangle _region;
    private bool _keyframe;
    private byte[]?[] _chunks = [];

    private readonly Channel<(Rectangle region, bool keyframe, byte[] jpeg, int srcW, int srcH)> _decode =
        Channel.CreateBounded<(Rectangle, bool, byte[], int, int)>(new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    private readonly Stopwatch _log = Stopwatch.StartNew();
    private int _rxChunks, _rxDone, _rxDropped, _rxDecodeErr;
    private bool _painted;

    public MirrorReceiver(byte id)
    {
        _id = id;
        Task.Run(DecodeLoop);
    }

    public void Handle(Frame f)
    {
        switch (f.Type)
        {
            case FrameType.MirrorBegin:
            {
                if (f.Payload.Length < 5) break;
                int w = f.U16(1), h = f.U16(3);
                string title = Frame.MirrorTitleOf(f);
                lock (_gate) { _srcW = w; _srcH = h; _title = title; }
                Log.Info($"mirror rx #{_id}: begin {w}x{h} \"{title}\"");
                Ui?.Invoke(() => { if (!_ended) Ensure().Prepare(w, h, title); });
                break;
            }
            case FrameType.MirrorChunk:
                HandleChunk(f);
                break;
            case FrameType.MirrorEnd:
                lock (_gate) Reset();
                _decode.Writer.TryComplete();
                Ui?.Invoke(Close);
                break;
        }
    }

    private void MaybeLog()
    {
        if (_log.ElapsedMilliseconds < 2000) return;
        Log.Info($"mirror rx #{_id}: {_rxChunks} chunks, {_rxDone} tiles painted, {_rxDropped} incomplete, {_rxDecodeErr} decode errors in {_log.ElapsedMilliseconds} ms");
        _rxChunks = _rxDone = _rxDropped = _rxDecodeErr = 0; _log.Restart();
    }

    private void HandleChunk(Frame f)
    {
        if (f.Payload.Length < Frame.MirrorChunkHeader) return;
        ushort frameId = f.U16(1), index = f.U16(3), count = f.U16(5);
        int x = f.U16(7), y = f.U16(9), w = f.U16(11), h = f.U16(13);
        bool keyframe = f.Payload[15] != 0;
        if (count == 0) return;

        (Rectangle region, bool key, byte[] jpeg, int sw, int sh)? done = null;
        lock (_gate)
        {
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
            if (++_have == _count)
            {
                int total = 0;
                foreach (var c in _chunks) total += c?.Length ?? 0;
                var jpeg = new byte[total];
                int o = 0;
                foreach (var c in _chunks) { c!.CopyTo(jpeg, o); o += c.Length; }
                done = (_region, _keyframe, jpeg, _srcW, _srcH);
                // (the title rides in MirrorBegin; a window opened by a tile before it arrived gets it then)
                _chunks = []; _have = 0; _count = 0; // _count=0 so the next tile is not miscounted as a drop
            }
        }
        if (done is { } d) _decode.Writer.TryWrite(d);
    }

    private async Task DecodeLoop()
    {
        var reader = _decode.Reader;
        while (await reader.WaitToReadAsync())
        {
            while (reader.TryRead(out var t))
            {
                Bitmap img;
                MemoryStream ms;
                try
                {
                    ms = new MemoryStream(t.jpeg);
                    img = new Bitmap(ms);   // decoded here, off the UI thread; the stream must outlive the bitmap
                }
                catch (Exception ex) { lock (_gate) _rxDecodeErr++; Log.Warn("mirror decode: " + ex.Message); continue; }
                var ui = Ui;
                if (ui == null) { img.Dispose(); ms.Dispose(); continue; }
                ui(() =>
                {
                    try
                    {
                        if (_ended) return;
                        var win = Ensure();
                        win.Prepare(t.srcW, t.srcH, _title);
                        win.Blit(t.region.Location, img, t.keyframe);
                        lock (_gate)
                        {
                            _rxDone++;
                            if (!_painted) { _painted = true; Log.Info($"mirror rx #{_id}: first tile painted, region {t.region}, backing {t.srcW}x{t.srcH}"); }
                        }
                    }
                    catch (Exception ex) { Log.Warn("mirror paint: " + ex.Message); }
                    finally { img.Dispose(); ms.Dispose(); }
                });
            }
        }
    }

    /// <summary>UI thread only.</summary>
    private MirrorWindow Ensure()
    {
        if (_window == null || _window.IsDisposed)
        {
            _window = new MirrorWindow(_id);
            _window.Input = f => Send?.Invoke(f);
            _window.FormClosed += (_, _) => { _window = null; lock (_gate) Reset(); };
        }
        return _window;
    }

    private void Reset() { _chunks = []; _have = 0; _frameId = 0; _count = 0; _painted = false; }

    /// <summary>UI thread only. The mirror is over: its window closes for good.</summary>
    public void Close()
    {
        _ended = true;
        _decode.Writer.TryComplete();
        _window?.Close();
        _window = null;
        lock (_gate) Reset();
    }
}
