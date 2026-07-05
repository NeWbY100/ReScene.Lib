using System.Buffers.Binary;
using System.IO.Hashing;

namespace ReScene.SRS;

internal class MKVContainerHandler : IContainerHandler
{
    private const int SignatureSize = TrackInfo.SignatureSize;

    /// <summary>
    /// Maximum signature length, in <see cref="SignatureSize"/>-byte steps (~10 KiB). Mirrors
    /// pyrescene's <c>max_loops</c>; a signature that stays "ASCII" up to this cap falls back to one step.
    /// </summary>
    private const int MaxSignatureBlocks = 40;

    /// <summary>
    /// Number of trailing bytes in each <see cref="SignatureSize"/>-byte step that must be
    /// all-ASCII for the signature to keep growing. Mirrors pyrescene's <c>minimum_signature_size</c>
    /// window heuristic.
    /// </summary>
    private const int SignatureAsciiWindowSize = 64;

    /// <summary>
    /// First byte value that is NOT ASCII. Bytes &lt; <see cref="AsciiBoundary"/> are codec
    /// parameter-set data; bytes &gt;= <see cref="AsciiBoundary"/> indicate real binary frame data.
    /// </summary>
    private const int AsciiBoundary = 0x80;

    public SRSContainerType ContainerType => SRSContainerType.MKV;

    #region EBML Constants

    /// <summary>
    /// EBML element IDs that we should step into during SRS writing (they are containers).
    /// This is a distinct 4-element set from <see cref="EBMLIds.IsContainer"/>.
    /// </summary>
    private static readonly HashSet<ulong> _mKVSRSContainers =
    [
        EBMLIds.Cluster,
        EBMLIds.BlockGroup,
        EBMLIds.Attachments,
        EBMLIds.AttachedFile,
    ];

    #endregion

    /// <summary>
    /// State tracked across recursive ProfileEBMLElements calls for MKV profiling.
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

