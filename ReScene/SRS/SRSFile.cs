using System.Text;

namespace ReScene.SRS;

/// <summary>
/// Parser for SRS (Sample ReScene) files.
/// Supports AVI, MKV, MP4, WMV, FLAC, MP3, and STREAM/M2TS container formats.
/// </summary>
public class SRSFile
{
    /// <summary>
    /// Gets the detected container format of the SRS file.
    /// </summary>
    public SRSContainerType ContainerType { get; private set; }

    /// <summary>
    /// Gets the parsed SRSF (file data) block, or null if not present.
    /// </summary>
    public SrsFileDataBlock? FileData { get; private set; }

    /// <summary>
    /// Gets the parsed SRST (track data) blocks.
    /// </summary>
    public List<SrsTrackDataBlock> Tracks { get; private set; } = [];

    /// <summary>
    /// Gets the container-native chunks (non-SRS elements) found in the file.
    /// </summary>
    public List<SrsContainerChunk> ContainerChunks { get; private set; } = [];

    /// <summary>
    /// Loads and parses an SRS file from the specified path.
    /// </summary>
    /// <param name="filePath">The path to the SRS file.</param>
    /// <returns>A parsed <see cref="SRSFile"/> instance.</returns>
    public static SRSFile Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("SRS file not found.", filePath);
        }

        var srs = new SRSFile();
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(fs);

        if (fs.Length < 4)
        {
            throw new InvalidDataException("File too small to be a valid SRS file.");
        }

        // Read first 16 bytes for container detection
        byte[] magic = new byte[Math.Min(16, fs.Length)];
        fs.ReadExactly(magic, 0, magic.Length);
        fs.Position = 0;

        srs.ContainerType = DetectContainer(magic);

        switch (srs.ContainerType)
        {
            case SRSContainerType.Stream:
                ParseStream(reader, fs, srs);
                break;
            case SRSContainerType.MP3:
                ParseMp3(reader, fs, srs);
                break;
            case SRSContainerType.FLAC:
                ParseFlac(reader, fs, srs);
                break;
            case SRSContainerType.AVI:
                ParseRiff(reader, fs, srs);
                break;
            case SRSContainerType.MP4:
                ParseMp4(reader, fs, srs);
                break;
            case SRSContainerType.WMV:
                ParseAsf(reader, fs, srs);
                break;
            case SRSContainerType.MKV:
                ParseEbml(reader, fs, srs);
                break;
        }

        return srs;
    }

    private static SRSContainerType DetectContainer(byte[] magic)
    {
        if (magic.Length < 4)
        {
            throw new InvalidDataException("Cannot detect container format.");
        }

        // RIFF (AVI)
        if (magic[0] == 'R' && magic[1] == 'I' && magic[2] == 'F' && magic[3] == 'F')
        {
            return SRSContainerType.AVI;
        }

        // STREAM/M2TS: "STRM\x08\x00\x00\x00" or "M2TS\x08\x00\x00\x00"
        if (magic.Length >= 8)
        {
            if ((magic[0] == 'S' && magic[1] == 'T' && magic[2] == 'R' && magic[3] == 'M'
                 && magic[4] == 0x08 && magic[5] == 0x00 && magic[6] == 0x00 && magic[7] == 0x00)
                || (magic[0] == 'M' && magic[1] == '2' && magic[2] == 'T' && magic[3] == 'S'
                    && magic[4] == 0x08 && magic[5] == 0x00 && magic[6] == 0x00 && magic[7] == 0x00))
                return SRSContainerType.Stream;
        }

        // FLAC
        if (magic[0] == 'f' && magic[1] == 'L' && magic[2] == 'a' && magic[3] == 'C')
        {
            return SRSContainerType.FLAC;
        }

        // MP4: bytes[4:8] == "ftyp"
        if (magic.Length >= 8 && magic[4] == 'f' && magic[5] == 't' && magic[6] == 'y' && magic[7] == 'p')
        {
            return SRSContainerType.MP4;
        }

        // MKV/EBML
        if (magic[0] == 0x1A && magic[1] == 0x45 && magic[2] == 0xDF && magic[3] == 0xA3)
        {
            return SRSContainerType.MKV;
        }

        // WMV/ASF
        if (magic[0] == 0x30 && magic[1] == 0x26 && magic[2] == 0xB2 && magic[3] == 0x75)
        {
            return SRSContainerType.WMV;
        }

        // MP3: ID3 tag, SRSF block, or sync word
        if (magic[0] == 'I' && magic[1] == 'D' && magic[2] == '3')
        {
            return SRSContainerType.MP3;
        }

        if (magic[0] == 'S' && magic[1] == 'R' && magic[2] == 'S' && magic[3] == 'F')
        {
            return SRSContainerType.MP3;
        }

        if (magic[0] == 0xFF && (magic[1] & 0xE0) == 0xE0)
        {
            return SRSContainerType.MP3;
        }

        throw new InvalidDataException("Unknown SRS container format.");
    }

    // ==================== Common Payload Parsers ====================

    private static SrsFileDataBlock ParseFileDataPayload(BinaryReader reader, long payloadStart,
        long frameOffset, int frameHeaderSize, long blockSize)
    {
        var block = new SrsFileDataBlock
        {
            BlockPosition = frameOffset,
            BlockSize = blockSize,
            FrameOffset = frameOffset,
            FrameHeaderSize = frameHeaderSize,
        };

        long p = payloadStart;
        reader.BaseStream.Position = p;

        block.FlagsOffset = p;
        block.Flags = reader.ReadUInt16();
        p += 2;

        block.AppNameSizeOffset = p;
        block.AppNameSize = reader.ReadUInt16();
        p += 2;

        block.AppNameOffset = p;
        if (block.AppNameSize > 0)
        {
            block.AppName = Encoding.UTF8.GetString(reader.ReadBytes(block.AppNameSize));
        }

        p += block.AppNameSize;

        block.FileNameSizeOffset = p;
        block.FileNameSize = reader.ReadUInt16();
        p += 2;

        block.FileNameOffset = p;
        if (block.FileNameSize > 0)
        {
            block.FileName = Encoding.UTF8.GetString(reader.ReadBytes(block.FileNameSize));
        }

        p += block.FileNameSize;

        block.SampleSizeOffset = p;
        block.SampleSize = reader.ReadUInt64();
        p += 8;

        block.Crc32Offset = p;
        block.Crc32 = reader.ReadUInt32();

        return block;
    }

    private static SrsTrackDataBlock ParseTrackDataPayload(BinaryReader reader, long payloadStart,
        long frameOffset, int frameHeaderSize, long blockSize)
    {
        var block = new SrsTrackDataBlock
        {
            BlockPosition = frameOffset,
            BlockSize = blockSize,
            FrameOffset = frameOffset,
            FrameHeaderSize = frameHeaderSize,
        };

        long p = payloadStart;
        reader.BaseStream.Position = p;

        block.FlagsOffset = p;
        block.Flags = reader.ReadUInt16();
        p += 2;

        // Track number: 4 bytes if flag 0x8, else 2 bytes
        block.TrackNumberOffset = p;
        if ((block.Flags & 0x8) != 0)
        {
            block.TrackNumberFieldSize = 4;
            block.TrackNumber = reader.ReadUInt32();
            p += 4;
        }
        else
        {
            block.TrackNumberFieldSize = 2;
            block.TrackNumber = reader.ReadUInt16();
            p += 2;
        }

        // Data length: 8 bytes if flag 0x4, else 4 bytes
        block.DataLengthOffset = p;
        if ((block.Flags & 0x4) != 0)
        {
            block.DataLengthFieldSize = 8;
            block.DataLength = reader.ReadUInt64();
            p += 8;
        }
        else
        {
            block.DataLengthFieldSize = 4;
            block.DataLength = reader.ReadUInt32();
            p += 4;
        }

        block.MatchOffsetOffset = p;
        block.MatchOffset = reader.ReadUInt64();
        p += 8;

        block.SignatureSizeOffset = p;
        block.SignatureSize = reader.ReadUInt16();
        p += 2;

        block.SignatureOffset = p;
        if (block.SignatureSize > 0)
        {
            block.Signature = reader.ReadBytes(block.SignatureSize);
        }

        return block;
    }

    // ==================== STREAM/M2TS Parser ====================

    private static void ParseStream(BinaryReader reader, FileStream fs, SRSFile srs)
    {
        while (fs.Position + 8 <= fs.Length)
        {
            long frameOffset = fs.Position;
            string tag = new(reader.ReadChars(4));
            uint totalSize = reader.ReadUInt32(); // includes 8-byte header
            if (totalSize < 8)
            {
                break;
            }

            long payloadStart = fs.Position;
            long payloadSize = totalSize - 8;
            int headerSize = 8;

            if (tag == "SRSF")
            {
                srs.FileData = ParseFileDataPayload(reader, payloadStart, frameOffset, headerSize, totalSize);
            }
            else if (tag == "SRST")
            {
                srs.Tracks.Add(ParseTrackDataPayload(reader, payloadStart, frameOffset, headerSize, totalSize));
            }
            else
            {
                srs.ContainerChunks.Add(new SrsContainerChunk
                {
                    BlockPosition = frameOffset,
                    BlockSize = totalSize,
                    Label = tag,
                    ChunkId = tag,
                    HeaderSize = headerSize,
                    PayloadSize = payloadSize
                });
            }

            fs.Position = frameOffset + totalSize;
        }
    }

    // ==================== MP3 Parser ====================

    private static void ParseMp3(BinaryReader reader, FileStream fs, SRSFile srs)
    {
        // Detect header tags (ID3v2, possibly multiple)
        long headerEnd = 0;
        while (true)
        {
            fs.Position = headerEnd;
            var (found, size) = Mp3TagReader.DetectId3v2(fs);
            if (found)
            {
                srs.ContainerChunks.Add(new SrsContainerChunk
                {
                    BlockPosition = headerEnd,
                    BlockSize = size,
                    Label = "ID3v2",
                    ChunkId = "ID3",
                    HeaderSize = 10,
                    PayloadSize = size - 10
                });
                headerEnd += size;
            }
            else
            {
                break;
            }
        }

        fs.Position = headerEnd;

        // Read SRSF/SRST/SRSP blocks (same 8-byte header as STREAM)
        while (fs.Position + 8 <= fs.Length)
        {
            long frameOffset = fs.Position;

            // Peek to check if we have an SRS block or audio frame
            byte[] peek = reader.ReadBytes(4);
            fs.Position = frameOffset;

            string tag = Encoding.ASCII.GetString(peek, 0, 4);
            if (tag is "SRSF" or "SRST" or "SRSP")
            {
                reader.ReadBytes(4); // skip tag
                uint totalSize = reader.ReadUInt32();
                if (totalSize < 8)
                {
                    break;
                }

                long payloadStart = fs.Position;
                int headerSize = 8;

                if (tag == "SRSF")
                {
                    srs.FileData = ParseFileDataPayload(reader, payloadStart, frameOffset, headerSize, totalSize);
                }
                else if (tag == "SRST")
                {
                    srs.Tracks.Add(ParseTrackDataPayload(reader, payloadStart, frameOffset, headerSize, totalSize));
                }
                else
                {
                    srs.ContainerChunks.Add(new SrsContainerChunk
                    {
                        BlockPosition = frameOffset,
                        BlockSize = totalSize,
                        Label = tag,
                        ChunkId = tag,
                        HeaderSize = headerSize,
                        PayloadSize = totalSize - 8
                    });
                }

                fs.Position = frameOffset + totalSize;
            }
            else
            {
                // Not an SRS block -- could be audio frames or footer tags
                break;
            }
        }

        // Detect footer tags working inward from the end of the file
        long endOffset = fs.Length;

        // Check for ID3v1
        var (id3v1Found, id3v1Size) = Mp3TagReader.DetectId3v1(fs);
        if (id3v1Found)
        {
            srs.ContainerChunks.Add(new SrsContainerChunk
            {
                BlockPosition = endOffset - id3v1Size,
                BlockSize = id3v1Size,
                Label = "ID3v1",
                ChunkId = "TAG",
                HeaderSize = 3,
                PayloadSize = id3v1Size - 3
            });
            endOffset -= id3v1Size;
        }

        // Check for Lyrics3v2
        var (lyrics3v2Found, lyrics3v2Size) = Mp3TagReader.DetectLyrics3v2(fs, endOffset);
        if (lyrics3v2Found)
        {
            srs.ContainerChunks.Add(new SrsContainerChunk
            {
                BlockPosition = endOffset - lyrics3v2Size,
                BlockSize = lyrics3v2Size,
                Label = "Lyrics3v2",
                ChunkId = "LYRICS200",
                HeaderSize = 11, // "LYRICSBEGIN"
                PayloadSize = lyrics3v2Size - 11
            });
            endOffset -= lyrics3v2Size;
        }
        else
        {
            // Check for Lyrics3v1
            var (lyrics3v1Found, lyrics3v1Size) = Mp3TagReader.DetectLyrics3v1(fs, endOffset);
            if (lyrics3v1Found)
            {
                srs.ContainerChunks.Add(new SrsContainerChunk
                {
                    BlockPosition = endOffset - lyrics3v1Size,
                    BlockSize = lyrics3v1Size,
                    Label = "Lyrics3v1",
                    ChunkId = "LYRICS",
                    HeaderSize = 11, // "LYRICSBEGIN"
                    PayloadSize = lyrics3v1Size - 11
                });
                endOffset -= lyrics3v1Size;
            }
        }

        // Check for APE tag
        var (apeFound, apeSize) = Mp3TagReader.DetectApeTag(fs, endOffset);
        if (apeFound)
        {
            srs.ContainerChunks.Add(new SrsContainerChunk
            {
                BlockPosition = endOffset - apeSize,
                BlockSize = apeSize,
                Label = "APEv2",
                ChunkId = "APETAGEX",
                HeaderSize = 32,
                PayloadSize = apeSize - 32
            });
        }
    }

    // ==================== FLAC Parser ====================

    private static void ParseFlac(BinaryReader reader, FileStream fs, SRSFile srs)
    {
        // Check for ID3v2 wrapper before fLaC marker
        var (id3Found, id3Size) = FlacMetadataReader.DetectId3v2Wrapper(fs);
        if (id3Found)
        {
            srs.ContainerChunks.Add(new SrsContainerChunk
            {
                BlockPosition = 0,
                BlockSize = id3Size,
                Label = "ID3v2",
                ChunkId = "ID3",
                HeaderSize = 10,
                PayloadSize = id3Size - 10
            });
            fs.Position = id3Size;
        }
        else
        {
            fs.Position = 0;
        }

        // Skip fLaC marker
        long markerPos = fs.Position;
        fs.Position += 4;

        srs.ContainerChunks.Add(new SrsContainerChunk
        {
            BlockPosition = markerPos,
            BlockSize = 4,
            Label = "fLaC",
            ChunkId = "fLaC",
            HeaderSize = 4,
            PayloadSize = 0
        });

        while (fs.Position + 4 <= fs.Length)
        {
            long frameOffset = fs.Position;
            var (isLast, type, payloadSize) = FlacMetadataReader.ReadMetadataBlockHeader(reader);
            int headerSize = 4;

            long payloadStart = fs.Position;

            if (type == 0x73) // 's' = SRSF
            {
                srs.FileData = ParseFileDataPayload(reader, payloadStart, frameOffset, headerSize, headerSize + payloadSize);
            }
            else if (type == 0x74) // 't' = SRST
            {
                srs.Tracks.Add(ParseTrackDataPayload(reader, payloadStart, frameOffset, headerSize, headerSize + payloadSize));
            }
            else
            {
                string label = FlacMetadataReader.GetBlockTypeName(type);

                srs.ContainerChunks.Add(new SrsContainerChunk
                {
                    BlockPosition = frameOffset,
                    BlockSize = headerSize + payloadSize,
                    Label = label,
                    ChunkId = $"0x{type:X2}",
                    HeaderSize = headerSize,
                    PayloadSize = payloadSize
                });
            }

            fs.Position = payloadStart + payloadSize;
            if (isLast)
            {
                break;
            }
        }
    }

    // ==================== AVI/RIFF Parser ====================

    private static void ParseRiff(BinaryReader reader, FileStream fs, SRSFile srs)
    {
        ParseRiffChunks(reader, fs, srs, 0, fs.Length);
    }

    private static void ParseRiffChunks(BinaryReader reader, FileStream fs, SRSFile srs,
        long start, long end)
    {
        fs.Position = start;

        while (fs.Position + 8 <= end)
        {
            long frameOffset = fs.Position;
            string fourcc = new(reader.ReadChars(4));
            uint payloadSize = reader.ReadUInt32();
            int headerSize = 8;

            if (fourcc is "RIFF" or "LIST")
            {
                // Container chunk: read 4-byte subtype
                string subType = new(reader.ReadChars(4));
                string label = $"{fourcc} {subType}";

                long totalSize = headerSize + payloadSize;
                srs.ContainerChunks.Add(new SrsContainerChunk
                {
                    BlockPosition = frameOffset,
                    BlockSize = totalSize,
                    Label = label,
                    ChunkId = fourcc,
                    HeaderSize = headerSize + 4, // includes subtype
                    PayloadSize = payloadSize - 4
                });

                // Recurse into sub-chunks (after the 4-byte subtype)
                long childStart = fs.Position;
                long childEnd = frameOffset + headerSize + payloadSize;
                if (childEnd > end)
                {
                    childEnd = end;
                }

                ParseRiffChunks(reader, fs, srs, childStart, childEnd);
                fs.Position = childEnd;

                // Pad to even boundary
                if (payloadSize % 2 != 0 && fs.Position < end)
                {
                    fs.Position++;
                }
            }
            else if (fourcc == "SRSF")
            {
                long payloadStart = fs.Position;
                srs.FileData = ParseFileDataPayload(reader, payloadStart, frameOffset, headerSize, headerSize + payloadSize);
                fs.Position = payloadStart + payloadSize;
                if (payloadSize % 2 != 0 && fs.Position < end)
                {
                    fs.Position++;
                }
            }
            else if (fourcc == "SRST")
            {
                long payloadStart = fs.Position;
                srs.Tracks.Add(ParseTrackDataPayload(reader, payloadStart, frameOffset, headerSize, headerSize + payloadSize));
                fs.Position = payloadStart + payloadSize;
                if (payloadSize % 2 != 0 && fs.Position < end)
                {
                    fs.Position++;
                }
            }
            else
            {
                srs.ContainerChunks.Add(new SrsContainerChunk
                {
                    BlockPosition = frameOffset,
                    BlockSize = headerSize + payloadSize,
                    Label = fourcc,
                    ChunkId = fourcc,
                    HeaderSize = headerSize,
                    PayloadSize = payloadSize
                });

                fs.Position = frameOffset + headerSize + payloadSize;
                if (payloadSize % 2 != 0 && fs.Position < end)
                {
                    fs.Position++;
                }
            }
        }
    }

    // ==================== MP4 Parser ====================

    private static void ParseMp4(BinaryReader reader, FileStream fs, SRSFile srs)
    {
        ParseMp4Atoms(reader, fs, srs, 0, fs.Length);
    }

    private static readonly HashSet<string> Mp4ContainerAtoms = new(StringComparer.Ordinal)
    {
        "moov", "trak", "mdia", "minf", "stbl", "edts", "udta"
    };

    private static void ParseMp4Atoms(BinaryReader reader, FileStream fs, SRSFile srs,
        long start, long end)
    {
        fs.Position = start;

        while (fs.Position + 8 <= end)
        {
            long frameOffset = fs.Position;

            // BE32 total size
            byte[] sizeBytes = reader.ReadBytes(4);
            uint size32 = (uint)((sizeBytes[0] << 24) | (sizeBytes[1] << 16) | (sizeBytes[2] << 8) | sizeBytes[3]);
            string type = new(reader.ReadChars(4));
            int headerSize = 8;
            long totalSize;

            if (size32 == 1)
            {
                // Extended size: next 8 bytes = BE64
                byte[] extBytes = reader.ReadBytes(8);
                totalSize = 0;
                for (int i = 0; i < 8; i++)
                {
                    totalSize = (totalSize << 8) | extBytes[i];
                }

                headerSize = 16;
            }
            else if (size32 == 0)
            {
                // Atom extends to end of file
                totalSize = end - frameOffset;
            }
            else
            {
                totalSize = size32;
            }

            if (totalSize < headerSize)
            {
                break;
            }

            long payloadSize = totalSize - headerSize;
            long payloadStart = frameOffset + headerSize;

            if (type == "SRSF")
            {
                srs.FileData = ParseFileDataPayload(reader, payloadStart, frameOffset, headerSize, totalSize);
                fs.Position = frameOffset + totalSize;
            }
            else if (type == "SRST")
            {
                srs.Tracks.Add(ParseTrackDataPayload(reader, payloadStart, frameOffset, headerSize, totalSize));
                fs.Position = frameOffset + totalSize;
            }
            else if (Mp4ContainerAtoms.Contains(type))
            {
                srs.ContainerChunks.Add(new SrsContainerChunk
                {
                    BlockPosition = frameOffset,
                    BlockSize = totalSize,
                    Label = type,
                    ChunkId = type,
                    HeaderSize = headerSize,
                    PayloadSize = payloadSize
                });

                // Recurse into children
                long childEnd = frameOffset + totalSize;
                if (childEnd > end)
                {
                    childEnd = end;
                }

                ParseMp4Atoms(reader, fs, srs, payloadStart, childEnd);
                fs.Position = childEnd;
            }
            else
            {
                srs.ContainerChunks.Add(new SrsContainerChunk
                {
                    BlockPosition = frameOffset,
                    BlockSize = totalSize,
                    Label = type,
                    ChunkId = type,
                    HeaderSize = headerSize,
                    PayloadSize = payloadSize
                });

                fs.Position = frameOffset + totalSize;
            }
        }
    }

    // ==================== WMV/ASF Parser ====================

    private static readonly byte[] GuidSrsFile = Encoding.ASCII.GetBytes("SRSFSRSFSRSFSRSF");
    private static readonly byte[] GuidSrsTrack = Encoding.ASCII.GetBytes("SRSTSRSTSRSTSRST");
    private static readonly byte[] GuidSrsPadding = Encoding.ASCII.GetBytes("PADDINGBYTESDATA");

    private static void ParseAsf(BinaryReader reader, FileStream fs, SRSFile srs)
    {
        while (fs.Position + 24 <= fs.Length)
        {
            long frameOffset = fs.Position;
            byte[] guid = reader.ReadBytes(16);
            ulong totalSize = reader.ReadUInt64();
            int headerSize = 24;

            if (totalSize < 24)
            {
                break;
            }

            long payloadSize = (long)totalSize - headerSize;
            long payloadStart = fs.Position;

            if (GuidEquals(guid, GuidSrsFile))
            {
                srs.FileData = ParseFileDataPayload(reader, payloadStart, frameOffset, headerSize, (long)totalSize);
            }
            else if (GuidEquals(guid, GuidSrsTrack))
            {
                srs.Tracks.Add(ParseTrackDataPayload(reader, payloadStart, frameOffset, headerSize, (long)totalSize));
            }
            else
            {
                string label = GuidEquals(guid, GuidSrsPadding) ? "SRS Padding" : FormatGuid(guid);

                srs.ContainerChunks.Add(new SrsContainerChunk
                {
                    BlockPosition = frameOffset,
                    BlockSize = (long)totalSize,
                    Label = label,
                    ChunkId = FormatGuid(guid),
                    HeaderSize = headerSize,
                    PayloadSize = payloadSize
                });
            }

            fs.Position = frameOffset + (long)totalSize;
        }
    }

    private static bool GuidEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }

    private static string FormatGuid(byte[] guid)
    {
        if (guid.Length != 16)
        {
            return BitConverter.ToString(guid);
        }

        return new Guid(guid).ToString("D").ToUpperInvariant();
    }

    // ==================== MKV/EBML Parser ====================

    private static void ParseEbml(BinaryReader reader, FileStream fs, SRSFile srs)
    {
        long fileLength = fs.Length;

        // Parse top-level EBML elements
        while (fs.Position < fileLength)
        {
            long frameOffset = fs.Position;
            if (!TryReadVint(fs, out ulong elementId, out int idLen))
            {
                break;
            }

            if (!TryReadVintSize(fs, out ulong dataSize, out int sizeLen))
            {
                break;
            }

            int headerSize = idLen + sizeLen;
            long declaredTotal = headerSize + (long)dataSize;
            long actualTotal = Math.Min(declaredTotal, fileLength - frameOffset);
            long actualPayload = actualTotal - headerSize;
            long payloadStart = fs.Position;

            // ReSample container ID: 0x1F697576
            if (elementId == 0x1F697576)
            {
                // Parse children of ReSample element
                srs.ContainerChunks.Add(new SrsContainerChunk
                {
                    BlockPosition = frameOffset,
                    BlockSize = actualTotal,
                    Label = "ReSample",
                    ChunkId = $"0x{elementId:X}",
                    HeaderSize = headerSize,
                    PayloadSize = actualPayload
                });

                long childEnd = Math.Min(payloadStart + (long)dataSize, fileLength);
                ParseEbmlReSampleChildren(reader, fs, srs, payloadStart, childEnd);
                fs.Position = childEnd;
            }
            else if (elementId == 0x18538067) // Segment
            {
                srs.ContainerChunks.Add(new SrsContainerChunk
                {
                    BlockPosition = frameOffset,
                    BlockSize = actualTotal,
                    Label = "Segment",
                    ChunkId = $"0x{elementId:X}",
                    HeaderSize = headerSize,
                    PayloadSize = actualPayload
                });

                // Parse children of Segment to find ReSample
                long childEnd = Math.Min(payloadStart + (long)dataSize, fileLength);
                ParseEbmlSegmentChildren(reader, fs, srs, payloadStart, childEnd);
                fs.Position = childEnd;
            }
            else
            {
                string label = GetEbmlElementName(elementId);
                srs.ContainerChunks.Add(new SrsContainerChunk
                {
                    BlockPosition = frameOffset,
                    BlockSize = actualTotal,
                    Label = label,
                    ChunkId = $"0x{elementId:X}",
                    HeaderSize = headerSize,
                    PayloadSize = actualPayload
                });

                fs.Position = Math.Min(payloadStart + (long)dataSize, fileLength);
            }
        }
    }

    private static void ParseEbmlSegmentChildren(BinaryReader reader, FileStream fs, SRSFile srs,
        long start, long end)
    {
        long fileLength = fs.Length;
        end = Math.Min(end, fileLength);
        fs.Position = start;

        while (fs.Position + 2 <= end && fs.Position < fileLength)
        {
            long frameOffset = fs.Position;
            if (!TryReadVint(fs, out ulong elementId, out int idLen))
            {
                break;
            }

            if (!TryReadVintSize(fs, out ulong dataSize, out int sizeLen))
            {
                break;
            }

            int headerSize = idLen + sizeLen;
            long declaredTotal = headerSize + (long)dataSize;
            long actualTotal = Math.Min(declaredTotal, fileLength - frameOffset);
            long actualPayload = actualTotal - headerSize;
            long payloadStart = fs.Position;

            if (elementId == 0x1F697576) // ReSample
            {
                srs.ContainerChunks.Add(new SrsContainerChunk
                {
                    BlockPosition = frameOffset,
                    BlockSize = actualTotal,
                    Label = "ReSample",
                    ChunkId = $"0x{elementId:X}",
                    HeaderSize = headerSize,
                    PayloadSize = actualPayload
                });

                long childEnd = Math.Min(payloadStart + (long)dataSize, fileLength);
                ParseEbmlReSampleChildren(reader, fs, srs, payloadStart, childEnd);
            }
            else
            {
                string label = GetEbmlElementName(elementId);
                srs.ContainerChunks.Add(new SrsContainerChunk
                {
                    BlockPosition = frameOffset,
                    BlockSize = actualTotal,
                    Label = label,
                    ChunkId = $"0x{elementId:X}",
                    HeaderSize = headerSize,
                    PayloadSize = actualPayload
                });
            }

            fs.Position = Math.Min(payloadStart + (long)dataSize, fileLength);
        }
    }

    private static void ParseEbmlReSampleChildren(BinaryReader reader, FileStream fs, SRSFile srs,
        long start, long end)
    {
        fs.Position = start;

        while (fs.Position + 2 < end)
        {
            long frameOffset = fs.Position;
            if (!TryReadVint(fs, out ulong elementId, out int idLen))
            {
                break;
            }

            if (!TryReadVintSize(fs, out ulong dataSize, out int sizeLen))
            {
                break;
            }

            int headerSize = idLen + sizeLen;
            long payloadStart = fs.Position;
            long totalSize = headerSize + (long)dataSize;

            if (elementId == 0x6A75) // RESAMPLE_FILE
            {
                srs.FileData = ParseFileDataPayload(reader, payloadStart, frameOffset, headerSize, totalSize);
            }
            else if (elementId == 0x6B75) // RESAMPLE_TRACK
            {
                srs.Tracks.Add(ParseTrackDataPayload(reader, payloadStart, frameOffset, headerSize, totalSize));
            }
            else
            {
                srs.ContainerChunks.Add(new SrsContainerChunk
                {
                    BlockPosition = frameOffset,
                    BlockSize = totalSize,
                    Label = $"Element 0x{elementId:X}",
                    ChunkId = $"0x{elementId:X}",
                    HeaderSize = headerSize,
                    PayloadSize = (long)dataSize
                });
            }

            fs.Position = payloadStart + (long)dataSize;
        }
    }

    /// <summary>
    /// Reads a VINT element ID (does NOT mask out the marker bit).
    /// </summary>
    private static bool TryReadVint(Stream stream, out ulong value, out int length)
    {
        value = 0;
        length = 0;
        int first = stream.ReadByte();
        if (first < 0)
        {
            return false;
        }

        // Count leading zeros to determine VINT length
        int mask = 0x80;
        length = 1;
        while (length <= 8 && (first & mask) == 0)
        {
            mask >>= 1;
            length++;
        }

        if (length > 8)
        {
            return false;
        }

        // For element IDs, keep the marker bit
        value = (ulong)first;
        for (int i = 1; i < length; i++)
        {
            int b = stream.ReadByte();
            if (b < 0)
            {
                return false;
            }

            value = (value << 8) | (uint)b;
        }

        return true;
    }

    /// <summary>
    /// Reads a VINT data size (masks out the marker bit).
    /// </summary>
    private static bool TryReadVintSize(Stream stream, out ulong value, out int length)
    {
        value = 0;
        length = 0;
        int first = stream.ReadByte();
        if (first < 0)
        {
            return false;
        }

        int mask = 0x80;
        length = 1;
        while (length <= 8 && (first & mask) == 0)
        {
            mask >>= 1;
            length++;
        }

        if (length > 8)
        {
            return false;
        }

        // For sizes, mask out the marker bit
        value = (ulong)(first & (mask - 1));
        for (int i = 1; i < length; i++)
        {
            int b = stream.ReadByte();
            if (b < 0)
            {
                return false;
            }

            value = (value << 8) | (uint)b;
        }

        return true;
    }

    private static string GetEbmlElementName(ulong id) => id switch
    {
        0x1A45DFA3 => "EBML",
        0x4286 => "EBMLVersion",
        0x42F7 => "EBMLReadVersion",
        0x42F2 => "EBMLMaxIDLength",
        0x42F3 => "EBMLMaxSizeLength",
        0x4282 => "DocType",
        0x4287 => "DocTypeVersion",
        0x4285 => "DocTypeReadVersion",
        0x18538067 => "Segment",
        0x114D9B74 => "SeekHead",
        0x1549A966 => "Info",
        0x1654AE6B => "Tracks",
        0x1F43B675 => "Cluster",
        0x1C53BB6B => "Cues",
        0x1941A469 => "Attachments",
        0x1043A770 => "Chapters",
        0x1254C367 => "Tags",
        0x1F697576 => "ReSample",
        _ => $"Element 0x{id:X}"
    };
}
