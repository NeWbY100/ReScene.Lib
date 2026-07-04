namespace ReScene.SRS;

/// <summary>
/// Rebuilds a FLAC sample: copies metadata blocks from SRS, then reads
/// audio frame data directly from the media file.
/// </summary>
internal class FlacContainerRebuilder : IContainerRebuilder
{
    public SRSContainerType ContainerType => SRSContainerType.FLAC;

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

        // Write fLaC marker
        outFs.Write("fLaC"u8);
        srsFs.Position = 4;

        int srsBlockCount = 0;

        while (srsFs.Position + 4 <= srsFs.Length)
        {
            ct.ThrowIfCancellationRequested();
            long blockStart = srsFs.Position;

            byte typeByte = reader.ReadByte();
            bool isLast = (typeByte & FlacConstants.LastBlockFlag) != 0;
            byte type = (byte)(typeByte & FlacConstants.BlockTypeMask);

            byte[] sizeBytes = reader.ReadBytes(FlacConstants.BlockSizeFieldWidth);
            int payloadSize = (sizeBytes[0] << 16) | (sizeBytes[1] << 8) | sizeBytes[2];

            // SRS FLAC blocks: 's' (SRSF), 't' (SRST), 'u' (fingerprint)
            if (((FlacSrsBlockType)type) is FlacSrsBlockType.Srsf or FlacSrsBlockType.Srst or FlacSrsBlockType.Fingerprint
                && srsBlockCount <= FlacConstants.MaxSrsBlockCount)
            {
                srsBlockCount++;
                srsFs.Position = blockStart + 4 + payloadSize;
                continue;
            }

            // Copy block header and content
            srsFs.Position = blockStart;
            byte[] rawHeader = new byte[4];
            srsFs.ReadExactly(rawHeader, 0, 4);
            outFs.Write(rawHeader);

            if (payloadSize > 0)
            {
                byte[] payload = StreamUtilities.ReadExactly(reader, payloadSize);
                outFs.Write(payload);
            }

            // After the last metadata block, write audio data from media file
            if (isLast && tracks.TryGetValue(1, out SRSTrackDataBlock? track) &&
                trackOffsets.TryGetValue(1, out long offset))
            {
                mediaFs.Position = offset;
                StreamUtilities.CopyBytes(mediaFs, outFs, (long)track.DataLength);
            }

            if (isLast)
            {
                break;
            }
        }
    }
}
