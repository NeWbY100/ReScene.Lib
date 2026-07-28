using ReScene.Core;

namespace ReScene.Tests;

/// <summary>
/// Direct tests for <see cref="Manager.FinalizeAssembledSet"/> — the transactional placement step
/// for a guided-assembly win: moves the reconstructor's ordered <c>WrittenPaths</c> verbatim into
/// the output directory, computing each destination file name per <see
/// cref="RAROptions.RenameToOriginalNames"/> (verbatim reuse of the assembled file's own name, or a
/// candidate-slug-based generated name that preserves the volume's own suffix via <see
/// cref="ReScene.RAR.RARVolumeNaming.GetBaseName"/>). These exercise it directly against a
/// filesystem fixture (no rar.exe, no SRR involved) — mirrors <see
/// cref="RenameMatchedOutputTests"/>'s style for the legacy finalizer.
/// </summary>
public class ManagerAssemblyFinalizeTests : TempDirTestBase
{
    private readonly string _rarOutputDir;
    private readonly string _assembledDir;
    private readonly Manager _manager;

    public ManagerAssemblyFinalizeTests()
    {
        _rarOutputDir = Path.Combine(TempDir, "output");
        _assembledDir = Path.Combine(TempDir, "assembled-scratch");
        Directory.CreateDirectory(_rarOutputDir);
        Directory.CreateDirectory(_assembledDir);
        _manager = new Manager();
    }

    private string CreateAssembled(string fileName, string contents = "data")
    {
        string path = Path.Combine(_assembledDir, fileName);
        File.WriteAllText(path, contents);
        return path;
    }

    private static BruteForceOptions MakeOptions(bool renameToOriginalNames)
        => new("winrar", "release", "output")
        {
            RAROptions = new RAROptions
            {
                RenameToOriginalNames = renameToOriginalNames,
            },
        };

    [Fact]
    public void OriginalNames_PlacesUnderWorkOutput()
    {
        // The assembled files already carry the ORIGINAL (SRR-recorded) volume names — with
        // RenameToOriginalNames on, the finalizer must place them verbatim, unchanged, directly
        // under the output directory.
        string v1 = CreateAssembled("release.rar");
        string v2 = CreateAssembled("release.r00");

        BruteForceOptions options = MakeOptions(renameToOriginalNames: true);

        (IReadOnlyList<string> placed, bool complete) =
            _manager.FinalizeAssembledSet(options, [v1, v2], "release", _rarOutputDir);

        Assert.True(complete);
        string expectedDest0 = Path.Combine(_rarOutputDir, "release.rar");
        string expectedDest1 = Path.Combine(_rarOutputDir, "release.r00");
        Assert.Equal([expectedDest0, expectedDest1], placed);
        Assert.True(File.Exists(expectedDest0));
        Assert.True(File.Exists(expectedDest1));
        Assert.False(File.Exists(v1));
        Assert.False(File.Exists(v2));
    }

    [Fact]
    public void GeneratedNames_PreservesPartNNSuffix_Distinct()
    {
        // Regression pin: a naive Path.GetFileNameWithoutExtension-based rename would strip only
        // ".rar" from both "release.part01.rar" and "release.part02.rar" (leaving "release.part01"
        // / "release.part02" as the "base"), or worse, collapse both to the same generated name.
        // RARVolumeNaming.GetBaseName strips the WHOLE ".partNN.rar" suffix, so the two volumes'
        // generated names stay DISTINCT.
        string v1 = CreateAssembled("release.part01.rar");
        string v2 = CreateAssembled("release.part02.rar");

        BruteForceOptions options = MakeOptions(renameToOriginalNames: false);

        (IReadOnlyList<string> placed, bool complete) =
            _manager.FinalizeAssembledSet(options, [v1, v2], "slug", _rarOutputDir);

        Assert.True(complete);
        string expectedDest0 = Path.Combine(_rarOutputDir, "slug-assembled.part01.rar");
        string expectedDest1 = Path.Combine(_rarOutputDir, "slug-assembled.part02.rar");
        Assert.Equal([expectedDest0, expectedDest1], placed);
        Assert.NotEqual(expectedDest0, expectedDest1);
        Assert.True(File.Exists(expectedDest0));
        Assert.True(File.Exists(expectedDest1));
    }

