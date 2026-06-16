using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace ReScene.SRS;

internal class AVIContainerHandler : IContainerHandler
{
    public SRSContainerType ContainerType => SRSContainerType.AVI;

    public (List<TrackInfo> Tracks, uint CRC32, long TotalSize) Profile(
        string samplePath,
        Action<long, long, int>? reportScanProgress,
        CancellationToken ct)
    {
        var trackMap = new Dictionary<int, TrackInfo>();
        long otherLength = 0;
        var crc = new Crc32();

        using var fs = new FileStream(samplePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(fs);

        ProfileRiffChunks(reader, fs, 0, fs.Length, trackMap, ref otherLength, crc,
            reportScanProgress, ct);

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
        using var reader = new BinaryReader(inFs);

        WriteRiffSRS(outFs, reader, inFs, 0, inFs.Length,
            tracks, samplePath, sampleSize, sampleCRC32, options, moviInjected: false, ct);
    }

    #region Profiling

    private static void ProfileRiffChunks(
        BinaryReader reader, Stream fs,
        long start, long end,
        Dictionary<int, TrackInfo> trackMap,
        ref long otherLength,
        Crc32 crc,
        Action<long, long, int>? reportScanProgress,
        CancellationToken ct)
    {
        fs.Position = start;
        long totalLength = fs.Length;
        int lastPercent = -1;

        while (fs.Position + 8 <= end)
        {
            ct.ThrowIfCancellationRequested();

            if (reportScanProgress is not null)
            {
                int pct = (int)(fs.Position * 100 / Math.Max(1L, totalLength));
                if (pct != lastPercent)
                {
                    lastPercent = pct;
                    reportScanProgress(fs.Position, totalLength, pct);
                }
            }

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

                long childEnd = chunkStart + 8 + chunkSize;
                if (childEnd > end)
                {
                    childEnd = end;
                }

                ProfileRiffChunks(reader, fs, fs.Position, childEnd, trackMap, ref otherLength, crc,
                    reportScanProgress, ct);

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
                    if (!trackMap.TryGetValue(trackNumber, out TrackInfo? track))
                    {
                        track = new TrackInfo { TrackNumber = trackNumber };
                        trackMap[trackNumber] = track;
                    }

                    track.DataLength += chunkSize;

                    // Read chunk data for CRC and signature
                    byte[] moviData = ReadExactly(reader, (int)chunkSize);
                    crc.Append(moviData);

                    // Build signature from first SignatureSize bytes of track data
                    track.AppendSignature(moviData, TrackInfo.SignatureSize);
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

    #endregion

    #region Writing

    private static void WriteRiffSRS(
        Stream outFs, BinaryReader reader, Stream inFs,
        long start, long end,
        List<TrackInfo> tracks, string samplePath, long sampleSize, uint sampleCRC32,
        SRSCreationOptions options,
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
                    WriteSrsfRiff(outFs, samplePath, sampleSize, sampleCRC32, options);
                    foreach (TrackInfo track in tracks)
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

                WriteRiffSRS(outFs, reader, inFs, inFs.Position, childEnd,
                    tracks, samplePath, sampleSize, sampleCRC32, options, moviInjected, ct);

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

    private static void WriteSrsfRiff(Stream outFs, string samplePath, long sampleSize, uint sampleCRC32,
        SRSCreationOptions options)
    {
        byte[] payload = SRSPayloadSerializer.SerializeSrsf(samplePath, sampleSize, sampleCRC32, options);
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
        byte[] payload = SRSPayloadSerializer.SerializeSrst(track, bigFile);
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

    #endregion
}
