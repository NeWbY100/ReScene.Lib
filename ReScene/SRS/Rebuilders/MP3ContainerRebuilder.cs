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
        Action<string, long, long, int>? reportScanProgress,
        CancellationToken ct)
    {
        using var srsFs = new FileStream(srsFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(srsFs);
        using var mediaFs = new FileStream(mediaFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 80 * 1024);
        using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);

        bool mainDataWritten = false;

        // Copy all leading ID3v2 header tags verbatim. The writer (MP3ContainerHandler.WriteSRS
        // via MP3TagReader.FindAudioStart) copies EVERY stacked leading ID3v2 tag before the
        // injected SRSF/SRST blocks, so there may be more than one. Running FindAudioStart on the
        // SRS stream stops at the first non-ID3v2 bytes — which are the SRSF block — yielding
        // exactly the header region [0, headerEnd). Previously only a single tag was copied, so a
        // sample with two stacked tags dumped tag 2 + the raw SRS blocks as "footer" with no audio
        // (audit #31).
        long headerEnd = MP3TagReader.FindAudioStart(srsFs);
        if (headerEnd > 0)
        {
            srsFs.Position = 0;
            byte[] headerData = StreamUtilities.ReadExactly(reader, (int)headerEnd);
            outFs.Write(headerData);
        }

        srsFs.Position = headerEnd;

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
