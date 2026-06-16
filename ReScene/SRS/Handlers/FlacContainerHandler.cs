using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace ReScene.SRS;

internal class FlacContainerHandler : IContainerHandler
{
    public SRSContainerType ContainerType => SRSContainerType.FLAC;

    public (List<TrackInfo> Tracks, uint CRC32, long TotalSize) Profile(
        string samplePath,
        Action<long, long, int>? reportScanProgress,
        CancellationToken ct)
    {
        var track = new TrackInfo { TrackNumber = 1 };
        long otherLength = 0;
        var crc = new Crc32();

        using var fs = new FileStream(samplePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        // Check for ID3v2 wrapper before fLaC marker
        (bool id3Found, int id3Size) = FlacMetadataReader.DetectId3v2Wrapper(fs);
        if (id3Found)
        {
            // CRC and account for the ID3v2 wrapper
            fs.Position = 0;
            byte[] id3Data = StreamUtilities.ReadAtMost(fs, id3Size);
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

            (bool isLast, byte blockType, int payloadSize) = FlacMetadataReader.ReadMetadataBlockHeader(reader);

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
                byte[] data = StreamUtilities.ReadAtMost(fs, payloadSize);
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

                        track.AppendSignature(buffer.AsSpan(0, actualRead), TrackInfo.SignatureSize);

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

    public void WriteSRS(
        string outputPath, string samplePath,
        List<TrackInfo> tracks, long sampleSize, uint sampleCRC32,
        SRSCreationOptions options, CancellationToken ct)
    {
        using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var inFs = new FileStream(samplePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(inFs);

        // Check for ID3v2 wrapper before fLaC marker
        (bool id3Found, int id3Size) = FlacMetadataReader.DetectId3v2Wrapper(inFs);
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
        WriteSrsfFlac(outFs, samplePath, sampleSize, sampleCRC32, options);
        foreach (TrackInfo track in tracks)
        {
            WriteSrstFlac(outFs, track, sampleSize >= 0x80000000);
        }

        // Copy metadata blocks, skip frame data
        while (inFs.Position + 4 <= inFs.Length)
        {
            ct.ThrowIfCancellationRequested();

            (bool isLast, byte blockType, int payloadSize) = FlacMetadataReader.ReadMetadataBlockHeader(reader);

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

    #region Writing Helpers

    private static void WriteSrsfFlac(Stream outFs, string samplePath, long sampleSize, uint sampleCRC32,
        SRSCreationOptions options)
    {
        byte[] payload = SRSPayloadSerializer.SerializeSrsf(samplePath, sampleSize, sampleCRC32, options);
        outFs.WriteByte(0x73); // 's' type
        // BE24 size
        outFs.WriteByte((byte)(payload.Length >> 16));
        outFs.WriteByte((byte)(payload.Length >> 8));
        outFs.WriteByte((byte)(payload.Length));
        outFs.Write(payload);
    }

    private static void WriteSrstFlac(Stream outFs, TrackInfo track, bool bigFile)
    {
        byte[] payload = SRSPayloadSerializer.SerializeSrst(track, bigFile);
        outFs.WriteByte(0x74); // 't' type
        // BE24 size
        outFs.WriteByte((byte)(payload.Length >> 16));
        outFs.WriteByte((byte)(payload.Length >> 8));
        outFs.WriteByte((byte)(payload.Length));
        outFs.Write(payload);
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
