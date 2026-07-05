using ReScene.RAR;
using Xunit;

namespace ReScene.Tests;

public class RARVolumeNamingTests
{
    [Theory]
    [InlineData(@"C:\foo\bar.rar", @"C:\foo\bar.r00")]
    [InlineData(@"C:\foo\bar.RAR", @"C:\foo\bar.R00")]
    [InlineData("relative.rar", "relative.r00")]
    public void OldNaming_FirstVolume_StepsToR00(string input, string expected) => Assert.Equal(expected, RARVolumeNaming.GetNextVolumePath(input, isOldNaming: true));

    [Theory]
    [InlineData("a.r00", "a.r01")]
    [InlineData("a.r05", "a.r06")]
    [InlineData("a.r99", "a.s00")]
    [InlineData("a.s99", "a.t00")]
    [InlineData("a.R99", "a.S00")]
    public void OldNaming_StepsThroughLetters(string input, string expected) => Assert.Equal(expected, RARVolumeNaming.GetNextVolumePath(input, isOldNaming: true));

    [Theory]
    [InlineData("disc.001", "disc.002")]
    [InlineData("disc.099", "disc.100")]
    [InlineData("disc.0001", "disc.0002")]
    public void OldNaming_NumericExtensionsIncrement(string input, string expected) => Assert.Equal(expected, RARVolumeNaming.GetNextVolumePath(input, isOldNaming: true));

    [Theory]
    [InlineData("a.txt")]
    [InlineData("noext")]
    [InlineData("a.zzz")]
    public void OldNaming_UnsupportedExtension_ReturnsNull(string input) => Assert.Null(RARVolumeNaming.GetNextVolumePath(input, isOldNaming: true));

    [Theory]
    [InlineData("foo.part1.rar", "foo.part2.rar")]
    [InlineData("foo.part01.rar", "foo.part02.rar")]
    [InlineData("foo.part09.rar", "foo.part10.rar")]
    [InlineData("foo.part99.rar", "foo.part100.rar")]
    [InlineData("foo.part001.rar", "foo.part002.rar")]
    [InlineData("foo.part999.rar", "foo.part1000.rar")]
    [InlineData("FOO.PART1.RAR", "FOO.PART2.RAR")]
    public void NewNaming_PartIncrements_PreservesWidth(string input, string expected) => Assert.Equal(expected, RARVolumeNaming.GetNextVolumePath(input, isOldNaming: false));

    [Theory]
    [InlineData(@"C:\folder\foo.part01.rar", @"C:\folder\foo.part02.rar")]
    [InlineData(@"\\server\share\bar.part1.rar", @"\\server\share\bar.part2.rar")]
    public void NewNaming_PreservesPathPrefix(string input, string expected) => Assert.Equal(expected, RARVolumeNaming.GetNextVolumePath(input, isOldNaming: false));

    [Theory]
    [InlineData("foo.rar")]
    [InlineData("foo.r00")]
    [InlineData("foo.001")]
    [InlineData("foo.txt")]
    public void NewNaming_NonPartNames_ReturnNull(string input) => Assert.Null(RARVolumeNaming.GetNextVolumePath(input, isOldNaming: false));

    #region GetBaseName

    [Theory]
    [InlineData("release.part01.rar", "release")]
    [InlineData("release.part1.rar", "release")]
    [InlineData("release.part001.rar", "release")]
    [InlineData("RELEASE.PART01.RAR", "RELEASE")]
    [InlineData("my.show.s01e01.part02.rar", "my.show.s01e01")]
    public void GetBaseName_StripsPartSegment(string fileName, string expected) => Assert.Equal(expected, RARVolumeNaming.GetBaseName(fileName));

    [Theory]
    [InlineData("release.rar", "release")]
    [InlineData("release.r00", "release")]
    [InlineData("release.001", "release")]
    [InlineData("my.show.s01e01.rar", "my.show.s01e01")]
    public void GetBaseName_NonPartNames_DropExtensionOnly(string fileName, string expected) => Assert.Equal(expected, RARVolumeNaming.GetBaseName(fileName));

    #endregion

    #region SecondVolumeCandidates

    [Fact]
    public void SecondVolumeCandidates_IncludesAllNamingForms()
    {
        string[] candidates = RARVolumeNaming.SecondVolumeCandidates(@"C:\out", "release");

        Assert.Contains(Path.Combine(@"C:\out", "release.part02.rar"), candidates);
        Assert.Contains(Path.Combine(@"C:\out", "release.part002.rar"), candidates);
        Assert.Contains(Path.Combine(@"C:\out", "release.part2.rar"), candidates);
        Assert.Contains(Path.Combine(@"C:\out", "release.r00"), candidates);
    }

    [Fact]
    public void SecondVolumeCandidates_CoversThreeDigitPadding_RegressionForPart002()
    {
        // Prior bug: the monitor only probed .part02.rar (2-digit) and missed
        // 3-digit zero-padded second volumes (.part002.rar).
        string[] candidates = RARVolumeNaming.SecondVolumeCandidates(@"C:\out", "release");
        Assert.Contains(Path.Combine(@"C:\out", "release.part002.rar"), candidates);
    }

    #endregion

    #region EnumerateVolumes

    [Fact]
    public void EnumerateVolumes_NewStyle_ReturnsAllPartsOrdered()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rvn_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "rel.part03.rar"), "c");
            File.WriteAllText(Path.Combine(dir, "rel.part01.rar"), "a");
            File.WriteAllText(Path.Combine(dir, "rel.part02.rar"), "b");

            List<string> vols = RARVolumeNaming.EnumerateVolumes(dir, "rel");

            Assert.Equal(
                ["rel.part01.rar", "rel.part02.rar", "rel.part03.rar"],
                vols.Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void EnumerateVolumes_OldStyle_ReturnsMainRARThenRxxOrdered()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rvn_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "rel.rar"), "main");
            File.WriteAllText(Path.Combine(dir, "rel.r01"), "1");
            File.WriteAllText(Path.Combine(dir, "rel.r00"), "0");

            List<string> vols = RARVolumeNaming.EnumerateVolumes(dir, "rel");

            // .rar first (main), then .r00, .r01 (the .r?? glob excludes .rar)
            Assert.Equal(
                ["rel.rar", "rel.r00", "rel.r01"],
                vols.Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void EnumerateVolumes_OldStyle_IncludesVolumesPastR99()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rvn_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Old-style sets with >101 volumes roll .r99 -> .s00 -> .s01 -> … These must all be
            // enumerated (previously the .r?? glob capped the set at 101, so the correct match was
            // rejected as a volume-count mismatch and rename/delete left orphans).
            File.WriteAllText(Path.Combine(dir, "rel.rar"), "main");
            File.WriteAllText(Path.Combine(dir, "rel.r99"), "r99");
            File.WriteAllText(Path.Combine(dir, "rel.s00"), "s00");
            File.WriteAllText(Path.Combine(dir, "rel.s01"), "s01");
            File.WriteAllText(Path.Combine(dir, "rel.z99"), "z99");
            File.WriteAllText(Path.Combine(dir, "rel.nfo"), "not a volume"); // must be excluded

            List<string> vols = RARVolumeNaming.EnumerateVolumes(dir, "rel");

            Assert.Equal(
                ["rel.rar", "rel.r99", "rel.s00", "rel.s01", "rel.z99"],
                vols.Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void EnumerateVolumes_NoVolumes_ReturnsEmpty()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rvn_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Empty(RARVolumeNaming.EnumerateVolumes(dir, "missing"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    #endregion
}
