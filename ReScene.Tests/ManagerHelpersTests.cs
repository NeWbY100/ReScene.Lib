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
        Assert.Empty(RARVersionSelector.FilterArgumentsForVersion(args, 400, RARArchiveVersion.RAR4));
    }

    [Fact]
    public void FilterArgumentsForVersion_ExcludesAboveMaximumVersion()
    {
        RARCommandLineArgument[] args = [new("-m5", minimumVersion: 300, maximumVersion: 500)];
        Assert.Empty(RARVersionSelector.FilterArgumentsForVersion(args, 600, RARArchiveVersion.RAR4));
    }

    [Fact]
    public void FilterArgumentsForVersion_IncludesWithinVersionRange()
    {
        RARCommandLineArgument[] args = [new("-m5", minimumVersion: 300, maximumVersion: 700)];
        Assert.Equal(new[] { "-m5" }, RARVersionSelector.FilterArgumentsForVersion(args, 600, RARArchiveVersion.RAR4));
    }

    [Fact]
    public void FilterArgumentsForVersion_ExcludesMismatchedArchiveVersion()
    {
        RARCommandLineArgument[] args = [new("-m5", minimumVersion: 0, archiveVersion: RARArchiveVersion.RAR5)];
        Assert.Empty(RARVersionSelector.FilterArgumentsForVersion(args, 600, RARArchiveVersion.RAR4));
    }

    [Fact]
    public void FilterArgumentsForVersion_IncludesMatchingArchiveVersionFlag()
    {
        RARCommandLineArgument[] args =
            [new("-m5", minimumVersion: 0, archiveVersion: RARArchiveVersion.RAR4 | RARArchiveVersion.RAR5)];
        Assert.Equal(new[] { "-m5" }, RARVersionSelector.FilterArgumentsForVersion(args, 600, RARArchiveVersion.RAR5));
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
        Assert.Equal(new[] { "-m5", "-s" }, RARVersionSelector.FilterArgumentsForVersion(args, 500, RARArchiveVersion.RAR4));
    }

    #endregion

    #region ShouldSkipRAR6TimestampCombination

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
    public void ShouldSkipRAR6TimestampCombination_MatchesKnownIssueMatrix(
        int version, RARArchiveVersion archiveVersion, string[] filteredArguments, bool expected)
        => Assert.Equal(expected, RARVersionSelector.ShouldSkipRAR6TimestampCombination(version, archiveVersion, filteredArguments));

    #endregion

    #region FindCreatedRARFile / MoveMatchedFile (filesystem)

    [Fact]
    public void FindCreatedRARFile_ExpectedFileExists_ReturnsIt()
    {
        using var tmp = new TempDir();
        string expected = tmp.File("movie.rar");
        Assert.Equal(expected, MatchedRARWriter.FindCreatedRARFile(expected));
    }

    [Fact]
    public void FindCreatedRARFile_Part01VolumeExists_ReturnsPart01()
    {
        using var tmp = new TempDir();
        string expected = Path.Combine(tmp.Path, "movie.rar"); // not created
        string part01 = tmp.File("movie.part01.rar");
        Assert.Equal(part01, MatchedRARWriter.FindCreatedRARFile(expected));
    }

    [Fact]
    public void FindCreatedRARFile_Part1VolumeExists_ReturnsPart1()
    {
        using var tmp = new TempDir();
        string expected = Path.Combine(tmp.Path, "movie.rar"); // not created
        string part1 = tmp.File("movie.part1.rar");
        Assert.Equal(part1, MatchedRARWriter.FindCreatedRARFile(expected));
    }

    [Fact]
    public void FindCreatedRARFile_OldStyleFirstVolumeExists_ReturnsRAR()
    {
        using var tmp = new TempDir();
        // Expected path absent, but the {base}.rar first volume is present (old-style set).
        string expected = Path.Combine(tmp.Path, "movie.xyz");
        string firstVolume = tmp.File("movie.rar");
        Assert.Equal(firstVolume, MatchedRARWriter.FindCreatedRARFile(expected));
    }

    [Fact]
    public void FindCreatedRARFile_OldStyleRARPlusR00_ReturnsRAR()
    {
        using var tmp = new TempDir();
        // Expected path absent; both the .rar first volume and a .r00 continuation exist —
        // exercises the multi-volume old-style branch (not just the single-volume fallback).
        string expected = Path.Combine(tmp.Path, "movie.xyz");
        string firstVolume = tmp.File("movie.rar");
        tmp.File("movie.r00");
        Assert.Equal(firstVolume, MatchedRARWriter.FindCreatedRARFile(expected));
    }

    [Fact]
    public void FindCreatedRARFile_NothingPresent_ReturnsNull()
    {
        using var tmp = new TempDir();
        Assert.Null(MatchedRARWriter.FindCreatedRARFile(Path.Combine(tmp.Path, "movie.rar")));
    }

    [Fact]
    public void MoveMatchedFile_SameSourceAndDestination_ReturnsTrueAndLeavesFile()
    {
        using var tmp = new TempDir();
        string path = tmp.File("movie.rar");
        Assert.True(MatchedRARWriter.MoveMatchedFile(path, path));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void MoveMatchedFile_DestinationFree_MovesAndReturnsTrue()
    {
        using var tmp = new TempDir();
        string source = tmp.File("420-m3.rar");
        string dest = Path.Combine(tmp.Path, "Movie.2020.rar");

        Assert.True(MatchedRARWriter.MoveMatchedFile(source, dest));
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(dest));
    }

    [Fact]
    public void MoveMatchedFile_DestinationOccupiedByDifferentFile_ReturnsFalseAndLeavesSource()
    {
        using var tmp = new TempDir();
        string source = tmp.File("420-m3.rar", "source");
        string dest = tmp.File("Movie.2020.rar", "existing");

        Assert.False(MatchedRARWriter.MoveMatchedFile(source, dest));
        Assert.True(File.Exists(source));
        Assert.Equal("existing", File.ReadAllText(dest));
    }

    [Fact]
    public void MoveMatchedFile_SamePathDifferentSeparatorStyle_TreatsAsNoOp()
    {
        // A raw string compare of the unresolved paths (the old implementation) would see these as
        // different and attempt a real move — which would fail as "destination occupied" since the
        // file already sits at that exact location. The filesystem-correct (full-path-normalized)
        // equality must recognize they refer to the SAME file and short-circuit.
        if (!OperatingSystem.IsWindows())
        {
            // Mixed separator styles for one file only exist on Windows — on POSIX '\' is a
            // literal name character, so the two spellings genuinely ARE different paths and
            // the no-op expectation would be wrong.
            return;
        }

        using var tmp = new TempDir();
        string subDir = Path.Combine(tmp.Path, "sub");
        Directory.CreateDirectory(subDir);
        string path = Path.Combine(subDir, "movie.rar");
        File.WriteAllText(path, "x");

        string backslashStyle = tmp.Path + "\\sub\\movie.rar";
        string forwardSlashStyle = tmp.Path.Replace('\\', '/') + "/sub/movie.rar";

        Assert.True(MatchedRARWriter.MoveMatchedFile(backslashStyle, forwardSlashStyle));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void MoveMatchedFile_SamePathWithRedundantSegment_TreatsAsNoOp()
    {
        using var tmp = new TempDir();
        string subDir = Path.Combine(tmp.Path, "sub");
        Directory.CreateDirectory(subDir);
        string path = Path.Combine(subDir, "movie.rar");
        File.WriteAllText(path, "x");

        string plain = Path.Combine(tmp.Path, "sub", "movie.rar");
        string withRedundantSegment = Path.Combine(tmp.Path, "sub", ".", "movie.rar");

        Assert.True(MatchedRARWriter.MoveMatchedFile(plain, withRedundantSegment));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void MoveMatchedFile_MovesSuccessfully_DestinationVerifiedToExistAfterward()
    {
        // Regression guard for the post-move existence check: a normal, successful move must still
        // report success (the check is defensive, not a behavior change for the happy path).
        using var tmp = new TempDir();
        string source = tmp.File("420-m3.rar");
        string dest = Path.Combine(tmp.Path, "Movie.2020.rar");

        Assert.True(MatchedRARWriter.MoveMatchedFile(source, dest));
        Assert.True(File.Exists(dest));
    }

    #endregion

    #region GetValidRARDirectories folder allow-list

    private static (string Path, int Version)[] RunGetValidRARDirectories(string root, IReadOnlyList<string> allowedFolders)
    {
        var options = new BruteForceOptions(root, root, root)
        {
            RAROptions = new RAROptions
            {
                RARVersions = [new VersionRange(390, 391)],   // covers the normalised version 390
                AllowedVersionFolders = [.. allowedFolders],
            },
        };

        return [.. RARVersionSelector.GetValidRARDirectories(Directory.GetDirectories(root), options, NullReSceneLogger.Instance, new object())];
    }

    [Fact]
    public void GetValidRARDirectories_AllowList_KeepsOnlyNamedSameVersionFolder()
    {
        // Both folders parse to version 390; the allow-list must distinguish them by folder name.
        using var tmp = new TempDir();
        string keep = tmp.VersionDir("winrar-390");
        tmp.VersionDir("winrar-390-beta1");

        (string Path, int Version)[] result = RunGetValidRARDirectories(tmp.Path, ["winrar-390"]);

        (string Path, int Version) only = Assert.Single(result);
        Assert.Equal(keep, only.Path);
        Assert.Equal(390, only.Version);
    }

    [Fact]
    public void GetValidRARDirectories_EmptyAllowList_KeepsEveryInRangeFolder()
    {
        using var tmp = new TempDir();
        tmp.VersionDir("winrar-390");
        tmp.VersionDir("winrar-390-beta1");

        (string Path, int Version)[] result = RunGetValidRARDirectories(tmp.Path, []);

        Assert.Equal(2, result.Length);
        Assert.All(result, r => Assert.Equal(390, r.Version));
    }

    [Fact]
    public void GetValidRARDirectories_AllowList_IsCaseInsensitive()
    {
        using var tmp = new TempDir();
        string keep = tmp.VersionDir("winrar-390");
        tmp.VersionDir("winrar-390-beta1");

        (string Path, int Version)[] result = RunGetValidRARDirectories(tmp.Path, ["WINRAR-390"]);

        Assert.Equal(keep, Assert.Single(result).Path);
    }

    #endregion

    #region IsCompletedRunFailure

    [Theory]
    [InlineData(127, true)]  // Linux loader failure (missing shared libraries) — ran, did no work
    [InlineData(126, true)]  // not executable via loader
    [InlineData(10, true)]   // rar "no files matching mask"
    [InlineData(2, true)]    // rar fatal error
    [InlineData(1, true)]    // rar warning yet nothing created — still no work done
    [InlineData(0, false)]   // clean exit without a file keeps the historical no-match treatment
    [InlineData(null, false)] // unknown (killed by cleanup before completing) stays conservative
    public void IsCompletedRunFailure_NonZeroKnownExitWithoutArchive_IsFailure(int? exitCode, bool expected)
        => Assert.Equal(expected, Manager.IsCompletedRunFailure(exitCode, cancellationRequested: false));

    [Theory]
    [InlineData(1)]    // the swallowed-cancel exit itself — the case that would masquerade as a failure
    [InlineData(127)]
    [InlineData(0)]
    [InlineData(null)]
    public void IsCompletedRunFailure_NeverAFailure_WhileCancellationRequested(int? exitCode)
    {
        // RARProcess.RunAsync swallows the cancellation exception and returns exit 1, so a user cancel
        // landing before rar creates its first file is indistinguishable from a failed run by exit code
        // alone — the classification must suppress on a requested cancellation.
        Assert.False(Manager.IsCompletedRunFailure(exitCode, cancellationRequested: true));
    }

    #endregion

    #region JoinExecutedArguments

    [Fact]
    public void JoinExecutedArguments_PlainTokens_JoinUnquoted()
        => Assert.Equal("-ma4 a -r -s- -m0", Manager.JoinExecutedArguments(["-ma4", "a", "-r", "-s-", "-m0"]));

    [Fact]
    public void JoinExecutedArguments_TokenWithSpace_IsWholeTokenQuoted()
    {
        // -z<commentfile> is the one token that carries a path; an output folder with a space would
        // otherwise split in the pasted shell line and rar would get a truncated -z plus a stray operand.
        Assert.Equal(
            "a -m0 \"-zD:\\My Releases\\out\\comment.txt\"",
            Manager.JoinExecutedArguments(["a", "-m0", @"-zD:\My Releases\out\comment.txt"]));
    }

    #endregion

    #region BuildFinalArguments — -cfg-

    // BuildFinalArguments is private; these drive it through the same live-Manager/FakeRunner flow
    // ManagerProducerLifecycleTests uses, and read the composed argument list off the pre-execution
    // BruteForceProgress event's ExecutedArguments — the exact (space-joined) string BuildFinalArguments
    // produces, fired before the candidate is launched so it doesn't depend on the (never-matching)
    // launch's own resolution.

    [Theory]
    [InlineData(203)]  // 2.x era: no other auto-added switch applies
    [InlineData(300)]  // 3.x era: -vn is opt-in (UseOldVolumeNaming defaults false), so still bare
    [InlineData(700)]  // 7.x era: at the -ma4 upper boundary, -ma4 must NOT be added
    public async Task BuildFinalArguments_NoOtherAutoAddedSwitch_IsCfgOnly(int version)
        => Assert.Equal("-cfg-", await RunSingleCandidateAsync(version));

    [Fact]
    public async Task BuildFinalArguments_Rar550Era_CfgPrecedesAutoAddedMa4()
        // -ma4 is inserted at index 0 by its own block; -cfg- must still land ahead of it.
        => Assert.Equal("-cfg- -ma4", await RunSingleCandidateAsync(550));

    /// <summary>
    /// Runs one Manager brute-force candidate for <paramref name="version"/> against a <see
    /// cref="FakeRunner"/> (no real rar.exe, no matching hash configured) and returns the
    /// ExecutedArguments captured off the first Phase 2 progress event.
    /// </summary>
    private static async Task<string> RunSingleCandidateAsync(int version)
    {
        using var tmp = new TempDir();
        string versionsDir = Path.Combine(tmp.Path, "versions");
        string versionDir = Path.Combine(versionsDir, $"rar{version}");
        string releaseDir = Path.Combine(tmp.Path, "release");
        string workDir = Path.Combine(tmp.Path, "work");
        Directory.CreateDirectory(versionDir);
        Directory.CreateDirectory(releaseDir);
        Directory.CreateDirectory(workDir);
        File.WriteAllBytes(Path.Combine(versionDir, RarExecutable.FileName), []);
        File.WriteAllBytes(Path.Combine(releaseDir, "a.bin"), new byte[16]);

        var options = new BruteForceOptions(versionsDir, releaseDir, workDir)
        {
            RAROptions = new RAROptions
            {
                RARVersions = [new VersionRange(version, version + 1)],
                CommandLineArguments = [Array.Empty<RARCommandLineArgument>()],
            },
        };

        var runner = new FakeRunner { OnLaunch = l => l.Exit.TrySetResult(0) };
        string? executedArguments = null;
        using var manager = new Manager(NullReSceneLogger.Instance, runner);
        manager.BruteForceProgress += (_, e) => executedArguments ??= e.ExecutedArguments;

        Task<BruteForceRunResult> runTask = manager.BruteForceRARVersionAsync(options);
        Task winner = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(10)));
        if (winner != runTask)
        {
            throw new TimeoutException("Timed out waiting for the brute-force run to finish.");
        }

        await runTask;

        Assert.NotNull(executedArguments);
        return executedArguments!;
    }

    #endregion

    #region CommentPhaseBruteForcer.BuildPhase1Arguments — -cfg-

    [Fact]
    public void BuildPhase1Arguments_OldVersion_IsCfgThenBaseArgsOnly()
    {
        List<string> args = CommentPhaseBruteForcer.BuildPhase1Arguments(200, "-m3", "-md64k", "comment.txt");
        Assert.Equal(["-cfg-", "a", "-r", "-m3", "-md64k", "-zcomment.txt"], args);
    }

    [Fact]
    public void BuildPhase1Arguments_Rar550Era_CfgPrecedesMa4AndTimestampsFollow()
    {
        List<string> args = CommentPhaseBruteForcer.BuildPhase1Arguments(600, "-m3", "-md64k", "comment.txt");
        Assert.Equal(["-cfg-", "a", "-r", "-m3", "-md64k", "-zcomment.txt", "-ma4", "-tsc-", "-tsa-"], args);
    }

    [Fact]
    public void BuildPhase1Arguments_Rar7Era_NoMa4ButCfgAndTimestampsPresent()
    {
        List<string> args = CommentPhaseBruteForcer.BuildPhase1Arguments(700, "-m3", "-md64k", "comment.txt");
        Assert.Equal(["-cfg-", "a", "-r", "-m3", "-md64k", "-zcomment.txt", "-tsc-", "-tsa-"], args);
    }

    #endregion

    /// <summary>A self-cleaning unique temporary directory for filesystem tests.</summary>
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"manager_helpers_{Guid.NewGuid():N}");

        public TempDir()
        {
            Directory.CreateDirectory(Path);
        }

        /// <summary>Creates a file in the temp directory and returns its full path.</summary>
        public string File(string name, string contents = "x")
        {
            string full = System.IO.Path.Combine(Path, name);
            System.IO.File.WriteAllText(full, contents);
            return full;
        }

        /// <summary>
        /// Creates a WinRAR version subfolder containing a rar-binary stub and returns its path.
        /// The stub carries the PLATFORM's binary name (rar.exe / rar) — the validity scan resolves
        /// via RarExecutable, so a hardcoded "rar.exe" stub makes every folder invalid on Unix.
        /// </summary>
        public string VersionDir(string name)
        {
            string dir = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, RarExecutable.FileName), "stub");
            return dir;
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
