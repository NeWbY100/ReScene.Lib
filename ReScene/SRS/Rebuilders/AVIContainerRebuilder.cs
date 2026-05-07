using System.Buffers.Binary;
using System.Text;

namespace ReScene.SRS;

/// <summary>
/// Rebuilds AVI/RIFF samples by replaying the RIFF structure from the SRS file,
/// skipping SRSF/SRST chunks and reading movi data directly from the media file.
/// </summary>
internal class AVIContainerRebuilder : IContainerRebuilder
{
    public SRSContainerType ContainerType => SRSContainerType.AVI;

    public void Rebuild(
        string srsFilePath,
        Dictionary<uint, SRSTrackDataBlock> tracks,
        string mediaFilePath,
        Dictionary<uint, long> trackOffsets,
        string outputPath,
        Action<string, int, int, double>? reportProgress,
        CancellationToken ct)
    {
        // Index the media file's movi chunks near the signature area.
        // This builds a per-track queue of (dataOffset, size) for each chunk.
        long minOffset = trackOffsets.Values.Min();
        long mediaScanStart = Math.Max(0, minOffset - 8);
        Dictionary<uint, Queue<(long DataOffset, int Size)>> mediaChunks =
            IndexMediaRiffChunks(mediaFilePath, mediaScanStart, tracks, ct);

        using var srsFs = new FileStream(srsFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(srsFs);
        using var mediaFs = new FileStream(mediaFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 80 * 1024);
        using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);

        RebuildRiffChunks(reader, srsFs, outFs, mediaFs, mediaChunks, 0, srsFs.Length, ct);
    }

    /// <summary>
    /// Walks the media file's RIFF structure from the scan start position and builds
    /// a per-track queue of chunk data offsets and sizes.
    /// </summary>
    private static Dictionary<uint, Queue<(long DataOffset, int Size)>> IndexMediaRiffChunks(
        string mediaFilePath,
        long scanStart,
        Dictionary<uint, SRSTrackDataBlock> tracks,
        CancellationToken ct)
    {
        var result = new Dictionary<uint, Queue<(long, int)>>();
        var remaining = new Dictionary<uint, long>();

        foreach ((uint trackNumber, SRSTrackDataBlock? track) in tracks)
        {
            result[trackNumber] = new Queue<(long, int)>();
            remaining[trackNumber] = (long)track.DataLength;
        }

        using var fs = new FileStream(mediaFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 80 * 1024);
        fs.Position = scanStart;

        byte[] headerBuf = new byte[8];

        while (fs.Position + 8 <= fs.Length)
        {
            ct.ThrowIfCancellationRequested();

            bool allDone = true;
            foreach (long r in remaining.Values)
            {
                if (r > 0)
                {
                    allDone = false;
                    break;
                }
            }

            if (allDone)
            {
                break;
            }

            int hdrRead = StreamUtilities.ReadFully(fs, headerBuf, 0, 8);
            if (hdrRead < 8)
            {
                break;
            }

            string fourcc = Encoding.ASCII.GetString(headerBuf, 0, 4);
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(headerBuf.AsSpan(4));

            if (fourcc is "RIFF" or "LIST")
            {
                fs.Position += 4; // skip subtype, scan children
                continue;
            }

            bool isMoviData = fourcc.Length == 4 &&
                              char.IsDigit(fourcc[0]) && char.IsDigit(fourcc[1]) &&
                              char.IsLetter(fourcc[2]) && char.IsLetter(fourcc[3]);

            if (isMoviData)
            {
                uint trackNumber = (uint)((fourcc[0] - '0') * 10 + (fourcc[1] - '0'));
                long dataOffset = fs.Position;

                if (remaining.TryGetValue(trackNumber, out long rem) && rem > 0)
                {
                    int toIndex = (int)Math.Min(chunkSize, rem);
                    result[trackNumber].Enqueue((dataOffset, toIndex));
                    remaining[trackNumber] -= toIndex;
                }
            }

            fs.Position += chunkSize;
            if (chunkSize % 2 != 0 && fs.Position < fs.Length)
            {
                fs.Position++;
            }
        }

        return result;
    }

    private static void RebuildRiffChunks(
        BinaryReader reader, Stream srsFs, Stream outFs,
        Stream mediaFs, Dictionary<uint, Queue<(long DataOffset, int Size)>> mediaChunks,
        long start, long end,
        CancellationToken ct)
    {
        srsFs.Position = start;

        while (srsFs.Position + 8 <= end)
        {
            ct.ThrowIfCancellationRequested();
            long chunkStart = srsFs.Position;

            byte[] header = reader.ReadBytes(8);
            if (header.Length < 8)
            {
                break;
            }

            string fourcc = Encoding.ASCII.GetString(header, 0, 4);
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));

            if (fourcc is "SRSF" or "SRST")
            {
                // Skip SRS metadata blocks - don't copy them to output
                srsFs.Position = chunkStart + 8 + chunkSize;
                if (chunkSize % 2 != 0 && srsFs.Position < end)
                {
                    srsFs.Position++;
                }

                continue;
            }

            // Write header to output
            outFs.Write(header);

            if (fourcc is "RIFF" or "LIST")
            {
                // Container chunk: copy 4-byte subtype and recurse
                byte[] subType = reader.ReadBytes(4);
                outFs.Write(subType);

                long childEnd = chunkStart + 8 + chunkSize;
                if (childEnd > end)
                {
                    childEnd = end;
                }

                RebuildRiffChunks(reader, srsFs, outFs, mediaFs, mediaChunks,
                    srsFs.Position, childEnd, ct);

                srsFs.Position = childEnd;
                if (chunkSize % 2 != 0 && srsFs.Position < end)
                {
                    byte pad = reader.ReadByte();
                    outFs.WriteByte(pad);
                }
            }
            else
            {
                // Check if this is a movi data chunk (e.g. "00dc", "01wb")
                bool isMovi = fourcc.Length == 4 &&
                              char.IsDigit(fourcc[0]) && char.IsDigit(fourcc[1]) &&
                              char.IsLetter(fourcc[2]) && char.IsLetter(fourcc[3]);

                if (isMovi)
                {
                    // Read data directly from media file at the indexed position
                    uint trackNumber = (uint)((fourcc[0] - '0') * 10 + (fourcc[1] - '0'));

                    if (mediaChunks.TryGetValue(trackNumber, out Queue<(long DataOffset, int Size)>? queue) && queue.Count > 0)
                    {
                        (long dataOffset, int size) = queue.Dequeue();
                        mediaFs.Position = dataOffset;
                        StreamUtilities.CopyBytes(mediaFs, outFs, size);
                    }

                    // In the SRS, no data follows the movi header (data was stripped).
                }
                else
                {
                    // Non-movi chunk: copy data verbatim from SRS
                    if (chunkSize > 0)
                    {
                        byte[] data = StreamUtilities.ReadExactly(reader, (int)chunkSize);
                        outFs.Write(data);
                    }
                }

                // Handle RIFF padding byte
                if (chunkSize % 2 != 0 && srsFs.Position < end)
                {
                    byte pad = reader.ReadByte();
                    outFs.WriteByte(pad);
                }
            }
        }
    }
}
