namespace ReScene.SRS;

/// <summary>
/// Interface for format-specific SRS sample rebuilding.
/// </summary>
internal interface IContainerRebuilder
{
    public SRSContainerType ContainerType { get; }

    /// <summary>
    /// Rebuilds the original sample from an SRS file and a media file.
    /// </summary>
    /// <param name="srsFilePath">
    /// Path to the .srs file.
    /// </param>
    /// <param name="tracks">
    /// Track data blocks keyed by track number.
    /// </param>
    /// <param name="mediaFilePath">
    /// Path to the full original media file.
    /// </param>
    /// <param name="trackOffsets">
    /// Found signature offsets keyed by track number.
    /// </param>
    /// <param name="outputPath">
    /// Path to write the reconstructed sample.
    /// </param>
    /// <param name="reportProgress">
    /// Optional callback to report progress (phase, trackNumber, totalTracks, percent).
    /// </param>
    /// <param name="ct">
    /// Cancellation token.
    /// </param>
    public void Rebuild(
        string srsFilePath,
        Dictionary<uint, SrsTrackDataBlock> tracks,
        string mediaFilePath,
        Dictionary<uint, long> trackOffsets,
        string outputPath,
        Action<string, int, int, double>? reportProgress,
        CancellationToken ct);
}