    public (List<TrackInfo> Tracks, uint CRC32, long TotalSize) Profile(
        string samplePath,
        Action<long, long, int>? reportScanProgress,
        CancellationToken ct)
    {
        var trackMap = new Dictionary<int, TrackInfo>();
        long otherLength = 0;
        var crc = new Crc32();

        using var fs = new FileStream(samplePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long totalLength = fs.Length;
        int lastPercent = -1;

        void onPosition()
        {
            if (reportScanProgress is null)
            {
                return;
            }

            int pct = (int)(fs.Position * 100 / Math.Max(1L, totalLength));
            if (pct != lastPercent)
            {
                lastPercent = pct;
                reportScanProgress(fs.Position, totalLength, pct);
            }
        }

        ProfileEBMLElements(fs, 0, fs.Length, trackMap, ref otherLength, crc,
            isSegmentLevel: false, ct, onPosition: onPosition);

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

    public void WriteSRS(
        string outputPath, string samplePath,
        List<TrackInfo> tracks, long sampleSize, uint sampleCRC32,
        SRSCreationOptions options, CancellationToken ct)
    {
        using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var inFs = new FileStream(samplePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        WriteMKVSRSElements(outFs, inFs, 0, inFs.Length, tracks, samplePath, sampleSize, sampleCRC32,
            options, resampleInjected: false, ct);
    }

    #region Profiling

    private static void ProfileEBMLElements(
        Stream fs, long start, long end,
        Dictionary<int, TrackInfo> trackMap,
        ref long otherLength,
        Crc32 crc,
        bool isSegmentLevel,
        CancellationToken ct,
        EBMLProfileState? state = null,
        Action? onPosition = null)
    {
        state ??= new EBMLProfileState();
        fs.Position = start;

        while (fs.Position < end)
        {
            ct.ThrowIfCancellationRequested();
            onPosition?.Invoke();
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

            if (EBMLIds.IsContainer(elemId))
            {
                // When entering ContentCompression, mark that compression is present
                if (elemId == EBMLIds.ContentCompression && trackMap.TryGetValue(state.CurrentTrackNumber, out TrackInfo? compTrack))
                {
                    // Mark that a compression element exists (exact algorithm comes from child)
                    compTrack.CompressionAlgorithm ??= TrackInfo.CompressionAlgoUnknown; // placeholder until we read ContentCompAlgo
                }

                // Step into container element
                ProfileEBMLElements(fs, dataStart, elemEnd, trackMap, ref otherLength, crc,
                    isSegmentLevel: elemId == EBMLIds.Segment || isSegmentLevel, ct, state, onPosition);
            }
            else if (elemId is EBMLIds.SimpleBlock or EBMLIds.Block)
            {
                // Parse block: track number (EBML VINT) + timecode (2 bytes) + flags (1 byte)
                if (!EBMLReader.TryReadSize(fs, out ulong trackNum, out int vintLen))
                {
                    fs.Position = elemEnd;
                    continue;
                }

                int blockHeaderBase = vintLen + MKVBlockLayout.FixedHeaderOverhead; // VINT + timecode + flags
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
                var laceType = (EBMLLaceType)(flagsByte & MKVBlockFlags.LacingMask);

                // Calculate remaining data after base block header
                int dataAfterBaseHeader = (int)((long)dataSize - blockHeaderBase);

                // Measure the lacing header with the shared, unbounded reader (the same one the
                // rebuilder uses) so creation and rebuild always agree on where frame data begins —
                // a >256-byte lacing header must not be truncated (finding #7).
                int lacingHeaderSize = 0;
                if (laceType != EBMLLaceType.None && dataAfterBaseHeader > 0)
                {
                    lacingHeaderSize = EBMLLacing.ReadLacingHeaderSize(fs, dataStart, blockHeaderBase, flagsByte);
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
                byte[] frameData = StreamUtilities.ReadAtMost(fs, (int)Math.Min(frameDataLen, elemEnd - fs.Position));
                crc.Append(frameData);

                // Build the track signature. Mirrors pyrescene's minimum_signature_size: grow the
                // signature in SignatureSize-byte steps while the data still looks like codec
                // parameter-set bytes (the last 64 bytes of each step are ASCII, all < 0x80), stopping
                // once real binary frame data appears — capped at MaxSignatureBlocks (~10 KiB), falling
                // back to one step for all-ASCII (e.g. subtitle) tracks. A fixed 256-byte signature is
                // not unique for x265 tracks whose long parameter sets would otherwise yield a wrong
                // match offset. As pyrescene notes, we can ignore laces: what we want starts at the block start.
                if (track.SignatureBytes.Length < SignatureSize)
                {
                    int target = MinimumSignatureSize(frameData, track.SignatureBytes.Length);
                    int take = Math.Min(target, frameData.Length);
                    if (take > 0)
                    {
                        byte[] newSig = new byte[track.SignatureBytes.Length + take];
                        track.SignatureBytes.CopyTo(newSig, 0);
                        Array.Copy(frameData, 0, newSig, track.SignatureBytes.Length, take);
                        track.SignatureBytes = newSig;
                    }
                }
            }
            else if (elemId == EBMLIds.TrackNumber)
            {
                // Read TrackNumber element to track current context
                long remaining = elemEnd - fs.Position;
                if (remaining > 0)
                {
                    byte[] data = StreamUtilities.ReadAtMost(fs, (int)remaining);
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
            else if (elemId == EBMLIds.ContentCompAlgo)
            {
                // Read compression algorithm
                long remaining = elemEnd - fs.Position;
                if (remaining > 0)
                {
                    byte[] data = StreamUtilities.ReadAtMost(fs, (int)remaining);
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

                    state.HeaderStrippingDetected = algorithm == EBMLIds.ContentCompAlgoHeaderStripping;
                }
            }
            else if (elemId == EBMLIds.ContentCompSettings)
            {
                // Read compression settings (stripped header bytes)
                long remaining = elemEnd - fs.Position;
                if (remaining > 0)
                {
                    byte[] data = StreamUtilities.ReadAtMost(fs, (int)remaining);
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
                    byte[] data = StreamUtilities.ReadAtMost(fs, (int)remaining);
                    otherLength += remaining;
                    crc.Append(data);
                }
            }

            fs.Position = elemEnd;
        }
    }

    #endregion

    #region Writing

    private static void WriteMKVSRSElements(
        Stream outFs, Stream inFs,
        long start, long end,
        List<TrackInfo> tracks, string samplePath, long sampleSize, uint sampleCRC32,
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

            if (elemId == EBMLIds.Segment)
            {
                outFs.Write(rawHeader);

                // Inject ReSample element
                if (!resampleInjected)
                {
                    WriteEBMLReSampleElement(outFs, tracks, samplePath, sampleSize, sampleCRC32, options);
                    resampleInjected = true;
                }

                WriteMKVSRSElements(outFs, inFs, dataStart, elemEnd, tracks, samplePath, sampleSize,
                    sampleCRC32, options, resampleInjected, ct);
            }
            else if (_mKVSRSContainers.Contains(elemId))
            {
                outFs.Write(rawHeader);
                WriteMKVSRSElements(outFs, inFs, dataStart, elemEnd, tracks, samplePath, sampleSize,
                    sampleCRC32, options, resampleInjected, ct);
            }
            else if (elemId == EBMLIds.FileData) // AttachedFileData - skip data
            {
                outFs.Write(rawHeader);
                // Skip attachment data
            }
            else if (elemId is EBMLIds.SimpleBlock or EBMLIds.Block)
            {
                // Write header + block header (including lacing header), skip frame data
                outFs.Write(rawHeader);

                // Parse and copy block header: track number VINT + timecode(2) + flags(1) + lacing header
                long blockParseStart = inFs.Position;
                if (EBMLReader.TryReadSize(inFs, out _, out int vintLen))
                {
                    int blockHeaderBase = vintLen + MKVBlockLayout.FixedHeaderOverhead; // VINT + timecode + flags
                    long available = elemEnd - blockParseStart;
                    if (blockHeaderBase <= available)
                    {
                        // Read the base block header to extract lace type from flags
                        inFs.Position = blockParseStart;
                        byte[] baseHeader = new byte[blockHeaderBase];
                        inFs.ReadExactly(baseHeader, 0, blockHeaderBase);

                        byte flagsByte = baseHeader[blockHeaderBase - 1];
                        var laceType = (EBMLLaceType)(flagsByte & MKVBlockFlags.LacingMask);

                        int lacingHeaderSize = 0;
                        if (laceType != EBMLLaceType.None)
                        {
                            int dataAfterBase = (int)((long)dataSize - blockHeaderBase);
                            if (dataAfterBase > 0)
                            {
                                // Shared, unbounded reader (matches the rebuilder) — no 256-byte cap (#7).
                                lacingHeaderSize = EBMLLacing.ReadLacingHeaderSize(inFs, blockParseStart, blockHeaderBase, flagsByte);
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

    private static void WriteEBMLReSampleElement(
        Stream outFs, List<TrackInfo> tracks,
        string samplePath, long sampleSize, uint sampleCRC32,
        SRSCreationOptions options)
    {
        // Build the file and track sub-elements
        byte[] srsfPayload = SRSPayloadSerializer.SerializeSRSF(samplePath, sampleSize, sampleCRC32, options);
        byte[] srsfElement = EBMLWriter.BuildEBMLElement(EBMLIds.ResampleFile, srsfPayload); // RESAMPLE_FILE

        bool bigFile = sampleSize >= SRSConstants.BigFileSizeThreshold;
        var trackElements = new List<byte[]>();
        foreach (TrackInfo track in tracks)
        {
            byte[] srstPayload = SRSPayloadSerializer.SerializeSRST(track, bigFile);
            trackElements.Add(EBMLWriter.BuildEBMLElement(EBMLIds.ResampleTrack, srstPayload)); // RESAMPLE_TRACK
        }

        // Total child size
        long childSize = srsfElement.Length;
        foreach (byte[] te in trackElements)
        {
            childSize += te.Length;
        }

        // Write the ReSample container element (ID: 0x1F697576)
        byte[] resampleHeader = EBMLWriter.BuildEBMLElementHeader(EBMLIds.ReSampleContainer, childSize);
        outFs.Write(resampleHeader);
        outFs.Write(srsfElement);
        foreach (byte[] te in trackElements)
        {
            outFs.Write(te);
        }
    }

    #endregion

    #region Signature sizing

    /// <summary>
    /// pyrescene's <c>minimum_signature_size</c>: returns how many more bytes of
    /// <paramref name="content"/> to append to a signature that already holds
    /// <paramref name="alreadyInSig"/> bytes. The signature grows in <see cref="SignatureSize"/>-byte
    /// steps while the last 64 bytes of each step are ASCII (all &lt; 0x80 — codec parameter-set data),
    /// stopping at the first step that contains real binary frame data. Capped at
    /// <see cref="MaxSignatureBlocks"/> steps; an all-ASCII run (e.g. subtitles) falls back to one step.
    /// </summary>
    internal static int MinimumSignatureSize(byte[] content, int alreadyInSig)
    {
        int loop;
        for (loop = 1; loop <= MaxSignatureBlocks; loop++)
        {
            int offs = SignatureSize * loop - alreadyInSig;
            if (!IsAsciiRange(content, offs - SignatureAsciiWindowSize, offs))
            {
                break;
            }
        }

        // Python's loop variable ends at MaxSignatureBlocks when the loop completes without breaking.
        if (loop > MaxSignatureBlocks)
        {
            loop = MaxSignatureBlocks;
        }

        // Reaching the cap (or staying ASCII throughout) keeps the signature minimal.
        int lsig = loop == MaxSignatureBlocks ? SignatureSize : SignatureSize * loop;
        return lsig - alreadyInSig;
    }

    /// <summary>
    /// True iff every byte of <paramref name="content"/> in Python's slice
    /// <c>content[start:end]</c> is ASCII (&lt; 0x80). Replicates Python's slicing exactly: a negative
    /// <paramref name="start"/> indexes from the end (<c>content.Length + start</c>, floored at 0), an
    /// out-of-range <paramref name="end"/> is clamped, and an empty/inverted range is ASCII (matching
    /// <c>b''.decode('ascii')</c>). The negative case arises only when a prior block already left
    /// &gt;192 signature bytes; for a track's first block (start = 192) it never triggers.
    /// </summary>
    internal static bool IsAsciiRange(byte[] content, int start, int end)
    {
        if (start < 0)
        {
            start = Math.Max(0, content.Length + start);
        }

        if (end > content.Length)
        {
            end = content.Length;
        }

        for (int i = start; i < end; i++)
        {
            if (content[i] >= AsciiBoundary)
            {
                return false;
            }
        }

        return true;
    }

    #endregion
}
