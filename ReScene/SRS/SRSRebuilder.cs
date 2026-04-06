using System.Buffers.Binary;

namespace ReScene.SRS;

/// <summary>
/// Result of SRS sample reconstruction.
/// </summary>
public record SrsReconstructionResult(
    bool Success,
    bool CrcMatch,
    uint ExpectedCrc,
    uint ActualCrc,
    long ExpectedSize,
    long ActualSize,
    string? ErrorMessage);

/// <summary>
/// Progress event args for SRS reconstruction.
/// </summary>
public class SrsReconstructionProgressEventArgs : EventArgs
{
    /// <summary>
    /// Gets the current phase description (e.g., "Loading SRS", "Rebuilding").
    /// </summary>
    public string Phase { get; init; } = "";

    /// <summary>
    /// Gets the current track number being processed.
    /// </summary>
    public int TrackNumber
    {
        get; init;
    }

    /// <summary>
    /// Gets the total number of tracks to process.
    /// </summary>
    public int TotalTracks
    {
        get; init;
    }

    /// <summary>
    /// Gets the overall progress percentage (0-100).
    /// </summary>
    public double ProgressPercent
    {
        get; init;
    }
}

/// <summary>
/// Progress data for signature scanning operations.
/// </summary>
public class SrsScanProgressEventArgs : EventArgs
{
    /// <summary>
    /// Gets the current phase description.
    /// </summary>
    public string Phase { get; init; } = string.Empty;

    /// <summary>
    /// Gets the bytes scanned so far.
    /// </summary>
    public long BytesScanned { get; init; }

    /// <summary>
    /// Gets the total bytes to scan.
    /// </summary>
    public long BytesTotal { get; init; }

    /// <summary>
    /// Gets the scan progress percentage (0-100).
    /// </summary>
    public int Percent { get; init; }
}

/// <summary>
/// Rebuilds original sample files from an SRS file and the full original media file.
/// Supports AVI, MKV, MP4, WMV, FLAC, MP3, and STREAM container formats.
///
/// Track data is read directly from the media file during rebuild — there is no
/// separate extraction step. Each format-specific rebuilder knows how to locate
/// interleaved track data within the media file's container structure.
/// </summary>
public class SRSRebuilder
{
    private const int SearchBufferSize = 0x10000; // 64 KiB

    private static readonly Dictionary<SRSContainerType, IContainerRebuilder> _rebuilders = new()
    {
        { SRSContainerType.AVI, new AviContainerRebuilder() },
        { SRSContainerType.MKV, new MkvContainerRebuilder() },
        { SRSContainerType.MP4, new Mp4ContainerRebuilder() },
        { SRSContainerType.WMV, new WmvContainerRebuilder() },
        { SRSContainerType.FLAC, new FlacContainerRebuilder() },
        { SRSContainerType.MP3, new Mp3ContainerRebuilder() },
        { SRSContainerType.Stream, new StreamContainerRebuilder() }
    };

    /// <summary>
    /// Occurs when reconstruction progress updates.
    /// </summary>
    public event EventHandler<SrsReconstructionProgressEventArgs>? Progress;

    /// <summary>
    /// Occurs during signature scanning to report scan progress (bytes scanned / total).
    /// </summary>
    public event EventHandler<SrsScanProgressEventArgs>? ScanProgress;

