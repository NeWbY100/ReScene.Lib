using ReScene.SRR;

namespace ReScene.Tests;

public class SRRWriterMultiInputTests : IDisposable
{
    private readonly string _root;
    private readonly string _out;
    private readonly SRRWriter _writer = new();

    public SRRWriterMultiInputTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "multi-" + Guid.NewGuid().ToString("N"));
        _out = Path.Combine(_root, "out.srr");
        BuildSet("CD1", "a");
        BuildSet("CD2", "b");
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private void BuildSet(string dir, string baseName)
    {
        string d = Path.Combine(_root, dir);
        Directory.CreateDirectory(d);
        // Two-volume store-mode set + matching SFV (CRC value is irrelevant to the writer).
        RarFixtures.WriteStoreModeRarSet(d, baseName, volumeCount: 2, payloadBytes: 64);
        File.WriteAllLines(Path.Combine(d, baseName + ".sfv"),
            [$"{baseName}.rar 00000000", $"{baseName}.r00 00000000"]);
    }

    private string Sfv(string dir, string baseName) => Path.Combine(_root, dir, baseName + ".sfv");

    [Fact]
    public async Task TwoSets_WritesStoredSfvsThenVolumesInOrder()
    {
        SRRCreationResult r = await _writer.CreateFromInputsAsync(
            _out, [Sfv("CD1", "a"), Sfv("CD2", "b")], _root, storeRelativePaths: true);

        Assert.Null(r.ErrorMessage);
        SRRFile srr = SRRFile.Load(_out);
        Assert.Equal(["CD1/a.sfv", "CD2/b.sfv"], srr.StoredFiles.Select(f => f.FileName));
        Assert.Equal(["CD1/a.rar", "CD1/a.r00", "CD2/b.rar", "CD2/b.r00"],
            srr.RARFiles.Select(f => f.FileName));
    }

    [Fact]
    public async Task ZeroInputs_WithStoredFile_WritesStorageOnlySrr()
    {
        string nfo = Path.Combine(_root, "r.nfo");
        File.WriteAllText(nfo, "nfo");
        SRRCreationResult r = await _writer.CreateFromInputsAsync(
            _out, [], _root, storeRelativePaths: true,
            additionalFiles: [new StoredFileEntry("r.nfo", nfo)]);

        Assert.Null(r.ErrorMessage);
        SRRFile srr = SRRFile.Load(_out);
        Assert.Single(srr.StoredFiles);
        Assert.Empty(srr.RARFiles);
    }

    [Fact]
    public async Task ZeroInputs_ZeroStored_WritesHeaderOnlySrr()
    {
        SRRCreationResult r = await _writer.CreateFromInputsAsync(_out, [], null, false);
        Assert.Null(r.ErrorMessage);
        SRRFile srr = SRRFile.Load(_out);
        Assert.Empty(srr.StoredFiles);
        Assert.Empty(srr.RARFiles);
    }

    [Fact]
    public async Task NonFirstVolumeRarInput_ReturnsError()
    {
        SRRCreationResult r = await _writer.CreateFromInputsAsync(
            _out, [Path.Combine(_root, "CD1", "a.r00")], _root, true);
        Assert.Contains("not a first RAR volume", r.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingVolume_PreservesExistingDestination_NoTempLeft()
    {
        File.WriteAllBytes(_out, [1, 2, 3]);
        File.Delete(Path.Combine(_root, "CD2", "b.r00")); // SFV references it; file gone
        SRRCreationResult r = await _writer.CreateFromInputsAsync(
            _out, [Sfv("CD1", "a"), Sfv("CD2", "b")], _root, true);

        Assert.NotNull(r.ErrorMessage);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(_out));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }

    [Fact]
    public async Task OutputEqualsInput_ReturnsError()
    {
        SRRCreationResult r = await _writer.CreateFromInputsAsync(
            Sfv("CD1", "a"), [Sfv("CD1", "a")], _root, true);
        Assert.NotNull(r.ErrorMessage);
    }

    [Fact]
    public async Task LogicalNameCollision_DistinctSources_ErrorNamingBoth()
    {
        string s1 = Path.Combine(_root, "CD1", "same.nfo");
        string s2 = Path.Combine(_root, "CD2", "same.nfo");
        File.WriteAllText(s1, "1");
        File.WriteAllText(s2, "2");
        SRRCreationResult r = await _writer.CreateFromInputsAsync(
            _out, [], _root, storeRelativePaths: false,   // flat names -> both "same.nfo"
            additionalFiles: [new StoredFileEntry("same.nfo", s1), new StoredFileEntry("same.nfo", s2)]);

        Assert.NotNull(r.ErrorMessage);
        Assert.Contains("CD1", r.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("CD2", r.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_PreservesDestination_CleansTemp()
    {
        File.WriteAllBytes(_out, [9]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _writer.CreateFromInputsAsync(_out, [Sfv("CD1", "a")], _root, true, ct: cts.Token));

        Assert.Equal([9], File.ReadAllBytes(_out));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }

    // --- Additional clause coverage (rootFolder validation, containment, first-volume naming
    // variants, chain grouping, stored-file dedup, overwrite, and a genuine post-tmp-creation
    // failure) beyond the required verbatim rows above. ---

    [Fact]
    public async Task RootFolderNull_WithStoreRelativePaths_ReturnsError()
    {
        SRRCreationResult r = await _writer.CreateFromInputsAsync(_out, [], null, storeRelativePaths: true);
        Assert.NotNull(r.ErrorMessage);
    }

    [Fact]
    public async Task VolumeOutsideRoot_ReturnsError()
    {
        string outsideDir = Path.Combine(Path.GetTempPath(), "multi-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDir);
        try
        {
            RarFixtures.WriteStoreModeRarSet(outsideDir, "x", volumeCount: 1, payloadBytes: 32);
            string outsideRar = Path.Combine(outsideDir, "x.rar");

            SRRCreationResult r = await _writer.CreateFromInputsAsync(
                _out, [outsideRar], _root, storeRelativePaths: true);

            Assert.NotNull(r.ErrorMessage);
        }
        finally
        {
            Directory.Delete(outsideDir, recursive: true);
        }
    }

    [Fact]
    public async Task SfvEntryEscapesDirectory_ReturnsError()
    {
        string sfvPath = Path.Combine(_root, "escape.sfv");
        File.WriteAllLines(sfvPath, ["../../../evil.rar 00000000"]);

        SRRCreationResult r = await _writer.CreateFromInputsAsync(
            _out, [sfvPath], _root, storeRelativePaths: true);

        Assert.NotNull(r.ErrorMessage);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task FirstVolumePartStyle_LowestNumbered_Accepted(int digitWidth)
    {
        string dir = Path.Combine(_root, "Part" + digitWidth);
        Directory.CreateDirectory(dir);
        RarFixtures.WriteStoreModePartRarSet(dir, "p", volumeCount: 2, payloadBytes: 32, digitWidth);
        string firstVolume = Path.Combine(dir, $"p.part{"1".PadLeft(digitWidth, '0')}.rar");
        string outPath = Path.Combine(dir, "out.srr");

        SRRCreationResult r = await _writer.CreateFromInputsAsync(
            outPath, [firstVolume], _root, storeRelativePaths: true);

        Assert.Null(r.ErrorMessage);
        SRRFile srr = SRRFile.Load(outPath);
        Assert.Equal(2, srr.RARFiles.Count);
    }

    [Fact]
    public async Task FirstVolumePartStyle_NotLowestNumbered_ReturnsError()
    {
        string dir = Path.Combine(_root, "PartReject");
        Directory.CreateDirectory(dir);
        RarFixtures.WriteStoreModePartRarSet(dir, "p", volumeCount: 2, payloadBytes: 32, digitWidth: 2);
        string secondVolume = Path.Combine(dir, "p.part02.rar");

        SRRCreationResult r = await _writer.CreateFromInputsAsync(
            Path.Combine(dir, "out.srr"), [secondVolume], _root, storeRelativePaths: true);

        Assert.Contains("not a first RAR volume", r.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InterleavedCrossDirectoryChains_GroupsAndSortsPerChain()
    {
        string dir1 = Path.Combine(_root, "X1");
        string dir2 = Path.Combine(_root, "X2");
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);
        RarFixtures.WriteStoreModeRarSet(dir1, "same", volumeCount: 2, payloadBytes: 32);
        RarFixtures.WriteStoreModeRarSet(dir2, "same", volumeCount: 2, payloadBytes: 32);

        // Same basename in two different directories, entries interleaved: must still group
        // per-chain (by directory + basename) and sort only within each chain.
        string sfvPath = Path.Combine(_root, "multi.sfv");
        File.WriteAllLines(sfvPath,
        [
            "X1/same.rar 00000000",
            "X2/same.rar 00000000",
            "X1/same.r00 00000000",
            "X2/same.r00 00000000",
        ]);

        SRRCreationResult r = await _writer.CreateFromInputsAsync(
            _out, [sfvPath], _root, storeRelativePaths: true);

        Assert.Null(r.ErrorMessage);
        SRRFile srr = SRRFile.Load(_out);
        Assert.Equal(
            ["X1/same.rar", "X1/same.r00", "X2/same.rar", "X2/same.r00"],
            srr.RARFiles.Select(f => f.FileName));
    }

    [Fact]
    public async Task AdditionalFiles_OrderPreserved_IdenticalSourceDeduped()
    {
        string nfo1 = Path.Combine(_root, "1.nfo");
        string nfo2 = Path.Combine(_root, "2.nfo");
        File.WriteAllText(nfo1, "one");
        File.WriteAllText(nfo2, "two");

        SRRCreationResult r = await _writer.CreateFromInputsAsync(
            _out, [], _root, storeRelativePaths: false,
            additionalFiles:
            [
                new StoredFileEntry("1.nfo", nfo1),
                new StoredFileEntry("1.nfo", nfo1), // identical name + source repeat -> silent dedup
                new StoredFileEntry("2.nfo", nfo2),
            ]);

        Assert.Null(r.ErrorMessage);
        SRRFile srr = SRRFile.Load(_out);
        Assert.Equal(["1.nfo", "2.nfo"], srr.StoredFiles.Select(f => f.FileName));
    }

    [Fact]
    public async Task Overwrite_ExistingDestination_OnSuccess_ReplacesContent()
    {
        File.WriteAllBytes(_out, [7, 7, 7]);

        SRRCreationResult r = await _writer.CreateFromInputsAsync(
            _out, [Sfv("CD1", "a")], _root, true);

        Assert.True(r.Success, r.ErrorMessage);
        SRRFile srr = SRRFile.Load(_out);
        Assert.Equal(2, srr.RARFiles.Count);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }

    [Fact]
    public async Task MissingVolume_FlatNaming_CaughtPreTmp_PreservesDestinationNoTempLeft()
    {
        // Flat naming (storeRelativePaths: false) never canonicalizes a volume path against the
        // root, but the C3 review fix (ReconcileVolumesAgainstStoredFiles) now runs GetFinalPath
        // over every resolved volume — regardless of naming mode — to seed the writer-wide
        // collision/dedup registry, BEFORE the tmp file is created. So a missing volume is caught
        // here, pre-tmp, in both naming modes (this test no longer reaches WriteVolumesAsync at
        // all; see VolumeOpenShareViolation_FailsAfterTmpCreated_... below for a genuine
        // post-tmp-creation failure).
        File.WriteAllBytes(_out, [4, 5, 6]);
        File.Delete(Path.Combine(_root, "CD2", "b.r00"));

        SRRCreationResult r = await _writer.CreateFromInputsAsync(
            _out, [Sfv("CD1", "a"), Sfv("CD2", "b")], rootFolder: null, storeRelativePaths: false);

        Assert.NotNull(r.ErrorMessage);
        Assert.Equal([4, 5, 6], File.ReadAllBytes(_out));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }

    // Genuine post-tmp-creation failure (peer follow-up to C3/C7): an exclusive lock on the
    // SECOND volume in a chain lets GetFinalPath (a zero-access metadata query, unaffected by
    // FileShare.None per GetFinalPathNameByHandle's documented pattern) succeed all the way
    // through resolution and reconciliation — so the tmp file is created and the SFV's stored
    // block and the first volume's headers are genuinely written — before ProcessRARVolume's
    // real read-access FileStream.Open on the locked volume hits a sharing-violation IOException,
    // exercising the `if (tmpCreated) TryDeleteFile(tmpPath)` branch for real.
    [Fact]
    public async Task VolumeOpenShareViolation_FailsAfterTmpCreated_PreservesDestinationAndCleansTemp()
    {
        File.WriteAllBytes(_out, [8, 8, 8]);
        string lockedVolume = Path.Combine(_root, "CD1", "a.r00");
        var exclusiveLock = new FileStream(lockedVolume, FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            SRRCreationResult r = await _writer.CreateFromInputsAsync(
                _out, [Sfv("CD1", "a")], _root, storeRelativePaths: true);

            Assert.NotNull(r.ErrorMessage);
        }
        finally
        {
            exclusiveLock.Dispose();
        }

        Assert.Equal([8, 8, 8], File.ReadAllBytes(_out));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }

    // --- Review-fix regression coverage (task-2-findings.md C1-C6; C7 has no deterministic test
    // per the finding's own allowance) ---

    // C1: output self-collision must also be checked against the FULLY RESOLVED emission set —
    // a.r00 is only ever discovered via the SFV, never an `inputFiles` entry itself.
    [Fact]
    public async Task OutputEqualsDiscoveredVolume_ReturnsError_SourcePreserved()
    {
        string volumePath = Path.Combine(_root, "CD1", "a.r00");
        byte[] originalBytes = File.ReadAllBytes(volumePath);

        SRRCreationResult r = await _writer.CreateFromInputsAsync(
            volumePath, [Sfv("CD1", "a")], _root, true);

        Assert.NotNull(r.ErrorMessage);
        Assert.Equal(originalBytes, File.ReadAllBytes(volumePath));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "CD1"), "*.tmp-*"));
    }

    // C2: flat volume/stored names must route through CanonicalizeLogicalName, not raw
    // Path.GetFileName. Not portably constructible as a real file on Windows (backslash is a
    // path separator here) — the direct assertion pins the canonicalizer boundary the flat
    // branches now call; the second test proves ordinary names still come out unchanged.
    [Fact]
    public void CanonicalizeLogicalName_BackslashParentTraversal_Throws() =>
        Assert.Throws<SrrNameException>(() => SrrNameCanonicalizer.CanonicalizeLogicalName("..\\evil.sfv"));

    [Fact]
    public async Task FlatNaming_RoutesVolumeAndStoredNamesThroughCanonicalizer()
    {
        SRRCreationResult r = await _writer.CreateFromInputsAsync(
            _out, [Sfv("CD1", "a")], _root, storeRelativePaths: false);

        Assert.Null(r.ErrorMessage);
        SRRFile srr = SRRFile.Load(_out);
        Assert.Equal(["a.sfv"], srr.StoredFiles.Select(f => f.FileName));
        Assert.Equal(["a.rar", "a.r00"], srr.RARFiles.Select(f => f.FileName));
    }

    // C3: the §1a collision/dedup policy is writer-wide (stored files AND volumes), checked in
    // emission order.
    [Fact]
    public async Task TwoSfvsReferenceSameVolume_WrittenOnce()
    {
        string sharedDir = Path.Combine(_root, "Shared");
        Directory.CreateDirectory(sharedDir);
        RarFixtures.WriteStoreModeRarSet(sharedDir, "shared", volumeCount: 2, payloadBytes: 32);

        string sfvA = Path.Combine(_root, "A.sfv");
        string sfvB = Path.Combine(_root, "B.sfv");
        File.WriteAllLines(sfvA, ["Shared/shared.rar 00000000", "Shared/shared.r00 00000000"]);
        File.WriteAllLines(sfvB, ["Shared/shared.rar 00000000", "Shared/shared.r00 00000000"]);

        SRRCreationResult r = await _writer.CreateFromInputsAsync(
            _out, [sfvA, sfvB], _root, storeRelativePaths: true);

        Assert.Null(r.ErrorMessage);
        SRRFile srr = SRRFile.Load(_out);
        Assert.Equal(["Shared/shared.rar", "Shared/shared.r00"], srr.RARFiles.Select(f => f.FileName));
    }

    [Fact]
    public async Task DistinctChainsSameFlatBasename_ReturnsErrorNamingBoth()
    {
        string dir1 = Path.Combine(_root, "Y1");
        string dir2 = Path.Combine(_root, "Y2");
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);
        RarFixtures.WriteStoreModeRarSet(dir1, "dup", volumeCount: 1, payloadBytes: 32);
        RarFixtures.WriteStoreModeRarSet(dir2, "dup", volumeCount: 1, payloadBytes: 32);

        SRRCreationResult r = await _writer.CreateFromInputsAsync(
            _out, [Path.Combine(dir1, "dup.rar"), Path.Combine(dir2, "dup.rar")], _root, storeRelativePaths: false);

        Assert.NotNull(r.ErrorMessage);
        Assert.Contains("Y1", r.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("Y2", r.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoredFileAndVolumeSameFlatName_ReturnsError()
    {
        string fakeRar = Path.Combine(_root, "fake.bin");
        File.WriteAllText(fakeRar, "not a rar");

        SRRCreationResult r = await _writer.CreateFromInputsAsync(
            _out, [Path.Combine(_root, "CD1", "a.rar")], _root, storeRelativePaths: false,
            additionalFiles: [new StoredFileEntry("a.rar", fakeRar)]);

        Assert.NotNull(r.ErrorMessage);
    }

    // C4: a lone .rNN with no .rar sibling on disk can never be a first volume.
    [Fact]
    public async Task LoneRNNWithoutRarSibling_DirectInput_ReturnsError()
    {
        string dir = Path.Combine(_root, "LoneR00");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "x.r00"), [0]);

        SRRCreationResult r = await _writer.CreateFromInputsAsync(
            Path.Combine(dir, "out.srr"), [Path.Combine(dir, "x.r00")], _root, storeRelativePaths: true);

        Assert.Contains("not a first RAR volume", r.ErrorMessage, StringComparison.Ordinal);
    }

    // C5: the atomic commit (File.Move) must be the LAST fallible action affecting the result —
    // a throwing Progress subscriber on the final "complete" event must not flip an
    // already-committed success into a failure, nor surface as a lost-destination cancel.
    [Fact]
    public async Task ProgressHandlerThrowsOnCompletion_StillReportsCommittedSuccess()
    {
        _writer.Progress += (_, args) =>
        {
            if (args.Message == "SRR creation complete.")
            {
                throw new InvalidOperationException("boom");
            }
        };

        SRRCreationResult r = await _writer.CreateFromInputsAsync(_out, [Sfv("CD1", "a")], _root, true);

        Assert.True(r.Success);
        Assert.Null(r.ErrorMessage);
        SRRFile srr = SRRFile.Load(_out);
        Assert.Equal(2, srr.RARFiles.Count);
    }

    [Fact]
    public async Task ProgressHandlerThrowsCancellationOnCompletion_DoesNotSurfaceAsCancelOrLoseDestination()
    {
        _writer.Progress += (_, args) =>
        {
            if (args.Message == "SRR creation complete.")
            {
                throw new OperationCanceledException("boom");
            }
        };

        SRRCreationResult r = await _writer.CreateFromInputsAsync(_out, [Sfv("CD1", "a")], _root, true);

        Assert.True(r.Success);
        Assert.Null(r.ErrorMessage);
        Assert.True(File.Exists(_out));
    }

    // C6: RARVolumeNaming.GetBaseName must anchor to the TRAILING .partN.rar suffix, not the
    // first ".part"-like substring, so two chains whose release name itself contains ".Part.N"
    // don't collapse into one.
    [Fact]
    public async Task PartNameContainingLiteralPartSegment_DoesNotMergeDistinctChains()
    {
        string dir = Path.Combine(_root, "TwoPartMovie");
        Directory.CreateDirectory(dir);
        RarFixtures.WriteStoreModePartRarSet(dir, "The.Movie.Part.1", volumeCount: 2, payloadBytes: 32, digitWidth: 2);
        RarFixtures.WriteStoreModePartRarSet(dir, "The.Movie.Part.2", volumeCount: 2, payloadBytes: 32, digitWidth: 2);

        string sfvPath = Path.Combine(dir, "movie.sfv");
        File.WriteAllLines(sfvPath,
        [
            "The.Movie.Part.1.part01.rar 00000000",
            "The.Movie.Part.2.part01.rar 00000000",
            "The.Movie.Part.1.part02.rar 00000000",
            "The.Movie.Part.2.part02.rar 00000000",
        ]);

        SRRCreationResult r = await _writer.CreateFromInputsAsync(
            Path.Combine(dir, "out.srr"), [sfvPath], _root, storeRelativePaths: true);

        Assert.Null(r.ErrorMessage);
        SRRFile srr = SRRFile.Load(Path.Combine(dir, "out.srr"));
        Assert.Equal(
            [
                "TwoPartMovie/The.Movie.Part.1.part01.rar",
                "TwoPartMovie/The.Movie.Part.1.part02.rar",
                "TwoPartMovie/The.Movie.Part.2.part01.rar",
                "TwoPartMovie/The.Movie.Part.2.part02.rar",
            ],
            srr.RARFiles.Select(f => f.FileName));
    }

    // ── Writer<->resolver agreement (codex Task 9 fix-3 G3/G4): the SFV branch now routes through
    //    the shared SfvVolumeResolver, the SAME code the folder-mode subtitle path uses. These
    //    prove the extracted resolver still handles the two cases the VM copy got wrong, INSIDE
    //    the writer's real end-to-end path. ──

    [Fact]
    public async Task Sfv_SpacedVolumeName_GroupsOneSet_NotDropped()
    {
        string dir = Path.Combine(_root, "Sub");
        Directory.CreateDirectory(dir);
        RarFixtures.WriteStoreModeRarSet(dir, "sub title", volumeCount: 2, payloadBytes: 64);
        string sfvPath = Path.Combine(dir, "s.sfv");
        File.WriteAllLines(sfvPath, ["sub title.rar 00000000", "sub title.r00 00000000"]);

        SRRCreationResult r = await _writer.CreateFromInputsAsync(
            Path.Combine(dir, "out.srr"), [sfvPath], _root, storeRelativePaths: true);

        Assert.Null(r.ErrorMessage);
        SRRFile srr = SRRFile.Load(Path.Combine(dir, "out.srr"));
        Assert.Equal(["Sub/sub title.rar", "Sub/sub title.r00"], srr.RARFiles.Select(f => f.FileName));
    }

    [Fact]
    public async Task Sfv_DotSlashContinuation_FoldsIntoHeadsSet_NoSplit()
    {
        string dir = Path.Combine(_root, "Sub");
        Directory.CreateDirectory(dir);
        RarFixtures.WriteStoreModeRarSet(dir, "eng", volumeCount: 2, payloadBytes: 64);
        string sfvPath = Path.Combine(dir, "s.sfv");
        File.WriteAllLines(sfvPath, ["eng.rar 00000000", @".\eng.r00 00000000"]);

        SRRCreationResult r = await _writer.CreateFromInputsAsync(
            Path.Combine(dir, "out.srr"), [sfvPath], _root, storeRelativePaths: true);

        Assert.Null(r.ErrorMessage);
        SRRFile srr = SRRFile.Load(Path.Combine(dir, "out.srr"));
        Assert.Equal(["Sub/eng.rar", "Sub/eng.r00"], srr.RARFiles.Select(f => f.FileName));
    }
}
