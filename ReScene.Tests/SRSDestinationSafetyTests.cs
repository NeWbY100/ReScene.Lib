using System.Text;
using ReScene.SRS;

namespace ReScene.Tests;

/// <summary>
/// Pins the destination contract for the SRS write paths: a failed creation or reconstruction must
/// leave a pre-existing destination BYTE-FOR-BYTE unchanged, and an output path that aliases one of
/// the call's own inputs must be rejected rather than destroying that input.
/// </summary>
/// <remarks>
/// Regression tests for the same defect fixed in <see cref="SRSWriter"/>'s SRR sibling: both entry
/// points cleaned up by deleting <c>outputPath</c> unconditionally from their catch blocks, even
/// when the throw came from validation that runs BEFORE anything is written — so a missing sample
/// or a missing media file destroyed an unrelated pre-existing file at the destination.
/// </remarks>
public class SRSDestinationSafetyTests : TempDirTestBase
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

    /// <summary>A synthetic Stream-container (.vob) sample, the cheapest format to profile.</summary>
    private string WriteStreamSample(string name = "sample.vob") =>
        SyntheticSampleBuilder.BuildStream(Path.Combine(TempDir, name));

    [Fact]
    public async Task CreateAsync_MissingSample_LeavesAPreExistingDestinationIntact()
    {
        string destination = WriteSentinel("already-here.srs");
        string missingSample = Path.Combine(TempDir, "nope.mkv");

        var writer = new SRSWriter();
        SRSCreationResult result = await writer.CreateAsync(destination, missingSample);

        Assert.False(result.Success);
        AssertIntact(destination, "the missing-sample check throws before anything is written");
    }

    [Fact]
    public async Task CreateAsync_OutputEqualsTheSample_IsRejectedAndLeavesTheSampleIntact()
    {
        string sample = WriteStreamSample();
        byte[] before = await File.ReadAllBytesAsync(sample);

        var writer = new SRSWriter();
        SRSCreationResult result = await writer.CreateAsync(sample, sample);

        Assert.False(result.Success);
        Assert.True(File.Exists(sample), "the sample being described was deleted.");
        Assert.Equal(before, await File.ReadAllBytesAsync(sample));
    }

    [Fact]
    public async Task CreateAsync_Success_StillReplacesAPreExistingDestination()
    {
        string sample = WriteStreamSample();
        string destination = WriteSentinel("already-here.srs");

        var writer = new SRSWriter();
        SRSCreationResult result = await writer.CreateAsync(destination, sample);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotEqual(Sentinel, await File.ReadAllTextAsync(destination, Encoding.UTF8));
    }

    [Fact]
    public async Task CreateAsync_FailureDuringCommit_LeavesNoStagingFileBehind()
    {
        // Reaching the far side of ReserveStagingPath is the whole point, and it is fiddlier here
        // than in the SRR writer. A missing sample throws during validation; a pre-cancelled token
        // makes Task.Run(handler.Profile, ct) throw WITHOUT running — both land before the staging
        // file exists, so asserting on leftovers there proves nothing.
        //
        // A DIRECTORY sitting at the destination path is the cheap failure that lands after the
        // staging file has been fully written: the final File.Move cannot replace a directory.
        string sample = WriteStreamSample();
        string destination = Path.Combine(TempDir, "out.srs");
        Directory.CreateDirectory(destination);

        var writer = new SRSWriter();
        SRSCreationResult result = await writer.CreateAsync(destination, sample);

        Assert.False(result.Success);
        Assert.Empty(Directory.GetFiles(TempDir, "out.srs.tmp-*"));
    }

    [Fact]
    public async Task RebuildAsync_MissingSrs_LeavesAPreExistingDestinationIntact()
    {
        string destination = WriteSentinel("already-here.mkv");
        string media = WriteStreamSample("media.vob");

        var rebuilder = new SRSRebuilder();
        SRSReconstructionResult result = await rebuilder.RebuildAsync(
            Path.Combine(TempDir, "nope.srs"), media, destination);

        Assert.False(result.Success);
        AssertIntact(destination, "the missing-SRS check throws before anything is written");
    }

    [Fact]
    public async Task RebuildAsync_MissingMedia_LeavesAPreExistingDestinationIntact()
    {
        string destination = WriteSentinel("already-here.mkv");
        string srs = Path.Combine(TempDir, "some.srs");
        await File.WriteAllTextAsync(srs, "not really an srs");

        var rebuilder = new SRSRebuilder();
        SRSReconstructionResult result = await rebuilder.RebuildAsync(
            srs, Path.Combine(TempDir, "nope.mkv"), destination);

        Assert.False(result.Success);
        AssertIntact(destination, "the missing-media check throws before anything is written");
    }

    /// <summary>Builds a REAL SRS describing <paramref name="sample"/>, so the rebuild path runs.</summary>
    private async Task<string> WriteRealSrsAsync(string sample)
    {
        string srs = Path.Combine(TempDir, "real.srs");
        SRSCreationResult created = await new SRSWriter().CreateAsync(srs, sample);
        Assert.True(created.Success, created.ErrorMessage);
        return srs;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RebuildAsync_OutputEqualsAnInput_IsRejectedAndLeavesThatInputIntact(bool aliasTheMedia)
    {
        // The SRS here is a REAL one. An earlier version passed a stub containing the literal
        // "not really an srs", so SRSFile.Load threw regardless and the test would have passed
        // with the self-collision guard deleted — it proved nothing about the guard.
        string media = WriteStreamSample("media.vob");
        string srs = await WriteRealSrsAsync(media);

        string aliased = aliasTheMedia ? media : srs;
        byte[] before = await File.ReadAllBytesAsync(aliased);

        var rebuilder = new SRSRebuilder();
        SRSReconstructionResult result = await rebuilder.RebuildAsync(srs, media, aliased);

        Assert.False(result.Success);
        Assert.True(File.Exists(aliased), "the input being read was deleted.");
        Assert.Equal(before, await File.ReadAllBytesAsync(aliased));
    }
}
