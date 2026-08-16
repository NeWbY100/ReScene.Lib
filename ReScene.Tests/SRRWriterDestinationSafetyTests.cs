using System.Text;
using ReScene.SRR;

namespace ReScene.Tests;

/// <summary>
/// Pins the destination contract shared by every <see cref="SRRWriter"/> creation entry point:
/// a failed creation must leave a pre-existing destination BYTE-FOR-BYTE unchanged, and an
/// output path that aliases one of the writer's own inputs must be rejected before anything is
/// opened.
/// </summary>
/// <remarks>
/// These are regression tests for a real defect: <c>CreateAsync</c> opened the destination with
/// <c>FileMode.Create</c> (truncating it) and then deleted it unconditionally from its catch
/// blocks — including when the throw came from the argument/existence validation that runs BEFORE
/// the destination is ever opened. Calling it with an empty volume list therefore destroyed a
/// pre-existing, entirely unrelated file. <c>CreateFromInputsAsync</c> already had the correct
/// shape; these tests hold the other two paths to it.
/// </remarks>
public class SRRWriterDestinationSafetyTests : TempDirTestBase
{
    private const string Sentinel = "PRE-EXISTING CONTENT THAT MUST SURVIVE";

    private string WriteSentinel(string name)
    {
        string path = Path.Combine(TempDir, name);
        File.WriteAllText(path, Sentinel, Encoding.UTF8);
        return path;
    }

    private static void AssertIntact(string path, string because)
    {
        Assert.True(File.Exists(path), $"{because}: the file was deleted.");
        Assert.Equal(Sentinel, File.ReadAllText(path, Encoding.UTF8));
    }

    [Fact]
    public async Task CreateAsync_EmptyVolumeList_LeavesAPreExistingDestinationIntact()
    {
        string destination = WriteSentinel("already-here.srr");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(destination, []);

        Assert.False(result.Success);
        AssertIntact(destination, "validation threw before the destination was ever opened");
    }

    [Fact]
    public async Task CreateAsync_MissingVolume_LeavesAPreExistingDestinationIntact()
    {
        string destination = WriteSentinel("already-here.srr");
        string missing = Path.Combine(TempDir, "nope.rar");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(destination, [missing]);

        Assert.False(result.Success);
        AssertIntact(destination, "the missing-volume check throws before the destination is opened");
    }

    [Fact]
    public async Task CreateAsync_MissingStoredFile_LeavesAPreExistingDestinationIntact()
    {
        RarFixtures.WriteStoreModeRarSet(TempDir, "release", volumeCount: 1, payloadBytes: 16);
        string volume = Path.Combine(TempDir, "release.rar");
        string destination = WriteSentinel("already-here.srr");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(
            destination,
            [volume],
            [new StoredFileEntry("gone.nfo", Path.Combine(TempDir, "gone.nfo"))]);

        Assert.False(result.Success);
        AssertIntact(destination, "the missing-stored-file check throws before the destination is opened");
    }

    [Fact]
    public async Task CreateAsync_CancelledAfterTheWriteBegins_LeavesAPreExistingDestinationIntact()
    {
        RarFixtures.WriteStoreModeRarSet(TempDir, "release", volumeCount: 1, payloadBytes: 16);
        string volume = Path.Combine(TempDir, "release.rar");

        string nfo = Path.Combine(TempDir, "release.nfo");
        await File.WriteAllTextAsync(nfo, "nfo body");

        string destination = WriteSentinel("already-here.srr");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(
            destination,
            [volume],
            [new StoredFileEntry("release.nfo", nfo)],
            options: null,
            ct: cts.Token);

        Assert.False(result.Success);
        AssertIntact(destination, "a cancellation after the writer opened its staging file must not touch the destination");
    }

    [Fact]
    public async Task CreateAsync_OutputEqualsAVolume_IsRejectedAndLeavesThatVolumeIntact()
    {
        RarFixtures.WriteStoreModeRarSet(TempDir, "release", volumeCount: 1, payloadBytes: 16);
        string volume = Path.Combine(TempDir, "release.rar");
        byte[] before = await File.ReadAllBytesAsync(volume);

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(volume, [volume]);

        Assert.False(result.Success);
        Assert.True(File.Exists(volume), "the RAR volume being described was deleted.");
        Assert.Equal(before, await File.ReadAllBytesAsync(volume));
    }

