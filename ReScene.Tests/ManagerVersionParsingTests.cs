using ReScene.Core;

namespace ReScene.Tests;

public sealed class ManagerVersionParsingTests
{
    [Theory]
    [InlineData("winrar-560", 560)]
    [InlineData("winrar-624", 624)]
    [InlineData("winrar-700", 700)]
    [InlineData("winrar-56", 560)]   // < 100 is normalised x10
    // Linux tarball folder names (rarlinux-…): concatenated and older dotted forms, with/without arch.
    [InlineData("rarlinux-x64-611", 611)]
    [InlineData("rarlinux-x64-723", 723)]
    [InlineData("rarlinux-x64-5.5.0", 550)]   // dotted → concatenated
    [InlineData("rarlinux-3.9.3", 393)]
    [InlineData("rarlinux-3.0", 300)]         // two-part dotted, < 100 run normalised x10
    [InlineData("rarlinux-x32-610", 610)]
    // macOS tarball folder names (rarosx-…): dotted.
    [InlineData("rarosx-3.1.0", 310)]
    [InlineData("rarosx-6.0.2", 602)]
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
    // *nix names: arch is consumed (not the version), betas keep their tag, dotted releases have no tag.
    [InlineData("rarlinux-x32-620b2", 620, "b2")]
    [InlineData("rarlinux-x64-701b1", 701, "b1")]
    [InlineData("rarlinux-x64-5.5.0", 550, "")]
    [InlineData("rarosx-3.1.0", 310, "")]
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
