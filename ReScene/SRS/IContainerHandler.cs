namespace ReScene.SRS;

/// <summary>
/// Shared MP4/ISO-BMFF constants used by the parser, profiler, and rebuilder.
/// </summary>
internal static class MP4Atoms
{
    /// <summary>
    /// Atoms that contain nested child atoms (and so must be descended into) rather than
    /// raw payload. Kept identical across the profiler, parser, and rebuilder so they agree
    /// on the box hierarchy. Deliberately excludes FullBox-style containers such as
    /// <c>meta</c>/<c>ilst</c>, whose 4-byte version/flags prefix would misalign naive recursion.
    /// </summary>
    public static readonly HashSet<string> ContainerAtoms = new(StringComparer.Ordinal)
    {
        "moov", "trak", "mdia", "minf", "stbl", "edts", "udta"
    };
}

internal interface IContainerHandler
{
    public SRSContainerType ContainerType { get; }
    public (List<TrackInfo> Tracks, uint CRC32, long TotalSize) Profile(
        string samplePath,
        Action<long, long, int>? reportScanProgress,
        CancellationToken ct);
    public void WriteSRS(string outputPath, string samplePath, List<TrackInfo> tracks, long sampleSize, uint sampleCRC32, SRSCreationOptions options, CancellationToken ct);
}
