using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace ReScene.SRS;

internal class StreamContainerHandler : IContainerHandler
{
    private const int SignatureSize = 256;

    public SRSContainerType ContainerType => SRSContainerType.Stream;

    public (List<TrackInfo> Tracks, uint Crc32, long TotalSize) Profile(string samplePath, CancellationToken ct)
    {
        // For stream types, the entire file is essentially one track
        var track = new TrackInfo { TrackNumber = 1 };
        var crc = new Crc32();

        using var fs = new FileStream(samplePath, FileMode.Open, FileAccess.Read, FileShare.Read);

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

    public void WriteSrs(
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
        foreach (TrackInfo track in tracks)
        {
            WriteSrstMp3(outFs, track, sampleSize >= 0x80000000);
        }
    }

    #region Writing Helpers

    private static void WriteSrsfMp3(Stream outFs, string samplePath, long sampleSize, uint sampleCrc32,
        SrsCreationOptions options)
    {
        byte[] payload = SrsPayloadSerializer.SerializeSrsf(samplePath, sampleSize, sampleCrc32, options);
        outFs.Write(Encoding.ASCII.GetBytes("SRSF"));
        Span<byte> sizeBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(sizeBytes, (uint)(4 + 4 + payload.Length));
        outFs.Write(sizeBytes);
        outFs.Write(payload);
    }

    private static void WriteSrstMp3(Stream outFs, TrackInfo track, bool bigFile)
    {
        byte[] payload = SrsPayloadSerializer.SerializeSrst(track, bigFile);
        outFs.Write(Encoding.ASCII.GetBytes("SRST"));
        Span<byte> sizeBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(sizeBytes, (uint)(8 + payload.Length));
        outFs.Write(sizeBytes);
        outFs.Write(payload);
    }

    #endregion
}
