using ReScene.Core;
using ReScene.Core.Cryptography;

namespace ReScene.Tests;

/// <summary>
/// Tests for <see cref="Manager.RenameMatchedOutput"/> — the transactional volume-placement step
/// that runs once a brute-force combo's hash has matched. These exercise it directly against a
/// filesystem fixture (no rar.exe involved): CAV and non-CAV success (including the very common
/// source==dest no-op), the occupied-destination and count-mismatch failure modes (both must
/// leave nothing moved), a transient mid-set failure that rolls back and lets a later attempt
/// succeed cleanly, and <see cref="Manager.RollBackMoves"/>'s own best-effort failure handling.
/// </summary>
public class RenameMatchedOutputTests : TempDirTestBase
{
    private readonly string _rarOutputDir;
    private readonly Manager _manager;

    public RenameMatchedOutputTests()
    {
        _rarOutputDir = Path.Combine(TempDir, "output");
        Directory.CreateDirectory(_rarOutputDir);
        _manager = new Manager();
    }

    private string CreateProduced(string fileName, string contents = "data")
    {
        string path = Path.Combine(_rarOutputDir, fileName);
        File.WriteAllText(path, contents);
        return path;
    }

    private static BruteForceOptions MakeOptions(IReadOnlyList<string> originalNames, bool completeAllVolumes, bool renameToOriginalNames = true)
        => new("winrar", "release", "output")
        {
            RAROptions = new RAROptions
            {
                OriginalRARFileNames = originalNames,
                CompleteAllVolumes = completeAllVolumes,
                RenameToOriginalNames = renameToOriginalNames,
            }
        };

    [Fact]
    public void Cav_FullyPlaced_ReturnsPlacedPathsAndCompleteTrue_IncludingSourceEqualsDestNoOp()
    {
        // Position 1's release name coincidentally equals the produced (generated) volume's own
        // on-disk name — MoveMatchedFile's source==dest short-circuit fires for it while the other
        // two volumes are genuinely renamed. All three still count as "placed".
        string v1 = CreateProduced("570-m5.part01.rar");

        _ = CreateProduced("570-m5.part02.rar");
        string v3 = CreateProduced("570-m5.part03.rar");

        BruteForceOptions options = MakeOptions(["aln-movie.rar", "570-m5.part02.rar", "aln-movie.r01"], completeAllVolumes: true);
        string rarFilePath = Path.Combine(_rarOutputDir, "570-m5.rar"); // expected path; never actually created

        (IReadOnlyList<string> placed, bool complete) = _manager.RenameMatchedOutput(options, rarFilePath, v1, _rarOutputDir);

        Assert.True(complete);
        string expectedDest0 = Path.Combine(_rarOutputDir, "aln-movie.rar");
        string expectedDest1 = Path.Combine(_rarOutputDir, "570-m5.part02.rar"); // no-op: same as v2
        string expectedDest2 = Path.Combine(_rarOutputDir, "aln-movie.r01");
        Assert.Equal([expectedDest0, expectedDest1, expectedDest2], placed);

        Assert.True(File.Exists(expectedDest0));
        Assert.False(File.Exists(v1));
        Assert.True(File.Exists(expectedDest1)); // still there — it never moved
        Assert.True(File.Exists(expectedDest2));
        Assert.False(File.Exists(v3));
    }

    [Fact]
    public void NonCav_SingleVolume_ReturnsPlacedPathAndCompleteTrue()
    {
        string produced = CreateProduced("570-m5.rar");
        BruteForceOptions options = MakeOptions(["Movie.rar"], completeAllVolumes: false);

        (IReadOnlyList<string> placed, bool complete) = _manager.RenameMatchedOutput(options, produced, produced, _rarOutputDir);

        string expectedDest = Path.Combine(_rarOutputDir, "Movie.rar");
        Assert.True(complete);
        Assert.Equal([expectedDest], placed);
        Assert.True(File.Exists(expectedDest));
        Assert.False(File.Exists(produced));
    }

