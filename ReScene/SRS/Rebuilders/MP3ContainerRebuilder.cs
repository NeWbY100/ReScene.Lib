using System.Text;

namespace ReScene.SRS;

/// <summary>
/// Rebuilds an MP3 sample: copies header tags from SRS, reads audio data
/// from the media file, then copies footer tags from SRS.
/// </summary>
internal class MP3ContainerRebuilder : IContainerRebuilder
{
    public SRSContainerType ContainerType => SRSContainerType.MP3;

    public void Rebuild(
        string srsFilePath,
        Dictionary<uint, SRSTrackDataBlock> tracks,
        string mediaFilePath,
        Dictionary<uint, long> trackOffsets,
        string outputPath,
        Action<string, int, int, double>? reportProgress,
        CancellationToken ct)
    {
        using var srsFs = new FileStream(srsFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(srsFs);
        using var mediaFs = new FileStream(mediaFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 80 * 1024);
        using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);

        bool mainDataWritten = false;

        // Check for ID3v2 header
        if (srsFs.Length >= 10)
        {
            byte[] id3Check = reader.ReadBytes(3);
            srsFs.Position = 0;

            if (id3Check[0] == 'I' && id3Check[1] == 'D' && id3Check[2] == '3')
            {
                // Read ID3v2 size
                srsFs.Position = 6;
                byte[] id3SizeBytes = reader.ReadBytes(4);
                int id3Size = (id3SizeBytes[0] << 21) | (id3SizeBytes[1] << 14) |
                              (id3SizeBytes[2] << 7) | id3SizeBytes[3];
                long id3TotalSize = 10 + id3Size;

                // Copy entire ID3v2 tag
                srsFs.Position = 0;
                byte[] id3Data = StreamUtilities.ReadExactly(reader, (int)id3TotalSize);
                outFs.Write(id3Data);
            }
            else
            {
                srsFs.Position = 0;
            }
        }

        // Read remaining blocks
        while (srsFs.Position + 8 <= srsFs.Length)
        {
            ct.ThrowIfCancellationRequested();
            long blockStart = srsFs.Position;

            byte[] peek = reader.ReadBytes(4);
            srsFs.Position = blockStart;

            string tag = Encoding.ASCII.GetString(peek, 0, 4);

            if (tag is "SRSF" or "SRST" or "SRSP")
            {
                // Write audio data from media file before skipping SRS blocks
                if (!mainDataWritten && tracks.TryGetValue(1, out SRSTrackDataBlock? track) &&
                    trackOffsets.TryGetValue(1, out long offset))
                {
                    mediaFs.Position = offset;
                    StreamUtilities.CopyBytes(mediaFs, outFs, (long)track.DataLength);
                    mainDataWritten = true;
                }

                // Skip the SRS block
                reader.ReadBytes(4); // tag
                uint totalSize = reader.ReadUInt32();
                srsFs.Position = blockStart + totalSize;
            }
            else
            {
                // Not an SRS block - break and copy remaining (footer tags)
                break;
            }
        }

        // Copy remaining footer data (ID3v1, APE tags, etc.)
        long remaining = srsFs.Length - srsFs.Position;
        if (remaining > 0)
        {
            byte[] footer = StreamUtilities.ReadExactly(reader, (int)remaining);
            outFs.Write(footer);
        }
    }
}
