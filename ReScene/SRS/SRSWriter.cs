namespace ReScene.SRS;

/// <summary>
/// Options for SRS file creation.
/// </summary>
public class SRSCreationOptions
{
    /// <summary>
    /// Application name to embed in the SRS file.
    /// </summary>
    public string AppName { get; set; } = "ReScene.NET";

    /// <summary>
    /// Optional path to the full "main" media file (e.g. the unpacked full
    /// movie) to verify the sample against. When set, the writer locates each
    /// track's signature inside this file and records the offset as
    /// <c>MatchOffset</c> in the SRS — mirroring the scene-tool behaviour
    /// (pyrescene's <c>-c</c> flag). When unset, MatchOffset stays at 0.
    /// </summary>
    public string? MainFilePath
    {
        get; set;
    }
}

/// <summary>
/// Result of SRS file creation.
/// </summary>
public class SRSCreationResult
{
    /// <summary>
    /// Whether SRS creation completed successfully.
    /// </summary>
    public bool Success
    {
        get; set;
    }

    /// <summary>
    /// Path to the created SRS file.
    /// </summary>
    public string? OutputPath
    {
        get; set;
    }

    /// <summary>
    /// Error message if creation failed.
    /// </summary>
    public string? ErrorMessage
    {
        get; set;
    }

    /// <summary>
    /// Detected container type of the sample file.
    /// </summary>
    public SRSContainerType ContainerType
    {
        get; set;
    }

    /// <summary>
    /// Number of tracks found in the sample file.
    /// </summary>
    public int TrackCount
    {
        get; set;
    }

    /// <summary>
    /// Size of the created SRS file in bytes.
    /// </summary>
    public long SRSFileSize
    {
        get; set;
    }

    /// <summary>
    /// CRC32 checksum of the original sample file.
    /// </summary>
    public uint SampleCRC32
    {
        get; set;
    }

    /// <summary>
    /// Size of the original sample file in bytes.
    /// </summary>
    public long SampleSize
    {
        get; set;
    }

    /// <summary>
    /// Non-fatal warnings encountered during creation.
    /// </summary>
    public IList<string> Warnings { get; } = [];
}

/// <summary>
/// Progress event args for SRS creation.
/// </summary>
public class SRSCreationProgressEventArgs : EventArgs
{
    /// <summary>
    /// Gets or sets the progress message describing the current creation step.
    /// </summary>
    public string Message { get; set; } = string.Empty;
}

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

            string? outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            if (!_handlers.TryGetValue(containerType, out IContainerHandler? handler))
            {
                throw new NotSupportedException($"Container type {containerType} is not supported.");
            }

            // Profile the sample to extract tracks and CRC
            ReportProgress("Profiling sample...");
            (List<TrackInfo>? tracks, uint crc32, long totalSize) = await Task.Run(
                () => handler.Profile(sampleFilePath, ReportScanProgress, ct), ct);

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
                        options.MainFilePath, containerType, tracks, result, ct), ct);
                }
            }

            // Write the SRS file
            ReportProgress("Writing SRS file...");
            await Task.Run(() => handler.WriteSRS(
                outputPath, sampleFilePath,
                tracks, sampleSize, crc32, options, ct), ct);

            result.SRSFileSize = new FileInfo(outputPath).Length;
            result.OutputPath = outputPath;
            result.Success = true;

            ReportProgress("SRS creation complete.");
        }
        catch (OperationCanceledException)
        {
            result.ErrorMessage = "Operation was cancelled.";
            StreamUtilities.TryDeleteFile(outputPath);
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            StreamUtilities.TryDeleteFile(outputPath);
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
        if (magic[0] == 'R' && magic[1] == 'I' && magic[2] == 'F' && magic[3] == 'F')
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
        if (read >= 8 && magic[4] == 'f' && magic[5] == 't' && magic[6] == 'y' && magic[7] == 'p')
        {
            return SRSContainerType.MP4;
        }

        // WMV/ASF
        if (magic[0] == 0x30 && magic[1] == 0x26 && magic[2] == 0xB2 && magic[3] == 0x75)
        {
            return SRSContainerType.WMV;
        }

        // FLAC
        if (magic[0] == 'f' && magic[1] == 'L' && magic[2] == 'a' && magic[3] == 'C')
        {
            return SRSContainerType.FLAC;
        }

        // ID3 tag (MP3 or FLAC with ID3v2)
        if (magic[0] == 'I' && magic[1] == 'D' && magic[2] == '3')
        {
            // Check if FLAC follows the ID3 header
            if (read >= 10)
            {
                int id3Size = (magic[6] << 21) | (magic[7] << 14) | (magic[8] << 7) | magic[9];
                fs.Position = 10 + id3Size;
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
        if ((magic[0] & 0xFF) == 0xFF && (magic[1] & 0xE0) == 0xE0)
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
            offsets = new Dictionary<uint, long>();
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
