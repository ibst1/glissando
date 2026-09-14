using Xunit;

namespace Glissando.Tests;

public class StartupTests
{
    private const string Exe = @"C:\Users\me\Program\Glissando\Glissando.exe";

    [Theory]
    [InlineData("\"C:\\Users\\me\\Program\\Glissando\\Glissando.exe\"")]                 // as Settings writes it
    [InlineData("C:\\Users\\me\\Program\\Glissando\\Glissando.exe")]                     // unquoted
    [InlineData("\"c:\\users\\ME\\program\\glissando\\GLISSANDO.EXE\"")]                 // case-insensitive
    [InlineData("  \"C:\\Users\\me\\Program\\Glissando\\Glissando.exe\"  ")]             // stray spaces
    [InlineData("\"C:\\Users\\me\\Program\\Glissando\\Glissando.exe\" --console")]       // with arguments
    [InlineData("C:\\Users\\me\\Program\\Glissando\\Glissando.exe --ble connect")]       // unquoted with arguments
    public void PointsAt_SameExe(string value) => Assert.True(Startup.RunKeyPointsAt(value, Exe));

    [Theory]
    [InlineData("\"C:\\Users\\me\\Program\\Glissando\\dist\\Glissando.exe\"")]           // the stale nested copy (RS)
    [InlineData("\"C:\\Users\\me\\OneDrive\\c\\Glissando\\dist\\Glissando.exe\"")]      // another location
    [InlineData("\"C:\\Users\\me\\Program\\Glissando\\Glissando-0.3.35.exe\"")]          // another file name
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void PointsAt_OtherOrNothing(string? value) => Assert.False(Startup.RunKeyPointsAt(value, Exe));

    [Fact]
    public void PointsAt_EmptyExe_IsFalse() => Assert.False(Startup.RunKeyPointsAt("\"" + Exe + "\"", ""));
}
