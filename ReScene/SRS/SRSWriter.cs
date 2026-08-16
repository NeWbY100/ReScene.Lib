using System.Text;

namespace ReScene.SRS;

/// <summary>
/// Creates SRS (Sample ReScene) files from media sample files.
/// Supports AVI, MKV, MP4, WMV, FLAC, MP3, and STREAM container formats.
/// </summary>
public class SRSWriter
{
    private static readonly Dictionary<SRSContainerType, IContainerHandler> _handlers = new()
    {
        { SRSContainerType.AVI, new AVIContainerHandler() },
        { SRSContainerType.MKV, new MKVContainerHandler() },
        { SRSContainerType.MP4, new MP4ContainerHandler() },
        { SRSContainerType.WMV, new WMVContainerHandler() },
        { SRSContainerType.FLAC, new FlacContainerHandler() },
        { SRSContainerType.MP3, new MP3ContainerHandler() },
        { SRSContainerType.Stream, new StreamContainerHandler() }
    };

    /// <summary>
    /// Occurs when SRS creation progress updates with a status message.
    /// </summary>
    public event EventHandler<SRSCreationProgressEventArgs>? Progress;

    /// <summary>
    /// Occurs during sample profiling to report byte-level scan progress
    /// (bytes scanned, total bytes, percent).
    /// </summary>
    public event EventHandler<SRSScanProgressEventArgs>? ScanProgress;

    /// <summary>
    /// Creates an SRS file from a sample media file.
    /// </summary>
    /// <param name="outputPath">
    /// The output path for the SRS file.
    /// </param>
    /// <param name="sampleFilePath">
    /// The path to the sample media file.
    /// </param>
    /// <param name="options">
    /// Optional creation options.
    /// </param>
    /// <param name="ct">
    /// Cancellation token.
    /// </param>
    /// <returns>
    /// The creation result containing status and track information.
    /// </returns>
    public async Task<SRSCreationResult> CreateAsync(
        string outputPath,
        string sampleFilePath,
        SRSCreationOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new SRSCreationOptions();
        var result = new SRSCreationResult();
        string stagingPath = string.Empty;
        bool staged = false;

        try
        {
            if (!File.Exists(sampleFilePath))
            {
                throw new FileNotFoundException("Sample file not found.", sampleFilePath);
            }

            long sampleSize = new FileInfo(sampleFilePath).Length;

            SRSContainerType containerType = DetectContainerType(sampleFilePath);
            result.ContainerType = containerType;

            ReportProgress($"Detected container: {containerType}");

            // Create the output directory BEFORE computing the collision key, which resolves
            // through that directory and therefore requires it to exist.
            string? outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            // Writing the SRS over its own sample would destroy the very file being described —
            // and the Stream handler would happily do it and report success.
            DestinationTransaction.RejectIfMatches(
                DestinationTransaction.ComputeKey(outputPath), [sampleFilePath], "the sample file");

            if (!_handlers.TryGetValue(containerType, out IContainerHandler? handler))
            {
                throw new NotSupportedException($"Container type {containerType} is not supported.");
            }

            // Profile the sample to extract tracks and CRC
            ReportProgress("Profiling sample...");
            (List<TrackInfo>? tracks, uint crc32, long totalSize) = await Task.Run(
                () => handler.Profile(sampleFilePath, ReportScanProgress, ct), ct).ConfigureAwait(false);

            if (tracks.Count == 0)
            {
                throw new InvalidDataException("No A/V track data found. The sample may be corrupted.");
            }

            if (totalSize != sampleSize)
            {
                result.Warnings.Add(
                    $"Parsed size ({totalSize:N0}) does not match file size ({sampleSize:N0}). " +
                    "The sample may be corrupted or incomplete.");
            }

            result.SampleCRC32 = crc32;
            result.SampleSize = sampleSize;
            result.TrackCount = tracks.Count;

            // Optionally verify against the main file to populate per-track
            // MatchOffset values (mirrors pyrescene's -c flag).
            if (!string.IsNullOrWhiteSpace(options.MainFilePath))
            {
                if (!File.Exists(options.MainFilePath))
                {
                    result.Warnings.Add(
                        $"Main file not found: {options.MainFilePath}. Match offsets will be 0.");
                }
                else
                {
                    ReportProgress($"Verifying sample against main file: {Path.GetFileName(options.MainFilePath)}");
                    await Task.Run(() => VerifyAgainstMainFile(
                        options.MainFilePath, containerType, tracks, result, ct), ct).ConfigureAwait(false);
                }
            }

            // Write the SRS file into a staging file beside the destination, so a failure here
            // leaves a pre-existing destination byte-for-byte unchanged.
            ReportProgress("Writing SRS file...");
            stagingPath = DestinationTransaction.ReserveStagingPath(outputPath);
            staged = true;
            await Task.Run(() => handler.WriteSRS(
                stagingPath, sampleFilePath,
                tracks, sampleSize, crc32, options, ct), ct).ConfigureAwait(false);

            // Size published only after the commit succeeds: a failed commit creates no file, and
            // reporting a size for one would contradict what SRSFileSize documents.
            long producedSize = new FileInfo(stagingPath).Length;
            DestinationTransaction.Commit(stagingPath, outputPath);
            result.SRSFileSize = producedSize;
            result.OutputPath = outputPath;
            result.Success = true;

            // The commit must be the LAST fallible action affecting the result — a throwing
            // Progress subscriber here must not flip an already-committed success into an error.
            try
            {
                ReportProgress("SRS creation complete.");
            }
            catch
            {
                // Intentionally ignored — see comment above.
            }
        }
        catch (OperationCanceledException)
        {
            result.ErrorMessage = "Operation was cancelled.";
            if (staged)
            {
                StreamUtilities.TryDeleteFile(stagingPath);
            }
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            if (staged)
            {
                StreamUtilities.TryDeleteFile(stagingPath);
            }
        }

        return result;
    }

