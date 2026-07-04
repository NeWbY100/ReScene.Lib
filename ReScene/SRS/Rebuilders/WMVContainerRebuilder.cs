using System.Buffers.Binary;

namespace ReScene.SRS;

/// <summary>
/// Rebuilds a WMV/ASF sample by replaying the ASF object structure from the SRS file.
/// Non-data objects are copied verbatim from the SRS; the Data Object's stripped packet
/// payload is restored from the media file (the SRS keeps only its 26-byte data header).
/// </summary>
internal class WMVContainerRebuilder : IContainerRebuilder
{
    public SRSContainerType ContainerType => SRSContainerType.WMV;

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

        byte[] sizeBuffer = new byte[8];

        while (srsFs.Position + AsfGuids.ObjectHeaderSize <= srsFs.Length)
        {
            ct.ThrowIfCancellationRequested();
            long objStart = srsFs.Position;

            byte[] guid = reader.ReadBytes(AsfGuids.GuidSize);
            ulong totalSize = reader.ReadUInt64();

            if (totalSize < AsfGuids.ObjectHeaderSize)
            {
                break;
            }

            long objEnd = objStart + (long)totalSize;

            // Skip injected SRS objects.
            if (GuidEquals(guid, AsfSrsGuids.GuidSRSFile) ||
                GuidEquals(guid, AsfSrsGuids.GuidSRSTrack) ||
                GuidEquals(guid, AsfSrsGuids.GuidSRSPadding))
            {
                srsFs.Position = objEnd;
                continue;
            }

            // Write the 24-byte object header verbatim. Its declared size still
            // reflects the original, un-stripped object.
            outFs.Write(guid);
            BinaryPrimitives.WriteUInt64LittleEndian(sizeBuffer, totalSize);
            outFs.Write(sizeBuffer);

            // The ASF Data Object (GUID prefix 36 26 B2 75) had its packet payload
            // stripped by the writer (only the 26-byte data header was kept, with
            // SRSF/SRST injected after it). Restore the data header from the SRS, then
            // the packets from the media file. The object's declared size reflects the
            // original, so we must NOT seek to objEnd here — the SRS only contains the
            // header followed by the injected SRS objects.
            bool isDataObject = guid.AsSpan().StartsWith(AsfGuids.DataObjectPrefix);

            if (isDataObject)
            {
                long dataHeaderLen = Math.Min(AsfGuids.DataObjectHeaderLength, objEnd - srsFs.Position);
                if (dataHeaderLen > 0)
                {
                    StreamUtilities.CopyBytes(srsFs, outFs, dataHeaderLen);
                }

                // Restore packet payload from the media file, tracks ordered by match offset.
                foreach ((uint trackNumber, SRSTrackDataBlock track) in tracks
                    .Where(kv => trackOffsets.ContainsKey(kv.Key))
                    .OrderBy(kv => trackOffsets[kv.Key]))
                {
                    mediaFs.Position = trackOffsets[trackNumber];
                    StreamUtilities.CopyBytes(mediaFs, outFs, (long)track.DataLength);
                }

                // Leave the SRS position just after the data header (at the injected
                // SRSF/SRST objects); the loop skips them on the next iterations.
                continue;
            }

            // Non-data object: copy the body verbatim from the SRS.
            long bodySize = (long)totalSize - AsfGuids.ObjectHeaderSize;
            if (bodySize > 0)
            {
                StreamUtilities.CopyBytes(srsFs, outFs, bodySize);
            }

            srsFs.Position = objEnd;
        }
    }

    private static bool GuidEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        return a.AsSpan().SequenceEqual(b);
    }
}
