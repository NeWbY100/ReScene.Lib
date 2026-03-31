using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace ReScene.SRS;

/// <summary>
/// Options for SRS file creation.
/// </summary>
public class SrsCreationOptions
{
    /// <summary>
    /// Application name to embed in the SRS file.
    /// </summary>
    public string AppName { get; set; } = "ReScene.NET";
}

/// <summary>
/// Result of SRS file creation.
/// </summary>
public class SrsCreationResult
{
    /// <summary>Whether SRS creation completed successfully.</summary>
    public bool Success { get; set; }
    /// <summary>Path to the created SRS file.</summary>
    public string? OutputPath { get; set; }
    /// <summary>Error message if creation failed.</summary>
    public string? ErrorMessage { get; set; }
    /// <summary>Detected container type of the sample file.</summary>
    public SRSContainerType ContainerType { get; set; }
    /// <summary>Number of tracks found in the sample file.</summary>
    public int TrackCount { get; set; }
    /// <summary>Size of the created SRS file in bytes.</summary>
    public long SrsFileSize { get; set; }
    /// <summary>CRC32 checksum of the original sample file.</summary>
    public uint SampleCrc32 { get; set; }
    /// <summary>Size of the original sample file in bytes.</summary>
    public long SampleSize { get; set; }
    /// <summary>Non-fatal warnings encountered during creation.</summary>
    public List<string> Warnings { get; set; } = [];
}

/// <summary>
/// Progress event args for SRS creation.
/// </summary>
public class SrsCreationProgressEventArgs : EventArgs
{
    /// <summary>
    /// Gets or sets the progress message describing the current creation step.
    /// </summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Track information collected during sample profiling.
/// </summary>
internal class TrackInfo
{
    public int TrackNumber { get; set; }
    public long DataLength { get; set; }
    public byte[] SignatureBytes { get; set; } = [];
    public long MatchOffset { get; set; }

    /// <summary>
    /// For MKV tracks: the compression algorithm from ContentCompAlgo.
    /// Null means no compression element present. 3 = header stripping.
    /// </summary>
    public int? CompressionAlgorithm { get; set; }

    /// <summary>
    /// For MKV tracks with header stripping (CompressionAlgorithm == 3):
    /// the stripped header bytes from ContentCompSettings.
    /// </summary>
    public byte[] CompressionSettings { get; set; } = [];
}

/// <summary>
/// Creates SRS (Sample ReScene) files from media sample files.
/// Supports AVI, MKV, MP4, WMV, FLAC, MP3, and STREAM container formats.
/// </summary>
public class SRSWriter
{
    private const int SignatureSize = 256;

    /// <summary>
    /// Occurs when SRS creation progress updates with a status message.
    /// </summary>
    public event EventHandler<SrsCreationProgressEventArgs>? Progress;