    #region Container Detection

    public static SRSContainerType DetectContainerType(string filePath)
    {
        Span<byte> magic = stackalloc byte[16];
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        int read = fs.Read(magic);
        if (read < 4)
        {
            throw new InvalidDataException("File too small to detect container format.");
        }

        // RIFF (AVI)
        if (Encoding.ASCII.GetString(magic[..4]) == RiffFourCC.Riff)
        {
            // Some old MP3s use RIFF container
            if (filePath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                return SRSContainerType.MP3;
            }

            return SRSContainerType.AVI;
        }

        // MKV/EBML
        if (magic[0] == 0x1A && magic[1] == 0x45 && magic[2] == 0xDF && magic[3] == 0xA3)
        {
            return SRSContainerType.MKV;
        }

        // MP4 (ftyp at offset 4)
        if (read >= 8 && Encoding.ASCII.GetString(magic.Slice(4, 4)) == MP4AtomTypes.Ftyp)
        {
            return SRSContainerType.MP4;
        }

        // WMV/ASF
        if (magic.StartsWith(ASFGuids.HeaderObjectPrefix))
        {
            return SRSContainerType.WMV;
        }

        // FLAC
        if (magic[0] == 'f' && magic[1] == 'L' && magic[2] == 'a' && magic[3] == 'C')
        {
            return SRSContainerType.FLAC;
        }

        // ID3 tag (MP3 or FLAC with ID3v2)
        if (magic[0] == MP3Constants.Id3v2Magic[0] && magic[1] == MP3Constants.Id3v2Magic[1] && magic[2] == MP3Constants.Id3v2Magic[2])
        {
            // Check if FLAC follows the ID3 header
            if (read >= MP3TagReader.Id3v2HeaderSize)
            {
                int id3Size = (magic[6] << 21) | (magic[7] << 14) | (magic[8] << 7) | magic[9];
                fs.Position = MP3TagReader.Id3v2HeaderSize + id3Size;
                Span<byte> check = stackalloc byte[4];
                if (fs.Read(check) == 4 &&
                    check[0] == 'f' && check[1] == 'L' && check[2] == 'a' && check[3] == 'C')
                {
                    return SRSContainerType.FLAC;
                }
            }

            return SRSContainerType.MP3;
        }

        // Check extension for stream types BEFORE MP3 sync word check,
        // because VOB files can start with 0xFF bytes which falsely match the sync word.
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext is ".vob" or ".mpeg" or ".mpg" or ".m2ts" or ".ts" or ".m2v" or ".evo")
        {
            return SRSContainerType.Stream;
        }

