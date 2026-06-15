using ReScene.Core.Comparison;
using ReScene.SRS;

namespace ReScene.Tests;

/// <summary>
/// Tests that CompareSRSFiles records the per-track properties surfaced (and highlighted) in the
/// Compare view — including Signature Size and Block Size, which were previously not compared.
/// </summary>
public class FileComparerSRSTests
{
    private static SRSFile SrsWithTrack(ushort signatureSize, long blockSize, byte[] signature)
    {
        var srs = new SRSFile();
        srs._tracks.Add(new SRSTrackDataBlock
        {
            TrackNumber = 1,
            DataLength = 1000,
            MatchOffset = 0,
            Flags = 0,
            SignatureSize = signatureSize,
            Signature = signature,
            BlockSize = blockSize,
        });
        return srs;
    }

    [Fact]
    public void CompareSRSFiles_FlagsSignatureSizeAndBlockSize_WhenTheyDiffer()
    {
        SRSFile left = SrsWithTrack(2560, 2582, new byte[2560]);
        SRSFile right = SrsWithTrack(256, 278, new byte[256]);
        var result = new CompareResult();

        FileComparer.CompareSRSFiles(left, right, result);

        FileDifference track = Assert.Single(result.FileDifferences);
        Assert.Contains(track.PropertyDifferences, d => d.PropertyName == "Signature Size");
        Assert.Contains(track.PropertyDifferences, d => d.PropertyName == "Block Size");
    }

    [Fact]
    public void CompareSRSFiles_RecordsNoDifference_ForIdenticalTracks()
    {
        byte[] sig = new byte[256];
        SRSFile left = SrsWithTrack(256, 278, sig);
        SRSFile right = SrsWithTrack(256, 278, (byte[])sig.Clone());
        var result = new CompareResult();

        FileComparer.CompareSRSFiles(left, right, result);

        Assert.Empty(result.FileDifferences);
    }
}
