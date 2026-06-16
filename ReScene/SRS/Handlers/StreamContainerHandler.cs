using System.Buffers.Binary;
using System.IO.Hashing;

namespace ReScene.SRS;

internal class StreamContainerHandler : IContainerHandler
{
    public SRSContainerType ContainerType => SRSContainerType.Stream;

    public (List<TrackInfo> Tracks, uint CRC32, long TotalSize) Profile(
        string samplePath,
        Action<long, long, int>? reportScanProgress,
        CancellationToken ct)
    {
        // For stream types, the entire file is essentially one track
        var track = new TrackInfo { TrackNumber = 1 };
        var crc = new Crc32();

        using var fs = new FileStream(samplePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        byte[] buffer = new byte[80 * 1024];
        long totalRead = 0;
        long fileLen = fs.Length;
        track.DataLength = fileLen;
        int lastPercent = -1;

        while (totalRead < fileLen)
        {
            ct.ThrowIfCancellationRequested();

            if (reportScanProgress is not null)
            {
                int pct = (int)(totalRead * 100 / Math.Max(1L, fileLen));
                if (pct != lastPercent)
                {
                    lastPercent = pct;
                    reportScanProgress(totalRead, fileLen, pct);
                }
            }

            int toRead = (int)Math.Min(buffer.Length, fileLen - totalRead);
            int actualRead = fs.Read(buffer, 0, toRead);
            if (actualRead <= 0)
            {
                break;
            }

            crc.Append(buffer.AsSpan(0, actualRead));

            track.AppendSignature(buffer.AsSpan(0, actualRead), TrackInfo.SignatureSize);

            totalRead += actualRead;
        }

        Span<byte> hash = stackalloc byte[4];
        crc.GetHashAndReset(hash);
        uint crc32Val = BinaryPrimitives.ReadUInt32LittleEndian(hash);

        return ([track], crc32Val, fileLen);
    }

    public void WriteSRS(
        string outputPath, string samplePath,
        List<TrackInfo> tracks, long sampleSize, uint sampleCRC32,
        SRSCreationOptions options, CancellationToken ct)
    {
        using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);

        // Write STREAM marker
        outFs.Write("STRM"u8);
        Span<byte> markerSize = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(markerSize, 8);
        outFs.Write(markerSize);

        // Write SRSF block
        SRSPayloadSerializer.WriteSrsfBlock(outFs, samplePath, sampleSize, sampleCRC32, options);

        // Write SRST blocks
        foreach (TrackInfo track in tracks)
        {
            SRSPayloadSerializer.WriteSrstBlock(outFs, track, sampleSize >= 0x80000000);
        }
    }
}