    [Fact]
    public void NonCav_NoRenameNoPatch_IsSourceEqualsDestNoOpAndStillComplete()
    {
        // The common case: RenameToOriginalNames is off and no patching happened, so the computed
        // output name is identical to the produced file's current name — a pure no-op that must
        // still report Complete=true with the file at its (unchanged) path.
        string produced = CreateProduced("570-m5.rar");
        BruteForceOptions options = MakeOptions([], completeAllVolumes: false, renameToOriginalNames: false);

        (IReadOnlyList<string> placed, bool complete) = _manager.RenameMatchedOutput(options, produced, produced, _rarOutputDir);

        Assert.True(complete);
        Assert.Equal([produced], placed);
        Assert.True(File.Exists(produced));
    }

    [Fact]
    public void Cav_DestinationPermanentlyOccupied_ReturnsIncompleteAndMovesNothing()
    {
        string v1 = CreateProduced("abc.part01.rar");
        string v2 = CreateProduced("abc.part02.rar");
        string v3 = CreateProduced("abc.part03.rar");

        // A different file already sits at what would be volume 2's destination.
        string decoyPath = Path.Combine(_rarOutputDir, "r2.rar");
        File.WriteAllText(decoyPath, "decoy");

        BruteForceOptions options = MakeOptions(["r1.rar", "r2.rar", "r3.rar"], completeAllVolumes: true);
        string rarFilePath = Path.Combine(_rarOutputDir, "abc.rar");

        (IReadOnlyList<string> placed, bool complete) = _manager.RenameMatchedOutput(options, rarFilePath, v1, _rarOutputDir);

        Assert.False(complete);
        Assert.Empty(placed);

        // Nothing was moved — not even volume 1, whose destination was free — because the whole
        // move map is validated before any file is touched.
        Assert.True(File.Exists(v1));
        Assert.True(File.Exists(v2));
        Assert.True(File.Exists(v3));
        Assert.Equal("decoy", File.ReadAllText(decoyPath));
        Assert.False(File.Exists(Path.Combine(_rarOutputDir, "r1.rar")));
        Assert.False(File.Exists(Path.Combine(_rarOutputDir, "r3.rar")));
    }

    [Fact]
    public void Cav_GeneratedNames_ProducedFewerVolumesThanReleaseExpects_ReturnsIncompleteAndMovesNothing()
    {
        // Only 2 of the release's 3 expected volumes were actually produced for this combo.
        string v1 = CreateProduced("x.part01.rar");
        string v2 = CreateProduced("x.part02.rar");

        BruteForceOptions options = MakeOptions(["a.rar", "b.rar", "c.rar"], completeAllVolumes: true, renameToOriginalNames: false);
        string rarFilePath = Path.Combine(_rarOutputDir, "x.rar");

        (IReadOnlyList<string> placed, bool complete) = _manager.RenameMatchedOutput(options, rarFilePath, v1, _rarOutputDir);

        Assert.False(complete);
        Assert.Empty(placed);
        Assert.True(File.Exists(v1));
        Assert.True(File.Exists(v2));
    }

    [Fact]
    public void Cav_Sha1VerifiedRunWithNoKnownVolumeCrcs_ProducedFewerVolumesThanReleaseExpects_ReturnsIncomplete()
    {
        // Regression for the bug this task fixes: when the release is verified by a whole-file
        // SHA1 (or the SFV carries zero/placeholder CRCs), BuildExpectedInOrder/ExpectedVolumeCrcs
        // is empty, so the OLD per-volume-CRC verification step in TryProcessCommandLinesAsync
        // never engaged at all — an incomplete set slipped through as a "match". RenameMatchedOutput
        // never reads ExpectedVolumeCrcs or HashType; its identity check is independent of both, so
        // it still catches the shortfall.
        string v1 = CreateProduced("y.part01.rar");
        string v2 = CreateProduced("y.part02.rar");

        BruteForceOptions options = MakeOptions(["r1.rar", "r2.rar", "r3.rar"], completeAllVolumes: true, renameToOriginalNames: true);
        options.HashType = HashType.SHA1;
        Assert.Empty(options.ExpectedVolumeCrcs); // nothing known per-volume — the exact bug scenario

        string rarFilePath = Path.Combine(_rarOutputDir, "y.rar");

        (IReadOnlyList<string> placed, bool complete) = _manager.RenameMatchedOutput(options, rarFilePath, v1, _rarOutputDir);

        Assert.False(complete);
        Assert.Empty(placed);
        Assert.True(File.Exists(v1));
        Assert.True(File.Exists(v2));
        Assert.False(File.Exists(Path.Combine(_rarOutputDir, "r1.rar")));
    }

