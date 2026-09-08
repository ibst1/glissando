using System.Drawing;
using Xunit;

namespace Glissando.Tests;

public class ProtocolTests
{
    [Fact]
    public void Hello_RoundTrips()
    {
        var nonce = Enumerable.Range(0, 16).Select(i => (byte)(i * 7)).ToArray();
        MonitorRect[] monitors =
        [
            new(new Rectangle(0, 1200, 2560, 1440), 1),
            new(new Rectangle(2560, 0, 1920, 1200), 4),
        ];
        var info = new HelloInfo(true, new Point(-640, 0), new Size(7680, 2640), 1234567890123, nonce, monitors, "Lärkan");

        var frame = Frame.Hello(info);
        Assert.True(Frame.TryDecode(frame.Encode(), out var decoded));
        var parsed = HelloInfo.Parse(decoded);

        Assert.NotNull(parsed);
        Assert.Equal(info.CaptureEnabled, parsed!.CaptureEnabled);
        Assert.Equal(info.PeerOrigin, parsed.PeerOrigin);
        Assert.Equal(info.Screen, parsed.Screen);
        Assert.Equal(info.PlacementUpdated, parsed.PlacementUpdated);
        Assert.Equal(info.Nonce, parsed.Nonce);
        Assert.Equal(monitors, parsed.Monitors);
        Assert.Equal("Lärkan", parsed.Name);
    }

    [Fact]
    public void Hello_WithAnotherVersion_IsRejected()
    {
        var frame = Frame.Hello(new HelloInfo(true, Point.Empty, new Size(1, 1), 0, new byte[16], [], "x"));
        frame.Payload[0] = 2;
        Assert.Null(HelloInfo.Parse(frame));
    }

    [Fact]
    public void EnterAndReturnEdge_RoundTrip()
    {
        var c = new Crossing(Dir.Up, new Point(-5, 123456));
        Assert.Equal(c, Frame.Enter(c).ReadCrossing());
        Assert.Equal(c, Frame.ReturnEdge(c).ReadCrossing());
    }

    [Fact]
    public void Ping_CarriesTheTimestampAndPongEchoesIt()
    {
        var ping = Frame.Ping(987654321);
        Assert.Equal(987654321, ping.I64(0));
        Assert.Equal(987654321, Frame.Pong(ping.Payload).I64(0));
    }

    [Fact]
    public void Clipboard_SplitsIntoChunksThatReassemble()
    {
        var text = string.Concat(Enumerable.Repeat("åäö0123456789", 200));
        var chunks = Frame.ClipboardText(7, text).ToArray();
        Assert.True(chunks.Length > 1);
        Assert.All(chunks, c => Assert.Equal(7, c.Payload[0]));
        Assert.All(chunks, c => Assert.Equal(chunks.Length, c.Payload[2]));
        var bytes = chunks.OrderBy(c => c.Payload[1]).SelectMany(c => c.Payload[3..]).ToArray();
        Assert.Equal(text, System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Crypto_SealsAndOpens_AndRejectsReplaysAndWrongKeys()
    {
        using var central = new Crypto("correct horse", isCentral: true);
        using var peripheral = new Crypto("correct horse", isCentral: false);
        using var stranger = new Crypto("wrong key", isCentral: false);

        var packet = central.Seal([1, 2, 3, 4]);
        Assert.True(peripheral.Open(packet, out var plain));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, plain);

        Assert.False(peripheral.Open(packet, out _));   // replay
        Assert.False(stranger.Open(central.Seal([9]), out _)); // wrong passphrase
        Assert.False(central.Open(central.Seal([9]), out _));  // reflected back to the sender: wrong direction
    }

    [Fact]
    public void Crypto_CountersAreIncreasingAcrossPackets()
    {
        using var a = new Crypto("k", true);
        using var b = new Crypto("k", false);
        for (int i = 0; i < 50; i++) Assert.True(b.Open(a.Seal([(byte)i]), out _));
    }

    [Fact]
    public void Settings_OtherMonitorList_RoundTrips()
    {
        var s = new Settings();
        MonitorRect[] monitors = [new(new Rectangle(0, 0, 1920, 1200), 1), new(new Rectangle(1920, -300, 2560, 1440), 3)];
        s.OtherMonitorList = monitors;
        Assert.Equal("0,0,1920,1200,1;1920,-300,2560,1440,3", s.OtherMonitors);
        Assert.Equal(monitors, s.OtherMonitorList);
        s.OtherMonitors = "garbage";
        Assert.Empty(s.OtherMonitorList);
    }
}
