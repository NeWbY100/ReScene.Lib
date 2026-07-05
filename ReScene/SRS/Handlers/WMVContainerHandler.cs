using System.Buffers.Binary;
using System.IO.Hashing;

namespace ReScene.SRS;

internal class WMVContainerHandler : IContainerHandler
{
    // WMV/ASF represents all packet data as a single virtual track.
    private const int VirtualTrackNumber = 1;

    public SRSContainerType ContainerType => SRSContainerType.WMV;

    public (List<TrackInfo> Tracks, uint CRC32, long TotalSize) Profile(
        string samplePath,
        Action<long, long, int>? reportScanProgress,
        CancellationToken ct)
    {
        var trackMap = new Dictionary<int, TrackInfo>();
        long totalLength = 0;
        var crc = new Crc32();

        using var fs = new FileStream(samplePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        while (fs.Position + AsfGuids.ObjectHeaderSize <= fs.Length)
        {
            ct.ThrowIfCancellationRequested();
            long objStart = fs.Position;

            byte[] header = new byte[AsfGuids.ObjectHeaderSize];
            if (fs.Read(header, 0, AsfGuids.ObjectHeaderSize) < AsfGuids.ObjectHeaderSize)
            {
                break;
            }

            ulong objSize = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(AsfGuids.GuidSize));
            if (objSize < AsfGuids.ObjectHeaderSize)
            {
                break;
            }

            totalLength += AsfGuids.ObjectHeaderSize;
            crc.Append(header);

            long dataSize = (long)objSize - AsfGuids.ObjectHeaderSize;
            long objEnd = objStart + (long)objSize;
            if (objEnd > fs.Length)
            {
                objEnd = fs.Length;
            }

            // Check if this is the Data Object (GUID: 3626B2758E66CF11A6D900AA0062CE6C)
            bool isDataObject = header.AsSpan().StartsWith(AsfGuids.DataObjectPrefix);

            if (isDataObject && dataSize >= AsfGuids.DataObjectHeaderLength)
            {
                // Data object has: file ID (16 bytes) + total packets (8 bytes) + reserved (2 bytes)
                byte[] dataHeader = StreamUtilities.ReadAtMost(fs, AsfGuids.DataObjectHeaderLength);
                totalLength += AsfGuids.DataObjectHeaderLength;
                crc.Append(dataHeader);

                ulong totalPackets = BinaryPrimitives.ReadUInt64LittleEndian(dataHeader.AsSpan(AsfGuids.DataObjectFileIdSize));
                long packetDataSize = objEnd - fs.Position;

                if (totalPackets > 0 && packetDataSize > 0)
                {
                    int packetSize = (int)(packetDataSize / (long)totalPackets);

                    for (ulong i = 0; i < totalPackets && fs.Position + packetSize <= objEnd; i++)
                    {
                        byte[] packetData = StreamUtilities.ReadAtMost(fs, packetSize);
                        crc.Append(packetData);

                        // For signature purposes, accumulate all packet data as one track.
                        int streamNum = VirtualTrackNumber;

                        if (!trackMap.TryGetValue(streamNum, out TrackInfo? track))
                        {
                            track = new TrackInfo { TrackNumber = streamNum };
                            trackMap[streamNum] = track;
                        }

                        track.DataLength += packetSize;

                        track.AppendSignature(packetData, TrackInfo.SignatureSize);
                    }
                }

                // Read any remaining
                if (fs.Position < objEnd)
                {
                    byte[] rest = StreamUtilities.ReadAtMost(fs, (int)(objEnd - fs.Position));
                    totalLength += rest.Length;
                    crc.Append(rest);
                }
            }
            else
            {
                // Non-data object: read and CRC
                if (dataSize > 0)
                {
                    byte[] data = StreamUtilities.ReadAtMost(fs, (int)Math.Min(dataSize, objEnd - fs.Position));
                    totalLength += data.Length;
                    crc.Append(data);
                }
            }

            fs.Position = objEnd;
        }

        Span<byte> hash = stackalloc byte[4];
        crc.GetHashAndReset(hash);
        uint crc32 = BinaryPrimitives.ReadUInt32LittleEndian(hash);

        // Packet payload bytes were accumulated into each track's DataLength but not into
        // totalLength; include them so the reported size equals the file size (matching the AVI/MKV/
        // MP4/FLAC handlers). Without this every valid WMV trips SRSWriter's "size mismatch" warning.
        List<TrackInfo> tracks = [.. trackMap.Values];
        long totalSize = totalLength;
        foreach (TrackInfo t in tracks)
        {
            totalSize += t.DataLength;
        }

        return (tracks, crc32, totalSize);
    }

