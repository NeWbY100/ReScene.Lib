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

    [Theory]
    [InlineData("winrar-250", 250, "")]
    [InlineData("winrar-250-beta1", 250, "beta1")]
    [InlineData("winrar-250b2", 250, "b2")]
    [InlineData("winrar-x64-250-beta1", 250, "beta1")]  // digits in "-x64-" must not be the version
    [InlineData("wrar-380-de", 380, "de")]
    [InlineData("winrar-56-beta", 560, "beta")]         // < 100 normalisation keeps the tag
    public void TryParseRARVersion_WithTag_ReturnsVersionAndVariantTag(string name, int expectedVersion, string expectedTag)
    {
        bool ok = Manager.TryParseRARVersion(name, out int version, out string variantTag);

        Assert.True(ok);
        Assert.Equal(expectedVersion, version);
        Assert.Equal(expectedTag, variantTag);
    }

    [Fact]
    public void TryParseRARVersion_WithTag_Unparseable_ReturnsFalseAndEmptyTag()
    {
        bool ok = Manager.TryParseRARVersion("winrar-beta", out int version, out string variantTag);

        Assert.False(ok);
        Assert.Equal(0, version);
        Assert.Equal(string.Empty, variantTag);
    }

    [Fact]
    public void ParseRARVersion_Unparseable_Throws() => Assert.Throws<FormatException>(() => Manager.ParseRARVersion("winrar-beta"));

    [Fact]
    public void ParseRARVersion_Valid_ReturnsSameAsTryParse() => Assert.Equal(560, Manager.ParseRARVersion("winrar-560"));
}