    /// <summary>
    /// Creates an SRS file from a sample media file.
    /// </summary>
    /// <param name="outputPath">The output path for the SRS file.</param>
    /// <param name="sampleFilePath">The path to the sample media file.</param>
    /// <param name="options">Optional creation options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The creation result containing status and track information.</returns>
    public async Task<SrsCreationResult> CreateAsync(
        string outputPath,
        string sampleFilePath,
        SrsCreationOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new SrsCreationOptions();
        var result = new SrsCreationResult();

        try
        {
            if (!File.Exists(sampleFilePath))
            {
                throw new FileNotFoundException("Sample file not found.", sampleFilePath);
            }

            long sampleSize = new FileInfo(sampleFilePath).Length;

            var containerType = DetectContainerType(sampleFilePath);
            result.ContainerType = containerType;

            ReportProgress($"Detected container: {containerType}");

            string? outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            // Profile the sample to extract tracks and CRC
            ReportProgress("Profiling sample...");
            var (tracks, crc32, totalSize) = await Task.Run(
                () => ProfileSample(sampleFilePath, containerType, ct), ct);

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

            result.SampleCrc32 = crc32;
            result.SampleSize = sampleSize;
            result.TrackCount = tracks.Count;

            // Write the SRS file
            ReportProgress("Writing SRS file...");
            await Task.Run(() => WriteSrs(
                outputPath, sampleFilePath, containerType,
                tracks, sampleSize, crc32, options, ct), ct);

            result.SrsFileSize = new FileInfo(outputPath).Length;
            result.OutputPath = outputPath;
            result.Success = true;

            ReportProgress("SRS creation complete.");
        }
        catch (OperationCanceledException)
        {
            result.ErrorMessage = "Operation was cancelled.";
            TryDeleteFile(outputPath);
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            TryDeleteFile(outputPath);
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
                    return SRSContainerType.FLAC;
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

    #region Sample Profiling

    private (List<TrackInfo> tracks, uint crc32, long totalSize) ProfileSample(
        string samplePath, SRSContainerType containerType, CancellationToken ct)
    {
        return containerType switch
        {
            SRSContainerType.AVI => ProfileAvi(samplePath, ct),
            SRSContainerType.MKV => ProfileMkv(samplePath, ct),
            SRSContainerType.MP4 => ProfileMp4(samplePath, ct),
            SRSContainerType.WMV => ProfileWmv(samplePath, ct),
            SRSContainerType.FLAC => ProfileFlac(samplePath, ct),
            SRSContainerType.MP3 => ProfileMp3(samplePath, ct),
            SRSContainerType.Stream => ProfileStream(samplePath, ct),
            _ => throw new NotSupportedException($"Container type {containerType} is not supported.")
        };
    }

    /// <summary>
    /// Profile an AVI sample: parse RIFF chunks, accumulate per-track data lengths
    /// and signatures, compute whole-file CRC32.
    /// </summary>
    private (List<TrackInfo> tracks, uint crc32, long totalSize) ProfileAvi(
        string path, CancellationToken ct)
    {
        var trackMap = new Dictionary<int, TrackInfo>();
        long otherLength = 0;
        var crc = new Crc32();

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(fs);

        SRSWriter.ProfileRiffChunks(reader, fs, 0, fs.Length, trackMap, ref otherLength, crc, ct);

        long totalSize = otherLength;
        foreach (var t in trackMap.Values)
        {
            totalSize += t.DataLength;
        }

        Span<byte> hash = stackalloc byte[4];
        crc.GetHashAndReset(hash);
        uint crc32 = BinaryPrimitives.ReadUInt32LittleEndian(hash);

        return (trackMap.Values.ToList(), crc32, totalSize);
    }

    private static void ProfileRiffChunks(
        BinaryReader reader, Stream fs,
        long start, long end,
        Dictionary<int, TrackInfo> trackMap,
        ref long otherLength,
        Crc32 crc,
        CancellationToken ct)
    {
        fs.Position = start;

        while (fs.Position + 8 <= end)
        {
            ct.ThrowIfCancellationRequested();
            long chunkStart = fs.Position;

            byte[] headerBytes = reader.ReadBytes(8);
            if (headerBytes.Length < 8)
            {
                break;
            }

            string fourcc = Encoding.ASCII.GetString(headerBytes, 0, 4);
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes.AsSpan(4));

            otherLength += 8;
            crc.Append(headerBytes);

            if (fourcc is "RIFF" or "LIST")
            {
                // Read 4-byte sub-type
                byte[] subType = reader.ReadBytes(4);
                otherLength += 4;
                crc.Append(subType);

                string listType = Encoding.ASCII.GetString(subType);

                long childEnd = chunkStart + 8 + chunkSize;
                if (childEnd > end)
                {
                    childEnd = end;
                }

                SRSWriter.ProfileRiffChunks(reader, fs, fs.Position, childEnd, trackMap, ref otherLength, crc, ct);

                fs.Position = childEnd;
                // Pad to even boundary
                if (chunkSize % 2 != 0 && fs.Position < end)
                {
                    byte[] pad = reader.ReadBytes(1);
                    otherLength += 1;
                    crc.Append(pad);
                }
            }
            else
            {
                // Is this a stream data chunk? (e.g. "00dc", "01wb", etc.)
                bool isMovi = fourcc.Length == 4 &&
                              char.IsDigit(fourcc[0]) && char.IsDigit(fourcc[1]) &&
                              char.IsLetter(fourcc[2]) && char.IsLetter(fourcc[3]);

                if (isMovi)
                {
                    int trackNumber = (fourcc[0] - '0') * 10 + (fourcc[1] - '0');
                    if (!trackMap.TryGetValue(trackNumber, out var track))
                    {
                        track = new TrackInfo { TrackNumber = trackNumber };
                        trackMap[trackNumber] = track;
                    }

                    track.DataLength += chunkSize;

                    // Read chunk data for CRC and signature
                    byte[] moviData = ReadExactly(reader, (int)chunkSize);
                    crc.Append(moviData);

                    // Build signature from first SignatureSize bytes of track data
                    if (track.SignatureBytes.Length < SignatureSize)
                    {
                        int need = SignatureSize - track.SignatureBytes.Length;
                        int take = Math.Min(need, moviData.Length);
                        if (take > 0)
                        {
                            byte[] newSig = new byte[track.SignatureBytes.Length + take];
                            track.SignatureBytes.CopyTo(newSig, 0);
                            Array.Copy(moviData, 0, newSig, track.SignatureBytes.Length, take);
                            track.SignatureBytes = newSig;
                        }
                    }
                }
                else
                {
                    // Non-stream chunk: copy for CRC
                    byte[] data = ReadExactly(reader, (int)chunkSize);
                    otherLength += chunkSize;
                    crc.Append(data);
                }

                // Pad to even boundary
                if (chunkSize % 2 != 0 && fs.Position < end)
                {
                    byte[] pad = reader.ReadBytes(1);
                    otherLength += 1;
                    crc.Append(pad);
                }
            }
        }
    }

    /// <summary>
    /// Profile an MKV sample: parse EBML elements, extract track data and signatures.
    /// </summary>
    private (List<TrackInfo> tracks, uint crc32, long totalSize) ProfileMkv(
        string path, CancellationToken ct)
    {
        var trackMap = new Dictionary<int, TrackInfo>();
        long otherLength = 0;
        var crc = new Crc32();

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        SRSWriter.ProfileEbmlElements(fs, 0, fs.Length, trackMap, ref otherLength, crc, isSegmentLevel: false, ct);

        long totalSize = otherLength;
        foreach (var t in trackMap.Values)
        {
            totalSize += t.DataLength;
        }

        Span<byte> hash = stackalloc byte[4];
        crc.GetHashAndReset(hash);
        uint crc32 = BinaryPrimitives.ReadUInt32LittleEndian(hash);

        return (trackMap.Values.ToList(), crc32, totalSize);
    }

    /// <summary>
    /// Container element IDs that we step into (no data of their own).
    /// </summary>
    private static readonly HashSet<ulong> EbmlContainerElements =
    [
        0x18538067, // Segment
        0x1F43B675, // Cluster
        0x1654AE6B, // Tracks
        0xAE,       // TrackEntry
        0x6D80,     // ContentEncodings
        0x6240,     // ContentEncoding
        0x5034,     // ContentCompression
        0xA0,       // BlockGroup
        0x1941A469, // Attachments
        0x61A7,     // AttachedFile
    ];

    private static readonly ulong EbmlIdBlock = 0xA3;             // SimpleBlock
    private static readonly ulong EbmlIdBlockGroup_Block = 0xA1;  // Block inside BlockGroup
    private static readonly ulong EbmlIdTrackNumber = 0xD7;       // TrackNumber in TrackEntry
    private static readonly ulong EbmlIdContentCompAlgo = 0x4254; // ContentCompAlgo
    private static readonly ulong EbmlIdContentCompSettings = 0x4255; // ContentCompSettings

    /// <summary>
    /// State tracked across recursive ProfileEbmlElements calls for MKV profiling.
    /// Stores the current track number context and header stripping flag during TrackEntry parsing.
    /// </summary>
    private class EbmlProfileState
    {
        public int CurrentTrackNumber { get; set; }
        public bool HeaderStrippingDetected { get; set; }
    }

    private static void ProfileEbmlElements(
        Stream fs, long start, long end,
        Dictionary<int, TrackInfo> trackMap,
        ref long otherLength,
        Crc32 crc,
        bool isSegmentLevel,
        CancellationToken ct,
        EbmlProfileState? state = null)
    {
        state ??= new EbmlProfileState();
        fs.Position = start;

        while (fs.Position < end)
        {
            ct.ThrowIfCancellationRequested();
            long elemStart = fs.Position;

            if (!TryReadEbmlId(fs, out ulong elemId, out int idLen))
            {
                break;
            }

            if (!TryReadEbmlSize(fs, out ulong dataSize, out int sizeLen))
            {
                break;
            }

            int headerSize = idLen + sizeLen;
            long dataStart = fs.Position;
            long elemEnd = Math.Min(dataStart + (long)dataSize, end);

            // CRC the raw header bytes
            fs.Position = elemStart;
            byte[] rawHeader = new byte[headerSize];
            fs.ReadExactly(rawHeader, 0, headerSize);
            otherLength += headerSize;
            crc.Append(rawHeader);

            if (EbmlContainerElements.Contains(elemId))
            {
                // When entering ContentCompression (0x5034), mark that compression is present
                if (elemId == 0x5034 && trackMap.TryGetValue(state.CurrentTrackNumber, out var compTrack))
                {
                    // Mark that a compression element exists (exact algorithm comes from child)
                    compTrack.CompressionAlgorithm ??= -1; // placeholder until we read ContentCompAlgo
                }

                // Step into container element
                SRSWriter.ProfileEbmlElements(fs, dataStart, elemEnd, trackMap, ref otherLength, crc,
                    isSegmentLevel: elemId == 0x18538067 || isSegmentLevel, ct, state);
            }
            else if (elemId == EbmlIdBlock || elemId == EbmlIdBlockGroup_Block)
            {
                // Parse block: track number (EBML VINT) + timecode (2 bytes) + flags (1 byte)
                if (!TryReadEbmlVint(fs, out ulong trackNum, out int vintLen))
                {
                    fs.Position = elemEnd;
                    continue;
                }

                int blockHeaderBase = vintLen + 2 + 1; // VINT + timecode + flags
                if (dataStart + blockHeaderBase > elemEnd)
                {
                    fs.Position = elemEnd;
                    continue;
                }

                // Read the base block header (track VINT + timecode + flags)
                byte[] blockHeader = new byte[blockHeaderBase];
                fs.Position = dataStart;
                fs.ReadExactly(blockHeader, 0, blockHeaderBase);

                // Extract lace type from flags byte (bits 1-2)
                byte flagsByte = blockHeader[blockHeaderBase - 1];
                var laceType = (EbmlLaceType)(flagsByte & 0x06);

                // Calculate remaining data after base block header
                int dataAfterBaseHeader = (int)((long)dataSize - blockHeaderBase);

                // Parse lacing to determine frame sizes and lacing header size
                int lacingHeaderSize = 0;
                if (laceType != EbmlLaceType.None && dataAfterBaseHeader > 0)
                {
                    // Read the lacing header data to parse it
                    byte[] lacingData = ReadExactly(fs, Math.Min(dataAfterBaseHeader, 256)); // lacing headers are small
                    var (_, bytesConsumed) = EbmlLacing.GetFrameLengths(
                        lacingData, laceType, dataAfterBaseHeader);
                    lacingHeaderSize = bytesConsumed;

                    // Seek back to re-read lacing bytes as part of the full block header
                    fs.Position = dataStart + blockHeaderBase;
                }

                // The full block header includes the lacing header
                int fullBlockHeaderSize = blockHeaderBase + lacingHeaderSize;
                if (lacingHeaderSize > 0)
                {
                    // Re-read the full block header for CRC
                    byte[] fullBlockHeader = new byte[fullBlockHeaderSize];
                    fs.Position = dataStart;
                    fs.ReadExactly(fullBlockHeader, 0, fullBlockHeaderSize);
                    otherLength += fullBlockHeaderSize;
                    crc.Append(fullBlockHeader);
                }
                else
                {
                    otherLength += blockHeaderBase;
                    crc.Append(blockHeader);
                }

                int tn = (int)trackNum;
                if (!trackMap.TryGetValue(tn, out var track))
                {
                    track = new TrackInfo { TrackNumber = tn };
                    trackMap[tn] = track;
                }

                long frameDataLen = (long)dataSize - fullBlockHeaderSize;
                track.DataLength += frameDataLen;

                // Read frame data for CRC and signature
                fs.Position = dataStart + fullBlockHeaderSize;
                byte[] frameData = ReadExactly(fs, (int)Math.Min(frameDataLen, elemEnd - fs.Position));
                crc.Append(frameData);

                // Build signature from frame data
                // As pyrescene notes: "we can completely ignore laces, because we know what
                // we're looking for always starts at the beginning"
                if (track.SignatureBytes.Length < SignatureSize)
                {
                    int need = SignatureSize - track.SignatureBytes.Length;
                    int take = Math.Min(need, frameData.Length);
                    if (take > 0)
                    {
                        byte[] newSig = new byte[track.SignatureBytes.Length + take];
                        track.SignatureBytes.CopyTo(newSig, 0);
                        Array.Copy(frameData, 0, newSig, track.SignatureBytes.Length, take);
                        track.SignatureBytes = newSig;
                    }
                }
            }
            else if (elemId == EbmlIdTrackNumber)
            {
                // Read TrackNumber element to track current context
                long remaining = elemEnd - fs.Position;
                if (remaining > 0)
                {
                    byte[] data = ReadExactly(fs, (int)remaining);
                    otherLength += remaining;
                    crc.Append(data);

                    // Parse track number (big-endian unsigned int)
                    int trackNumber = 0;
                    for (int i = 0; i < data.Length; i++)
                    {
                        trackNumber = (trackNumber << 8) | data[i];
                    }

                    state.CurrentTrackNumber = trackNumber;

                    if (!trackMap.ContainsKey(trackNumber))
                    {
                        trackMap[trackNumber] = new TrackInfo { TrackNumber = trackNumber };
                    }
                }
            }
            else if (elemId == EbmlIdContentCompAlgo)
            {
                // Read compression algorithm
                long remaining = elemEnd - fs.Position;
                if (remaining > 0)
                {
                    byte[] data = ReadExactly(fs, (int)remaining);
                    otherLength += remaining;
                    crc.Append(data);

                    int algorithm = 0;
                    for (int i = 0; i < data.Length; i++)
                    {
                        algorithm = (algorithm << 8) | data[i];
                    }

                    if (trackMap.TryGetValue(state.CurrentTrackNumber, out var track))
                    {
                        track.CompressionAlgorithm = algorithm;
                    }

                    state.HeaderStrippingDetected = algorithm == 3;
                }
            }
            else if (elemId == EbmlIdContentCompSettings)
            {
                // Read compression settings (stripped header bytes)
                long remaining = elemEnd - fs.Position;
                if (remaining > 0)
                {
                    byte[] data = ReadExactly(fs, (int)remaining);
                    otherLength += remaining;
                    crc.Append(data);

                    if (state.HeaderStrippingDetected &&
                        trackMap.TryGetValue(state.CurrentTrackNumber, out var track))
                    {
                        track.CompressionSettings = data;
                    }
                }
            }
            else
            {
                // Metadata element: read and CRC
                long remaining = elemEnd - fs.Position;
                if (remaining > 0)
                {
                    byte[] data = ReadExactly(fs, (int)remaining);
                    otherLength += remaining;
                    crc.Append(data);
                }
            }

            fs.Position = elemEnd;
        }
    }

    /// <summary>
    /// Profile an MP4 sample.
    /// </summary>
    private (List<TrackInfo> tracks, uint crc32, long totalSize) ProfileMp4(
        string path, CancellationToken ct)
    {
        var trackMap = new SortedDictionary<int, TrackInfo>();
        long metaLength = 0;
        long mdatSize = 0;
        var crc = new Crc32();
        int currentTrackId = 0;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        SRSWriter.ProfileMp4Atoms(fs, 0, fs.Length, trackMap, ref metaLength, ref mdatSize, ref currentTrackId, crc, ct);

        // For MP4, we need to reconstruct track data from stbl tables
        // In the simplest case, mdat is one big track
        // pyrescene reads stsc/stco/stsz tables to split mdat into tracks
        // For SRS creation we need track signatures from the mdat

        long totalSize = metaLength + mdatSize;

        Span<byte> hash = stackalloc byte[4];
        crc.GetHashAndReset(hash);
        uint crc32 = BinaryPrimitives.ReadUInt32LittleEndian(hash);

        // If no tracks were populated from stbl parsing, create/update a single track from mdat.
        // A track may already exist from signature gathering (with DataLength == 0).
        if (mdatSize > 0)
        {
            bool needsTrack = trackMap.Count == 0 ||
                              trackMap.Values.All(t => t.DataLength == 0);

            if (needsTrack)
            {
                int trackNum = trackMap.Count > 0 ? trackMap.Keys.First() : 1;
                if (trackMap.TryGetValue(trackNum, out var existing))
                {
                    existing.DataLength = mdatSize;
                }
                else
                {
                    trackMap[trackNum] = new TrackInfo { TrackNumber = trackNum, DataLength = mdatSize };
                }
            }
        }

        return (trackMap.Values.ToList(), crc32, totalSize);
    }

    private static readonly HashSet<string> Mp4ContainerAtoms =
        ["moov", "trak", "mdia", "minf", "stbl", "edts", "udta", "meta", "ilst"];

    private static void ProfileMp4Atoms(
        Stream fs, long start, long end,
        SortedDictionary<int, TrackInfo> trackMap,
        ref long metaLength, ref long mdatSize,
        ref int currentTrackId,
        Crc32 crc,
        CancellationToken ct)
    {
        fs.Position = start;

        while (fs.Position + 8 <= end)
        {
            ct.ThrowIfCancellationRequested();
            long atomStart = fs.Position;

            byte[] header = new byte[8];
            if (fs.Read(header, 0, 8) < 8)
            {
                break;
            }

            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            string type = Encoding.ASCII.GetString(header, 4, 4);

            int headerSize = 8;
            long totalSize;

            if (size32 == 1)
            {
                // Extended 64-bit size
                byte[] ext = new byte[8];
                if (fs.Read(ext, 0, 8) < 8)
                {
                    break;
                }

                totalSize = (long)BinaryPrimitives.ReadUInt64BigEndian(ext);
                headerSize = 16;

                // CRC the full header
                metaLength += 16;
                crc.Append(header);
                crc.Append(ext);
            }
            else if (size32 == 0)
            {
                totalSize = end - atomStart;
                metaLength += 8;
                crc.Append(header);
            }
            else
            {
                totalSize = size32;
                metaLength += 8;
                crc.Append(header);
            }

            if (totalSize < headerSize)
            {
                break;
            }

            long payloadSize = totalSize - headerSize;
            long atomEnd = atomStart + totalSize;
            if (atomEnd > end)
            {
                atomEnd = end;
            }

            if (type == "mdat")
            {
                mdatSize += payloadSize;
                // Read mdat for CRC and extract track signatures
                // For simplicity, read and CRC. pyrescene uses stbl data to assign to tracks.
                // We'll use the whole mdat as track data and build signatures from it.
                long dataRemaining = atomEnd - fs.Position;
                bool signatureDone = trackMap.Values.All(t => t.SignatureBytes.Length >= SignatureSize);

                // Read mdat in chunks for CRC
                byte[] buffer = new byte[80 * 1024];
                long bytesRead = 0;
                while (bytesRead < dataRemaining)
                {
                    int toRead = (int)Math.Min(buffer.Length, dataRemaining - bytesRead);
                    int actualRead = fs.Read(buffer, 0, toRead);
                    if (actualRead <= 0)
                    {
                        break;
                    }

                    crc.Append(buffer.AsSpan(0, actualRead));

                    // Build signature for track 1 if we haven't from stbl parsing
                    if (trackMap.Count == 0 || !signatureDone)
                    {
                        int trackNum = trackMap.Count > 0 ? trackMap.Keys.First() : 1;
                        if (!trackMap.TryGetValue(trackNum, out var track))
                        {
                            track = new TrackInfo { TrackNumber = trackNum };
                            trackMap[trackNum] = track;
                        }

                        if (track.SignatureBytes.Length < SignatureSize)
                        {
                            int need = SignatureSize - track.SignatureBytes.Length;
                            int take = Math.Min(need, actualRead);
                            byte[] newSig = new byte[track.SignatureBytes.Length + take];
                            track.SignatureBytes.CopyTo(newSig, 0);
                            Array.Copy(buffer, 0, newSig, track.SignatureBytes.Length, take);
                            track.SignatureBytes = newSig;
                        }
                    }

                    bytesRead += actualRead;
                }
            }
            else if (type == "tkhd" && payloadSize >= 12)
            {
                // Track header: extract track ID for MP4 track mapping
                byte[] data = ReadExactly(fs, (int)(atomEnd - fs.Position));
                crc.Append(data);
                // Track ID is at offset 12 (version 0) or 20 (version 1)
                int version = data[0];
                int trackIdOffset = version == 1 ? 19 : 11;
                if (trackIdOffset + 4 <= data.Length)
                {
                    currentTrackId = (int)BinaryPrimitives.ReadUInt32BigEndian(
                        data.AsSpan(trackIdOffset, 4));
                    if (!trackMap.ContainsKey(currentTrackId))
                    {
                        trackMap[currentTrackId] = new TrackInfo { TrackNumber = currentTrackId };
                    }
                }
            }
            else if (Mp4ContainerAtoms.Contains(type))
            {
                // Step into children
                SRSWriter.ProfileMp4Atoms(fs, fs.Position, atomEnd, trackMap, ref metaLength, ref mdatSize,
                    ref currentTrackId, crc, ct);
            }
            else
            {
                // Metadata atom
                long remaining = atomEnd - fs.Position;
                if (remaining > 0)
                {
                    byte[] data = ReadExactly(fs, (int)remaining);
                    metaLength += remaining;
                    crc.Append(data);
                }
            }

            fs.Position = atomEnd;
        }
    }

    /// <summary>
    /// Profile a WMV/ASF sample.
    /// </summary>
    private (List<TrackInfo> tracks, uint crc32, long totalSize) ProfileWmv(
        string path, CancellationToken ct)
    {
        var trackMap = new Dictionary<int, TrackInfo>();
        long totalLength = 0;
        var crc = new Crc32();

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        while (fs.Position + 24 <= fs.Length)
        {
            ct.ThrowIfCancellationRequested();
            long objStart = fs.Position;

            byte[] header = new byte[24];
            if (fs.Read(header, 0, 24) < 24)
            {
                break;
            }

            ulong objSize = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(16));
            if (objSize < 24)
            {
                break;
            }

            totalLength += 24;
            crc.Append(header);

            long dataSize = (long)objSize - 24;
            long objEnd = objStart + (long)objSize;
            if (objEnd > fs.Length)
            {
                objEnd = fs.Length;
            }

            // Check if this is the Data Object (GUID: 3626B2758E66CF11A6D900AA0062CE6C)
            bool isDataObject = header[0] == 0x36 && header[1] == 0x26 && header[2] == 0xB2 && header[3] == 0x75;

            if (isDataObject && dataSize >= 26)
            {
                // Data object has: file ID (16 bytes) + total packets (8 bytes) + reserved (2 bytes)
                byte[] dataHeader = ReadExactly(fs, 26);
                totalLength += 26;
                crc.Append(dataHeader);

                ulong totalPackets = BinaryPrimitives.ReadUInt64LittleEndian(dataHeader.AsSpan(16));
                long packetDataSize = objEnd - fs.Position;

                if (totalPackets > 0 && packetDataSize > 0)
                {
                    int packetSize = (int)(packetDataSize / (long)totalPackets);

                    for (ulong i = 0; i < totalPackets && fs.Position + packetSize <= objEnd; i++)
                    {
                        byte[] packetData = ReadExactly(fs, packetSize);
                        crc.Append(packetData);

                        // Parse ASF packet to find stream number
                        // Minimal parsing: first byte is error correction flags
                        // We'll just use stream 1 for simplicity
                        int streamNum = 1;
                        if (packetData.Length > 5)
                        {
                            // After error correction, property flags byte tells us about the packet
                            // Stream number is in the payload headers
                            // For signature purposes, just accumulate all packet data as one track
                            streamNum = 1;
                        }

                        if (!trackMap.TryGetValue(streamNum, out var track))
                        {
                            track = new TrackInfo { TrackNumber = streamNum };
                            trackMap[streamNum] = track;
                        }

                        track.DataLength += packetSize;

                        if (track.SignatureBytes.Length < SignatureSize)
                        {
                            int need = SignatureSize - track.SignatureBytes.Length;
                            int take = Math.Min(need, packetData.Length);
                            byte[] newSig = new byte[track.SignatureBytes.Length + take];
                            track.SignatureBytes.CopyTo(newSig, 0);
                            Array.Copy(packetData, 0, newSig, track.SignatureBytes.Length, take);
                            track.SignatureBytes = newSig;
                        }
                    }
                }

                // Read any remaining
                if (fs.Position < objEnd)
                {
                    byte[] rest = ReadExactly(fs, (int)(objEnd - fs.Position));
                    totalLength += rest.Length;
                    crc.Append(rest);
                }
            }
            else
            {
                // Non-data object: read and CRC
                if (dataSize > 0)
                {
                    byte[] data = ReadExactly(fs, (int)Math.Min(dataSize, objEnd - fs.Position));
                    totalLength += data.Length;
                    crc.Append(data);
                }
            }

            fs.Position = objEnd;
        }

        Span<byte> hash = stackalloc byte[4];
        crc.GetHashAndReset(hash);
        uint crc32 = BinaryPrimitives.ReadUInt32LittleEndian(hash);

        return (trackMap.Values.ToList(), crc32, totalLength);
    }

