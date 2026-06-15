using System.Text;
using ReScene.Hex;

namespace ReScene.Tests;

public class HexSearchPatternTests
{
    #region TryParse — hex mode

    [Fact]
    public void TryParse_Hex_NullInput_ReturnsNull()
    {
        var result = HexSearchPattern.TryParse(null!, asHex: true);
        Assert.Null(result);
    }

    [Fact]
    public void TryParse_Hex_EmptyInput_ReturnsNull()
    {
        var result = HexSearchPattern.TryParse(string.Empty, asHex: true);
        Assert.Null(result);
    }

    [Fact]
    public void TryParse_Hex_WhitespaceInput_ReturnsNull()
    {
        var result = HexSearchPattern.TryParse("   ", asHex: true);
        Assert.Null(result);
    }

    [Fact]
    public void TryParse_Hex_ValidTwoBytes_ParsesCorrectly()
    {
        var result = HexSearchPattern.TryParse("5261", asHex: true);
        Assert.NotNull(result);
        Assert.Equal(2, result!.Bytes.Length);
        Assert.Equal(0x52, result.Bytes.Span[0]);
        Assert.Equal(0x61, result.Bytes.Span[1]);
        Assert.True(result.IsHex);
    }

    [Fact]
    public void TryParse_Hex_WithSpaces_ParsesCorrectly()
    {
        var result = HexSearchPattern.TryParse("52 61", asHex: true);
        Assert.NotNull(result);
        Assert.Equal(2, result!.Bytes.Length);
        Assert.Equal(0x52, result.Bytes.Span[0]);
        Assert.Equal(0x61, result.Bytes.Span[1]);
    }

    [Fact]
    public void TryParse_Hex_WithDashes_ParsesCorrectly()
    {
        var result = HexSearchPattern.TryParse("52-61", asHex: true);
        Assert.NotNull(result);
        Assert.Equal(2, result!.Bytes.Length);
        Assert.Equal(0x52, result.Bytes.Span[0]);
        Assert.Equal(0x61, result.Bytes.Span[1]);
    }

    [Fact]
    public void TryParse_Hex_OddLength_ReturnsNull()
    {
        var result = HexSearchPattern.TryParse("5", asHex: true);
        Assert.Null(result);
    }

    [Fact]
    public void TryParse_Hex_NonHexChars_ReturnsNull()
    {
        var result = HexSearchPattern.TryParse("GZ", asHex: true);
        Assert.Null(result);
    }

    [Fact]
    public void TryParse_Hex_DisplayTextIsTrimmedInput()
    {
        var result = HexSearchPattern.TryParse("  52 61  ", asHex: true);
        Assert.NotNull(result);
        Assert.Equal("52 61", result!.DisplayText);
    }

    #endregion

    #region TryParse — ASCII mode

    [Fact]
    public void TryParse_Ascii_NullInput_ReturnsNull()
    {
        var result = HexSearchPattern.TryParse(null!, asHex: false);
        Assert.Null(result);
    }

    [Fact]
    public void TryParse_Ascii_EmptyInput_ReturnsNull()
    {
        var result = HexSearchPattern.TryParse(string.Empty, asHex: false);
        Assert.Null(result);
    }

    [Fact]
    public void TryParse_Ascii_WhitespaceInput_ReturnsNull()
    {
        var result = HexSearchPattern.TryParse("   ", asHex: false);
        Assert.Null(result);
    }

    [Fact]
    public void TryParse_Ascii_RarBang_ParsesCorrectUtf8Bytes()
    {
        var result = HexSearchPattern.TryParse("Rar!", asHex: false);
        Assert.NotNull(result);

        byte[] expected = Encoding.UTF8.GetBytes("Rar!");
        Assert.Equal(expected, result!.Bytes.ToArray());
        Assert.False(result.IsHex);
    }

    [Fact]
    public void TryParse_Ascii_DisplayTextIsPreserved()
    {
        var result = HexSearchPattern.TryParse("Rar!", asHex: false);
        Assert.NotNull(result);
        Assert.Equal("Rar!", result!.DisplayText);
    }

    [Fact]
    public void TryParse_Ascii_IsHexIsFalse()
    {
        var result = HexSearchPattern.TryParse("hello", asHex: false);
        Assert.NotNull(result);
        Assert.False(result!.IsHex);
    }

    #endregion
}