    [Fact]
    public void Cav_TransientMidSetFailure_RollsBackAndLetsSubsequentCallSucceed()
    {
        // Two volumes' release names collide ("same.rar" for both positions 0 and 1) — a
        // data-quality edge case, but a deterministic way to force a REAL move (not just an
        // upfront-validated occupied destination) to fail partway through: nothing occupies
        // "same.rar" at validation time (before any move), so validation passes; then volume 0's
        // move claims it, and volume 1's move — for a DIFFERENT source — finds it freshly taken.
        string v1 = CreateProduced("dup.part01.rar");
        string v2 = CreateProduced("dup.part02.rar");
        string v3 = CreateProduced("dup.part03.rar");

        BruteForceOptions failingOptions = MakeOptions(["same.rar", "same.rar", "third.rar"], completeAllVolumes: true);
        string rarFilePath = Path.Combine(_rarOutputDir, "dup.rar");

        (IReadOnlyList<string> placed, bool complete) = _manager.RenameMatchedOutput(failingOptions, rarFilePath, v1, _rarOutputDir);

        Assert.False(complete);
        Assert.Empty(placed);

        // Volume 0's move was rolled back: its destination is freed and it is back at its
        // original produced path. Volumes 1 and 2 were never touched (the loop stopped at 1).
        Assert.True(File.Exists(v1));
        Assert.True(File.Exists(v2));
        Assert.True(File.Exists(v3));
        Assert.False(File.Exists(Path.Combine(_rarOutputDir, "same.rar")));

        // A subsequent, fully-placeable combo (distinct names this time) must succeed cleanly —
        // no leftover partial output from the failed attempt collides with it.
        BruteForceOptions succeedingOptions = MakeOptions(["one.rar", "two.rar", "three.rar"], completeAllVolumes: true);
        (IReadOnlyList<string> placed2, bool complete2) = _manager.RenameMatchedOutput(succeedingOptions, rarFilePath, v1, _rarOutputDir);

        Assert.True(complete2);
        string d0 = Path.Combine(_rarOutputDir, "one.rar");
        string d1 = Path.Combine(_rarOutputDir, "two.rar");
        string d2 = Path.Combine(_rarOutputDir, "three.rar");
        Assert.Equal([d0, d1, d2], placed2);
        Assert.True(File.Exists(d0));
        Assert.True(File.Exists(d1));
        Assert.True(File.Exists(d2));
    }

    [Fact]
    public void RollBackMoves_DestinationReoccupied_LogsAndLeavesMovedFileInPlaceWithoutThrowing()
    {
        // Isolates RollBackMoves itself: the moved file's original path has been reclaimed by
        // something else by the time rollback runs (e.g. another cleanup step), so the rollback
        // move can't succeed. It must not throw, must log the failure, and must leave the
        // (unrollable) file exactly where it was moved to.
        string original = Path.Combine(TempDir, "original.rar");
        File.WriteAllText(original, "moved-content");
        string finalDest = Path.Combine(TempDir, "final.rar");
        File.Move(original, finalDest); // simulate the completed move that is about to be undone

        File.WriteAllText(original, "decoy"); // something now occupies the original path

        var logger = new RecordingLogger();
        var manager = new Manager(logger);

        manager.RollBackMoves([(original, finalDest)]);

        Assert.True(File.Exists(finalDest));
        Assert.Equal("moved-content", File.ReadAllText(finalDest));
        Assert.Equal("decoy", File.ReadAllText(original));
        Assert.Contains(logger.WarningMessages, m => m.Contains("Rollback failed", StringComparison.Ordinal));
    }
}
