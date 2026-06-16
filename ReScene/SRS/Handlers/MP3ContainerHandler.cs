using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace ReScene.SRS;

internal class MP3ContainerHandler : IContainerHandler
{
    public SRSContainerType ContainerType => SRSContainerType.MP3;

    public (List<TrackInfo> Tracks, uint CRC32, long TotalSize) Profile(
        string samplePath,
        Action<long, long, int>? reportScanProgress,
        CancellationToken ct)
    {
        var track = new TrackInfo { TrackNumber = 1 };
        var crc = new Crc32();

        using var fs = new FileStream(samplePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long fileLen = fs.Length;

        // Use MP3TagReader to find audio boundaries (handles ID3v2, ID3v1,
        // Lyrics3v1, Lyrics3v2, APEv1/v2)
        long audioStart = MP3TagReader.FindAudioStart(fs);
        long audioEnd = MP3TagReader.FindAudioEnd(fs);

        // Read entire file for CRC
        fs.Position = 0;
        byte[] buffer = new byte[80 * 1024];
        long totalRead = 0;

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

            // Build signature from audio data
            if (totalRead + actualRead > audioStart && track.SignatureBytes.Length < TrackInfo.SignatureSize)
            {
                long sigStart = Math.Max(audioStart, totalRead);
                int offset = (int)(sigStart - totalRead);
                int available = actualRead - offset;
                if (available > 0)
                {
                    track.AppendSignature(buffer.AsSpan(offset, available), TrackInfo.SignatureSize);
                }
            }

            totalRead += actualRead;
        }

        // Track data = audio portion
        track.DataLength = audioEnd - audioStart;

        Span<byte> hash = stackalloc byte[4];
        crc.GetHashAndReset(hash);
        uint crc32Val = BinaryPrimitives.ReadUInt32LittleEndian(hash);

        return (track.DataLength > 0 ? [track] : [], crc32Val, fileLen);
    }

    public void WriteSRS(
        string outputPath, string samplePath,
        List<TrackInfo> tracks, long sampleSize, uint sampleCRC32,
        SRSCreationOptions options, CancellationToken ct)
    {
        using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var inFs = new FileStream(samplePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(inFs);

        // Use MP3TagReader to find audio boundaries
        long audioStart = MP3TagReader.FindAudioStart(inFs);
        long audioEnd = MP3TagReader.FindAudioEnd(inFs);

        // Copy all header tags (ID3v2 etc.) verbatim
        if (audioStart > 0)
        {
            inFs.Position = 0;
            byte[] headerTags = ReadExactly(reader, (int)audioStart);
            outFs.Write(headerTags);
        }

        // Write SRSF/SRST blocks (replaces audio data)
        WriteSrsfMP3(outFs, samplePath, sampleSize, sampleCRC32, options);
        foreach (TrackInfo track in tracks)
        {
            WriteSrstMP3(outFs, track, sampleSize >= 0x80000000);
        }

        // Copy all footer tags (APE, Lyrics3, ID3v1 etc.) verbatim
        long footerSize = inFs.Length - audioEnd;
        if (footerSize > 0)
        {
            inFs.Position = audioEnd;
            byte[] footerTags = ReadExactly(reader, (int)footerSize);
            outFs.Write(footerTags);
        }
    }

    #region Writing Helpers

    private static void WriteSrsfMP3(Stream outFs, string samplePath, long sampleSize, uint sampleCRC32,
        SRSCreationOptions options)
    {
        byte[] payload = SRSPayloadSerializer.SerializeSrsf(samplePath, sampleSize, sampleCRC32, options);
        outFs.Write(Encoding.ASCII.GetBytes("SRSF"));
        Span<byte> sizeBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(sizeBytes, (uint)(4 + 4 + payload.Length));
        outFs.Write(sizeBytes);
        outFs.Write(payload);
    }

    private static void WriteSrstMP3(Stream outFs, TrackInfo track, bool bigFile)
    {
        byte[] payload = SRSPayloadSerializer.SerializeSrst(track, bigFile);
        outFs.Write(Encoding.ASCII.GetBytes("SRST"));
        Span<byte> sizeBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(sizeBytes, (uint)(8 + payload.Length));
        outFs.Write(sizeBytes);
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
