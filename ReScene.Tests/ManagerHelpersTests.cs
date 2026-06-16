using ReScene.Core;
using ReScene.Core.Diagnostics;

namespace ReScene.Tests;

/// <summary>
/// Unit tests for the pure and filesystem helpers of <see cref="Manager"/> — the brute-force
/// orchestrator that previously had no coverage. These exercise version parsing, archive-format
/// detection, the argument-filter and RAR 6.x timestamp-skip predicates, and the file-move/locate
/// helpers, all independently of running rar.exe.
/// </summary>
public class ManagerHelpersTests
{
    #region ParseRARVersion

    [Theory]
    [InlineData("winrar-560", 560)]
    [InlineData("WinRAR-393", 393)]
    [InlineData("winrar-700", 700)]
    [InlineData("winrar-x64-620", 620)]
    [InlineData("winrar-560b1", 560)]      // build suffix ignored
    [InlineData("rar5", 50)]               // bare two-or-fewer-digit version is scaled ×10
    [InlineData("winrar-29", 290)]         // < 100 → ×10
    public void ParseRARVersion_ValidNames_ReturnsNormalizedVersion(string directoryName, int expected)
        => Assert.Equal(expected, Manager.ParseRARVersion(directoryName));

    [Theory]
    [InlineData("notaversion")]
    [InlineData("")]
    public void ParseRARVersion_NoVersion_Throws(string directoryName)
        => Assert.Throws<FormatException>(() => Manager.ParseRARVersion(directoryName));

    #endregion

    #region ParseRARArchiveVersion

    [Fact]
    public void ParseRARArchiveVersion_Ma4Flag_OverridesVersion()
    {
        RARCommandLineArgument[] args = [new("-ma4", 0)];
        Assert.Equal(RARArchiveVersion.RAR4, Manager.ParseRARArchiveVersion(args, 700));
    }

    [Fact]
    public void ParseRARArchiveVersion_Ma5Flag_OverridesVersion()
    {
        RARCommandLineArgument[] args = [new("-ma5", 0)];
        Assert.Equal(RARArchiveVersion.RAR5, Manager.ParseRARArchiveVersion(args, 400));
    }

    [Theory]
    [InlineData(400, RARArchiveVersion.RAR4)]
    [InlineData(499, RARArchiveVersion.RAR4)]
    [InlineData(500, RARArchiveVersion.RAR5)]
    [InlineData(560, RARArchiveVersion.RAR5)]
    [InlineData(699, RARArchiveVersion.RAR5)]
    [InlineData(700, RARArchiveVersion.RAR7)]
    [InlineData(710, RARArchiveVersion.RAR7)]
    public void ParseRARArchiveVersion_NoFlag_FollowsVersion(int version, RARArchiveVersion expected)
        => Assert.Equal(expected, Manager.ParseRARArchiveVersion([], version));

    #endregion

    #region FilterArgumentsForVersion

    [Fact]
    public void FilterArgumentsForVersion_ExcludesBelowMinimumVersion()
    {
        RARCommandLineArgument[] args = [new("-m5", minimumVersion: 500)];
        Assert.Empty(RarVersionSelector.FilterArgumentsForVersion(args, 400, RARArchiveVersion.RAR4));
    }

    [Fact]
    public void FilterArgumentsForVersion_ExcludesAboveMaximumVersion()
    {
        RARCommandLineArgument[] args = [new("-m5", minimumVersion: 300, maximumVersion: 500)];
        Assert.Empty(RarVersionSelector.FilterArgumentsForVersion(args, 600, RARArchiveVersion.RAR4));
    }

    [Fact]
    public void FilterArgumentsForVersion_IncludesWithinVersionRange()
    {
        RARCommandLineArgument[] args = [new("-m5", minimumVersion: 300, maximumVersion: 700)];
        Assert.Equal(new[] { "-m5" }, RarVersionSelector.FilterArgumentsForVersion(args, 600, RARArchiveVersion.RAR4));
    }

    [Fact]
    public void FilterArgumentsForVersion_ExcludesMismatchedArchiveVersion()
    {
        RARCommandLineArgument[] args = [new("-m5", minimumVersion: 0, archiveVersion: RARArchiveVersion.RAR5)];
        Assert.Empty(RarVersionSelector.FilterArgumentsForVersion(args, 600, RARArchiveVersion.RAR4));
    }

    [Fact]
    public void FilterArgumentsForVersion_IncludesMatchingArchiveVersionFlag()
    {
        RARCommandLineArgument[] args =
            [new("-m5", minimumVersion: 0, archiveVersion: RARArchiveVersion.RAR4 | RARArchiveVersion.RAR5)];
        Assert.Equal(new[] { "-m5" }, RarVersionSelector.FilterArgumentsForVersion(args, 600, RARArchiveVersion.RAR5));
    }

    [Fact]
    public void FilterArgumentsForVersion_PreservesOrderAndMapsToArgumentStrings()
    {
        RARCommandLineArgument[] args =
        [
            new("-m5", 0),
            new("-md64m", 600),    // excluded at version 500
            new("-s", 0),
        ];
        Assert.Equal(new[] { "-m5", "-s" }, RarVersionSelector.FilterArgumentsForVersion(args, 500, RARArchiveVersion.RAR4));
    }

    #endregion

    #region ShouldSkipRar6TimestampCombination

