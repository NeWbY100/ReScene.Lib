using System.Text;

namespace ReScene.SRS;

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
internal class MKVContainerRebuilder : IContainerRebuilder
{
    public SRSContainerType ContainerType => SRSContainerType.MKV;

    public void Rebuild(
        string srsFilePath,
        Dictionary<uint, SRSTrackDataBlock> tracks,
        string mediaFilePath,
        Dictionary<uint, long> trackOffsets,
        string outputPath,
        Action<string, int, int, double>? reportProgress,
        CancellationToken ct)
    {
        // Extract attachment data from the media file (fonts, etc.).
        // The SRS preserves attachment headers with original sizes but strips
        // the data, so we need to source it from the media file.
        Queue<(string Name, byte[] Data)> attachments = ExtractMediaAttachments(mediaFilePath, ct);

        // Collect per-track frame data from the media file into MemoryStreams.
        // The SRS file preserves the original block headers (including lacing);
        // only the raw frame bytes come from the media file.
        Dictionary<uint, MemoryStream> frameData =
            CollectMediaFrameData(mediaFilePath, trackOffsets, tracks, reportProgress, ct);

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
            RebuildEBMLFromSRS(srsFs, outFs, frameData, attachments, reportProgress, ct);
        }
        finally
        {
            foreach (MemoryStream ms in frameData.Values)
            {
                ms.Dispose();
            }
        }
    }

    #region Extract Attachments

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
            bufferSize: 80 * 1024);

        while (fs.Position < fs.Length)
        {
            ct.ThrowIfCancellationRequested();

            if (!EBMLReader.TryReadId(fs, out ulong elemId, out int idLen))
            {
                break;
            }

            if (!EBMLReader.TryReadSize(fs, out ulong dataSize, out int sizeLen))
            {
                break;
            }

            long dataStart = fs.Position;
            bool unknownSize = IsEBMLSizeUnknown(dataSize, sizeLen);

            // Container elements: step into
            if (unknownSize || IsEBMLContainerElement(elemId))
            {
                continue;
            }

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
                int totalRead = StreamUtilities.ReadFully(fs, data, 0, data.Length);
                if (totalRead < data.Length)
                {
                    Array.Resize(ref data, totalRead);
                }

                attachments.Enqueue((currentName ?? string.Empty, data));
                currentName = null;
                continue;
            }

            // Skip all other elements
            fs.Position = dataStart + (long)dataSize;
        }

        return attachments;
    }

    #endregion

    #region Collect Frame Data

    /// <summary>
    /// Walks the media file's EBML structure and collects per-track frame data
    /// (after lacing headers) into MemoryStreams. The SRS file preserves the
    /// original sample's block headers including lacing; only the raw frame
    /// bytes are needed from the media file.
    /// </summary>
    private static Dictionary<uint, MemoryStream> CollectMediaFrameData(
        string mediaFilePath,
        Dictionary<uint, long> trackOffsets,
        Dictionary<uint, SRSTrackDataBlock> tracks,
        Action<string, int, int, double>? reportProgress,
        CancellationToken ct)
    {
        var streams = new Dictionary<uint, MemoryStream>();
        var remaining = new Dictionary<uint, long>();

        foreach ((uint trackNumber, SRSTrackDataBlock? track) in tracks)
        {
            streams[trackNumber] = new MemoryStream((int)track.DataLength);
            remaining[trackNumber] = (long)track.DataLength;
        }

        long minOffset = trackOffsets.Values.Min();
        var started = new HashSet<uint>();

        reportProgress?.Invoke("Collecting frame data", 0, tracks.Count, 40);

        using var fs = new FileStream(mediaFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 80 * 1024);

        byte[] copyBuf = new byte[80 * 1024];
        int blocksMatched = 0;

        while (fs.Position < fs.Length)
        {
            ct.ThrowIfCancellationRequested();

            bool allDone = true;
            foreach (long r in remaining.Values)
            {
                if (r > 0)
                {
                    allDone = false;
                    break;
                }
            }

            if (allDone)
            {
                break;
            }

            if (!EBMLReader.TryReadId(fs, out ulong elemId, out int idLen))
            {
                break;
            }

            if (!EBMLReader.TryReadSize(fs, out ulong dataSize, out int sizeLen))
            {
                break;
            }

            long dataStart = fs.Position;
            bool unknownSize = IsEBMLSizeUnknown(dataSize, sizeLen);

            // SimpleBlock (0xA3) or Block (0xA1): extract frame data
            if (elemId is 0xA3 or 0xA1)
            {
                long blockStart = fs.Position;
                if (EBMLReader.TryReadSize(fs, out ulong trackNum, out int vintLen))
                {
                    int blockHeaderBase = vintLen + 2 + 1; // VINT + timecode(2) + flags(1)

                    // Parse lacing to find where frame data starts
                    fs.Position = blockStart + vintLen + 2;
                    int flagsByte = fs.ReadByte();
                    if (flagsByte < 0)
                    {
                        break;
                    }

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
                                        if (b < 0)
                                        {
                                            break;
                                        }

                                        lacingHeaderSize++;
                                    } while (b == 255);
                                }
                            }
                            else if (laceType == 3) // EBML lacing
                            {
                                if (EBMLReader.TryReadSize(fs, out _, out int firstSizeLen))
                                {
                                    lacingHeaderSize += firstSizeLen;
                                    for (int i = 1; i < laceCount; i++)
                                    {
                                        if (EBMLReader.TryReadSize(fs, out _, out int deltaLen))
                                        {
                                            lacingHeaderSize += deltaLen;
                                        }
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
                                    int read = StreamUtilities.ReadFully(fs, verifyBuf, 0, verifyLen);
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
                            if (started.Add(tn))
                            {
                                reportProgress?.Invoke(
                                    $"Track {tn} located at offset {frameDataOffset:N0}", 0, 0, 50);
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
                                if (read == 0)
                                {
                                    break;
                                }

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
            if (unknownSize || IsEBMLContainerElement(elemId))
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

        reportProgress?.Invoke($"Collected {blocksMatched:N0} blocks from media file", 0, 0, 60);

        // Reset all streams to the beginning for reading
        foreach (MemoryStream ms in streams.Values)
        {
            ms.Position = 0;
        }

        return streams;
    }

    #endregion

    #region Rebuild EBML from SRS

    /// <summary>
    /// Walks the SRS file sequentially, writing to the output.
    /// ReSample/SRSF/SRST elements are skipped by ID. Container elements are stepped into.
    /// SimpleBlock/Block: base block header (track VINT + timecode + flags) from SRS,
    /// then lacing + frame data from per-track MemoryStreams collected from the media file.
    /// AttachedFileData: data from media file attachments (stripped from SRS).
    /// </summary>
    private static void RebuildEBMLFromSRS(
        Stream srsFs, Stream outFs,
        Dictionary<uint, MemoryStream> frameData,
        Queue<(string Name, byte[] Data)> attachments,
        Action<string, int, int, double>? reportProgress,
        CancellationToken ct)
    {
        byte[] copyBuf = new byte[80 * 1024];
        int blockCount = 0;

        while (srsFs.Position < srsFs.Length)
        {
            ct.ThrowIfCancellationRequested();
            long elemStart = srsFs.Position;

            if (!EBMLReader.TryReadId(srsFs, out ulong elemId, out int idLen))
            {
                break;
            }

            if (!EBMLReader.TryReadSize(srsFs, out ulong dataSize, out int sizeLen))
            {
                break;
            }

            long dataStart = srsFs.Position;
            bool unknownSize = IsEBMLSizeUnknown(dataSize, sizeLen);
            int headerSize = idLen + sizeLen;

            // Skip ReSample container and SRS child elements
            if (elemId is 0x1F697576 or 0x6A75 or 0x6B75)
            {
                if (!unknownSize)
                {
                    srsFs.Position = dataStart + (long)dataSize;
                }

                continue;
            }

            // Copy the element header to output
            srsFs.Position = elemStart;
            byte[] rawHeader = new byte[headerSize];
            srsFs.ReadExactly(rawHeader, 0, headerSize);
            outFs.Write(rawHeader);

            // Container elements: step into
            if (IsEBMLContainerElement(elemId))
            {
                continue;
            }

            // AttachedFileData (0x465C): data is stripped from SRS, source from media
            if (elemId == 0x465C)
            {
                if (attachments.Count > 0)
                {
                    (string _, byte[]? data) = attachments.Dequeue();
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
                if (EBMLReader.TryReadSize(srsFs, out ulong trackNum, out int vintLen))
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
                            if (EBMLReader.TryReadId(srsFs, out ulong probeId, out _)
                                && IsKnownMKVElementId(probeId))
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
                                    int read = StreamUtilities.ReadFully(srsFs, lacingPeek, 0, peekLen);
                                    if (read > 0)
                                    {
                                        var lacingType = (EBMLLaceType)(flagsByte & 0x06);
                                        (int[] _, int bytesConsumed) = EBMLLacing.GetFrameLengths(
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
                        if (frameData.TryGetValue(tn, out MemoryStream? ms))
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
                                if (rd == 0)
                                {
                                    break;
                                }

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
            {
                StreamUtilities.CopyBytes(srsFs, outFs, (long)dataSize);
            }
        }

        reportProgress?.Invoke($"Wrote {blockCount:N0} blocks to output", 0, 0, 80);
    }

    #endregion

    #region EBML Utilities

    private static bool IsEBMLSizeUnknown(ulong value, int length)
        => value == (1UL << (7 * length)) - 1;

    private static bool IsEBMLContainerElement(ulong id) => id is
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
    private static bool IsKnownMKVElementId(ulong id) => id is
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

    #endregion
}