    /// <summary>
    /// Rebuilds the original sample file from an SRS file and the full media file.
    /// </summary>
    /// <param name="srsFilePath">
    /// Path to the .srs file
    /// </param>
    /// <param name="mediaFilePath">
    /// Path to the full original media file
    /// </param>
    /// <param name="outputPath">
    /// Path to write the reconstructed sample
    /// </param>
    /// <param name="ct">
    /// Cancellation token
    /// </param>
    /// <returns>
    /// Reconstruction result with CRC verification status
    /// </returns>
    public async Task<SrsReconstructionResult> RebuildAsync(
        string srsFilePath, string mediaFilePath, string outputPath, CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(() => RebuildCore(srsFilePath, mediaFilePath, outputPath, ct), ct);
        }
        catch (OperationCanceledException)
        {
            StreamUtilities.TryDeleteFile(outputPath);
            return new SrsReconstructionResult(false, false, 0, 0, 0, 0, "Operation was cancelled.");
        }
        catch (Exception ex)
        {
            StreamUtilities.TryDeleteFile(outputPath);
            return new SrsReconstructionResult(false, false, 0, 0, 0, 0, ex.Message);
        }
    }

    private SrsReconstructionResult RebuildCore(
        string srsFilePath, string mediaFilePath, string outputPath, CancellationToken ct)
    {
        if (!File.Exists(srsFilePath))
        {
            throw new FileNotFoundException("SRS file not found.", srsFilePath);
        }

        if (!File.Exists(mediaFilePath))
        {
            throw new FileNotFoundException("Media file not found.", mediaFilePath);
        }

        // Step 1: Parse SRS
        ReportProgress("Loading SRS", 0, 0, 0);
        var srs = SRSFile.Load(srsFilePath);

        if (srs.FileData is null)
        {
            throw new InvalidDataException("SRS file does not contain file data (SRSF block).");
        }

        if (srs.Tracks.Count == 0)
        {
            throw new InvalidDataException("SRS file does not contain any track data (SRST blocks).");
        }

        SrsFileDataBlock fileData = srs.FileData;
        List<SrsTrackDataBlock> tracks = srs.Tracks;
        long expectedSize = (long)fileData.SampleSize;
        uint expectedCrc = fileData.Crc32;

        // Build a dictionary keyed by track number for easy lookup
        var trackDict = new Dictionary<uint, SrsTrackDataBlock>();
        foreach (SrsTrackDataBlock track in tracks)
        {
            trackDict[track.TrackNumber] = track;
        }

        // Step 2: Find sample streams (locate signatures in media file)
        ReportProgress("Finding tracks", 0, tracks.Count, 10);
        Dictionary<uint, long> trackOffsets = FindSampleStreams(mediaFilePath, trackDict, ct);

        // Step 3: Rebuild the sample (reads track data directly from media file)
        ReportProgress("Rebuilding", 0, tracks.Count, 40);
        string? outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        RebuildSample(srsFilePath, srs.ContainerType, trackDict,
            mediaFilePath, trackOffsets, outputPath, ct);

        // Step 4: Verify CRC
        ReportProgress("Verifying CRC", 0, tracks.Count, 90);
        long actualSize = new FileInfo(outputPath).Length;
        uint actualCrc = CrcUtility.ComputeFileCrc32(outputPath, ct);

        // The SRSF CRC may be stored in either byte order depending on the tool
        // that created the SRS. Check both the direct value and byte-reversed.
        bool crcMatch = actualCrc == expectedCrc
            || actualCrc == BinaryPrimitives.ReverseEndianness(expectedCrc);
        bool sizeMatch = actualSize == expectedSize;

        ReportProgress("Complete", 0, tracks.Count, 100);

        return new SrsReconstructionResult(
            Success: crcMatch && sizeMatch,
            CrcMatch: crcMatch,
            ExpectedCrc: expectedCrc,
            ActualCrc: actualCrc,
            ExpectedSize: expectedSize,
            ActualSize: actualSize,
            ErrorMessage: !crcMatch
                ? $"CRC mismatch: expected 0x{expectedCrc:X8}, got 0x{actualCrc:X8} (size: {actualSize:N0}/{expectedSize:N0})"
                : !sizeMatch
                    ? $"Size mismatch: expected {expectedSize:N0}, got {actualSize:N0}"
                    : null);
    }

    #region Find Sample Streams

    /// <summary>
    /// Locates each track's signature in the media file and returns the found offsets.
    /// </summary>
    private Dictionary<uint, long> FindSampleStreams(
        string mediaFilePath,
        Dictionary<uint, SrsTrackDataBlock> tracks,
        CancellationToken ct)
    {
        var offsets = new Dictionary<uint, long>();

        using var fs = new FileStream(mediaFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 80 * 1024);

        int trackIndex = 0;
        foreach ((uint trackNumber, SrsTrackDataBlock? track) in tracks)
        {
            ct.ThrowIfCancellationRequested();
            trackIndex++;
            ReportProgress("Finding tracks", trackIndex, tracks.Count,
                10 + 20.0 * trackIndex / tracks.Count);

            if (track.Signature.Length == 0)
            {
                offsets[trackNumber] = 0;
                continue;
            }

            long foundOffset = FindSignature(fs, track.Signature, (long)track.MatchOffset, ct);

            if (foundOffset < 0)
            {
                throw new InvalidDataException(
                    $"Unable to locate track signature for track {trackNumber} in the media file.");
            }

            offsets[trackNumber] = foundOffset;
        }

        return offsets;
    }

    /// <summary>
    /// Searches for a byte signature in a stream. Tries the hint offset first,
    /// then a nearby window, then a full file scan.
    /// </summary>
    /// <param name="stream">
    /// The stream to search.
    /// </param>
    /// <param name="signature">
    /// The byte signature to find.
    /// </param>
    /// <param name="hintOffset">
    /// The expected offset to try first.
    /// </param>
    /// <param name="ct">
    /// Cancellation token.
    /// </param>
    /// <returns>
    /// The offset where the signature was found, or -1 if not found.
    /// </returns>
    internal long FindSignature(Stream stream, byte[] signature, long hintOffset,
        CancellationToken ct = default)
    {
        if (signature.Length == 0)
        {
            return hintOffset;
        }

        // Try exact hint offset first
        if (hintOffset >= 0)
        {
            ReportScanProgress("Checking hint offset...", 0, stream.Length, 0);
            if (SignatureScanner.MatchesAt(stream, signature, hintOffset))
            {
                return hintOffset;
            }
        }

        // Search nearby: +/- 64KB around the hint offset
        if (hintOffset >= 0)
        {
            ReportScanProgress("Searching near hint offset...", 0, stream.Length, 0);
            long searchStart = Math.Max(0, hintOffset - SearchBufferSize);
            long searchEnd = Math.Min(stream.Length, hintOffset + SearchBufferSize + signature.Length);
            long found = SignatureScanner.Scan(stream, signature, searchStart, searchEnd,
                (scanned, total, pct) => ReportScanProgress("Searching near hint...", scanned, total, pct), ct);
            if (found >= 0)
            {
                return found;
            }
        }

        // Full file scan
        ReportScanProgress("Full file scan...", 0, stream.Length, 0);
        return SignatureScanner.Scan(stream, signature, 0, stream.Length,
            (scanned, total, pct) => ReportScanProgress("Scanning media file...", scanned, total, pct), ct);
    }

    private void ReportScanProgress(string phase, long bytesScanned, long bytesTotal, int percent)
    {
        ScanProgress?.Invoke(this, new SrsScanProgressEventArgs
        {
            Phase = phase,
            BytesScanned = bytesScanned,
            BytesTotal = bytesTotal,
            Percent = percent
        });
    }

    #endregion

    #region Rebuild Sample

    /// <summary>
    /// Rebuilds the sample by dispatching to the appropriate format-specific rebuilder.
    /// </summary>
    private void RebuildSample(
        string srsFilePath,
        SRSContainerType containerType,
        Dictionary<uint, SrsTrackDataBlock> tracks,
        string mediaFilePath,
        Dictionary<uint, long> trackOffsets,
        string outputPath,
        CancellationToken ct)
    {
        if (!_rebuilders.TryGetValue(containerType, out IContainerRebuilder? rebuilder))
        {
            throw new NotSupportedException(
                $"Container type {containerType} is not supported for rebuilding.");
        }

        rebuilder.Rebuild(srsFilePath, tracks, mediaFilePath, trackOffsets, outputPath,
            ReportProgress, ct);
    }

    #endregion

    #region Helpers

    private void ReportProgress(string phase, int trackNumber, int totalTracks, double percent)
    {
        Progress?.Invoke(this, new SrsReconstructionProgressEventArgs
        {
            Phase = phase,
            TrackNumber = trackNumber,
            TotalTracks = totalTracks,
            ProgressPercent = percent
        });
    }

    #endregion
}
