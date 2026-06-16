using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace ReScene.SRS;

internal class MP4ContainerHandler : IContainerHandler
{
    public SRSContainerType ContainerType => SRSContainerType.MP4;

    private static readonly HashSet<string> _mP4ContainerAtoms =
        ["moov", "trak", "mdia", "minf", "stbl", "edts", "udta", "meta", "ilst"];

    public (List<TrackInfo> Tracks, uint CRC32, long TotalSize) Profile(
        string samplePath,
        Action<long, long, int>? reportScanProgress,
        CancellationToken ct)
    {
        var trackMap = new SortedDictionary<int, TrackInfo>();
        long metaLength = 0;
        long mdatSize = 0;
        var crc = new Crc32();
        int currentTrackId = 0;

        using var fs = new FileStream(samplePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        ProfileMP4Atoms(fs, 0, fs.Length, trackMap, ref metaLength, ref mdatSize, ref currentTrackId, crc,
            reportScanProgress, ct);

        // For MP4, we need to reconstruct track data from stbl tables
        // In the simplest case, mdat is one big track
        // pyrescene reads stsc/stco/stsz tables to split mdat into tracks
        // For SRS creation we need track signatures from the mdat

        long totalSize = metaLength + mdatSize;

        Span<byte> hash = stackalloc byte[4];
        crc.GetHashAndReset(hash);
        uint crc32 = BinaryPrimitives.ReadUInt32LittleEndian(hash);

        // If no tracks were populated from stbl parsing, create/update a single track from mdat.
        // A track may already exist from signature gathering (with DataLength == 0).
        if (mdatSize > 0)
        {
            bool needsTrack = trackMap.Count == 0 ||
                              trackMap.Values.All(t => t.DataLength == 0);

            if (needsTrack)
            {
                int trackNum = trackMap.Count > 0 ? trackMap.Keys.First() : 1;
                if (trackMap.TryGetValue(trackNum, out TrackInfo? existing))
                {
                    existing.DataLength = mdatSize;
                }
                else
                {
                    trackMap[trackNum] = new TrackInfo { TrackNumber = trackNum, DataLength = mdatSize };
                }
            }
        }

        return (trackMap.Values.ToList(), crc32, totalSize);
    }

    public void WriteSRS(
        string outputPath, string samplePath,
        List<TrackInfo> tracks, long sampleSize, uint sampleCRC32,
        SRSCreationOptions options, CancellationToken ct)
    {
        using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var inFs = new FileStream(samplePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        while (inFs.Position + 8 <= inFs.Length)
        {
            ct.ThrowIfCancellationRequested();
            long atomStart = inFs.Position;

            byte[] header = new byte[8];
            inFs.ReadExactly(header, 0, 8);

            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            string type = Encoding.ASCII.GetString(header, 4, 4);

            int headerSize = 8;
            long totalSize;
            byte[]? extHeader = null;

            if (size32 == 1 && inFs.Position + 8 <= inFs.Length)
            {
                extHeader = new byte[8];
                inFs.ReadExactly(extHeader, 0, 8);
                totalSize = (long)BinaryPrimitives.ReadUInt64BigEndian(extHeader);
                headerSize = 16;
            }
            else if (size32 == 0)
            {
                totalSize = inFs.Length - atomStart;
            }
            else
            {
                totalSize = size32;
            }

            if (totalSize < headerSize)
            {
                break;
            }

            long atomEnd = atomStart + totalSize;
            if (atomEnd > inFs.Length)
            {
                atomEnd = inFs.Length;
            }

            if (type == "mdat")
            {
                // Inject SRSF/SRST before mdat
                WriteSrsfMov(outFs, samplePath, sampleSize, sampleCRC32, options);
                foreach (TrackInfo track in tracks)
                {
                    WriteSrstMov(outFs, track, sampleSize >= 0x80000000);
                }

                // Write mdat header only (skip stream data)
                outFs.Write(header);
                if (extHeader is not null)
                {
                    outFs.Write(extHeader);
                }
            }
            else
            {
                // Copy atom verbatim
                outFs.Write(header);
                if (extHeader is not null)
                {
                    outFs.Write(extHeader);
                }

                long payloadSize = atomEnd - inFs.Position;
                if (payloadSize > 0)
                {
                    StreamUtilities.CopyBytes(inFs, outFs, payloadSize);
                }
            }

            inFs.Position = atomEnd;
        }
    }

    #region Profiling

    private static void ProfileMP4Atoms(
        Stream fs, long start, long end,
        SortedDictionary<int, TrackInfo> trackMap,
        ref long metaLength, ref long mdatSize,
        ref int currentTrackId,
        Crc32 crc,
        Action<long, long, int>? reportScanProgress,
        CancellationToken ct)
    {
        fs.Position = start;
        long totalLength = fs.Length;
        int lastPercent = -1;

        while (fs.Position + 8 <= end)
        {
            ct.ThrowIfCancellationRequested();

            if (reportScanProgress is not null)
            {
                int pct = (int)(fs.Position * 100 / Math.Max(1L, totalLength));
                if (pct != lastPercent)
                {
                    lastPercent = pct;
                    reportScanProgress(fs.Position, totalLength, pct);
                }
            }

            long atomStart = fs.Position;

            byte[] header = new byte[8];
            if (fs.Read(header, 0, 8) < 8)
            {
                break;
            }

            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            string type = Encoding.ASCII.GetString(header, 4, 4);

            int headerSize = 8;
            long totalSize;

            if (size32 == 1)
            {
                // Extended 64-bit size
                byte[] ext = new byte[8];
                if (fs.Read(ext, 0, 8) < 8)
                {
                    break;
                }

                totalSize = (long)BinaryPrimitives.ReadUInt64BigEndian(ext);
                headerSize = 16;

                // CRC the full header
                metaLength += 16;
                crc.Append(header);
                crc.Append(ext);
            }
            else if (size32 == 0)
            {
                totalSize = end - atomStart;
                metaLength += 8;
                crc.Append(header);
            }
            else
            {
                totalSize = size32;
                metaLength += 8;
                crc.Append(header);
            }

            if (totalSize < headerSize)
            {
                break;
            }

            long payloadSize = totalSize - headerSize;
            long atomEnd = atomStart + totalSize;
            if (atomEnd > end)
            {
                atomEnd = end;
            }

            if (type == "mdat")
            {
                mdatSize += payloadSize;
                // Read mdat for CRC and extract track signatures
                // For simplicity, read and CRC. pyrescene uses stbl data to assign to tracks.
                // We'll use the whole mdat as track data and build signatures from it.
                long dataRemaining = atomEnd - fs.Position;
                bool signatureDone = trackMap.Values.All(t => t.SignatureBytes.Length >= TrackInfo.SignatureSize);

                // Read mdat in chunks for CRC
                byte[] buffer = new byte[80 * 1024];
                long bytesRead = 0;
                while (bytesRead < dataRemaining)
                {
                    int toRead = (int)Math.Min(buffer.Length, dataRemaining - bytesRead);
                    int actualRead = fs.Read(buffer, 0, toRead);
                    if (actualRead <= 0)
                    {
                        break;
                    }

                    crc.Append(buffer.AsSpan(0, actualRead));

                    // Build signature for track 1 if we haven't from stbl parsing
                    if (trackMap.Count == 0 || !signatureDone)
                    {
                        int trackNum = trackMap.Count > 0 ? trackMap.Keys.First() : 1;
                        if (!trackMap.TryGetValue(trackNum, out TrackInfo? track))
                        {
                            track = new TrackInfo { TrackNumber = trackNum };
                            trackMap[trackNum] = track;
                        }

                        track.AppendSignature(buffer.AsSpan(0, actualRead), TrackInfo.SignatureSize);
                    }

                    bytesRead += actualRead;
                }
            }
            else if (type == "tkhd" && payloadSize >= 12)
            {
                // Track header: extract track ID for MP4 track mapping
                byte[] data = StreamUtilities.ReadAtMost(fs, (int)(atomEnd - fs.Position));
                crc.Append(data);
                // Track ID is at offset 12 (version 0) or 20 (version 1)
                int version = data[0];
                int trackIdOffset = version == 1 ? 19 : 11;
                if (trackIdOffset + 4 <= data.Length)
                {
                    currentTrackId = (int)BinaryPrimitives.ReadUInt32BigEndian(
                        data.AsSpan(trackIdOffset, 4));
                    if (!trackMap.ContainsKey(currentTrackId))
                    {
                        trackMap[currentTrackId] = new TrackInfo { TrackNumber = currentTrackId };
                    }
                }
            }
            else if (_mP4ContainerAtoms.Contains(type))
            {
                // Step into children
                ProfileMP4Atoms(fs, fs.Position, atomEnd, trackMap, ref metaLength, ref mdatSize,
                    ref currentTrackId, crc, reportScanProgress, ct);
            }
            else
            {
                // Metadata atom
                long remaining = atomEnd - fs.Position;
                if (remaining > 0)
                {
                    byte[] data = StreamUtilities.ReadAtMost(fs, (int)remaining);
                    metaLength += remaining;
                    crc.Append(data);
                }
            }

            fs.Position = atomEnd;
        }
    }

    #endregion

    #region Writing Helpers

    private static void WriteSrsfMov(Stream outFs, string samplePath, long sampleSize, uint sampleCRC32,
        SRSCreationOptions options)
    {
        byte[] payload = SRSPayloadSerializer.SerializeSrsf(samplePath, sampleSize, sampleCRC32, options);
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(payload.Length + 8));
        header[4] = (byte)'S';
        header[5] = (byte)'R';
        header[6] = (byte)'S';
        header[7] = (byte)'F';
        outFs.Write(header);
        outFs.Write(payload);
    }

    private static void WriteSrstMov(Stream outFs, TrackInfo track, bool bigFile)
    {
        byte[] payload = SRSPayloadSerializer.SerializeSrst(track, bigFile);
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(payload.Length + 8));
        header[4] = (byte)'S';
        header[5] = (byte)'R';
        header[6] = (byte)'S';
        header[7] = (byte)'T';
        outFs.Write(header);
        outFs.Write(payload);
    }

    #endregion
}
