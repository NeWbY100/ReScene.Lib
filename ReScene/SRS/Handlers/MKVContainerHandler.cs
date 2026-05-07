using System.Buffers.Binary;
using System.IO.Hashing;

namespace ReScene.SRS;

internal class MKVContainerHandler : IContainerHandler
{
    private const int SignatureSize = 256;

    public SRSContainerType ContainerType => SRSContainerType.MKV;

    #region EBML Constants

    /// <summary>
    /// Container element IDs that we step into (no data of their own).
    /// </summary>
    private static readonly HashSet<ulong> _eBMLContainerElements =
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

    private static readonly ulong _eBMLIdBlock = 0xA3;             // SimpleBlock
    private static readonly ulong _eBMLIdBlockGroupBlock = 0xA1;  // Block inside BlockGroup
    private static readonly ulong _eBMLIdTrackNumber = 0xD7;       // TrackNumber in TrackEntry
    private static readonly ulong _eBMLIdContentCompAlgo = 0x4254; // ContentCompAlgo
    private static readonly ulong _eBMLIdContentCompSettings = 0x4255; // ContentCompSettings

    /// <summary>
    /// EBML element IDs that we should step into during SRS writing (they are containers).
    /// </summary>
    private static readonly HashSet<ulong> _mKVSrsContainers =
    [
        0x1F43B675, // Cluster
        0xA0,       // BlockGroup
        0x1941A469, // Attachments
        0x61A7,     // AttachedFile
    ];

    #endregion

    /// <summary>
    /// State tracked across recursive ProfileEbmlElements calls for MKV profiling.
    /// Stores the current track number context and header stripping flag during TrackEntry parsing.
    /// </summary>
    private class EBMLProfileState
    {
        public int CurrentTrackNumber
        {
            get; set;
        }
        public bool HeaderStrippingDetected
        {
            get; set;
        }
    }