    [Fact]
    public async Task CreateAsync_OutputEqualsAStoredSource_IsRejectedAndLeavesThatSourceIntact()
    {
        RarFixtures.WriteStoreModeRarSet(TempDir, "release", volumeCount: 1, payloadBytes: 16);
        string volume = Path.Combine(TempDir, "release.rar");
        string stored = WriteSentinel("release.nfo");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(
            stored,
            [volume],
            [new StoredFileEntry("release.nfo", stored)]);

        Assert.False(result.Success);
        AssertIntact(stored, "the stored source and the output are the same file");
    }

    [Fact]
    public async Task CreateFromSFVAsync_OutputEqualsTheSfv_IsRejectedAndLeavesItIntact()
    {
        RarFixtures.WriteStoreModeRarSet(TempDir, "release", volumeCount: 1, payloadBytes: 16);

        string sfv = Path.Combine(TempDir, "release.sfv");
        await File.WriteAllTextAsync(sfv, "release.rar 00000000\n", Encoding.UTF8);
        byte[] before = await File.ReadAllBytesAsync(sfv);

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateFromSFVAsync(sfv, sfv);

        Assert.False(result.Success);
        Assert.True(File.Exists(sfv), "the SFV the writer was reading from was deleted.");
        Assert.Equal(before, await File.ReadAllBytesAsync(sfv));
    }

    [Fact]
    public async Task CreateFromSFVAsync_OutputDirectoryDoesNotExistYet_StillCreatesTheSRR()
    {
        // The self-collision guard added to this overload computes a key that resolves THROUGH the
        // output directory, and the resolver requires its target to exist. Guarding before
        // creating the directory made this path fail outright for a destination in a new folder —
        // which CreateAsync, the method it delegates to, would have created itself.
        RarFixtures.WriteStoreModeRarSet(TempDir, "release", volumeCount: 1, payloadBytes: 16);

        string sfv = Path.Combine(TempDir, "release.sfv");
        await File.WriteAllTextAsync(sfv, "release.rar 00000000\n", Encoding.UTF8);

        string destination = Path.Combine(TempDir, "does-not-exist-yet", "out.srr");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateFromSFVAsync(destination, sfv);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(File.Exists(destination));
    }

    [Fact]
    public async Task CreateAsync_Success_StillReplacesAPreExistingDestination()
    {
        RarFixtures.WriteStoreModeRarSet(TempDir, "release", volumeCount: 1, payloadBytes: 16);
        string volume = Path.Combine(TempDir, "release.rar");
        string destination = WriteSentinel("already-here.srr");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(destination, [volume]);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotEqual(Sentinel, await File.ReadAllTextAsync(destination, Encoding.UTF8));
        Assert.NotNull(SRRFile.Load(destination).HeaderBlock);
    }

    [Fact]
    public async Task CreateAsync_FailureAfterStaging_LeavesNoStagingFileBehind()
    {
        // Deliberately a failure that occurs AFTER the staging file exists. An earlier version of
        // this test used a missing volume, which throws during validation — before the staging
        // file is ever created — so it passed no matter what the cleanup did. Cancellation is
        // the cheapest failure that lands on the far side of CreateExclusiveTempFile.
        RarFixtures.WriteStoreModeRarSet(TempDir, "release", volumeCount: 1, payloadBytes: 16);
        string volume = Path.Combine(TempDir, "release.rar");

        string nfo = Path.Combine(TempDir, "release.nfo");
        await File.WriteAllTextAsync(nfo, "nfo body");

        string destination = Path.Combine(TempDir, "output.srr");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(
            destination,
            [volume],
            [new StoredFileEntry("release.nfo", nfo)],
            options: null,
            ct: cts.Token);

        Assert.False(result.Success);
        Assert.Empty(Directory.GetFiles(TempDir, "output.srr.tmp-*"));
        Assert.False(File.Exists(destination), "a cancelled creation must not leave the destination behind.");
    }
}
