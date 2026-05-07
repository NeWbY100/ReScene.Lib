using ReScene.RAR;
using Xunit;

namespace ReScene.Tests;

public class RARVolumeNamingTests
{
    [Theory]
    [InlineData(@"C:\foo\bar.rar", @"C:\foo\bar.r00")]
    [InlineData(@"C:\foo\bar.RAR", @"C:\foo\bar.R00")]
    [InlineData("relative.rar", "relative.r00")]
    public void OldNaming_FirstVolume_StepsToR00(string input, string expected)
    {
        Assert.Equal(expected, RARVolumeNaming.GetNextVolumePath(input, isOldNaming: true));
    }

    [Theory]
    [InlineData("a.r00", "a.r01")]
    [InlineData("a.r05", "a.r06")]
    [InlineData("a.r99", "a.s00")]
    [InlineData("a.s99", "a.t00")]
    [InlineData("a.R99", "a.S00")]
    public void OldNaming_StepsThroughLetters(string input, string expected)
    {
        Assert.Equal(expected, RARVolumeNaming.GetNextVolumePath(input, isOldNaming: true));
    }

    [Theory]
    [InlineData("disc.001", "disc.002")]
    [InlineData("disc.099", "disc.100")]
    [InlineData("disc.0001", "disc.0002")]
    public void OldNaming_NumericExtensionsIncrement(string input, string expected)
    {
        Assert.Equal(expected, RARVolumeNaming.GetNextVolumePath(input, isOldNaming: true));
    }

    [Theory]
    [InlineData("a.txt")]
    [InlineData("noext")]
    [InlineData("a.zzz")]
    public void OldNaming_UnsupportedExtension_ReturnsNull(string input)
    {
        Assert.Null(RARVolumeNaming.GetNextVolumePath(input, isOldNaming: true));
    }

    [Theory]
    [InlineData("foo.part1.rar", "foo.part2.rar")]
    [InlineData("foo.part01.rar", "foo.part02.rar")]
    [InlineData("foo.part09.rar", "foo.part10.rar")]
    [InlineData("foo.part99.rar", "foo.part100.rar")]
    [InlineData("foo.part001.rar", "foo.part002.rar")]
    [InlineData("foo.part999.rar", "foo.part1000.rar")]
    [InlineData("FOO.PART1.RAR", "FOO.PART2.RAR")]
    public void NewNaming_PartIncrements_PreservesWidth(string input, string expected)
    {
        Assert.Equal(expected, RARVolumeNaming.GetNextVolumePath(input, isOldNaming: false));
    }

    [Theory]
    [InlineData(@"C:\folder\foo.part01.rar", @"C:\folder\foo.part02.rar")]
    [InlineData(@"\\server\share\bar.part1.rar", @"\\server\share\bar.part2.rar")]
    public void NewNaming_PreservesPathPrefix(string input, string expected)
    {
        Assert.Equal(expected, RARVolumeNaming.GetNextVolumePath(input, isOldNaming: false));
    }

    [Theory]
    [InlineData("foo.rar")]
    [InlineData("foo.r00")]
    [InlineData("foo.001")]
    [InlineData("foo.txt")]
    public void NewNaming_NonPartNames_ReturnNull(string input)
    {
        Assert.Null(RARVolumeNaming.GetNextVolumePath(input, isOldNaming: false));
    }
}