    /// <summary>
    /// Profile a FLAC sample.
    /// </summary>
    private (List<TrackInfo> tracks, uint crc32, long totalSize) ProfileFlac(
        string path, CancellationToken ct)
    {
        var track = new TrackInfo { TrackNumber = 1 };
        long otherLength = 0;
        var crc = new Crc32();

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        // Check for ID3v2 wrapper before fLaC marker
        var (id3Found, id3Size) = FlacMetadataReader.DetectId3v2Wrapper(fs);
        if (id3Found)
        {
            // CRC and account for the ID3v2 wrapper
            fs.Position = 0;
            byte[] id3Data = ReadExactly(fs, id3Size);
            otherLength += id3Size;
            crc.Append(id3Data);
        }
        else
        {
            fs.Position = 0;
        }

        // Read fLaC marker
        byte[] marker = new byte[4];
        fs.ReadExactly(marker, 0, 4);
        otherLength += 4;
        crc.Append(marker);

        using var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

        while (fs.Position + 4 <= fs.Length)
        {
            ct.ThrowIfCancellationRequested();

            var (isLast, blockType, payloadSize) = FlacMetadataReader.ReadMetadataBlockHeader(reader);

            // Reconstruct the 4-byte header for CRC
            byte typeByte = (byte)((isLast ? 0x80 : 0) | blockType);
            byte[] blockHeader =
            [
                typeByte,
                (byte)(payloadSize >> 16),
                (byte)(payloadSize >> 8),
                (byte)payloadSize
            ];
            otherLength += 4;
            crc.Append(blockHeader);

            if (payloadSize > 0)
            {
                byte[] data = ReadExactly(fs, payloadSize);
                crc.Append(data);

                if (blockType > 6)
                {
                    // Non-standard block type; shouldn't happen in well-formed FLAC
                    track.DataLength += payloadSize;
                }
                else
                {
                    otherLength += payloadSize;
                }
            }

            if (isLast)
            {
                // Everything remaining is frame data (may include trailing ID3v1)
                long remaining = fs.Length - fs.Position;
                if (remaining > 0)
                {
                    track.DataLength = remaining;
                    byte[] buffer = new byte[80 * 1024];
                    long totalRead = 0;
                    while (totalRead < remaining)
                    {
                        int toRead = (int)Math.Min(buffer.Length, remaining - totalRead);
                        int actualRead = fs.Read(buffer, 0, toRead);
                        if (actualRead <= 0)
                        {
                            break;
                        }

                        crc.Append(buffer.AsSpan(0, actualRead));

                        if (track.SignatureBytes.Length < SignatureSize)
                        {
                            int need = SignatureSize - track.SignatureBytes.Length;
                            int take = Math.Min(need, actualRead);
                            byte[] newSig = new byte[track.SignatureBytes.Length + take];
                            track.SignatureBytes.CopyTo(newSig, 0);
                            Array.Copy(buffer, 0, newSig, track.SignatureBytes.Length, take);
                            track.SignatureBytes = newSig;
                        }

                        totalRead += actualRead;
                    }
                }

                break;
            }
        }

        long totalSize = otherLength + track.DataLength;

        Span<byte> hash = stackalloc byte[4];
        crc.GetHashAndReset(hash);
        uint crc32Val = BinaryPrimitives.ReadUInt32LittleEndian(hash);

        return (track.DataLength > 0 ? [track] : [], crc32Val, totalSize);
    }