    public (List<TrackInfo> Tracks, uint CRC32, long TotalSize) Profile(string samplePath, CancellationToken ct)
    {
        var trackMap = new Dictionary<int, TrackInfo>();
        long otherLength = 0;
        var crc = new Crc32();

        using var fs = new FileStream(samplePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        ProfileEbmlElements(fs, 0, fs.Length, trackMap, ref otherLength, crc, isSegmentLevel: false, ct);

        long totalSize = otherLength;
        foreach (TrackInfo t in trackMap.Values)
        {
            totalSize += t.DataLength;
        }

        Span<byte> hash = stackalloc byte[4];
        crc.GetHashAndReset(hash);
        uint crc32 = BinaryPrimitives.ReadUInt32LittleEndian(hash);

        return (trackMap.Values.ToList(), crc32, totalSize);
    }

    public void WriteSrs(
        string outputPath, string samplePath,
        List<TrackInfo> tracks, long sampleSize, uint sampleCrc32,
        SRSCreationOptions options, CancellationToken ct)
    {
        using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var inFs = new FileStream(samplePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        WriteMkvSrsElements(outFs, inFs, 0, inFs.Length, tracks, samplePath, sampleSize, sampleCrc32,
            options, resampleInjected: false, ct);
    }

    #region Profiling

    private static void ProfileEbmlElements(
        Stream fs, long start, long end,
        Dictionary<int, TrackInfo> trackMap,
        ref long otherLength,
        Crc32 crc,
        bool isSegmentLevel,
        CancellationToken ct,
        EBMLProfileState? state = null)
    {
        state ??= new EBMLProfileState();
        fs.Position = start;

        while (fs.Position < end)
        {
            ct.ThrowIfCancellationRequested();
            long elemStart = fs.Position;

            if (!EBMLReader.TryReadId(fs, out ulong elemId, out int idLen))
            {
                break;
            }

            if (!EBMLReader.TryReadSize(fs, out ulong dataSize, out int sizeLen))
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

            if (_eBMLContainerElements.Contains(elemId))
            {
                // When entering ContentCompression (0x5034), mark that compression is present
                if (elemId == 0x5034 && trackMap.TryGetValue(state.CurrentTrackNumber, out TrackInfo? compTrack))
                {
                    // Mark that a compression element exists (exact algorithm comes from child)
                    compTrack.CompressionAlgorithm ??= -1; // placeholder until we read ContentCompAlgo
                }

                // Step into container element
                ProfileEbmlElements(fs, dataStart, elemEnd, trackMap, ref otherLength, crc,
                    isSegmentLevel: elemId == 0x18538067 || isSegmentLevel, ct, state);
            }
            else if (elemId == _eBMLIdBlock || elemId == _eBMLIdBlockGroupBlock)
            {
                // Parse block: track number (EBML VINT) + timecode (2 bytes) + flags (1 byte)
                if (!EBMLReader.TryReadSize(fs, out ulong trackNum, out int vintLen))
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
                var laceType = (EBMLLaceType)(flagsByte & 0x06);

                // Calculate remaining data after base block header
                int dataAfterBaseHeader = (int)((long)dataSize - blockHeaderBase);

                // Parse lacing to determine frame sizes and lacing header size
                int lacingHeaderSize = 0;
                if (laceType != EBMLLaceType.None && dataAfterBaseHeader > 0)
                {
                    // Read the lacing header data to parse it
                    byte[] lacingData = ReadExactly(fs, Math.Min(dataAfterBaseHeader, 256)); // lacing headers are small
                    (int[] _, int bytesConsumed) = EBMLLacing.GetFrameLengths(
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
                if (!trackMap.TryGetValue(tn, out TrackInfo? track))
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
            else if (elemId == _eBMLIdTrackNumber)
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
            else if (elemId == _eBMLIdContentCompAlgo)
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

                    if (trackMap.TryGetValue(state.CurrentTrackNumber, out TrackInfo? track))
                    {
                        track.CompressionAlgorithm = algorithm;
                    }

                    state.HeaderStrippingDetected = algorithm == 3;
                }
            }
            else if (elemId == _eBMLIdContentCompSettings)
            {
                // Read compression settings (stripped header bytes)
                long remaining = elemEnd - fs.Position;
                if (remaining > 0)
                {
                    byte[] data = ReadExactly(fs, (int)remaining);
                    otherLength += remaining;
                    crc.Append(data);

                    if (state.HeaderStrippingDetected &&
                        trackMap.TryGetValue(state.CurrentTrackNumber, out TrackInfo? track))
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

    #endregion

    #region Writing

    private static void WriteMkvSrsElements(
        Stream outFs, Stream inFs,
        long start, long end,
        List<TrackInfo> tracks, string samplePath, long sampleSize, uint sampleCrc32,
        SRSCreationOptions options,
        bool resampleInjected,
        CancellationToken ct)
    {
        inFs.Position = start;

        while (inFs.Position < end)
        {
            ct.ThrowIfCancellationRequested();
            long elemStart = inFs.Position;

            if (!EBMLReader.TryReadId(inFs, out ulong elemId, out int idLen))
            {
                break;
            }

            if (!EBMLReader.TryReadSize(inFs, out ulong dataSize, out int sizeLen))
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

                WriteMkvSrsElements(outFs, inFs, dataStart, elemEnd, tracks, samplePath, sampleSize,
                    sampleCrc32, options, resampleInjected, ct);
            }
            else if (_mKVSrsContainers.Contains(elemId))
            {
                outFs.Write(rawHeader);
                WriteMkvSrsElements(outFs, inFs, dataStart, elemEnd, tracks, samplePath, sampleSize,
                    sampleCrc32, options, resampleInjected, ct);
            }
            else if (elemId == 0x465C) // AttachedFileData - skip data
            {
                outFs.Write(rawHeader);
                // Skip attachment data
            }
            else if (elemId == _eBMLIdBlock || elemId == _eBMLIdBlockGroupBlock)
            {
                // Write header + block header (including lacing header), skip frame data
                outFs.Write(rawHeader);

                // Parse and copy block header: track number VINT + timecode(2) + flags(1) + lacing header
                long blockParseStart = inFs.Position;
                if (EBMLReader.TryReadSize(inFs, out _, out int vintLen))
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
                        var laceType = (EBMLLaceType)(flagsByte & 0x06);

                        int lacingHeaderSize = 0;
                        if (laceType != EBMLLaceType.None)
                        {
                            // Read lacing header to determine its size
                            int dataAfterBase = (int)((long)dataSize - blockHeaderBase);
                            if (dataAfterBase > 0)
                            {
                                byte[] lacingPeek = ReadExactly(inFs, Math.Min(dataAfterBase, 256));
                                (int[] _, int bytesConsumed) = EBMLLacing.GetFrameLengths(
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
                    StreamUtilities.CopyBytes(inFs, outFs, remaining);
                }
            }

            inFs.Position = elemEnd;
        }
    }

    private static void WriteEbmlReSampleElement(
        Stream outFs, List<TrackInfo> tracks,
        string samplePath, long sampleSize, uint sampleCrc32,
        SRSCreationOptions options)
    {
        // Build the file and track sub-elements
        byte[] srsfPayload = SRSPayloadSerializer.SerializeSrsf(samplePath, sampleSize, sampleCrc32, options);
        byte[] srsfElement = BuildEbmlElement(0x6A75, srsfPayload); // RESAMPLE_FILE

        bool bigFile = sampleSize >= 0x80000000;
        var trackElements = new List<byte[]>();
        foreach (TrackInfo track in tracks)
        {
            byte[] srstPayload = SRSPayloadSerializer.SerializeSrst(track, bigFile);
            trackElements.Add(BuildEbmlElement(0x6B75, srstPayload)); // RESAMPLE_TRACK
        }

        // Total child size
        long childSize = srsfElement.Length;
        foreach (var te in trackElements)
        {
            childSize += te.Length;
        }

        // Write the ReSample container element (ID: 0x1F697576)
        byte[] resampleHeader = BuildEbmlElementHeader(0x1F697576, childSize);
        outFs.Write(resampleHeader);
        outFs.Write(srsfElement);
        foreach (var te in trackElements)
        {
            outFs.Write(te);
        }
    }

    #endregion

    #region EBML Helpers

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

    #endregion
}
