namespace ReScene.SRS;

/// <summary>
/// Interface for format-specific SRS sample rebuilding.
/// </summary>
internal interface IContainerRebuilder
{
    public SRSContainerType ContainerType { get; }

    /// <summary>
    /// Locates each track's data offset in the media file using container-aware logic.
    /// Returns null to defer to the generic raw byte-signature scan in
    /// <see cref="SRSRebuilder"/>.
    /// </summary>
    /// <param name="mediaFilePath">
    /// Path to the full original media file.
    /// </param>
    /// <param name="tracks">
    /// Track data blocks keyed by track number.
    /// </param>
    /// <param name="reportProgress">
    /// Optional callback to report progress (phase, trackNumber, totalTracks, percent).
    /// </param>
    /// <param name="reportScanProgress">
    /// Optional callback to report scan progress (phase, bytesScanned, bytesTotal, percent).
    /// </param>
    /// <param name="ct">
    /// Cancellation token.
    /// </param>
    /// <returns>
    /// A dictionary of track number to file offset where each track's first matching
    /// frame data begins, or null if the rebuilder does not implement container-aware
    /// signature matching.
    /// </returns>
    public Dictionary<uint, long>? FindSampleStreams(
        string mediaFilePath,
        Dictionary<uint, SRSTrackDataBlock> tracks,
        Action<string, int, int, double>? reportProgress,
        Action<string, long, long, int>? reportScanProgress,
        CancellationToken ct) => null;

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
    /// <param name="reportScanProgress">
    /// Optional callback to report byte-level scan progress while reading the
    /// media file (phase, bytesScanned, bytesTotal, percent).
    /// </param>
    /// <param name="ct">
    /// Cancellation token.
    /// </param>
    public void Rebuild(
        string srsFilePath,
        Dictionary<uint, SRSTrackDataBlock> tracks,
        string mediaFilePath,
        Dictionary<uint, long> trackOffsets,
        string outputPath,
        Action<string, int, int, double>? reportProgress,
        Action<string, long, long, int>? reportScanProgress,
        CancellationToken ct);
}
