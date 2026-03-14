using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace SRR;

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
    public string Phase { get; init; } = "";
    public int TrackNumber { get; init; }
    public int TotalTracks { get; init; }
    public double ProgressPercent { get; init; }
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
    private const int SignatureSize = 256;
    private const int SearchBufferSize = 0x10000; // 64 KiB

    public event EventHandler<SrsReconstructionProgressEventArgs>? Progress;

    /// <summary>
    /// Rebuilds the original sample file from an SRS file and the full media file.
    /// </summary>
    /// <param name="srsFilePath">Path to the .srs file</param>
    /// <param name="mediaFilePath">Path to the full original media file</param>
    /// <param name="outputPath">Path to write the reconstructed sample</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Reconstruction result with CRC verification status</returns>
    public async Task<SrsReconstructionResult> RebuildAsync(
        string srsFilePath, string mediaFilePath, string outputPath, CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(() => RebuildCore(srsFilePath, mediaFilePath, outputPath, ct), ct);
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(outputPath);
            return new SrsReconstructionResult(false, false, 0, 0, 0, 0, "Operation was cancelled.");
        }
        catch (Exception ex)
        {
            TryDeleteFile(outputPath);
            return new SrsReconstructionResult(false, false, 0, 0, 0, 0, ex.Message);
        }
    }

    private SrsReconstructionResult RebuildCore(
        string srsFilePath, string mediaFilePath, string outputPath, CancellationToken ct)
    {
        if (!File.Exists(srsFilePath))
            throw new FileNotFoundException("SRS file not found.", srsFilePath);
        if (!File.Exists(mediaFilePath))
            throw new FileNotFoundException("Media file not found.", mediaFilePath);

        // Step 1: Parse SRS
        ReportProgress("Loading SRS", 0, 0, 0);
        var srs = SRSFile.Load(srsFilePath);

        if (srs.FileData is null)
            throw new InvalidDataException("SRS file does not contain file data (SRSF block).");
        if (srs.Tracks.Count == 0)
            throw new InvalidDataException("SRS file does not contain any track data (SRST blocks).");

        var fileData = srs.FileData;
        var tracks = srs.Tracks;
        long expectedSize = (long)fileData.SampleSize;
        uint expectedCrc = fileData.Crc32;

        // Build a dictionary keyed by track number for easy lookup
        var trackDict = new Dictionary<uint, SrsTrackDataBlock>();
        foreach (var track in tracks)
        {
            trackDict[track.TrackNumber] = track;
        }

        // Step 2: Find sample streams (locate signatures in media file)
        ReportProgress("Finding tracks", 0, tracks.Count, 10);
        var trackOffsets = FindSampleStreams(mediaFilePath, trackDict, srs.ContainerType, ct);

        // Step 3: Rebuild the sample (reads track data directly from media file)
        ReportProgress("Rebuilding", 0, tracks.Count, 40);
        string? outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        RebuildSample(srsFilePath, srs.ContainerType, trackDict,
            mediaFilePath, trackOffsets, outputPath, ct);

        // Step 4: Verify CRC
        ReportProgress("Verifying CRC", 0, tracks.Count, 90);
        long actualSize = new FileInfo(outputPath).Length;
        uint actualCrc = ComputeFileCrc32(outputPath, ct);

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
        SRSContainerType containerType,
        CancellationToken ct)
    {
        var offsets = new Dictionary<uint, long>();

        using var fs = new FileStream(mediaFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920);

        int trackIndex = 0;
        foreach (var (trackNumber, track) in tracks)
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
                throw new InvalidDataException(
                    $"Unable to locate track signature for track {trackNumber} in the media file.");

            offsets[trackNumber] = foundOffset;
        }

        return offsets;
    }

    /// <summary>
    /// Searches for a byte signature in a stream. Tries the hint offset first,
    /// then a nearby window, then a full file scan.
    /// </summary>
    internal static long FindSignature(Stream stream, byte[] signature, long hintOffset,
        CancellationToken ct = default)
    {
        if (signature.Length == 0)
            return hintOffset;

        // Try exact hint offset first
        if (hintOffset >= 0 && hintOffset + signature.Length <= stream.Length)
        {
            stream.Position = hintOffset;
            byte[] buffer = new byte[signature.Length];
            int read = ReadFully(stream, buffer, 0, buffer.Length);
            if (read == signature.Length && buffer.AsSpan().SequenceEqual(signature))
                return hintOffset;
        }

        // Search nearby: +/- 64KB around the hint offset
        if (hintOffset >= 0)
        {
            long searchStart = Math.Max(0, hintOffset - SearchBufferSize);
            long searchEnd = Math.Min(stream.Length, hintOffset + SearchBufferSize + signature.Length);
            long found = ScanForSignature(stream, signature, searchStart, searchEnd, ct);
            if (found >= 0)
                return found;
        }

        // Full file scan
        return ScanForSignature(stream, signature, 0, stream.Length, ct);
    }

    /// <summary>
    /// Scans a region of the stream for the given signature using a sliding window.
    /// </summary>
    private static long ScanForSignature(Stream stream, byte[] signature,
        long regionStart, long regionEnd, CancellationToken ct)
    {
        if (regionEnd - regionStart < signature.Length)
            return -1;

        int bufSize = Math.Max(SearchBufferSize, signature.Length * 2);
        byte[] buffer = new byte[bufSize];
        long position = regionStart;
        int carry = 0;

        while (position < regionEnd)
        {
            ct.ThrowIfCancellationRequested();

            stream.Position = position - carry;
            int toRead = (int)Math.Min(bufSize, regionEnd - position + carry);
            int bytesRead = ReadFully(stream, buffer, 0, toRead);
            if (bytesRead < signature.Length)
                break;

            // Search within the buffer
            int searchLimit = bytesRead - signature.Length;
            for (int i = 0; i <= searchLimit; i++)
            {
                if (buffer.AsSpan(i, signature.Length).SequenceEqual(signature))
                    return position - carry + i;
            }

            // Advance, keeping overlap for boundary matches
            long advance = bytesRead - signature.Length + 1;
            if (advance <= 0) advance = 1;
            position = position - carry + bytesRead;
            carry = signature.Length - 1;
            if (carry > bytesRead) carry = bytesRead;
        }

        return -1;
    }

    #endregion

    #region Rebuild Sample

    /// <summary>
    /// Rebuilds the sample by replaying the SRS file structure and reading
    /// track data directly from the media file at the found offsets.
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
        switch (containerType)
        {
            case SRSContainerType.AVI:
                RebuildAvi(srsFilePath, tracks, mediaFilePath, trackOffsets, outputPath, ct);
                break;
            case SRSContainerType.MKV:
                RebuildMkv(srsFilePath, tracks, mediaFilePath, trackOffsets, outputPath, ct);
                break;
            case SRSContainerType.MP4:
                RebuildMp4(srsFilePath, tracks, mediaFilePath, trackOffsets, outputPath, ct);
                break;
            case SRSContainerType.WMV:
                RebuildWmv(srsFilePath, tracks, mediaFilePath, trackOffsets, outputPath, ct);
                break;
            case SRSContainerType.FLAC:
                RebuildFlac(srsFilePath, tracks, mediaFilePath, trackOffsets, outputPath, ct);
                break;
            case SRSContainerType.MP3:
                RebuildMp3(srsFilePath, tracks, mediaFilePath, trackOffsets, outputPath, ct);
                break;
            case SRSContainerType.Stream:
                RebuildStream(tracks, mediaFilePath, trackOffsets, outputPath, ct);
                break;
        }
    }

    // ==================== AVI/RIFF Rebuilder ====================

    /// <summary>
    /// Rebuilds an AVI sample by replaying the RIFF structure from the SRS file,
    /// skipping SRSF/SRST chunks and reading movi data directly from the media file.
    /// The media file is walked in parallel to locate interleaved track data chunks.
    /// </summary>
    private void RebuildAvi(
        string srsFilePath,
        Dictionary<uint, SrsTrackDataBlock> tracks,
        string mediaFilePath,
        Dictionary<uint, long> trackOffsets,
        string outputPath,
        CancellationToken ct)
    {
        // Index the media file's movi chunks near the signature area.
        // This builds a per-track queue of (dataOffset, size) for each chunk.
        long minOffset = trackOffsets.Values.Min();
        long mediaScanStart = Math.Max(0, minOffset - 8);
        var mediaChunks = IndexMediaRiffChunks(mediaFilePath, mediaScanStart, tracks, ct);

        using var srsFs = new FileStream(srsFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(srsFs);
        using var mediaFs = new FileStream(mediaFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920);
        using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);

        RebuildRiffChunks(reader, srsFs, outFs, mediaFs, mediaChunks, 0, srsFs.Length, ct);
    }

    /// <summary>
    /// Walks the media file's RIFF structure from the scan start position and builds
    /// a per-track queue of chunk data offsets and sizes.
    /// </summary>
    private static Dictionary<uint, Queue<(long DataOffset, int Size)>> IndexMediaRiffChunks(
        string mediaFilePath,
        long scanStart,
        Dictionary<uint, SrsTrackDataBlock> tracks,
        CancellationToken ct)
    {
        var result = new Dictionary<uint, Queue<(long, int)>>();
        var remaining = new Dictionary<uint, long>();

        foreach (var (trackNumber, track) in tracks)
        {
            result[trackNumber] = new Queue<(long, int)>();
            remaining[trackNumber] = (long)track.DataLength;
        }

        using var fs = new FileStream(mediaFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920);
        fs.Position = scanStart;

        byte[] headerBuf = new byte[8];

        while (fs.Position + 8 <= fs.Length)
        {
            ct.ThrowIfCancellationRequested();

            bool allDone = true;
            foreach (long r in remaining.Values)
            {
                if (r > 0) { allDone = false; break; }
            }
            if (allDone) break;

            int hdrRead = ReadFully(fs, headerBuf, 0, 8);
            if (hdrRead < 8) break;

            string fourcc = Encoding.ASCII.GetString(headerBuf, 0, 4);
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(headerBuf.AsSpan(4));

            if (fourcc is "RIFF" or "LIST")
            {
                fs.Position += 4; // skip subtype, scan children
                continue;
            }

            bool isMoviData = fourcc.Length == 4 &&
                              char.IsDigit(fourcc[0]) && char.IsDigit(fourcc[1]) &&
                              char.IsLetter(fourcc[2]) && char.IsLetter(fourcc[3]);

            if (isMoviData)
            {
                uint trackNumber = (uint)((fourcc[0] - '0') * 10 + (fourcc[1] - '0'));
                long dataOffset = fs.Position;

                if (remaining.TryGetValue(trackNumber, out long rem) && rem > 0)
                {
                    int toIndex = (int)Math.Min(chunkSize, rem);
                    result[trackNumber].Enqueue((dataOffset, toIndex));
                    remaining[trackNumber] -= toIndex;
                }
            }

            fs.Position += chunkSize;
            if (chunkSize % 2 != 0 && fs.Position < fs.Length)
                fs.Position++;
        }

        return result;
    }

    private void RebuildRiffChunks(
        BinaryReader reader, Stream srsFs, Stream outFs,
        Stream mediaFs, Dictionary<uint, Queue<(long DataOffset, int Size)>> mediaChunks,
        long start, long end,
        CancellationToken ct)
    {
        srsFs.Position = start;

        while (srsFs.Position + 8 <= end)
        {
            ct.ThrowIfCancellationRequested();
            long chunkStart = srsFs.Position;

            byte[] header = reader.ReadBytes(8);
            if (header.Length < 8) break;

            string fourcc = Encoding.ASCII.GetString(header, 0, 4);
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));

            if (fourcc is "SRSF" or "SRST")
            {
                // Skip SRS metadata blocks - don't copy them to output
                srsFs.Position = chunkStart + 8 + chunkSize;
                if (chunkSize % 2 != 0 && srsFs.Position < end)
                    srsFs.Position++;
                continue;
            }

            // Write header to output
            outFs.Write(header);

            if (fourcc is "RIFF" or "LIST")
            {
                // Container chunk: copy 4-byte subtype and recurse
                byte[] subType = reader.ReadBytes(4);
                outFs.Write(subType);

                long childEnd = chunkStart + 8 + chunkSize;
                if (childEnd > end) childEnd = end;

                RebuildRiffChunks(reader, srsFs, outFs, mediaFs, mediaChunks,
                    srsFs.Position, childEnd, ct);

                srsFs.Position = childEnd;
                if (chunkSize % 2 != 0 && srsFs.Position < end)
                {
                    byte pad = reader.ReadByte();
                    outFs.WriteByte(pad);
                }
            }
            else
            {
                // Check if this is a movi data chunk (e.g. "00dc", "01wb")
                bool isMovi = fourcc.Length == 4 &&
                              char.IsDigit(fourcc[0]) && char.IsDigit(fourcc[1]) &&
                              char.IsLetter(fourcc[2]) && char.IsLetter(fourcc[3]);

                if (isMovi)
                {
                    // Read data directly from media file at the indexed position
                    uint trackNumber = (uint)((fourcc[0] - '0') * 10 + (fourcc[1] - '0'));

                    if (mediaChunks.TryGetValue(trackNumber, out var queue) && queue.Count > 0)
                    {
                        var (dataOffset, size) = queue.Dequeue();
                        mediaFs.Position = dataOffset;
                        CopyBytes(mediaFs, outFs, size);
                    }

                    // In the SRS, no data follows the movi header (data was stripped).
                }
                else
                {
                    // Non-movi chunk: copy data verbatim from SRS
                    if (chunkSize > 0)
                    {
                        byte[] data = ReadExactly(reader, (int)chunkSize);
                        outFs.Write(data);
                    }
                }

                // Handle RIFF padding byte
                if (chunkSize % 2 != 0 && srsFs.Position < end)
                {
                    byte pad = reader.ReadByte();
                    outFs.WriteByte(pad);
                }
            }
        }
    }

    // ==================== Stream Rebuilder ====================

    /// <summary>
    /// Rebuilds a STREAM/VOB sample. The entire file is just the track data,
    /// read contiguously from the media file.
    /// </summary>
    private static void RebuildStream(
        Dictionary<uint, SrsTrackDataBlock> tracks,
        string mediaFilePath,
        Dictionary<uint, long> trackOffsets,
        string outputPath,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var mediaFs = new FileStream(mediaFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920);
        using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);

        if (tracks.TryGetValue(1, out var track) && trackOffsets.TryGetValue(1, out long offset))
        {
            mediaFs.Position = offset;
            CopyBytes(mediaFs, outFs, (long)track.DataLength);
        }
    }

    // ==================== MKV Rebuilder ====================

    /// <summary>
    /// Rebuilds an MKV sample from an SRS file and a media file.
    ///
    /// Handles two SRS variants:
    /// 1. SRS files with stripped Cluster/SimpleBlock headers (our writer):
    ///    The SRS walk copies metadata + block headers; frame data comes from indexed media blocks.
    /// 2. SRS files with metadata only, no Cluster data (pyrescene):
    ///    The SRS walk copies metadata; the entire Cluster region is then copied from the media file.
    ///
    /// Both cases are handled by the same pipeline: walk the SRS, then fill any remaining
    /// bytes (up to the expected sample size) from the media file.
    /// </summary>
    private void RebuildMkv(
        string srsFilePath,
        Dictionary<uint, SrsTrackDataBlock> tracks,
        string mediaFilePath,
        Dictionary<uint, long> trackOffsets,
        string outputPath,
        CancellationToken ct)
    {
        // Extract attachment data from the media file (fonts, etc.).
        // The SRS preserves attachment headers with original sizes but strips
        // the data, so we need to source it from the media file.
        var attachments = ExtractMediaAttachments(mediaFilePath, ct);

        // Collect per-track frame data from the media file into MemoryStreams.
        // The SRS file preserves the original block headers (including lacing);
        // only the raw frame bytes come from the media file.
        var frameData = CollectMediaFrameData(mediaFilePath, trackOffsets, tracks, ct);

        try
        {
            using var srsFs = new FileStream(srsFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);

            // Walk the SRS file and write to output:
            // - Metadata elements are copied verbatim
            // - ReSample/SRSF/SRST elements are skipped
            // - Container elements (Segment, Cluster, BlockGroup) are stepped into
            // - SimpleBlock/Block: full block header (with lacing) from SRS,
            //   frame data from per-track MemoryStreams
            // - AttachedFileData: data from media file attachments
            RebuildEbmlFromSrs(srsFs, outFs, frameData, attachments, ct);
        }
        finally
        {
            foreach (var ms in frameData.Values)
                ms.Dispose();
        }
    }

    /// <summary>
    /// Walks the media file's EBML structure to extract attachment data.
    /// Returns a queue of (name, data) pairs in file order for use during rebuild.
    /// The SRS file preserves attachment element headers with original sizes but
    /// strips the actual data, so we need to source it from the media file.
    /// </summary>
    private static Queue<(string Name, byte[] Data)> ExtractMediaAttachments(
        string mediaFilePath, CancellationToken ct)
    {
        var attachments = new Queue<(string, byte[])>();
        string? currentName = null;

        using var fs = new FileStream(mediaFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920);

        while (fs.Position < fs.Length)
        {
            ct.ThrowIfCancellationRequested();

            if (!TryReadEbmlId(fs, out ulong elemId, out int idLen)) break;
            if (!TryReadEbmlSize(fs, out ulong dataSize, out int sizeLen)) break;

            long dataStart = fs.Position;
            bool unknownSize = IsEbmlSizeUnknown(dataSize, sizeLen);

            // Container elements: step into
            if (unknownSize || IsEbmlContainerElement(elemId))
                continue;

            // AttachedFileName (0x466E): read and remember
            if (elemId == 0x466E && (long)dataSize > 0 && (long)dataSize < 1048576)
            {
                byte[] nameBytes = new byte[(long)dataSize];
                fs.ReadExactly(nameBytes, 0, nameBytes.Length);
                currentName = Encoding.UTF8.GetString(nameBytes);
                continue;
            }

            // AttachedFileData (0x465C): read data and pair with current name
            if (elemId == 0x465C && (long)dataSize > 0)
            {
                byte[] data = new byte[(long)dataSize];
                int totalRead = ReadFully(fs, data, 0, data.Length);
                if (totalRead < data.Length)
                    Array.Resize(ref data, totalRead);

                attachments.Enqueue((currentName ?? string.Empty, data));
                currentName = null;
                continue;
            }

            // Skip all other elements
            fs.Position = dataStart + (long)dataSize;
        }

        return attachments;
    }

    /// <summary>
    /// Walks the media file's EBML structure and collects per-track frame data
    /// (after lacing headers) into MemoryStreams. The SRS file preserves the
    /// original sample's block headers including lacing; only the raw frame
    /// bytes are needed from the media file.
    /// </summary>
    private Dictionary<uint, MemoryStream> CollectMediaFrameData(
        string mediaFilePath,
        Dictionary<uint, long> trackOffsets,
        Dictionary<uint, SrsTrackDataBlock> tracks,
        CancellationToken ct)
    {
        var streams = new Dictionary<uint, MemoryStream>();
        var remaining = new Dictionary<uint, long>();

        foreach (var (trackNumber, track) in tracks)
        {
            streams[trackNumber] = new MemoryStream((int)track.DataLength);
            remaining[trackNumber] = (long)track.DataLength;
        }

        long minOffset = trackOffsets.Values.Min();
        var started = new HashSet<uint>();

        ReportProgress("Collecting frame data", 0, tracks.Count, 40);

        using var fs = new FileStream(mediaFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920);

        byte[] copyBuf = new byte[81920];
        int blocksMatched = 0;

        while (fs.Position < fs.Length)
        {
            ct.ThrowIfCancellationRequested();

            bool allDone = true;
            foreach (long r in remaining.Values)
            {
                if (r > 0) { allDone = false; break; }
            }
            if (allDone) break;

            if (!TryReadEbmlId(fs, out ulong elemId, out int idLen)) break;
            if (!TryReadEbmlSize(fs, out ulong dataSize, out int sizeLen)) break;

            long dataStart = fs.Position;
            bool unknownSize = IsEbmlSizeUnknown(dataSize, sizeLen);

            // SimpleBlock (0xA3) or Block (0xA1): extract frame data
            if (elemId is 0xA3 or 0xA1)
            {
                long blockStart = fs.Position;
                if (TryReadEbmlVint(fs, out ulong trackNum, out int vintLen))
                {
                    int blockHeaderBase = vintLen + 2 + 1; // VINT + timecode(2) + flags(1)

                    // Parse lacing to find where frame data starts
                    fs.Position = blockStart + vintLen + 2;
                    int flagsByte = fs.ReadByte();
                    if (flagsByte < 0) break;
                    int laceType = (flagsByte >> 1) & 0x03;

                    int lacingHeaderSize = 0;
                    if (laceType != 0)
                    {
                        fs.Position = blockStart + blockHeaderBase;
                        int laceCount = fs.ReadByte();
                        if (laceCount >= 0)
                        {
                            lacingHeaderSize = 1;
                            if (laceType == 1) // Xiph lacing
                            {
                                for (int i = 0; i < laceCount; i++)
                                {
                                    int b;
                                    do
                                    {
                                        b = fs.ReadByte();
                                        if (b < 0) break;
                                        lacingHeaderSize++;
                                    } while (b == 255);
                                }
                            }
                            else if (laceType == 3) // EBML lacing
                            {
                                if (TryReadEbmlSize(fs, out _, out int firstSizeLen))
                                {
                                    lacingHeaderSize += firstSizeLen;
                                    for (int i = 1; i < laceCount; i++)
                                    {
                                        if (TryReadEbmlSize(fs, out _, out int deltaLen))
                                            lacingHeaderSize += deltaLen;
                                    }
                                }
                            }
                            // Fixed-size lacing (type 2) has no extra size data
                        }
                    }

                    long frameDataOffset = blockStart + blockHeaderBase + lacingHeaderSize;
                    int frameDataLen = (int)((long)dataSize - blockHeaderBase - lacingHeaderSize);

                    uint tn = (uint)trackNum;
                    if (frameDataLen > 0 &&
                        remaining.TryGetValue(tn, out long rem) && rem > 0 &&
                        trackOffsets.TryGetValue(tn, out long trackStart))
                    {
                        bool include;

                        if (!started.Contains(tn))
                        {
                            // First block: frame data must start at or near the
                            // signature offset found by FindSignature.
                            //
                            // Case 1: signature falls within frame data range
                            //   (frameDataOffset <= trackStart < frameDataOffset + frameDataLen)
                            //
                            // Case 2: frame data starts shortly AFTER the signature offset.
                            //   FindSignature does a raw byte scan and can match at a
                            //   position where element/block header bytes preceding the
                            //   actual frame data happen to match the start of the
                            //   signature. In this case, frameDataOffset is a few bytes
                            //   after trackStart (up to ~20 bytes of header overlap).
                            //   We verify the actual frame data matches the signature.
                            const int MaxHeaderOverlap = 20;

                            if (frameDataOffset <= trackStart
                                && frameDataOffset + frameDataLen > trackStart)
                            {
                                include = true;
                            }
                            else if (frameDataOffset > trackStart
                                && frameDataOffset <= trackStart + MaxHeaderOverlap)
                            {
                                // Verify frame data matches the expected signature
                                // at the appropriate offset within it.
                                int sigOffset = (int)(frameDataOffset - trackStart);
                                byte[] sig = tracks[tn].Signature;
                                int verifyLen = Math.Min(sig.Length - sigOffset, frameDataLen);

                                if (verifyLen > 0)
                                {
                                    long savedPos = fs.Position;
                                    fs.Position = frameDataOffset;
                                    byte[] verifyBuf = new byte[verifyLen];
                                    int read = ReadFully(fs, verifyBuf, 0, verifyLen);
                                    fs.Position = savedPos;

                                    include = read == verifyLen
                                        && verifyBuf.AsSpan(0, read)
                                            .SequenceEqual(sig.AsSpan(sigOffset, read));
                                }
                                else
                                {
                                    include = false;
                                }
                            }
                            else
                            {
                                include = false;
                            }
                        }
                        else
                        {
                            include = true;
                        }

                        if (include)
                        {
                            if (!started.Contains(tn))
                            {
                                started.Add(tn);
                                ReportProgress($"Track {tn} located at offset {frameDataOffset:N0}", 0, 0, 50);
                            }
                            blocksMatched++;

                            // Copy lacing + frame data to the track's MemoryStream.
                            // We collect from right after the base block header (track VINT +
                            // timecode + flags), including any lacing bytes. This way the
                            // MemoryStream has everything needed for reconstruction regardless
                            // of whether the SRS includes lacing bytes or not.
                            int collectLen = (int)((long)dataSize - blockHeaderBase);
                            fs.Position = blockStart + blockHeaderBase;
                            int toRead = collectLen;
                            while (toRead > 0)
                            {
                                int chunk = Math.Min(toRead, copyBuf.Length);
                                int read = fs.Read(copyBuf, 0, chunk);
                                if (read == 0) break;
                                streams[tn].Write(copyBuf, 0, read);
                                toRead -= read;
                            }
                            remaining[tn] -= Math.Max(frameDataLen, 1);
                        }
                    }
                }

                fs.Position = dataStart + (long)dataSize;
                continue;
            }

            // Container elements or unknown sizes: step into
            if (unknownSize || IsEbmlContainerElement(elemId))
            {
                if (!unknownSize)
                {
                    long elemEnd = dataStart + (long)dataSize;
                    if (elemEnd <= minOffset - 4096)
                    {
                        fs.Position = elemEnd;
                        continue;
                    }
                }
                continue; // step into children
            }

            // Non-container, non-block, known size: skip past
            fs.Position = dataStart + (long)dataSize;
        }

        ReportProgress($"Collected {blocksMatched:N0} blocks from media file", 0, 0, 60);

        // Reset all streams to the beginning for reading
        foreach (var ms in streams.Values)
            ms.Position = 0;

        return streams;
    }

    /// <summary>
    /// Returns true if the EBML size value represents "unknown" (all data bits are 1).
    /// </summary>
    private static bool IsEbmlSizeUnknown(ulong value, int length)
        => value == (1UL << (7 * length)) - 1;

    /// <summary>
    /// Walks the SRS file sequentially, writing to the output.
    /// ReSample/SRSF/SRST elements are skipped by ID. Container elements are stepped into.
    /// SimpleBlock/Block: base block header (track VINT + timecode + flags) from SRS,
    /// then lacing + frame data from per-track MemoryStreams collected from the media file.
    /// AttachedFileData: data from media file attachments (stripped from SRS).
    /// </summary>
    private void RebuildEbmlFromSrs(
        Stream srsFs, Stream outFs,
        Dictionary<uint, MemoryStream> frameData,
        Queue<(string Name, byte[] Data)> attachments,
        CancellationToken ct)
    {
        byte[] copyBuf = new byte[81920];
        int blockCount = 0;

        while (srsFs.Position < srsFs.Length)
        {
            ct.ThrowIfCancellationRequested();
            long elemStart = srsFs.Position;

            if (!TryReadEbmlId(srsFs, out ulong elemId, out int idLen)) break;
            if (!TryReadEbmlSize(srsFs, out ulong dataSize, out int sizeLen)) break;

            long dataStart = srsFs.Position;
            bool unknownSize = IsEbmlSizeUnknown(dataSize, sizeLen);
            int headerSize = idLen + sizeLen;

            // Skip ReSample container and SRS child elements
            if (elemId is 0x1F697576 or 0x6A75 or 0x6B75)
            {
                if (!unknownSize)
                    srsFs.Position = dataStart + (long)dataSize;
                continue;
            }

            // Copy the element header to output
            srsFs.Position = elemStart;
            byte[] rawHeader = new byte[headerSize];
            srsFs.ReadExactly(rawHeader, 0, headerSize);
            outFs.Write(rawHeader);

            // Container elements: step into
            if (IsEbmlContainerElement(elemId))
                continue;

            // AttachedFileData (0x465C): data is stripped from SRS, source from media
            if (elemId == 0x465C)
            {
                if (attachments.Count > 0)
                {
                    var (_, data) = attachments.Dequeue();
                    outFs.Write(data);
                }
                // SRS has no data bytes after the header — stream position is already correct
                continue;
            }

            // SimpleBlock (0xA3) or Block (0xA1)
            // The element size preserves the ORIGINAL size (base header + lacing + frame data).
            // The SRS may store only the base block header (pyrescene format) or
            // the base header + lacing bytes (our writer format). We detect which
            // by probing the byte after blockHeaderBase: if it's a known MKV element
            // ID start, the SRS has no lacing and the next element follows immediately.
            // The pre-collected MemoryStreams contain lacing + frame data from the media.
            if (elemId is 0xA3 or 0xA1)
            {
                blockCount++;
                long blockStart = srsFs.Position;
                if (TryReadEbmlVint(srsFs, out ulong trackNum, out int vintLen))
                {
                    int blockHeaderBase = vintLen + 2 + 1; // VINT + timecode(2) + flags(1)

                    // Read flags byte to check lacing type
                    srsFs.Position = blockStart + vintLen + 2;
                    int flagsByte = srsFs.ReadByte();
                    int laceType = flagsByte >= 0 ? (flagsByte >> 1) & 0x03 : 0;

                    // Determine how many bytes of block header the SRS stores
                    int srsBlockHeaderSize = blockHeaderBase;
                    bool srsHasLacing = false;

                    if (laceType != 0)
                    {
                        // Probe: check if the byte after blockHeaderBase is a known
                        // MKV element ID. If so, the SRS has no lacing bytes (pyrescene).
                        // If not, the SRS includes lacing bytes (our writer).
                        long probePos = blockStart + blockHeaderBase;
                        if (probePos < srsFs.Length)
                        {
                            srsFs.Position = probePos;
                            if (TryReadEbmlId(srsFs, out ulong probeId, out _)
                                && IsKnownMkvElementId(probeId))
                            {
                                srsHasLacing = false;
                            }
                            else
                            {
                                // Parse lacing from SRS to determine header size
                                srsFs.Position = probePos;
                                int dataAfterBase = (int)((long)dataSize - blockHeaderBase);
                                int peekLen = Math.Min(dataAfterBase, 256);
                                if (peekLen > 0)
                                {
                                    byte[] lacingPeek = new byte[peekLen];
                                    int read = ReadFully(srsFs, lacingPeek, 0, peekLen);
                                    if (read > 0)
                                    {
                                        var lacingType = (EbmlLaceType)(flagsByte & 0x06);
                                        var (_, bytesConsumed) = EbmlLacing.GetFrameLengths(
                                            lacingPeek.AsSpan(0, read), lacingType, dataAfterBase);
                                        srsBlockHeaderSize = blockHeaderBase + bytesConsumed;
                                        srsHasLacing = bytesConsumed > 0;
                                    }
                                }
                            }
                        }
                    }

                    // Copy block header from SRS to output
                    srsFs.Position = blockStart;
                    byte[] blockHeader = new byte[srsBlockHeaderSize];
                    srsFs.ReadExactly(blockHeader, 0, srsBlockHeaderSize);
                    outFs.Write(blockHeader);

                    // Write remaining data from the track's MemoryStream.
                    // If SRS included lacing, skip those bytes in the MemoryStream
                    // since they were already written from SRS.
                    long msDataSize = (long)dataSize - srsBlockHeaderSize;
                    if (msDataSize > 0)
                    {
                        uint tn = (uint)trackNum;
                        if (frameData.TryGetValue(tn, out var ms))
                        {
                            if (srsHasLacing)
                            {
                                int lacingSkip = srsBlockHeaderSize - blockHeaderBase;
                                ms.Position += lacingSkip;
                            }

                            long toWrite = msDataSize;
                            while (toWrite > 0)
                            {
                                int chunk = (int)Math.Min(toWrite, copyBuf.Length);
                                int rd = ms.Read(copyBuf, 0, chunk);
                                if (rd == 0) break;
                                outFs.Write(copyBuf, 0, rd);
                                toWrite -= rd;
                            }
                        }
                    }
                }
                continue;
            }

            // Non-container, non-block: copy data verbatim from SRS
            if (!unknownSize && (long)dataSize > 0)
                CopyBytes(srsFs, outFs, (long)dataSize);
        }

        ReportProgress($"Wrote {blockCount:N0} blocks to output", 0, 0, 80);
    }

    private static bool IsEbmlContainerElement(ulong id) => id is
        0x18538067 or // Segment
        0x1F43B675 or // Cluster
        0x1654AE6B or // Tracks
        0xAE or       // TrackEntry
        0x6D80 or     // ContentEncodings
        0x6240 or     // ContentEncoding
        0x5034 or     // ContentCompression
        0xA0 or       // BlockGroup
        0x1941A469 or // Attachments
        0x61A7;       // AttachedFile

    /// <summary>
    /// Returns true if the given EBML element ID is part of the known MKV vocabulary.
    /// Used to probe whether the SRS includes lacing bytes after the base block header:
    /// if the bytes parse as a known element ID, the SRS has no lacing (pyrescene format).
    /// </summary>
    private static bool IsKnownMkvElementId(ulong id) => id is
        0x1A45DFA3 or // EBML
        0x18538067 or // Segment
        0x1F43B675 or // Cluster
        0x1654AE6B or // Tracks
        0x114D9B74 or // SeekHead
        0x1549A966 or // Info
        0x1C53BB6B or // Cues
        0x1941A469 or // Attachments
        0x1043A770 or // Chapters
        0x1254C367 or // Tags
        0x1F697576 or // ReSample container
        0x6A75 or     // SRSF
        0x6B75 or     // SRST
        0xAE or       // TrackEntry
        0xA3 or       // SimpleBlock
        0xA1 or       // Block
        0xA0 or       // BlockGroup
        0xE7 or       // Timestamp
        0xAB or       // PrevSize
        0xA7 or       // Position
        0xBF or       // CRC-32
        0xEC or       // Void
        0x61A7 or     // AttachedFile
        0x466E or     // FileName
        0x4660 or     // FileMimeType
        0x465C or     // FileData
        0xD7 or       // TrackNumber
        0x73C5 or     // TrackUID
        0x83 or       // TrackType
        0x86 or       // CodecID
        0x6D80 or     // ContentEncodings
        0x6240 or     // ContentEncoding
        0x5034 or     // ContentCompression
        0x9B or       // BlockDuration
        0xFB;         // ReferenceBlock

    // ==================== MP4 Rebuilder ====================

    /// <summary>
    /// Rebuilds an MP4 sample by replaying the atom structure from the SRS file,
    /// skipping SRSF/SRST atoms and reading mdat content from the media file.
    /// </summary>
    private void RebuildMp4(
        string srsFilePath,
        Dictionary<uint, SrsTrackDataBlock> tracks,
        string mediaFilePath,
        Dictionary<uint, long> trackOffsets,
        string outputPath,
        CancellationToken ct)
    {
        using var srsFs = new FileStream(srsFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var mediaFs = new FileStream(mediaFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920);
        using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);

        RebuildMp4Atoms(srsFs, outFs, mediaFs, tracks, trackOffsets, 0, srsFs.Length, ct);
    }

    private void RebuildMp4Atoms(
        Stream srsFs, Stream outFs,
        Stream mediaFs,
        Dictionary<uint, SrsTrackDataBlock> tracks,
        Dictionary<uint, long> trackOffsets,
        long start, long end,
        CancellationToken ct)
    {
        srsFs.Position = start;

        while (srsFs.Position + 8 <= end)
        {
            ct.ThrowIfCancellationRequested();
            long atomStart = srsFs.Position;

            byte[] sizeBytes = new byte[4];
            srsFs.ReadExactly(sizeBytes, 0, 4);
            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(sizeBytes);

            byte[] typeBytes = new byte[4];
            srsFs.ReadExactly(typeBytes, 0, 4);
            string type = Encoding.ASCII.GetString(typeBytes);

            int headerSize = 8;
            long totalSize;

            if (size32 == 1)
            {
                byte[] extBytes = new byte[8];
                srsFs.ReadExactly(extBytes, 0, 8);
                totalSize = (long)BinaryPrimitives.ReadUInt64BigEndian(extBytes);
                headerSize = 16;
            }
            else if (size32 == 0)
            {
                totalSize = end - atomStart;
            }
            else
            {
                totalSize = size32;
            }

            if (totalSize < headerSize) break;
            long payloadStart = atomStart + headerSize;
            long atomEnd = Math.Min(atomStart + totalSize, end);

            // Skip SRSF/SRST atoms
            if (type is "SRSF" or "SRST")
            {
                srsFs.Position = atomEnd;
                continue;
            }

            // Write header
            srsFs.Position = atomStart;
            byte[] rawHeader = new byte[headerSize];
            srsFs.ReadExactly(rawHeader, 0, headerSize);
            outFs.Write(rawHeader);

            if (type == "mdat")
            {
                // mdat: write track data from media file, sorted by match offset
                var sortedTracks = tracks
                    .Where(kv => trackOffsets.ContainsKey(kv.Key))
                    .OrderBy(kv => trackOffsets[kv.Key])
                    .ToList();

                foreach (var (trackNumber, track) in sortedTracks)
                {
                    mediaFs.Position = trackOffsets[trackNumber];
                    CopyBytes(mediaFs, outFs, (long)track.DataLength);
                }

                srsFs.Position = atomEnd;
            }
            else if (Mp4ContainerAtoms.Contains(type))
            {
                // Recurse into container atoms
                RebuildMp4Atoms(srsFs, outFs, mediaFs, tracks, trackOffsets,
                    payloadStart, atomEnd, ct);
                srsFs.Position = atomEnd;
            }
            else
            {
                // Copy metadata atom verbatim
                long remaining = atomEnd - srsFs.Position;
                if (remaining > 0)
                    CopyBytes(srsFs, outFs, remaining);
                srsFs.Position = atomEnd;
            }
        }
    }

    private static readonly HashSet<string> Mp4ContainerAtoms =
        ["moov", "trak", "mdia", "minf", "stbl", "edts", "udta"];

    // ==================== WMV/ASF Rebuilder ====================

    private static readonly byte[] GuidSrsFile = Encoding.ASCII.GetBytes("SRSFSRSFSRSFSRSF");
    private static readonly byte[] GuidSrsTrack = Encoding.ASCII.GetBytes("SRSTSRSTSRSTSRST");
    private static readonly byte[] GuidSrsPadding = Encoding.ASCII.GetBytes("PADDINGBYTESDATA");

    /// <summary>
    /// Rebuilds a WMV/ASF sample by replaying the ASF object structure from the SRS file,
    /// skipping SRS GUID objects. Body data is copied verbatim from SRS.
    /// </summary>
    private void RebuildWmv(
        string srsFilePath,
        Dictionary<uint, SrsTrackDataBlock> tracks,
        string mediaFilePath,
        Dictionary<uint, long> trackOffsets,
        string outputPath,
        CancellationToken ct)
    {
        using var srsFs = new FileStream(srsFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(srsFs);
        using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);

        byte[] sizeBuffer = new byte[8];

        while (srsFs.Position + 24 <= srsFs.Length)
        {
            ct.ThrowIfCancellationRequested();
            long objStart = srsFs.Position;

            byte[] guid = reader.ReadBytes(16);
            ulong totalSize = reader.ReadUInt64();

            if (totalSize < 24) break;
            long objEnd = objStart + (long)totalSize;

            // Skip SRS objects
            if (GuidEquals(guid, GuidSrsFile) ||
                GuidEquals(guid, GuidSrsTrack) ||
                GuidEquals(guid, GuidSrsPadding))
            {
                srsFs.Position = objEnd;
                continue;
            }

            // Write header
            outFs.Write(guid);
            BinaryPrimitives.WriteUInt64LittleEndian(sizeBuffer, totalSize);
            outFs.Write(sizeBuffer);

            // Copy body verbatim
            long bodySize = (long)totalSize - 24;
            if (bodySize > 0)
                CopyBytes(srsFs, outFs, bodySize);

            srsFs.Position = objEnd;
        }
    }

    // ==================== FLAC Rebuilder ====================

    /// <summary>
    /// Rebuilds a FLAC sample: copies metadata blocks from SRS, then reads
    /// audio frame data directly from the media file.
    /// </summary>
    private void RebuildFlac(
        string srsFilePath,
        Dictionary<uint, SrsTrackDataBlock> tracks,
        string mediaFilePath,
        Dictionary<uint, long> trackOffsets,
        string outputPath,
        CancellationToken ct)
    {
        using var srsFs = new FileStream(srsFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(srsFs);
        using var mediaFs = new FileStream(mediaFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920);
        using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);

        // Write fLaC marker
        outFs.Write("fLaC"u8);
        srsFs.Position = 4;

        int srsBlockCount = 0;

        while (srsFs.Position + 4 <= srsFs.Length)
        {
            ct.ThrowIfCancellationRequested();
            long blockStart = srsFs.Position;

            byte typeByte = reader.ReadByte();
            bool isLast = (typeByte & 0x80) != 0;
            byte type = (byte)(typeByte & 0x7F);

            byte[] sizeBytes = reader.ReadBytes(3);
            int payloadSize = (sizeBytes[0] << 16) | (sizeBytes[1] << 8) | sizeBytes[2];

            // SRS FLAC blocks: 's' (0x73) = SRSF, 't' (0x74) = SRST, 'u' (0x75) = fingerprint
            if (type is 0x73 or 0x74 or 0x75 && srsBlockCount <= 3)
            {
                srsBlockCount++;
                srsFs.Position = blockStart + 4 + payloadSize;
                continue;
            }

            // Copy block header and content
            srsFs.Position = blockStart;
            byte[] rawHeader = new byte[4];
            srsFs.ReadExactly(rawHeader, 0, 4);
            outFs.Write(rawHeader);

            if (payloadSize > 0)
            {
                byte[] payload = ReadExactly(reader, payloadSize);
                outFs.Write(payload);
            }

            // After the last metadata block, write audio data from media file
            if (isLast && tracks.TryGetValue(1, out var track) &&
                trackOffsets.TryGetValue(1, out long offset))
            {
                mediaFs.Position = offset;
                CopyBytes(mediaFs, outFs, (long)track.DataLength);
            }

            if (isLast) break;
        }
    }

    // ==================== MP3 Rebuilder ====================

    /// <summary>
    /// Rebuilds an MP3 sample: copies header tags from SRS, reads audio data
    /// from the media file, then copies footer tags from SRS.
    /// </summary>
    private void RebuildMp3(
        string srsFilePath,
        Dictionary<uint, SrsTrackDataBlock> tracks,
        string mediaFilePath,
        Dictionary<uint, long> trackOffsets,
        string outputPath,
        CancellationToken ct)
    {
        using var srsFs = new FileStream(srsFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(srsFs);
        using var mediaFs = new FileStream(mediaFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920);
        using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);

        bool mainDataWritten = false;

        // Check for ID3v2 header
        if (srsFs.Length >= 10)
        {
            byte[] id3Check = reader.ReadBytes(3);
            srsFs.Position = 0;

            if (id3Check[0] == 'I' && id3Check[1] == 'D' && id3Check[2] == '3')
            {
                // Read ID3v2 size
                srsFs.Position = 6;
                byte[] id3SizeBytes = reader.ReadBytes(4);
                int id3Size = (id3SizeBytes[0] << 21) | (id3SizeBytes[1] << 14) |
                              (id3SizeBytes[2] << 7) | id3SizeBytes[3];
                long id3TotalSize = 10 + id3Size;

                // Copy entire ID3v2 tag
                srsFs.Position = 0;
                byte[] id3Data = ReadExactly(reader, (int)id3TotalSize);
                outFs.Write(id3Data);
            }
            else
            {
                srsFs.Position = 0;
            }
        }

        // Read remaining blocks
        while (srsFs.Position + 8 <= srsFs.Length)
        {
            ct.ThrowIfCancellationRequested();
            long blockStart = srsFs.Position;

            byte[] peek = reader.ReadBytes(4);
            srsFs.Position = blockStart;

            string tag = Encoding.ASCII.GetString(peek, 0, 4);

            if (tag is "SRSF" or "SRST" or "SRSP")
            {
                // Write audio data from media file before skipping SRS blocks
                if (!mainDataWritten && tracks.TryGetValue(1, out var track) &&
                    trackOffsets.TryGetValue(1, out long offset))
                {
                    mediaFs.Position = offset;
                    CopyBytes(mediaFs, outFs, (long)track.DataLength);
                    mainDataWritten = true;
                }

                // Skip the SRS block
                reader.ReadBytes(4); // tag
                uint totalSize = reader.ReadUInt32();
                srsFs.Position = blockStart + totalSize;
            }
            else
            {
                // Not an SRS block - break and copy remaining (footer tags)
                break;
            }
        }

        // Copy remaining footer data (ID3v1, APE tags, etc.)
        long remaining = srsFs.Length - srsFs.Position;
        if (remaining > 0)
        {
            byte[] footer = ReadExactly(reader, (int)remaining);
            outFs.Write(footer);
        }
    }

    #endregion

    #region CRC32

    private static uint ComputeFileCrc32(string filePath, CancellationToken ct)
    {
        var crc = new Crc32();
        byte[] buffer = new byte[81920];

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        int bytesRead;
        while ((bytesRead = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            crc.Append(buffer.AsSpan(0, bytesRead));
        }

        Span<byte> hash = stackalloc byte[4];
        crc.GetHashAndReset(hash);
        return BinaryPrimitives.ReadUInt32LittleEndian(hash);
    }

    #endregion

    #region EBML Helpers

    private static bool TryReadEbmlId(Stream stream, out ulong value, out int length)
    {
        value = 0;
        length = 0;
        int first = stream.ReadByte();
        if (first < 0) return false;

        int mask = 0x80;
        length = 1;
        while (length <= 8 && (first & mask) == 0)
        {
            mask >>= 1;
            length++;
        }
        if (length > 8) return false;

        // For element IDs, keep the marker bit
        value = (ulong)first;
        for (int i = 1; i < length; i++)
        {
            int b = stream.ReadByte();
            if (b < 0) return false;
            value = (value << 8) | (uint)b;
        }

        return true;
    }

    private static bool TryReadEbmlSize(Stream stream, out ulong value, out int length)
    {
        value = 0;
        length = 0;
        int first = stream.ReadByte();
        if (first < 0) return false;

        int mask = 0x80;
        length = 1;
        while (length <= 8 && (first & mask) == 0)
        {
            mask >>= 1;
            length++;
        }
        if (length > 8) return false;

        // For sizes, mask out the marker bit
        value = (ulong)(first & (mask - 1));
        for (int i = 1; i < length; i++)
        {
            int b = stream.ReadByte();
            if (b < 0) return false;
            value = (value << 8) | (uint)b;
        }

        return true;
    }

    private static bool TryReadEbmlVint(Stream stream, out ulong value, out int length)
    {
        value = 0;
        length = 0;
        int first = stream.ReadByte();
        if (first < 0) return false;

        int mask = 0x80;
        length = 1;
        while (length <= 8 && (first & mask) == 0)
        {
            mask >>= 1;
            length++;
        }
        if (length > 8) return false;

        // Mask out the marker bit for data (track number)
        value = (ulong)(first & (mask - 1));
        for (int i = 1; i < length; i++)
        {
            int b = stream.ReadByte();
            if (b < 0) return false;
            value = (value << 8) | (uint)b;
        }

        return true;
    }

    #endregion

    #region Helpers

    private static bool GuidEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        return a.AsSpan().SequenceEqual(b);
    }

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

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static byte[] ReadExactly(BinaryReader reader, int count)
    {
        byte[] data = reader.ReadBytes(count);
        if (data.Length < count)
            throw new EndOfStreamException(
                $"Expected {count} bytes but got {data.Length}.");
        return data;
    }

    private static int ReadFully(Stream stream, byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = stream.Read(buffer, offset + totalRead, count - totalRead);
            if (read == 0) break;
            totalRead += read;
        }
        return totalRead;
    }

    private static void CopyBytes(Stream source, Stream dest, long count)
    {
        byte[] buffer = new byte[Math.Min(81920, count)];
        long remaining = count;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = source.Read(buffer, 0, toRead);
            if (read == 0) break;
            dest.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    #endregion
}