    /// <summary>
    /// Profile an MP3 sample.
    /// </summary>
    private (List<TrackInfo> tracks, uint crc32, long totalSize) ProfileMp3(
        string path, CancellationToken ct)
    {
        var track = new TrackInfo { TrackNumber = 1 };
        var crc = new Crc32();

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long fileLen = fs.Length;

        // Use Mp3TagReader to find audio boundaries (handles ID3v2, ID3v1,
        // Lyrics3v1, Lyrics3v2, APEv1/v2)
        long audioStart = Mp3TagReader.FindAudioStart(fs);
        long audioEnd = Mp3TagReader.FindAudioEnd(fs);

        // Read entire file for CRC
        fs.Position = 0;
        byte[] buffer = new byte[80 * 1024];
        long totalRead = 0;

        while (totalRead < fileLen)
        {
            ct.ThrowIfCancellationRequested();
            int toRead = (int)Math.Min(buffer.Length, fileLen - totalRead);
            int actualRead = fs.Read(buffer, 0, toRead);
            if (actualRead <= 0)
            {
                break;
            }

            crc.Append(buffer.AsSpan(0, actualRead));

            // Build signature from audio data
            if (totalRead + actualRead > audioStart && track.SignatureBytes.Length < SignatureSize)
            {
                long sigStart = Math.Max(audioStart, totalRead);
                int offset = (int)(sigStart - totalRead);
                int available = actualRead - offset;
                if (available > 0)
                {
                    int need = SignatureSize - track.SignatureBytes.Length;
                    int take = Math.Min(need, available);
                    byte[] newSig = new byte[track.SignatureBytes.Length + take];
                    track.SignatureBytes.CopyTo(newSig, 0);
                    Array.Copy(buffer, offset, newSig, track.SignatureBytes.Length, take);
                    track.SignatureBytes = newSig;
                }
            }

            totalRead += actualRead;
        }

        // Track data = audio portion
        track.DataLength = audioEnd - audioStart;

        Span<byte> hash = stackalloc byte[4];
        crc.GetHashAndReset(hash);
        uint crc32Val = BinaryPrimitives.ReadUInt32LittleEndian(hash);

        return (track.DataLength > 0 ? [track] : [], crc32Val, fileLen);
    }

