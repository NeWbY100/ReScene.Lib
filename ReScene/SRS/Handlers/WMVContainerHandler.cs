using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace ReScene.SRS;

internal class WMVContainerHandler : IContainerHandler
{
    private const int SignatureSize = 256;

    private static readonly byte[] _guidSRSFile = Encoding.ASCII.GetBytes("SRSFSRSFSRSFSRSF");
    private static readonly byte[] _guidSRSTrack = Encoding.ASCII.GetBytes("SRSTSRSTSRSTSRST");

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

                        // For signature purposes, accumulate all packet data as one track.
                        int streamNum = 1;

                        if (!trackMap.TryGetValue(streamNum, out TrackInfo? track))
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

    public void WriteSRS(
        string outputPath, string samplePath,
        List<TrackInfo> tracks, long sampleSize, uint sampleCRC32,
        SRSCreationOptions options, CancellationToken ct)
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

                    // Data packets are stripped: the SRS keeps the ASF header objects + SRSF/SRST.
                }

                // Skip to end of data object
                inFs.Position = objEnd;

                // Inject SRSF/SRST after data object
                WriteSrsfASF(outFs, samplePath, sampleSize, sampleCRC32, options);
                foreach (TrackInfo track in tracks)
                {
                    WriteSrstASF(outFs, track, sampleSize >= 0x80000000);
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
        outFs.Write(_guidSRSFile);
        Span<byte> sizeBytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(sizeBytes, (ulong)(payload.Length + 16 + 8));
        outFs.Write(sizeBytes);
        outFs.Write(payload);
    }

    private static void WriteSrstASF(Stream outFs, TrackInfo track, bool bigFile)
    {
        byte[] payload = SRSPayloadSerializer.SerializeSrst(track, bigFile);
        outFs.Write(_guidSRSTrack);
        Span<byte> sizeBytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(sizeBytes, (ulong)(payload.Length + 16 + 8));
        outFs.Write(sizeBytes);
        outFs.Write(payload);
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
