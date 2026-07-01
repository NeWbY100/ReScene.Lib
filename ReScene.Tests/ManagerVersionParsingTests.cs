using ReScene.Core;

namespace ReScene.Tests;

public sealed class ManagerVersionParsingTests
{
    [Theory]
    [InlineData("winrar-560", 560)]
    [InlineData("winrar-624", 624)]
    [InlineData("winrar-700", 700)]
    [InlineData("winrar-56", 560)]   // < 100 is normalised x10
    public void TryParseRARVersion_ValidNames_ReturnsNormalisedVersion(string name, int expected)
    {
        bool ok = Manager.TryParseRARVersion(name, out int version);

        Assert.True(ok);
        Assert.Equal(expected, version);
    }

    [Theory]
    [InlineData("winrar-beta")]
    [InlineData("no-digits-here")]
    [InlineData("")]
    public void TryParseRARVersion_Unparseable_ReturnsFalse(string name)
    {
        bool ok = Manager.TryParseRARVersion(name, out int version);

        Assert.False(ok);
        Assert.Equal(0, version);
    }

    [Fact]
    public void ParseRARVersion_Unparseable_Throws()
    {
        Assert.Throws<FormatException>(() => Manager.ParseRARVersion("winrar-beta"));
    }

    [Fact]
    public void ParseRARVersion_Valid_ReturnsSameAsTryParse()
    {
        Assert.Equal(560, Manager.ParseRARVersion("winrar-560"));
    }
}