    /// <summary>
    /// Profile a STREAM/VOB/M2TS sample.
    /// </summary>
    private (List<TrackInfo> tracks, uint crc32, long totalSize) ProfileStream(
        string path, CancellationToken ct)
    {
        // For stream types, the entire file is essentially one track
        var track = new TrackInfo { TrackNumber = 1 };
        var crc = new Crc32();

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        byte[] buffer = new byte[80 * 1024];
        long totalRead = 0;
        long fileLen = fs.Length;
        track.DataLength = fileLen;

        while (totalRead < fileLen)
        {
            ct.ThrowIfCancellationRequested();
            int toRead = (int)Math.Min(buffer.Length, fileLen - totalRead);
            int actualRead = fs.Read(buffer, 0, toRead);
            if (actualRead <= 0)
            {
                break;
            }

            crc.Append(buffer.AsSpan(0, actualRead));

            if (track.SignatureBytes.Length < SignatureSize)
            {
                int need = SignatureSize - track.SignatureBytes.Length;
                int take = Math.Min(need, actualRead);
                byte[] newSig = new byte[track.SignatureBytes.Length + take];
                track.SignatureBytes.CopyTo(newSig, 0);
                Array.Copy(buffer, 0, newSig, track.SignatureBytes.Length, take);
                track.SignatureBytes = newSig;
            }

            totalRead += actualRead;
        }

        Span<byte> hash = stackalloc byte[4];
        crc.GetHashAndReset(hash);
        uint crc32Val = BinaryPrimitives.ReadUInt32LittleEndian(hash);

        return ([track], crc32Val, fileLen);
    }

    #endregion

    #region SRS Writing

    private void WriteSrs(
        string outputPath, string samplePath,
        SRSContainerType containerType,
        List<TrackInfo> tracks, long sampleSize, uint sampleCrc32,
        SrsCreationOptions options,
        CancellationToken ct)
    {
        switch (containerType)
        {
            case SRSContainerType.AVI:
                WriteAviSrs(outputPath, samplePath, tracks, sampleSize, sampleCrc32, options, ct);
                break;
            case SRSContainerType.MKV:
                WriteMkvSrs(outputPath, samplePath, tracks, sampleSize, sampleCrc32, options, ct);
                break;
            case SRSContainerType.MP4:
                SRSWriter.WriteMp4Srs(outputPath, samplePath, tracks, sampleSize, sampleCrc32, options, ct);
                break;
            case SRSContainerType.WMV:
                SRSWriter.WriteWmvSrs(outputPath, samplePath, tracks, sampleSize, sampleCrc32, options, ct);
                break;
            case SRSContainerType.FLAC:
                SRSWriter.WriteFlacSrs(outputPath, samplePath, tracks, sampleSize, sampleCrc32, options, ct);
                break;
            case SRSContainerType.MP3:
                SRSWriter.WriteMp3Srs(outputPath, samplePath, tracks, sampleSize, sampleCrc32, options, ct);
                break;
            case SRSContainerType.Stream:
                WriteStreamSrs(outputPath, samplePath, tracks, sampleSize, sampleCrc32, options, ct);
                break;
        }
    }

    // ==================== AVI SRS ====================

    private void WriteAviSrs(
        string outputPath, string samplePath,
        List<TrackInfo> tracks, long sampleSize, uint sampleCrc32,
        SrsCreationOptions options, CancellationToken ct)
    {
        using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var inFs = new FileStream(samplePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(inFs);

        SRSWriter.WriteRiffSrs(outFs, reader, inFs, 0, inFs.Length,
            tracks, samplePath, sampleSize, sampleCrc32, options, moviInjected: false, ct);
    }

    private static void WriteRiffSrs(
        Stream outFs, BinaryReader reader, Stream inFs,
        long start, long end,
        List<TrackInfo> tracks, string samplePath, long sampleSize, uint sampleCrc32,
        SrsCreationOptions options,
        bool moviInjected,
        CancellationToken ct)
    {
        inFs.Position = start;

        while (inFs.Position + 8 <= end)
        {
            ct.ThrowIfCancellationRequested();
            long chunkStart = inFs.Position;

            byte[] header = reader.ReadBytes(8);
            if (header.Length < 8)
            {
                break;
            }

            string fourcc = Encoding.ASCII.GetString(header, 0, 4);
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));

            if (fourcc is "RIFF" or "LIST")
            {
                outFs.Write(header);
                byte[] subType = reader.ReadBytes(4);
                outFs.Write(subType);

                string listType = Encoding.ASCII.GetString(subType);

                // Inject SRSF/SRST as first children of LIST movi
                if (fourcc == "LIST" && listType == "movi" && !moviInjected)
                {
                    WriteSrsfRiff(outFs, samplePath, sampleSize, sampleCrc32, options);
                    foreach (var track in tracks)
                    {
                        WriteSrstRiff(outFs, track, sampleSize >= 0x80000000);
                    }

                    moviInjected = true;
                }

                long childEnd = chunkStart + 8 + chunkSize;
                if (childEnd > end)
                {
                    childEnd = end;
                }

                SRSWriter.WriteRiffSrs(outFs, reader, inFs, inFs.Position, childEnd,
                    tracks, samplePath, sampleSize, sampleCrc32, options, moviInjected, ct);

                inFs.Position = childEnd;
                if (chunkSize % 2 != 0 && inFs.Position < end)
                {
                    byte pad = reader.ReadByte();
                    outFs.WriteByte(pad);
                }
            }
            else
            {
                bool isMovi = fourcc.Length == 4 &&
                              char.IsDigit(fourcc[0]) && char.IsDigit(fourcc[1]) &&
                              char.IsLetter(fourcc[2]) && char.IsLetter(fourcc[3]);

                // Write chunk header for all chunks (movi and non-movi alike)
                // This matches pyrescene's format: headers are preserved, data is stripped for movi
                outFs.Write(header);

                if (isMovi)
                {
                    // Skip stream data (don't copy to SRS, only header is kept)
                    inFs.Seek(chunkSize, SeekOrigin.Current);
                }
                else
                {
                    // Copy metadata chunk data
                    if (chunkSize > 0)
                    {
                        byte[] data = ReadExactly(reader, (int)chunkSize);
                        outFs.Write(data);
                    }
                }

                if (chunkSize % 2 != 0 && inFs.Position < end)
                {
                    byte pad = reader.ReadByte();
                    outFs.WriteByte(pad);
                }
            }
        }
    }

    private static void WriteSrsfRiff(Stream outFs, string samplePath, long sampleSize, uint sampleCrc32,
        SrsCreationOptions options)
    {
        byte[] payload = SerializeSrsf(samplePath, sampleSize, sampleCrc32, options);
        outFs.Write(Encoding.ASCII.GetBytes("SRSF"));
        Span<byte> sizeBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(sizeBytes, (uint)payload.Length);
        outFs.Write(sizeBytes);
        outFs.Write(payload);
        if (payload.Length % 2 != 0)
        {
            outFs.WriteByte(0);
        }
    }

    private static void WriteSrstRiff(Stream outFs, TrackInfo track, bool bigFile)
    {
        byte[] payload = SerializeSrst(track, bigFile);
        outFs.Write(Encoding.ASCII.GetBytes("SRST"));
        Span<byte> sizeBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(sizeBytes, (uint)payload.Length);
        outFs.Write(sizeBytes);
        outFs.Write(payload);
        if (payload.Length % 2 != 0)
        {
            outFs.WriteByte(0);
        }
    }

    // ==================== MKV SRS ====================

