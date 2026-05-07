namespace ReScene.SRS;

internal interface IContainerHandler
{
    public SRSContainerType ContainerType { get; }
    public (List<TrackInfo> Tracks, uint CRC32, long TotalSize) Profile(string samplePath, CancellationToken ct);
    public void WriteSrs(string outputPath, string samplePath, List<TrackInfo> tracks, long sampleSize, uint sampleCrc32, SRSCreationOptions options, CancellationToken ct);
}
