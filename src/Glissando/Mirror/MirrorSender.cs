using System.Diagnostics;
using System.Drawing;

namespace Glissando.Mirror;

/// <summary>
/// Source side of window mirroring (increment 2). Captures one window on a background thread and sends it as a
/// grid of small, independent JPEG tiles. Every frame sends the tiles that CHANGED (so typing shows at once)
/// plus a few rotating "refresh" tiles that heal any tile a dropped chunk left stale -- there is no periodic
/// full-window keyframe to saturate the link and make edits queue behind it. Sending is paced against the send
/// queue so it never overflows: input frames must never wait behind window pixels.
///
/// Two paths: BLE (always there; slow, so tightly budgeted) and the UDP pixel path over a shared network when
/// it is up (see UdpPixelLink), chosen afresh every frame. Tiles are encoded in parallel straight out of the
/// captured pixel buffer: one thread cloning and encoding 128 px tiles one by one managed ~360 tiles/s, which
/// made a full-window change on a 4K window take more than a second even with megabytes per second to spare.
/// </summary>
internal sealed class MirrorSender
{
    private const int Fps = 12;
    private const long Quality = 45;        // documents stay readable; fewer bytes per tile = less link backlog
    private const int Tile = 128;
    private const int ChunkData = 400;
    private const int RefreshEveryMs = 250; // after the first pass: heal one stale tile this often at most, and only on an idle link
    private const int PaceBelow = 40;       // keep the send queue short so a tile shows soon after it changes

    // Cap the JPEG bytes sent per frame; a bigger change (a scroll, an Edge repaint) is split across frames
    // instead of dumped in one burst that saturates the link and freezes the viewer. The cap is tight when
    // this PC is the BLE peripheral, whose notify direction is the slow one -- 283 kB in a 2 s window there
    // stalled both the picture and the viewer's clicks (2026-09-12). At 12 fps the fast budget is ~180 kB/s,
    // the slow one ~34 kB/s. A change never lost: an unsent dirty tile is kept dirty (below) for a later frame.
    private const int SlowFrameBytes = 2800;
    private const int FastFrameBytes = 15000;

    // Over the UDP pixel path (Wi-Fi, measured 2.5 MB/s) everything is a size up: a datagram holds most of a
    // tile, tiles are 256 px (a quarter of the encodes and datagrams per area), the quality is higher, the
    // frame rate too, and the budget is an order of magnitude bigger. The capture is what costs then.
    private const int UdpTile = 256;
    private const long UdpQuality = 60;
    private const int UdpChunkData = 1100;
    private const int UdpFrameBytes = 160_000;
    private const int UdpFps = 30;
    private const int UdpRefreshEveryMs = 40;

    private readonly IntPtr _hwnd;
    private readonly Action<Frame> _send;
    private readonly Func<int> _queueDepth;
    private readonly bool _slowLink;
    private readonly bool _hide;
    private readonly Func<bool> _udpUp;
    private readonly Action<Frame> _sendUdp;
    private readonly MirrorTarget _target;
    private CancellationTokenSource? _cts;
    private GpuCapture? _gpu;

    /// <param name="send">The BLE path: control frames always, pixels when UDP is down.</param>
    /// <param name="udpUp">Whether the UDP pixel path is up right now (re-read every frame).</param>
    /// <param name="sendUdp">The UDP path for pixel chunks.</param>
    public MirrorSender(IntPtr hwnd, Action<Frame> send, Func<int> queueDepth, bool slowLink, bool hide, Func<bool> udpUp, Action<Frame> sendUdp)
    {
        _hwnd = hwnd;
        _send = send;
        _queueDepth = queueDepth;
        _slowLink = slowLink;
        _hide = hide;
        _udpUp = udpUp;
        _sendUdp = sendUdp;
        _target = new MirrorTarget(hwnd);
    }

    /// <summary>The viewer interacting with the mirror: apply it to the source window.</summary>
    public void Input(Frame f) => _target.Apply(f);

    /// <summary>True while the source window is invisible on this PC (see WindowHider).</summary>
    public bool Hidden => WindowHider.IsHidden(_hwnd);

    public IntPtr Hwnd => _hwnd;

    /// <summary>Someone clicked or typed on this PC: show the window here again, and keep mirroring it. An
    /// invisible foreground window would otherwise swallow their clicks.</summary>
    public void Reveal()
    {
        if (!Hidden) return;
        Log.Info("mirror: local input on this PC; showing the mirrored window here again");
        WindowHider.Unhide();
    }

    public void Start()
    {
        if (_hide) WindowHider.Hide(_hwnd);
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        new Thread(() => Loop(ct)) { IsBackground = true, Name = "mirror-capture" }.Start();
    }

    public void Stop()
    {
        _cts?.Cancel();
        _target.ReleaseAll();
        WindowHider.Unhide();
        // Three copies, like MirrorBegin: a lone MirrorEnd is a best-effort frame, and on the slow direction it
        // got lost often enough that the viewer's window stayed open showing a stale picture, which read as
        // "the mirror cannot be turned off" (2026-09-12). The receiver's End is idempotent.
        try { for (int i = 0; i < 3; i++) _send(Frame.MirrorEnd()); } catch { }
    }