    private void WriteMkvSrs(
        string outputPath, string samplePath,
        List<TrackInfo> tracks, long sampleSize, uint sampleCrc32,
        SrsCreationOptions options, CancellationToken ct)
    {
        using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var inFs = new FileStream(samplePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        SRSWriter.WriteMkvSrsElements(outFs, inFs, 0, inFs.Length, tracks, samplePath, sampleSize, sampleCrc32,
            options, resampleInjected: false, ct);
    }

    /// <summary>
    /// EBML element IDs that we should step into (they are containers).
    /// </summary>
    private static readonly HashSet<ulong> MkvSrsContainers =
    [
        0x1F43B675, // Cluster
        0xA0,       // BlockGroup
        0x1941A469, // Attachments
        0x61A7,     // AttachedFile
    ];

    private static void WriteMkvSrsElements(
        Stream outFs, Stream inFs,
        long start, long end,
        List<TrackInfo> tracks, string samplePath, long sampleSize, uint sampleCrc32,
        SrsCreationOptions options,
        bool resampleInjected,
        CancellationToken ct)
    {
        inFs.Position = start;

        while (inFs.Position < end)
        {
            ct.ThrowIfCancellationRequested();
            long elemStart = inFs.Position;

            if (!TryReadEbmlId(inFs, out ulong elemId, out int idLen))
            {
                break;
            }

            if (!TryReadEbmlSize(inFs, out ulong dataSize, out int sizeLen))
            {
                break;
            }

            int headerSize = idLen + sizeLen;
            long dataStart = inFs.Position;
            long elemEnd = Math.Min(dataStart + (long)dataSize, end);

            // Read raw header
            inFs.Position = elemStart;
            byte[] rawHeader = new byte[headerSize];
            inFs.ReadExactly(rawHeader, 0, headerSize);

            if (elemId == 0x18538067) // Segment
            {
                outFs.Write(rawHeader);

                // Inject ReSample element
                if (!resampleInjected)
                {
                    WriteEbmlReSampleElement(outFs, tracks, samplePath, sampleSize, sampleCrc32, options);
                    resampleInjected = true;
                }

                SRSWriter.WriteMkvSrsElements(outFs, inFs, dataStart, elemEnd, tracks, samplePath, sampleSize,
                    sampleCrc32, options, resampleInjected, ct);
            }
            else if (MkvSrsContainers.Contains(elemId))
            {
                outFs.Write(rawHeader);
                SRSWriter.WriteMkvSrsElements(outFs, inFs, dataStart, elemEnd, tracks, samplePath, sampleSize,
                    sampleCrc32, options, resampleInjected, ct);
            }
            else if (elemId == 0x465C) // AttachedFileData - skip data
            {
                outFs.Write(rawHeader);
                // Skip attachment data
            }
            else if (elemId == EbmlIdBlock || elemId == EbmlIdBlockGroup_Block)
            {
                // Write header + block header (including lacing header), skip frame data
                outFs.Write(rawHeader);

                // Parse and copy block header: track number VINT + timecode(2) + flags(1) + lacing header
                long blockParseStart = inFs.Position;
                if (TryReadEbmlVint(inFs, out _, out int vintLen))
                {
                    int blockHeaderBase = vintLen + 2 + 1; // VINT + timecode(2) + flags(1)
                    long available = elemEnd - blockParseStart;
                    if (blockHeaderBase <= available)
                    {
                        // Read the base block header to extract lace type from flags
                        inFs.Position = blockParseStart;
                        byte[] baseHeader = new byte[blockHeaderBase];
                        inFs.ReadExactly(baseHeader, 0, blockHeaderBase);

                        byte flagsByte = baseHeader[blockHeaderBase - 1];
                        var laceType = (EbmlLaceType)(flagsByte & 0x06);

                        int lacingHeaderSize = 0;
                        if (laceType != EbmlLaceType.None)
                        {
                            // Read lacing header to determine its size
                            int dataAfterBase = (int)((long)dataSize - blockHeaderBase);
                            if (dataAfterBase > 0)
                            {
                                byte[] lacingPeek = ReadExactly(inFs, Math.Min(dataAfterBase, 256));
                                var (_, bytesConsumed) = EbmlLacing.GetFrameLengths(
                                    lacingPeek, laceType, dataAfterBase);
                                lacingHeaderSize = bytesConsumed;
                            }
                        }

                        // Re-read and write the full block header (base + lacing)
                        int fullBlockHeaderSize = blockHeaderBase + lacingHeaderSize;
                        inFs.Position = blockParseStart;
                        byte[] fullBlockHeader = new byte[fullBlockHeaderSize];
                        inFs.ReadExactly(fullBlockHeader, 0, fullBlockHeaderSize);
                        outFs.Write(fullBlockHeader);
                    }
                }
                // Skip remaining frame data
            }
            else
            {
                // Metadata: copy verbatim
                outFs.Write(rawHeader);
                long remaining = elemEnd - inFs.Position;
                if (remaining > 0)
                {
                    CopyStream(inFs, outFs, remaining);
                }
            }

            inFs.Position = elemEnd;
        }
    }

    private static void WriteEbmlReSampleElement(
        Stream outFs, List<TrackInfo> tracks,
        string samplePath, long sampleSize, uint sampleCrc32,
        SrsCreationOptions options)
    {
        // Build the file and track sub-elements
        byte[] srsfPayload = SerializeSrsf(samplePath, sampleSize, sampleCrc32, options);
        byte[] srsfElement = BuildEbmlElement(0x6A75, srsfPayload); // RESAMPLE_FILE

        bool bigFile = sampleSize >= 0x80000000;
        var trackElements = new List<byte[]>();
        foreach (var track in tracks)
        {
            byte[] srstPayload = SerializeSrst(track, bigFile);
            trackElements.Add(BuildEbmlElement(0x6B75, srstPayload)); // RESAMPLE_TRACK
        }

        // Total child size
        long childSize = srsfElement.Length;
        foreach (var te in trackElements) childSize += te.Length;

        // Write the ReSample container element (ID: 0x1F697576)
        byte[] resampleHeader = BuildEbmlElementHeader(0x1F697576, childSize);
        outFs.Write(resampleHeader);
        outFs.Write(srsfElement);
        foreach (var te in trackElements)
        {
            outFs.Write(te);
        }
    }

    // ==================== MP4 SRS ====================

    private static void WriteMp4Srs(
        string outputPath, string samplePath,
        List<TrackInfo> tracks, long sampleSize, uint sampleCrc32,
        SrsCreationOptions options, CancellationToken ct)
    {
        using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var inFs = new FileStream(samplePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        while (inFs.Position + 8 <= inFs.Length)
        {
            ct.ThrowIfCancellationRequested();
            long atomStart = inFs.Position;

            byte[] header = new byte[8];
            inFs.ReadExactly(header, 0, 8);

            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            string type = Encoding.ASCII.GetString(header, 4, 4);

            int headerSize = 8;
            long totalSize;
            byte[]? extHeader = null;

            if (size32 == 1 && inFs.Position + 8 <= inFs.Length)
            {
                extHeader = new byte[8];
                inFs.ReadExactly(extHeader, 0, 8);
                totalSize = (long)BinaryPrimitives.ReadUInt64BigEndian(extHeader);
                headerSize = 16;
            }
            else if (size32 == 0)
            {
                totalSize = inFs.Length - atomStart;
            }
            else
            {
                totalSize = size32;
            }

            if (totalSize < headerSize)
            {
                break;
            }

            long atomEnd = atomStart + totalSize;
            if (atomEnd > inFs.Length)
            {
                atomEnd = inFs.Length;
            }

            if (type == "mdat")
            {
                // Inject SRSF/SRST before mdat
                WriteSrsfMov(outFs, samplePath, sampleSize, sampleCrc32, options);
                foreach (var track in tracks)
                {
                    WriteSrstMov(outFs, track, sampleSize >= 0x80000000);
                }

                // Write mdat header only (skip stream data)
                outFs.Write(header);
                if (extHeader != null)
                {
                    outFs.Write(extHeader);
                }
            }
            else
            {
                // Copy atom verbatim
                outFs.Write(header);
                if (extHeader != null)
                {
                    outFs.Write(extHeader);
                }

                long payloadSize = atomEnd - inFs.Position;
                if (payloadSize > 0)
                {
                    CopyStream(inFs, outFs, payloadSize);
                }
            }

            inFs.Position = atomEnd;
        }
    }

    private static void WriteSrsfMov(Stream outFs, string samplePath, long sampleSize, uint sampleCrc32,
        SrsCreationOptions options)
    {
        byte[] payload = SerializeSrsf(samplePath, sampleSize, sampleCrc32, options);
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(payload.Length + 8));
        header[4] = (byte)'S'; header[5] = (byte)'R'; header[6] = (byte)'S'; header[7] = (byte)'F';
        outFs.Write(header);
        outFs.Write(payload);
    }

    private static void WriteSrstMov(Stream outFs, TrackInfo track, bool bigFile)
    {
        byte[] payload = SerializeSrst(track, bigFile);
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(payload.Length + 8));
        header[4] = (byte)'S'; header[5] = (byte)'R'; header[6] = (byte)'S'; header[7] = (byte)'T';
        outFs.Write(header);
        outFs.Write(payload);
    }

    // ==================== WMV/ASF SRS ====================

    private static readonly byte[] GuidSrsFile = Encoding.ASCII.GetBytes("SRSFSRSFSRSFSRSF");
    private static readonly byte[] GuidSrsTrack = Encoding.ASCII.GetBytes("SRSTSRSTSRSTSRST");

