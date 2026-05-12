namespace ReScene.SRS;

internal interface IContainerHandler
{
    public SRSContainerType ContainerType { get; }
    public (List<TrackInfo> Tracks, uint CRC32, long TotalSize) Profile(
        string samplePath,
        Action<long, long, int>? reportScanProgress,
        CancellationToken ct);
    public void WriteSRS(string outputPath, string samplePath, List<TrackInfo> tracks, long sampleSize, uint sampleCRC32, SRSCreationOptions options, CancellationToken ct);
}