    private void Loop(CancellationToken ct)
    {
        _gpu = GpuCapture.TryCreate(_hwnd);
        _target.UseVisibleBounds = _gpu != null;
        Log.Info(_gpu != null ? "mirror tx: capture via Windows.Graphics.Capture (GPU)" : "mirror tx: capture via PrintWindow");
        try { LoopCore(ct); }
        finally { _gpu?.Dispose(); _gpu = null; }
    }

    private void LoopCore(CancellationToken ct)
    {
        byte[]? prev = null, spare = null;
        int prevStride = 0, prevW = 0, prevH = 0, lastW = -1, lastH = -1, lastTile = 0, refreshCursor = 0;
        ushort frameId = 0;
        var logClock = Stopwatch.StartNew();
        var refreshClock = Stopwatch.StartNew();
        int txTiles = 0, txChunks = 0, txBytes = 0, frames = 0;
        long tCapture = 0, tDiff = 0, tEncode = 0, tSend = 0;
        string lastMode = "";
        int tileEstimate = 2000;   // running average of an encoded tile, seeds the per-frame budget check
        bool lastUdp = false, pathLogged = false;

        while (!ct.IsCancellationRequested)
        {
            int t0 = Environment.TickCount;
            bool udp = _udpUp();
            int interval = Math.Max(1, 1000 / (udp ? UdpFps : Fps));
            int tile = udp ? UdpTile : Tile;
            long quality = udp ? UdpQuality : Quality;
            int chunk = udp ? UdpChunkData : ChunkData;
            var sender = udp ? _sendUdp : _send;
            if (udp != lastUdp || !pathLogged)
            {
                lastUdp = udp; pathLogged = true;
                Log.Info(udp ? "mirror tx: pixels via UDP (shared network)" : "mirror tx: pixels via BLE");
            }
            Bitmap? bmp = null;
            try
            {
                long s0 = Stopwatch.GetTimestamp();
                bool hidden = Hidden;
                byte[] cur; int w, h, stride; bool fresh; string mode;
                if (_gpu != null)
                {
                    // GPU: a frame only when the window changed; otherwise nothing new (cur = prev) and the
                    // heal below still gets its turn. TryGetFrame already waited up to one interval.
                    if (_gpu.Closed || WindowCapture.SizeOf(_hwnd) == null) { Log.Info("mirror: source window gone"); WindowHider.Unhide(); break; }
                    var buf = spare;
                    fresh = _gpu.TryGetFrame(ref buf, interval, out w, out h, out stride);
                    if (fresh) { cur = buf!; spare = prev; WindowCapture.OverlayPopupsOnto(cur, stride, w, h, _hwnd); }
                    else if (prev != null) { cur = prev; w = prevW; h = prevH; stride = prevStride; }
                    else continue;
                    mode = "gpu";
                }
                else
                {
                    bmp = WindowCapture.Capture(_hwnd, hidden, out bool fromScreen);
                    if (bmp == null) { Log.Info("mirror: source window gone"); WindowHider.Unhide(); break; }
                    mode = hidden ? "hidden" : fromScreen ? "screen" : "print";
                    w = bmp.Width; h = bmp.Height;
                    cur = WindowCapture.Pixels(bmp, out stride);
                    fresh = true;
                }
                if (mode != lastMode)
                {
                    lastMode = mode;
                    Log.Info(mode switch
                    {
                        "gpu" => "mirror tx: capturing via Windows.Graphics.Capture (the window's own surface, hidden or not; popups overlaid from the screen)",
                        "hidden" => "mirror tx: window hidden here; capturing via PrintWindow, popups overlaid from the screen",
                        "screen" => "mirror tx: capturing from the screen (window in front; popups show)",
                        _ => $"mirror tx: capturing via PrintWindow (window not in front; foreground is \"{WindowCapture.Title(WindowCapture.Foreground())}\")",
                    });
                }

                if (w != lastW || h != lastH)
                {
                    for (int i = 0; i < 3; i++) _send(Frame.MirrorBegin(w, h)); // redundant: the size must arrive
                    lastW = w; lastH = h; refreshCursor = 0;                    // a fresh heal pass for the new size
                    if (!fresh || !ReferenceEquals(cur, prev)) prev = null;
                    Log.Info($"mirror tx: begin {w}x{h}");
                }
                if (tile != lastTile) { lastTile = tile; refreshCursor = 0; }

                long s1 = Stopwatch.GetTimestamp();
                bool sameShape = prev != null && prevW == w && prevH == h && prevStride == stride;
                bool first = !sameShape;

                int tilesX = (w + tile - 1) / tile, tilesY = (h + tile - 1) / tile, tileCount = tilesX * tilesY;
                Rectangle RegionOf(int idx)
                {
                    int col = idx % tilesX, row = idx / tilesX;
                    int tx = col * tile, ty = row * tile;
                    return new Rectangle(tx, ty, Math.Min(tile, w - tx), Math.Min(tile, h - ty));
                }

                // Decide the frame's tiles: every dirty tile within the byte budget, deferring the rest.
                var regions = new List<Rectangle>();
                var fulls = new List<bool>();
                int budget = udp ? UdpFrameBytes : _slowLink ? SlowFrameBytes : FastFrameBytes;
                int planned = 0;
                for (int idx = 0; (first || fresh) && idx < tileCount && !ct.IsCancellationRequested; idx++)
                {
                    var r = RegionOf(idx);
                    if (!(first || !WindowCapture.TileEqual(prev!, cur, stride, r.Left, r.Top, r.Right, r.Bottom)))
                        continue;
                    // The first frame (start or resize) has no prior to defer against, so it goes whole; after
                    // that, once the frame's byte budget is spent the rest of a big change waits. An unsent
                    // dirty tile must not be forgotten: revert it in cur to the old pixels so it still differs
                    // next frame and is picked up then (a few frames to fully redraw a scroll, never a freeze).
                    // Budget is checked with the expected size of the NEXT tile, not after the fact: testing
                    // spent < budget alone let every frame overshoot by one tile, twelve times a second.
                    if (first || planned + tileEstimate <= budget)
                    {
                        regions.Add(r); fulls.Add(first);
                        planned += tileEstimate;
                    }
                    else
                        WindowCapture.CopyTile(prev!, cur, stride, r.Left, r.Top, r.Right, r.Bottom);
                }

                // Heal stale tiles in the background, paced by the LINK rather than a clock: a heal tile goes
                // only when nothing changed this frame AND the send queue is empty, so it never takes capacity
                // from edits or from the viewer's input. The first pass after (re)start runs as fast as the
                // queue drains -- that is what fills the tiles the opening burst lost -- later passes at most
                // every RefreshEveryMs. A fixed 60 ms trickle at 12 fps was 12 tiles/s, ~40 kB/s of constant
                // load, which saturated the slow BLE direction (peripheral -> central) even with nothing to show.
                bool healing = refreshCursor < tileCount;
                if (!first && regions.Count == 0 && (udp || _queueDepth() == 0)
                    && (healing || refreshClock.ElapsedMilliseconds >= (udp ? UdpRefreshEveryMs : RefreshEveryMs)))
                {
                    regions.Add(RegionOf(refreshCursor++ % tileCount)); fulls.Add(true);
                    refreshClock.Restart();
                }
                long s2 = Stopwatch.GetTimestamp();

                if (regions.Count > 0)
                {
                    var jpegs = WindowCapture.EncodeTiles(cur, stride, regions, quality);
                    long s3 = Stopwatch.GetTimestamp();
                    int sum = 0;
                    for (int i = 0; i < regions.Count && !ct.IsCancellationRequested; i++)
                    {
                        if (!udp) while (_queueDepth() > PaceBelow && !ct.IsCancellationRequested) ct.WaitHandle.WaitOne(3);
                        SendTile(frameId++, regions[i], fulls[i], jpegs[i], chunk, sender);
                        sum += jpegs[i].Length;
                        txChunks += Math.Max(1, (jpegs[i].Length + chunk - 1) / chunk);
                    }
                    txTiles += regions.Count; txBytes += sum;
                    tileEstimate = (tileEstimate + sum / regions.Count) / 2;
                    tEncode += s3 - s2; tSend += Stopwatch.GetTimestamp() - s3;
                }
                tCapture += s1 - s0; tDiff += s2 - s1; frames++;

                prev = cur; prevStride = stride; prevW = w; prevH = h;
            }
            catch (Exception ex) { Log.Warn("mirror capture: " + ex.Message); }
            finally { bmp?.Dispose(); }

            if (logClock.ElapsedMilliseconds >= 2000)
            {
                double ms = 1000.0 / Stopwatch.Frequency;
                int f = Math.Max(1, frames);
                Log.Info($"mirror tx: {txTiles} tiles, {txChunks} chunks, {txBytes / 1024} kB in {logClock.ElapsedMilliseconds} ms; {frames} frames, per frame capture {tCapture * ms / f:F0} ms, diff {tDiff * ms / f:F0} ms, encode {tEncode * ms / f:F0} ms, send {tSend * ms / f:F0} ms");
                txTiles = txChunks = txBytes = frames = 0; tCapture = tDiff = tEncode = tSend = 0; logClock.Restart();
            }

            int spent = Environment.TickCount - t0;
            if (spent < interval) ct.WaitHandle.WaitOne(interval - spent);
        }
    }

    private static void SendTile(ushort frameId, Rectangle region, bool full, byte[] jpeg, int chunkData, Action<Frame> send)
    {
        int count = Math.Max(1, (jpeg.Length + chunkData - 1) / chunkData);
        for (int i = 0; i < count; i++)
        {
            int off = i * chunkData;
            int len = Math.Min(chunkData, jpeg.Length - off);
            send(Frame.MirrorChunk(frameId, (ushort)i, (ushort)count, region.X, region.Y, region.Width, region.Height, full, jpeg.AsSpan(off, len)));
        }
    }
}
