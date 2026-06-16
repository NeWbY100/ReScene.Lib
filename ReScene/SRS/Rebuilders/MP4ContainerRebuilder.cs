using System.Buffers.Binary;
using System.Text;

namespace ReScene.SRS;

/// <summary>
/// Rebuilds an MP4 sample by replaying the atom structure from the SRS file,
/// skipping SRSF/SRST atoms and reading mdat content from the media file.
/// </summary>
internal class MP4ContainerRebuilder : IContainerRebuilder
{
    public SRSContainerType ContainerType => SRSContainerType.MP4;

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
        using var mediaFs = new FileStream(mediaFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 80 * 1024);
        using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);

        RebuildMP4Atoms(srsFs, outFs, mediaFs, tracks, trackOffsets, 0, srsFs.Length, ct);
    }

    private static void RebuildMP4Atoms(
        Stream srsFs, Stream outFs,
        Stream mediaFs,
        Dictionary<uint, SRSTrackDataBlock> tracks,
        Dictionary<uint, long> trackOffsets,
        long start, long end,
        CancellationToken ct)
    {
        srsFs.Position = start;

        while (srsFs.Position + 8 <= end)
        {
            ct.ThrowIfCancellationRequested();
            long atomStart = srsFs.Position;

            byte[] sizeBytes = new byte[4];
            srsFs.ReadExactly(sizeBytes, 0, 4);
            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(sizeBytes);

            byte[] typeBytes = new byte[4];
            srsFs.ReadExactly(typeBytes, 0, 4);
            string type = Encoding.ASCII.GetString(typeBytes);

            int headerSize = 8;
            long totalSize;

            if (size32 == 1)
            {
                byte[] extBytes = new byte[8];
                srsFs.ReadExactly(extBytes, 0, 8);
                totalSize = (long)BinaryPrimitives.ReadUInt64BigEndian(extBytes);
                headerSize = 16;
            }
            else if (size32 == 0)
            {
                totalSize = end - atomStart;
            }
            else
            {
                totalSize = size32;
            }

            if (totalSize < headerSize)
            {
                break;
            }

            long payloadStart = atomStart + headerSize;
            long atomEnd = Math.Min(atomStart + totalSize, end);

            // Skip SRSF/SRST atoms
            if (type is "SRSF" or "SRST")
            {
                srsFs.Position = atomEnd;
                continue;
            }

            // Write header
            srsFs.Position = atomStart;
            byte[] rawHeader = new byte[headerSize];
            srsFs.ReadExactly(rawHeader, 0, headerSize);
            outFs.Write(rawHeader);

            if (type == "mdat")
            {
                // mdat: write track data from media file, sorted by match offset
                var sortedTracks = tracks
                    .Where(kv => trackOffsets.ContainsKey(kv.Key))
                    .OrderBy(kv => trackOffsets[kv.Key])
                    .ToList();

                foreach ((uint trackNumber, SRSTrackDataBlock? track) in sortedTracks)
                {
                    mediaFs.Position = trackOffsets[trackNumber];
                    StreamUtilities.CopyBytes(mediaFs, outFs, (long)track.DataLength);
                }

                srsFs.Position = atomEnd;
            }
            else if (MP4Atoms.ContainerAtoms.Contains(type))
            {
                // Recurse into container atoms
                RebuildMP4Atoms(srsFs, outFs, mediaFs, tracks, trackOffsets,
                    payloadStart, atomEnd, ct);
                srsFs.Position = atomEnd;
            }
            else
            {
                // Copy metadata atom verbatim
                long remaining = atomEnd - srsFs.Position;
                if (remaining > 0)
                {
                    StreamUtilities.CopyBytes(srsFs, outFs, remaining);
                }

                srsFs.Position = atomEnd;
            }
        }
    }
}
