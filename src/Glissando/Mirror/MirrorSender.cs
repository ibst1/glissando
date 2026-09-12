using System.Diagnostics;
using System.Drawing;

namespace Glissando.Mirror;

/// <summary>
/// Source side of window mirroring (increment 2). Captures one window on a background thread and sends it as a
/// grid of small, independent JPEG tiles. Every frame sends the tiles that CHANGED (so typing shows at once)
/// plus a few rotating "refresh" tiles that heal any tile a dropped chunk left stale -- there is no periodic
/// full-window keyframe to saturate the link and make edits queue behind it. Sending is paced against the send
/// queue so it never overflows: input frames must never wait behind window pixels.
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

    private readonly IntPtr _hwnd;
    private readonly Action<Frame> _send;
    private readonly Func<int> _queueDepth;
    private readonly bool _slowLink;
    private readonly bool _hide;
    private readonly MirrorTarget _target;
    private CancellationTokenSource? _cts;

    public MirrorSender(IntPtr hwnd, Action<Frame> send, Func<int> queueDepth, bool slowLink, bool hide)
    {
        _hwnd = hwnd;
        _send = send;
        _queueDepth = queueDepth;
        _slowLink = slowLink;
        _hide = hide;
        _target = new MirrorTarget(hwnd);
    }

    /// <summary>The viewer interacting with the mirror: apply it to the source window.</summary>
    public void Input(Frame f) => _target.Apply(f);

    /// <summary>True while the source window is invisible on this PC (see WindowHider).</summary>
    public bool Hidden => WindowHider.IsHidden(_hwnd);

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
        byte[]? prev = null;
        int prevStride = 0, prevW = 0, prevH = 0, lastW = -1, lastH = -1, refreshCursor = 0;
        ushort frameId = 0;
        int interval = Math.Max(1, 1000 / Fps);
        var logClock = Stopwatch.StartNew();
        var refreshClock = Stopwatch.StartNew();
        int txTiles = 0, txChunks = 0, txBytes = 0;
        string lastMode = "";
        int tileEstimate = 2000;   // running average of an encoded tile, seeds the per-frame budget check

        while (!ct.IsCancellationRequested)
        {
            int t0 = Environment.TickCount;
            try
            {
                bool hidden = Hidden;
                using var bmp = WindowCapture.Capture(_hwnd, hidden, out bool fromScreen);
                if (bmp == null) { Log.Info("mirror: source window gone"); WindowHider.Unhide(); break; }
                string mode = hidden ? "hidden" : fromScreen ? "screen" : "print";
                if (mode != lastMode)
                {
                    lastMode = mode;
                    Log.Info(mode switch
                    {
                        "hidden" => "mirror tx: window hidden here; capturing via PrintWindow, popups overlaid from the screen",
                        "screen" => "mirror tx: capturing from the screen (window in front; popups show)",
                        _ => $"mirror tx: capturing via PrintWindow (window not in front; foreground is \"{WindowCapture.Title(WindowCapture.Foreground())}\")",
                    });
                }
                int w = bmp.Width, h = bmp.Height;

                if (w != lastW || h != lastH)
                {
                    for (int i = 0; i < 3; i++) _send(Frame.MirrorBegin(w, h)); // redundant: the size must arrive
                    lastW = w; lastH = h; prev = null; refreshCursor = 0;       // a fresh heal pass for the new size
                    Log.Info($"mirror tx: begin {w}x{h}");
                }

                var cur = WindowCapture.Pixels(bmp, out int stride);
                bool sameShape = prev != null && prevW == w && prevH == h && prevStride == stride;
                bool first = !sameShape;

                int tilesX = (w + Tile - 1) / Tile, tilesY = (h + Tile - 1) / Tile, tileCount = tilesX * tilesY;
                var sent = new bool[tileCount];

                int SendIndex(int idx, bool full)
                {
                    int col = idx % tilesX, row = idx / tilesX;
                    int tx = col * Tile, ty = row * Tile;
                    int tw = Math.Min(Tile, w - tx), th = Math.Min(Tile, h - ty);
                    while (_queueDepth() > PaceBelow && !ct.IsCancellationRequested) ct.WaitHandle.WaitOne(3);
                    var region = new Rectangle(tx, ty, tw, th);
                    byte[] jpeg = WindowCapture.EncodeJpeg(bmp, Quality, region);
                    SendTile(frameId++, region, full, jpeg);
                    sent[idx] = true;
                    txTiles++; txBytes += jpeg.Length;
                    txChunks += Math.Max(1, (jpeg.Length + ChunkData - 1) / ChunkData);
                    return jpeg.Length;
                }

                int budget = _slowLink ? SlowFrameBytes : FastFrameBytes;
                int spentBytes = 0;
                for (int idx = 0; idx < tileCount && !ct.IsCancellationRequested; idx++)
                {
                    int col = idx % tilesX, row = idx / tilesX;
                    int tx = col * Tile, ty = row * Tile;
                    int tw = Math.Min(Tile, w - tx), th = Math.Min(Tile, h - ty);
                    if (!(first || !WindowCapture.TileEqual(prev!, cur, stride, tx, ty, tx + tw, ty + th)))
                        continue;
                    // The first frame (start or resize) has no prior to defer against, so it goes whole; after
                    // that, once the frame's byte budget is spent the rest of a big change waits. An unsent
                    // dirty tile must not be forgotten: revert it in cur to the old pixels so it still differs
                    // next frame and is picked up then (a few frames to fully redraw a scroll, never a freeze).
                    // Budget is checked with the expected size of the NEXT tile, not after the fact: testing
                    // spentBytes < budget alone let every frame overshoot by one tile, twelve times a second,
                    // which showed as 90-100 kB per 2 s against a 67 kB budget (2026-09-12).
                    if (first || spentBytes + tileEstimate <= budget)
                    {
                        int n = SendIndex(idx, first);
                        spentBytes += n;
                        tileEstimate = (tileEstimate * 3 + n) / 4;
                    }
                    else
                        WindowCapture.CopyTile(prev!, cur, stride, tx, ty, tx + tw, ty + th);
                }

                // Heal stale tiles in the background, paced by the LINK rather than a clock: a heal tile goes
                // only when nothing changed this frame AND the send queue is empty, so it never takes capacity
                // from edits or from the viewer's input. The first pass after (re)start runs as fast as the
                // queue drains -- that is what fills the tiles the opening burst lost -- later passes at most
                // every RefreshEveryMs. A fixed 60 ms trickle at 12 fps was 12 tiles/s, ~40 kB/s of constant
                // load, which saturated the slow BLE direction (peripheral -> central) even with nothing to show.
                bool healing = refreshCursor < tileCount;
                if (!first && sent.Count(b => b) == 0 && _queueDepth() == 0
                    && (healing || refreshClock.ElapsedMilliseconds >= RefreshEveryMs))
                {
                    int idx = refreshCursor++ % tileCount;
                    SendIndex(idx, true);
                    refreshClock.Restart();
                }

                prev = cur; prevStride = stride; prevW = w; prevH = h;
            }
            catch (Exception ex) { Log.Warn("mirror capture: " + ex.Message); }

            if (logClock.ElapsedMilliseconds >= 2000)
            {
                Log.Info($"mirror tx: {txTiles} tiles, {txChunks} chunks, {txBytes / 1024} kB in {logClock.ElapsedMilliseconds} ms");
                txTiles = txChunks = txBytes = 0; logClock.Restart();
            }

            int spent = Environment.TickCount - t0;
            if (spent < interval) ct.WaitHandle.WaitOne(interval - spent);
        }
    }

    private void SendTile(ushort frameId, Rectangle region, bool full, byte[] jpeg)
    {
        int count = Math.Max(1, (jpeg.Length + ChunkData - 1) / ChunkData);
        for (int i = 0; i < count; i++)
        {
            int off = i * ChunkData;
            int len = Math.Min(ChunkData, jpeg.Length - off);
            _send(Frame.MirrorChunk(frameId, (ushort)i, (ushort)count, region.X, region.Y, region.Width, region.Height, full, jpeg.AsSpan(off, len)));
        }
    }
}
