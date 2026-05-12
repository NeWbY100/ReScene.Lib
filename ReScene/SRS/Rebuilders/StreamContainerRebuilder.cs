namespace ReScene.SRS;

/// <summary>
/// Rebuilds a STREAM/VOB sample. The entire file is just the track data,
/// read contiguously from the media file.
/// </summary>
internal class StreamContainerRebuilder : IContainerRebuilder
{
    public SRSContainerType ContainerType => SRSContainerType.Stream;

    public void Rebuild(
        string srsFilePath,
        Dictionary<uint, SRSTrackDataBlock> tracks,
        string mediaFilePath,
        Dictionary<uint, long> trackOffsets,
        string outputPath,
        Action<string, int, int, double>? reportProgress,
        Action<string, long, long, int>? reportScanProgress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var mediaFs = new FileStream(mediaFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 80 * 1024);
        using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);

        if (tracks.TryGetValue(1, out SRSTrackDataBlock? track) && trackOffsets.TryGetValue(1, out long offset))
        {
            mediaFs.Position = offset;
            StreamUtilities.CopyBytes(mediaFs, outFs, (long)track.DataLength);
        }
    }
}
