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
    public async Task MissingVolume_FlatNaming_FailsMidWrite_PreservesDestinationAndCleansTemp()
    {
        // Flat naming (storeRelativePaths: false) never canonicalizes a volume path against the
        // root, so the missing volume isn't caught until the writer actually tries to open it —
        // a genuine failure AFTER the tmp file already holds the header and stored-file blocks.
        File.WriteAllBytes(_out, [4, 5, 6]);
        File.Delete(Path.Combine(_root, "CD2", "b.r00"));

        SRRCreationResult r = await _writer.CreateFromInputsAsync(
            _out, [Sfv("CD1", "a"), Sfv("CD2", "b")], rootFolder: null, storeRelativePaths: false);

        Assert.NotNull(r.ErrorMessage);
        Assert.Equal([4, 5, 6], File.ReadAllBytes(_out));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }
}