    private static void WriteWmvSrs(
        string outputPath, string samplePath,
        List<TrackInfo> tracks, long sampleSize, uint sampleCrc32,
        SrsCreationOptions options, CancellationToken ct)
    {
        using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var inFs = new FileStream(samplePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        while (inFs.Position + 24 <= inFs.Length)
        {
            ct.ThrowIfCancellationRequested();
            long objStart = inFs.Position;

            byte[] header = new byte[24];
            inFs.ReadExactly(header, 0, 24);

            ulong objSize = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(16));
            if (objSize < 24)
            {
                break;
            }

            long objEnd = objStart + (long)objSize;
            if (objEnd > inFs.Length)
            {
                objEnd = inFs.Length;
            }

            // Check if Data Object
            bool isDataObject = header[0] == 0x36 && header[1] == 0x26 && header[2] == 0xB2 && header[3] == 0x75;

            outFs.Write(header);

            if (isDataObject)
            {
                // Parse data packets, write only packet headers (strip payload data)
                long dataRemaining = objEnd - inFs.Position;
                if (dataRemaining >= 26)
                {
                    byte[] dataHeader = new byte[26];
                    inFs.ReadExactly(dataHeader, 0, 26);
                    outFs.Write(dataHeader);

                    ulong totalPackets = BinaryPrimitives.ReadUInt64LittleEndian(dataHeader.AsSpan(16));
                    long packetDataSize = objEnd - inFs.Position;
                    if (totalPackets > 0 && packetDataSize > 0)
                    {
                        int packetSize = (int)(packetDataSize / (long)totalPackets);
                        for (ulong i = 0; i < totalPackets && inFs.Position + packetSize <= objEnd; i++)
                        {
                            // Read packet, parse header, write only header portion
                            byte[] packet = new byte[packetSize];
                            inFs.ReadExactly(packet, 0, packetSize);

                            // For ASF, we'd need full packet parsing to separate headers from payload
                            // pyrescene does asf_data_get_packet for this
                            // For now, skip data packets entirely
                            // The SRS will have the ASF header objects + SRSF/SRST
                        }
                    }
                }

                // Skip to end of data object
                inFs.Position = objEnd;

                // Inject SRSF/SRST after data object
                WriteSrsfAsf(outFs, samplePath, sampleSize, sampleCrc32, options);
                foreach (var track in tracks)
                {
                    WriteSrstAsf(outFs, track, sampleSize >= 0x80000000);
                }
            }
            else
            {
                // Copy object verbatim
                long remaining = objEnd - inFs.Position;
                if (remaining > 0)
                {
                    CopyStream(inFs, outFs, remaining);
                }
            }

            inFs.Position = objEnd;
        }
    }

    private static void WriteSrsfAsf(Stream outFs, string samplePath, long sampleSize, uint sampleCrc32,
        SrsCreationOptions options)
    {
        byte[] payload = SerializeSrsf(samplePath, sampleSize, sampleCrc32, options);
        outFs.Write(GuidSrsFile);
        Span<byte> sizeBytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(sizeBytes, (ulong)(payload.Length + 16 + 8));
        outFs.Write(sizeBytes);
        outFs.Write(payload);
    }

    private static void WriteSrstAsf(Stream outFs, TrackInfo track, bool bigFile)
    {
        byte[] payload = SerializeSrst(track, bigFile);
        outFs.Write(GuidSrsTrack);
        Span<byte> sizeBytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(sizeBytes, (ulong)(payload.Length + 16 + 8));
        outFs.Write(sizeBytes);
        outFs.Write(payload);
    }

    // ==================== FLAC SRS ====================