    [Fact]
    public void GeneratedNames_OldStyleSuffixes()
    {
        // Old-style volumes (.rar/.r00/.r01): GetBaseName falls back to
        // Path.GetFileNameWithoutExtension, so each volume's own single-extension suffix
        // (".rar"/".r00"/".r01") is preserved on the generated name.
        string v1 = CreateAssembled("release.rar");
        string v2 = CreateAssembled("release.r00");
        string v3 = CreateAssembled("release.r01");

        BruteForceOptions options = MakeOptions(renameToOriginalNames: false);

        (IReadOnlyList<string> placed, bool complete) =
            _manager.FinalizeAssembledSet(options, [v1, v2, v3], "slug", _rarOutputDir);

        Assert.True(complete);
        string expectedDest0 = Path.Combine(_rarOutputDir, "slug-assembled.rar");
        string expectedDest1 = Path.Combine(_rarOutputDir, "slug-assembled.r00");
        string expectedDest2 = Path.Combine(_rarOutputDir, "slug-assembled.r01");
        Assert.Equal([expectedDest0, expectedDest1, expectedDest2], placed);
        Assert.True(File.Exists(expectedDest0));
        Assert.True(File.Exists(expectedDest1));
        Assert.True(File.Exists(expectedDest2));
    }

    [Fact]
    public void GeneratedNames_NoCollisionWithRetainedCarriers()
    {
        // DeleteRARFiles=false: the candidate's own carrier file is retained in rarOutputDir under
        // its OWN (candidate-generated) name. The finalizer's "{candidateSlug}-assembled{suffix}"
        // naming must never collide with it.
        string carrierPath = Path.Combine(_rarOutputDir, "570-m5.rar");
        File.WriteAllText(carrierPath, "carrier-bytes");

        string v1 = CreateAssembled("release.rar");
        BruteForceOptions options = MakeOptions(renameToOriginalNames: false);

        (IReadOnlyList<string> placed, bool complete) =
            _manager.FinalizeAssembledSet(options, [v1], "570-m5", _rarOutputDir);

        Assert.True(complete);
        string expectedDest = Path.Combine(_rarOutputDir, "570-m5-assembled.rar");
        Assert.Equal([expectedDest], placed);
        Assert.True(File.Exists(expectedDest));

        // The carrier is a distinct file, untouched by the finalizer.
        Assert.True(File.Exists(carrierPath));
        Assert.Equal("carrier-bytes", File.ReadAllText(carrierPath));
    }

    [Fact]
    public void Transactional_RollsBackWhenDestinationOccupied()
    {
        string v1 = CreateAssembled("release.rar");
        string v2 = CreateAssembled("release.r00");

        // A different file already occupies what would be volume 2's destination.
        string decoyPath = Path.Combine(_rarOutputDir, "slug-assembled.r00");
        File.WriteAllText(decoyPath, "decoy");

        BruteForceOptions options = MakeOptions(renameToOriginalNames: false);

        (IReadOnlyList<string> placed, bool complete) =
            _manager.FinalizeAssembledSet(options, [v1, v2], "slug", _rarOutputDir);

        Assert.False(complete);
        Assert.Empty(placed);

        // Nothing was moved — not even volume 1, whose destination was free — because the whole
        // move map is validated before any file is touched (ExecuteMovePlan's own invariant).
        Assert.True(File.Exists(v1));
        Assert.True(File.Exists(v2));
        Assert.Equal("decoy", File.ReadAllText(decoyPath));
        Assert.False(File.Exists(Path.Combine(_rarOutputDir, "slug-assembled.rar")));
    }
}