    [Theory]
    // RAR 6.x + RAR4 format + timestamp option → skip
    [InlineData(620, RARArchiveVersion.RAR4, new[] { "-tsc-" }, true)]
    // RAR 6.x, declared RAR5 but no -ma5 present → still treated as RAR4 format → skip
    [InlineData(620, RARArchiveVersion.RAR5, new[] { "-tsc-" }, true)]
    // RAR 6.x with explicit -ma5 → genuine RAR5 format → not skipped
    [InlineData(620, RARArchiveVersion.RAR5, new[] { "-tsc-", "-ma5" }, false)]
    // Below 6.x → not skipped
    [InlineData(560, RARArchiveVersion.RAR4, new[] { "-tsc-" }, false)]
    // 7.x → excluded (handles timestamps natively)
    [InlineData(700, RARArchiveVersion.RAR4, new[] { "-tsc-" }, false)]
    // No timestamp option → nothing to skip
    [InlineData(620, RARArchiveVersion.RAR4, new[] { "-m5" }, false)]
    [InlineData(620, RARArchiveVersion.RAR4, new string[0], false)]
    public void ShouldSkipRar6TimestampCombination_MatchesKnownIssueMatrix(
        int version, RARArchiveVersion archiveVersion, string[] filteredArguments, bool expected)
        => Assert.Equal(expected, RarVersionSelector.ShouldSkipRar6TimestampCombination(version, archiveVersion, filteredArguments));

    #endregion

    #region FindCreatedRARFile / MoveMatchedFile (filesystem)

    [Fact]
    public void FindCreatedRARFile_ExpectedFileExists_ReturnsIt()
    {
        using var tmp = new TempDir();
        string expected = tmp.File("movie.rar");
        Assert.Equal(expected, MatchedRarWriter.FindCreatedRARFile(expected));
    }

    [Fact]
    public void FindCreatedRARFile_Part01VolumeExists_ReturnsPart01()
    {
        using var tmp = new TempDir();
        string expected = Path.Combine(tmp.Path, "movie.rar"); // not created
        string part01 = tmp.File("movie.part01.rar");
        Assert.Equal(part01, MatchedRarWriter.FindCreatedRARFile(expected));
    }

    [Fact]
    public void FindCreatedRARFile_Part1VolumeExists_ReturnsPart1()
    {
        using var tmp = new TempDir();
        string expected = Path.Combine(tmp.Path, "movie.rar"); // not created
        string part1 = tmp.File("movie.part1.rar");
        Assert.Equal(part1, MatchedRarWriter.FindCreatedRARFile(expected));
    }

    [Fact]
    public void FindCreatedRARFile_OldStyleFirstVolumeExists_ReturnsRar()
    {
        using var tmp = new TempDir();
        // Expected path absent, but the {base}.rar first volume is present (old-style set).
        string expected = Path.Combine(tmp.Path, "movie.xyz");
        string firstVolume = tmp.File("movie.rar");
        Assert.Equal(firstVolume, MatchedRarWriter.FindCreatedRARFile(expected));
    }

    [Fact]
    public void FindCreatedRARFile_OldStyleRarPlusR00_ReturnsRar()
    {
        using var tmp = new TempDir();
        // Expected path absent; both the .rar first volume and a .r00 continuation exist —
        // exercises the multi-volume old-style branch (not just the single-volume fallback).
        string expected = Path.Combine(tmp.Path, "movie.xyz");
        string firstVolume = tmp.File("movie.rar");
        tmp.File("movie.r00");
        Assert.Equal(firstVolume, MatchedRarWriter.FindCreatedRARFile(expected));
    }

    [Fact]
    public void FindCreatedRARFile_NothingPresent_ReturnsNull()
    {
        using var tmp = new TempDir();
        Assert.Null(MatchedRarWriter.FindCreatedRARFile(Path.Combine(tmp.Path, "movie.rar")));
    }

    [Fact]
    public void MoveMatchedFile_SameSourceAndDestination_ReturnsTrueAndLeavesFile()
    {
        using var tmp = new TempDir();
        string path = tmp.File("movie.rar");
        Assert.True(MatchedRarWriter.MoveMatchedFile(path, path));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void MoveMatchedFile_DestinationFree_MovesAndReturnsTrue()
    {
        using var tmp = new TempDir();
        string source = tmp.File("420-m3.rar");
        string dest = Path.Combine(tmp.Path, "Movie.2020.rar");

        Assert.True(MatchedRarWriter.MoveMatchedFile(source, dest));
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(dest));
    }

    [Fact]
    public void MoveMatchedFile_DestinationOccupiedByDifferentFile_ReturnsFalseAndLeavesSource()
    {
        using var tmp = new TempDir();
        string source = tmp.File("420-m3.rar", "source");
        string dest = tmp.File("Movie.2020.rar", "existing");

        Assert.False(MatchedRarWriter.MoveMatchedFile(source, dest));
        Assert.True(File.Exists(source));
        Assert.Equal("existing", File.ReadAllText(dest));
    }

    #endregion

    /// <summary>A self-cleaning unique temporary directory for filesystem tests.</summary>
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"manager_helpers_{Guid.NewGuid():N}");

        public TempDir() => Directory.CreateDirectory(Path);

        /// <summary>Creates a file in the temp directory and returns its full path.</summary>
        public string File(string name, string contents = "x")
        {
            string full = System.IO.Path.Combine(Path, name);
            System.IO.File.WriteAllText(full, contents);
            return full;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch
            {
            }
        }
    }
}
