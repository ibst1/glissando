using System.Windows.Forms;
using Xunit;

namespace Glissando.Tests;

public class TrayIconProbeTests
{
    // Pins the NotifyIcon internals the tray watchdog reads by reflection (_window, _id) against the WinForms
    // build actually bundled: an icon that was never shown must give a definite "not registered", never "cannot
    // tell". The first shipped probe matched _id as int; .NET 8 declares it uint, and the watchdog silently
    // degraded to one blind re-add.
    [Fact]
    public void Probe_CanReadAnUnshownIcon_AndSaysNotRegistered()
    {
        using var icon = new NotifyIcon { Text = "probe", Visible = false };
        Assert.Equal(false, TrayIconProbe.IsRegistered(icon));
    }
}