    private static void WriteFlacSrs(
        string outputPath, string samplePath,
        List<TrackInfo> tracks, long sampleSize, uint sampleCrc32,
        SrsCreationOptions options, CancellationToken ct)
    {
        using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var inFs = new FileStream(samplePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(inFs);

        // Check for ID3v2 wrapper before fLaC marker
        var (id3Found, id3Size) = FlacMetadataReader.DetectId3v2Wrapper(inFs);
        if (id3Found)
        {
            // Copy the ID3v2 wrapper verbatim
            inFs.Position = 0;
            byte[] id3Data = ReadExactly(reader, id3Size);
            outFs.Write(id3Data);
        }
        else
        {
            inFs.Position = 0;
        }

        // Copy fLaC marker
        byte[] marker = reader.ReadBytes(4);
        outFs.Write(marker);

        // Inject SRSF/SRST right after fLaC marker
        WriteSrsfFlac(outFs, samplePath, sampleSize, sampleCrc32, options);
        foreach (var track in tracks)
        {
            WriteSrstFlac(outFs, track, sampleSize >= 0x80000000);
        }

        // Copy metadata blocks, skip frame data
        while (inFs.Position + 4 <= inFs.Length)
        {
            ct.ThrowIfCancellationRequested();

            var (isLast, blockType, payloadSize) = FlacMetadataReader.ReadMetadataBlockHeader(reader);

            // Write block header
            byte typeByte = (byte)((isLast ? 0x80 : 0) | blockType);
            outFs.WriteByte(typeByte);
            outFs.WriteByte((byte)(payloadSize >> 16));
            outFs.WriteByte((byte)(payloadSize >> 8));
            outFs.WriteByte((byte)payloadSize);

            if (payloadSize > 0)
            {
                // Copy metadata block data
                byte[] data = ReadExactly(reader, payloadSize);
                outFs.Write(data);
            }

            if (isLast)
            {
                // Don't copy frame data
                break;
            }
        }
    }

    private static void WriteSrsfFlac(Stream outFs, string samplePath, long sampleSize, uint sampleCrc32,
        SrsCreationOptions options)
    {
        byte[] payload = SerializeSrsf(samplePath, sampleSize, sampleCrc32, options);
        outFs.WriteByte(0x73); // 's' type
        // BE24 size
        outFs.WriteByte((byte)(payload.Length >> 16));
        outFs.WriteByte((byte)(payload.Length >> 8));
        outFs.WriteByte((byte)(payload.Length));
        outFs.Write(payload);
    }

    private static void WriteSrstFlac(Stream outFs, TrackInfo track, bool bigFile)
    {
        byte[] payload = SerializeSrst(track, bigFile);
        outFs.WriteByte(0x74); // 't' type
        // BE24 size
        outFs.WriteByte((byte)(payload.Length >> 16));
        outFs.WriteByte((byte)(payload.Length >> 8));
        outFs.WriteByte((byte)(payload.Length));
        outFs.Write(payload);
    }

    // ==================== MP3 SRS ====================

    private static void WriteMp3Srs(
        string outputPath, string samplePath,
        List<TrackInfo> tracks, long sampleSize, uint sampleCrc32,
        SrsCreationOptions options, CancellationToken ct)
    {
        using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var inFs = new FileStream(samplePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(inFs);

        // Use Mp3TagReader to find audio boundaries
        long audioStart = Mp3TagReader.FindAudioStart(inFs);
        long audioEnd = Mp3TagReader.FindAudioEnd(inFs);

        // Copy all header tags (ID3v2 etc.) verbatim
        if (audioStart > 0)
        {
            inFs.Position = 0;
            byte[] headerTags = ReadExactly(reader, (int)audioStart);
            outFs.Write(headerTags);
        }

        // Write SRSF/SRST blocks (replaces audio data)
        WriteSrsfMp3(outFs, samplePath, sampleSize, sampleCrc32, options);
        foreach (var track in tracks)
        {
            WriteSrstMp3(outFs, track, sampleSize >= 0x80000000);
        }

        // Copy all footer tags (APE, Lyrics3, ID3v1 etc.) verbatim
        long footerSize = inFs.Length - audioEnd;
        if (footerSize > 0)
        {
            inFs.Position = audioEnd;
            byte[] footerTags = ReadExactly(reader, (int)footerSize);
            outFs.Write(footerTags);
        }
    }

    private static void WriteSrsfMp3(Stream outFs, string samplePath, long sampleSize, uint sampleCrc32,
        SrsCreationOptions options)
    {
        byte[] payload = SerializeSrsf(samplePath, sampleSize, sampleCrc32, options);
        outFs.Write(Encoding.ASCII.GetBytes("SRSF"));
        Span<byte> sizeBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(sizeBytes, (uint)(4 + 4 + payload.Length));
        outFs.Write(sizeBytes);
        outFs.Write(payload);
    }

    private static void WriteSrstMp3(Stream outFs, TrackInfo track, bool bigFile)
    {
        byte[] payload = SerializeSrst(track, bigFile);
        outFs.Write(Encoding.ASCII.GetBytes("SRST"));
        Span<byte> sizeBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(sizeBytes, (uint)(8 + payload.Length));
        outFs.Write(sizeBytes);
        outFs.Write(payload);
    }

    // ==================== STREAM SRS ====================

    private void WriteStreamSrs(
        string outputPath, string samplePath,
        List<TrackInfo> tracks, long sampleSize, uint sampleCrc32,
        SrsCreationOptions options, CancellationToken ct)
    {
        using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);

        // Write STREAM marker
        outFs.Write("STRM"u8);
        Span<byte> markerSize = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(markerSize, 8);
        outFs.Write(markerSize);

        // Write SRSF block
        WriteSrsfMp3(outFs, samplePath, sampleSize, sampleCrc32, options);

        // Write SRST blocks
        foreach (var track in tracks)
        {
            WriteSrstMp3(outFs, track, sampleSize >= 0x80000000);
        }
    }

    #endregion

    #region Payload Serialization

    /// <summary>
    /// Serializes the SRSF (file data) payload - format matches pyrescene exactly.
    /// Layout: flags(2) + appNameLen(2) + appName + fileNameLen(2) + fileName + sampleSize(8) + crc32(4)
    /// </summary>
    private static byte[] SerializeSrsf(string samplePath, long sampleSize, uint sampleCrc32,
        SrsCreationOptions options)
    {
        byte[] appNameBytes = Encoding.UTF8.GetBytes(options.AppName);
        byte[] fileNameBytes = Encoding.UTF8.GetBytes(Path.GetFileName(samplePath));

        int totalLen = 2 + 2 + appNameBytes.Length + 2 + fileNameBytes.Length + 8 + 4;
        byte[] buffer = new byte[totalLen];
        int pos = 0;

        // Flags: SIMPLE_BLOCK_FIX | ATTACHMENTS_REMOVED = 0x03
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos), 0x0003);
        pos += 2;

        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos), (ushort)appNameBytes.Length);
        pos += 2;
        appNameBytes.CopyTo(buffer, pos);
        pos += appNameBytes.Length;

        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos), (ushort)fileNameBytes.Length);
        pos += 2;
        fileNameBytes.CopyTo(buffer, pos);
        pos += fileNameBytes.Length;

        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(pos), (ulong)sampleSize);
        pos += 8;

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), sampleCrc32);

        return buffer;
    }

    /// <summary>
    /// Serializes the SRST (track data) payload - format matches pyrescene exactly.
    /// Layout: flags(2) + trackNum(2|4) + dataLength(4|8) + matchOffset(8) + sigLen(2) + sig
    /// </summary>
    private static byte[] SerializeSrst(TrackInfo track, bool bigFile)
    {
        ushort flags = 0;
        bool bigTrackNumber = track.TrackNumber >= 65536;

        if (bigFile)
        {
            flags |= 0x4;
        }

        if (bigTrackNumber)
        {
            flags |= 0x8;
        }

        int trackNumSize = bigTrackNumber ? 4 : 2;
        int dataLenSize = bigFile ? 8 : 4;
        int totalLen = 2 + trackNumSize + dataLenSize + 8 + 2 + track.SignatureBytes.Length;

        byte[] buffer = new byte[totalLen];
        int pos = 0;

        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos), flags);
        pos += 2;

        if (bigTrackNumber)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), (uint)track.TrackNumber);
            pos += 4;
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos), (ushort)track.TrackNumber);
            pos += 2;
        }

        if (bigFile)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(pos), (ulong)track.DataLength);
            pos += 8;
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), (uint)track.DataLength);
            pos += 4;
        }

        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(pos), (ulong)track.MatchOffset);
        pos += 8;

        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos), (ushort)track.SignatureBytes.Length);
        pos += 2;

        track.SignatureBytes.CopyTo(buffer, pos);

        return buffer;
    }

    #endregion

    #region EBML Helpers

    private static bool TryReadEbmlId(Stream stream, out ulong value, out int length)
    {
        value = 0;
        length = 0;
        int first = stream.ReadByte();
        if (first < 0)
        {
            return false;
        }

        int mask = 0x80;
        length = 1;
        while (length <= 8 && (first & mask) == 0)
        {
            mask >>= 1;
            length++;
        }

        if (length > 8)
        {
            return false;
        }

        value = (ulong)first;
        for (int i = 1; i < length; i++)
        {
            int b = stream.ReadByte();
            if (b < 0)
            {
                return false;
            }

            value = (value << 8) | (uint)b;
        }

        return true;
    }

    private static bool TryReadEbmlSize(Stream stream, out ulong value, out int length)
    {
        value = 0;
        length = 0;
        int first = stream.ReadByte();
        if (first < 0)
        {
            return false;
        }

        int mask = 0x80;
        length = 1;
        while (length <= 8 && (first & mask) == 0)
        {
            mask >>= 1;
            length++;
        }

        if (length > 8)
        {
            return false;
        }

        value = (ulong)(first & (mask - 1));
        for (int i = 1; i < length; i++)
        {
            int b = stream.ReadByte();
            if (b < 0)
            {
                return false;
            }

            value = (value << 8) | (uint)b;
        }

        return true;
    }

    /// <summary>

    /// Reads a VINT value (masks out marker bit).

    /// </summary>
    private static bool TryReadEbmlVint(Stream stream, out ulong value, out int length)
    {
        return TryReadEbmlSize(stream, out value, out length);
    }

    private static byte[] MakeEbmlUInt(long value)
    {
        // Encode value as EBML variable-length unsigned integer (size descriptor)
        if (value < 0x7F)
        {
            return [(byte)(0x80 | value)];
        }

        if (value < 0x3FFF)
        {
            return [(byte)(0x40 | (value >> 8)), (byte)(value & 0xFF)];
        }

        if (value < 0x1FFFFF)
        {
            return [(byte)(0x20 | (value >> 16)), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF)];
        }

        if (value < 0x0FFFFFFF)
        {
            return [(byte)(0x10 | (value >> 24)), (byte)((value >> 16) & 0xFF), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF)];
        }

        // 5+ bytes
        var result = new List<byte>();
        int width = 5;
        long max = 0x07FFFFFFFF;
        while (value > max && width < 8)
        {
            width++;
            max = (max << 8) | 0xFF;
        }

        byte marker = (byte)(1 << (8 - width));
        result.Add((byte)(marker | (byte)(value >> ((width - 1) * 8))));
        for (int i = width - 2; i >= 0; i--)
        {
            result.Add((byte)((value >> (i * 8)) & 0xFF));
        }

        return result.ToArray();
    }

    private static byte[] MakeEbmlId(ulong id)
    {
        // Encode element ID as big-endian bytes (preserve marker bit)
        if (id < 0x100)
        {
            return [(byte)id];
        }

        if (id < 0x10000)
        {
            return [(byte)(id >> 8), (byte)(id & 0xFF)];
        }

        if (id < 0x1000000)
        {
            return [(byte)(id >> 16), (byte)((id >> 8) & 0xFF), (byte)(id & 0xFF)];
        }

        return [(byte)(id >> 24), (byte)((id >> 16) & 0xFF), (byte)((id >> 8) & 0xFF), (byte)(id & 0xFF)];
    }

    private static byte[] BuildEbmlElement(ulong id, byte[] data)
    {
        byte[] idBytes = MakeEbmlId(id);
        byte[] sizeBytes = MakeEbmlUInt(data.Length);
        byte[] result = new byte[idBytes.Length + sizeBytes.Length + data.Length];
        idBytes.CopyTo(result, 0);
        sizeBytes.CopyTo(result, idBytes.Length);
        data.CopyTo(result, idBytes.Length + sizeBytes.Length);
        return result;
    }

    private static byte[] BuildEbmlElementHeader(ulong id, long dataSize)
    {
        byte[] idBytes = MakeEbmlId(id);
        byte[] sizeBytes = MakeEbmlUInt(dataSize);
        byte[] result = new byte[idBytes.Length + sizeBytes.Length];
        idBytes.CopyTo(result, 0);
        sizeBytes.CopyTo(result, idBytes.Length);
        return result;
    }

    #endregion

    #region Utilities

    private static byte[] ReadExactly(BinaryReader reader, int count)
    {
        if (count <= 0)
        {
            return [];
        }

        byte[] data = reader.ReadBytes(count);
        return data;
    }

    private static byte[] ReadExactly(Stream stream, int count)
    {
        if (count <= 0)
        {
            return [];
        }

        byte[] buffer = new byte[count];
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = stream.Read(buffer, totalRead, count - totalRead);
            if (read <= 0)
            {
                break;
            }

            totalRead += read;
        }

        if (totalRead < count)
        {
            Array.Resize(ref buffer, totalRead);
        }

        return buffer;
    }

    private static void CopyStream(Stream source, Stream destination, long bytes)
    {
        byte[] buffer = new byte[80 * 1024];
        long remaining = bytes;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = source.Read(buffer, 0, toRead);
            if (read <= 0)
            {
                break;
            }

            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private void ReportProgress(string message)
    {
        Progress?.Invoke(this, new SrsCreationProgressEventArgs { Message = message });
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { }
    }

    #endregion
}