        // MP4/QuickTime without ftyp atom (older MOV files may start with moov/mdat)
        if (ext is ".mov" or ".m4v")
        {
            return SRSContainerType.MP4;
        }

        // MP3 sync word
        if (magic[0] == MP3Constants.SyncByte0 && (magic[1] & MP3Constants.SyncMask1) == MP3Constants.SyncMask1)
        {
            return SRSContainerType.MP3;
        }

        // Last attempt: ID3v1 at end of file for MP3
        fs.Position = Math.Max(0, fs.Length - 128);
        Span<byte> tail = stackalloc byte[3];
        if (fs.Read(tail) == 3 && tail[0] == 'T' && tail[1] == 'A' && tail[2] == 'G')
        {
            return SRSContainerType.MP3;
        }

        throw new InvalidDataException(
            "Could not detect a supported container format (AVI, MKV, MP4, WMV, FLAC, MP3, STREAM).");
    }

    #endregion

    #region Utilities

    /// <summary>
    /// Locates each track's signature in <paramref name="mainFilePath"/> and
    /// writes the byte offset into <c>TrackInfo.MatchOffset</c>. MKV uses an
    /// EBML walker (handles subtitle-style tracks whose signatures span many
    /// non-contiguous blocks); other containers use a raw byte-signature scan.
    /// Tracks not located keep <c>MatchOffset</c> at 0 and produce a warning.
    /// </summary>
    private void VerifyAgainstMainFile(
        string mainFilePath,
        SRSContainerType containerType,
        List<TrackInfo> tracks,
        SRSCreationResult result,
        CancellationToken ct)
    {
        Dictionary<uint, long> offsets;

        if (containerType == SRSContainerType.MKV)
        {
            var sigs = new Dictionary<uint, byte[]>();
            foreach (TrackInfo t in tracks)
            {
                sigs[(uint)t.TrackNumber] = t.SignatureBytes;
            }

            offsets = MKVContainerRebuilder.FindTrackOffsetsByEBMLWalk(
                mainFilePath,
                sigs,
                reportProgress: null,
                reportScanProgress: (phase, scanned, total, pct)
                    => ReportScanProgress(scanned, total, pct),
                ct);
        }
        else
        {
            offsets = [];
            using var fs = new FileStream(mainFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 80 * 1024);
            long totalLen = fs.Length;
            int lastPercent = -1;

            foreach (TrackInfo t in tracks)
            {
                ct.ThrowIfCancellationRequested();

                if (t.SignatureBytes.Length == 0)
                {
                    offsets[(uint)t.TrackNumber] = 0;
                    continue;
                }

                long found = SignatureScanner.Scan(fs, t.SignatureBytes, 0, fs.Length,
                    (scanned, _, pct) =>
                    {
                        if (pct != lastPercent)
                        {
                            lastPercent = pct;
                            ReportScanProgress(scanned, totalLen, pct);
                        }
                    },
                    ct);

                if (found >= 0)
                {
                    offsets[(uint)t.TrackNumber] = found;
                }
            }
        }

        int matched = 0;
        foreach (TrackInfo t in tracks)
        {
            if (offsets.TryGetValue((uint)t.TrackNumber, out long offset) && offset > 0)
            {
                t.MatchOffset = offset;
                matched++;
            }
        }

        if (matched < tracks.Count(tr => tr.SignatureBytes.Length > 0))
        {
            result.Warnings.Add(
                "Not every track was located in the main file — affected tracks keep MatchOffset = 0.");
        }
    }

    private void ReportProgress(string message) => Progress?.Invoke(this, new SRSCreationProgressEventArgs { Message = message });

    private void ReportScanProgress(long bytesScanned, long bytesTotal, int percent)
        => ScanProgress?.Invoke(this, new SRSScanProgressEventArgs
        {
            Phase = "Profiling sample",
            BytesScanned = bytesScanned,
            BytesTotal = bytesTotal,
            Percent = percent
        });

    #endregion
}
