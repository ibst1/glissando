using System.Text.Json;
using Xunit;

namespace Glissando.Tests;

public class PassphraseTests
{
    [Fact]
    public void GeneratedIsStrongAndUnique()
    {
        var a = Passphrases.Generate();
        var b = Passphrases.Generate();
        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{4}(-[0-9A-HJKMNP-TV-Z]{4}){5}$", a);
        Assert.NotEqual(a, b);
        Assert.True(Passphrases.IsGenerated(a));
        Assert.True(Passphrases.IsStrong(a));
    }

    [Fact]
    public void TypedCodeNormalizesToTheGeneratedOne()
    {
        var code = Passphrases.Generate();
        var typed = code.Replace("-", " ").ToLowerInvariant().Replace('0', 'o').Replace('1', 'l');
        Assert.Equal(code, Passphrases.Normalize(typed));
    }

    [Fact]
    public void OldPassphrasesAreKeptAsTypedButWeak()
    {
        const string old = "cello-viola-oboe-harp-123";
        Assert.Equal(old, Passphrases.Normalize("  " + old + " "));
        Assert.False(Passphrases.IsStrong(old));
        Assert.True(Passphrases.IsStrong("a sentence of my own, long enough"));
    }

    [Fact]
    public void PassphraseIsNotStoredInPlainText()
    {
        var s = new Settings { Passphrase = "ABCD-EFGH-JKMN-PQRS-TVWX-YZ01" };
        var json = JsonSerializer.Serialize(s);
        Assert.DoesNotContain("ABCD", json);
        var back = JsonSerializer.Deserialize<Settings>(json)!;
        Assert.Equal(s.Passphrase, back.Passphrase);
    }

    [Fact]
    public void PlainPassphraseFromAnOldFileIsRead()
    {
        var back = JsonSerializer.Deserialize<Settings>("""{ "Passphrase": "cello-viola-oboe-harp-123" }""")!;
        Assert.Equal("cello-viola-oboe-harp-123", back.Passphrase);
        Assert.DoesNotContain("cello", JsonSerializer.Serialize(back));
    }
}
