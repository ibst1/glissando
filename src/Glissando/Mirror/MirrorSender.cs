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
    private const int RefreshEveryMs = 200; // heal a single stale tile this often -- a trickle, not a stream
    private const int PaceBelow = 40;       // keep the send queue short so a tile shows soon after it changes

    private readonly IntPtr _hwnd;
    private readonly Action<Frame> _send;
    private readonly Func<int> _queueDepth;
    private CancellationTokenSource? _cts;

    public MirrorSender(IntPtr hwnd, Action<Frame> send, Func<int> queueDepth)
    {
        _hwnd = hwnd;
        _send = send;
        _queueDepth = queueDepth;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        new Thread(() => Loop(ct)) { IsBackground = true, Name = "mirror-capture" }.Start();
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _send(Frame.MirrorEnd()); } catch { }
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

        while (!ct.IsCancellationRequested)
        {
            int t0 = Environment.TickCount;
            try
            {
                using var bmp = WindowCapture.Capture(_hwnd);
                if (bmp == null) { Log.Info("mirror: source window gone"); break; }
                int w = bmp.Width, h = bmp.Height;

                if (w != lastW || h != lastH)
                {
                    for (int i = 0; i < 3; i++) _send(Frame.MirrorBegin(w, h)); // redundant: the size must arrive
                    lastW = w; lastH = h; prev = null;
                    Log.Info($"mirror tx: begin {w}x{h}");
                }

                var cur = WindowCapture.Pixels(bmp, out int stride);
                bool sameShape = prev != null && prevW == w && prevH == h && prevStride == stride;
                bool first = !sameShape;

                int tilesX = (w + Tile - 1) / Tile, tilesY = (h + Tile - 1) / Tile, tileCount = tilesX * tilesY;
                var sent = new bool[tileCount];

                void SendIndex(int idx, bool full)
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
                }

                for (int idx = 0; idx < tileCount && !ct.IsCancellationRequested; idx++)
                {
                    int col = idx % tilesX, row = idx / tilesX;
                    int tx = col * Tile, ty = row * Tile;
                    int tw = Math.Min(Tile, w - tx), th = Math.Min(Tile, h - ty);
                    if (first || !WindowCapture.TileEqual(prev!, cur, stride, tx, ty, tx + tw, ty + th))
                        SendIndex(idx, first);
                }

                // Heal a single stale tile now and then, a trickle in the background; the whole window comes
                // round over tileCount * RefreshEveryMs. Skipped while the user is actively changing tiles, so
                // typing owns the whole link.
                if (!first && sent.Count(b => b) == 0 && refreshClock.ElapsedMilliseconds >= RefreshEveryMs)
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