    public void WriteSRS(
        string outputPath, string samplePath,
        List<TrackInfo> tracks, long sampleSize, uint sampleCRC32,
        SRSCreationOptions options, CancellationToken ct)
    {
        using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var inFs = new FileStream(samplePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        while (inFs.Position + AsfGuids.ObjectHeaderSize <= inFs.Length)
        {
            ct.ThrowIfCancellationRequested();
            long objStart = inFs.Position;

            byte[] header = new byte[AsfGuids.ObjectHeaderSize];
            inFs.ReadExactly(header, 0, AsfGuids.ObjectHeaderSize);

            ulong objSize = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(AsfGuids.GuidSize));
            if (objSize < AsfGuids.ObjectHeaderSize)
            {
                break;
            }

            long objEnd = objStart + (long)objSize;
            if (objEnd > inFs.Length)
            {
                objEnd = inFs.Length;
            }

            // Check if Data Object
            bool isDataObject = header.AsSpan().StartsWith(AsfGuids.DataObjectPrefix);

            outFs.Write(header);

            if (isDataObject)
            {
                // Parse data packets, write only packet headers (strip payload data)
                long dataRemaining = objEnd - inFs.Position;
                if (dataRemaining >= AsfGuids.DataObjectHeaderLength)
                {
                    byte[] dataHeader = new byte[AsfGuids.DataObjectHeaderLength];
                    inFs.ReadExactly(dataHeader, 0, AsfGuids.DataObjectHeaderLength);
                    outFs.Write(dataHeader);

                    // Data packets are stripped: the SRS keeps the ASF header objects + SRSF/SRST.
                }

                // Skip to end of data object
                inFs.Position = objEnd;

                // Inject SRSF/SRST after data object
                WriteSrsfASF(outFs, samplePath, sampleSize, sampleCRC32, options);
                foreach (TrackInfo track in tracks)
                {
                    WriteSrstASF(outFs, track, sampleSize >= SrsConstants.BigFileSizeThreshold);
                }
            }
            else
            {
                // Copy object verbatim
                long remaining = objEnd - inFs.Position;
                if (remaining > 0)
                {
                    StreamUtilities.CopyBytes(inFs, outFs, remaining);
                }
            }

            inFs.Position = objEnd;
        }
    }

    #region Writing Helpers

    private static void WriteSrsfASF(Stream outFs, string samplePath, long sampleSize, uint sampleCRC32,
        SRSCreationOptions options)
    {
        byte[] payload = SRSPayloadSerializer.SerializeSrsf(samplePath, sampleSize, sampleCRC32, options);
        outFs.Write(AsfSrsGuids.GuidSRSFile);
        Span<byte> sizeBytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(sizeBytes, (ulong)(payload.Length + AsfGuids.ObjectHeaderSize));
        outFs.Write(sizeBytes);
        outFs.Write(payload);
    }

    private static void WriteSrstASF(Stream outFs, TrackInfo track, bool bigFile)
    {
        byte[] payload = SRSPayloadSerializer.SerializeSrst(track, bigFile);
        outFs.Write(AsfSrsGuids.GuidSRSTrack);
        Span<byte> sizeBytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(sizeBytes, (ulong)(payload.Length + AsfGuids.ObjectHeaderSize));
        outFs.Write(sizeBytes);
        outFs.Write(payload);
    }

    #endregion
}
