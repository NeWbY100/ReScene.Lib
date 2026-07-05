using System.Text;

namespace ReScene.RAR;

/// <summary>
/// Reads RAR 5.0 headers from a stream.
/// </summary>
/// <remarks>
/// Creates a new RAR 5.0 header reader.
/// </remarks>
internal class RAR5HeaderReader(Stream stream)
{
    /// <summary>
    /// RAR 5.0 marker bytes. Thin alias over <see cref="RARUtils.RAR5Marker"/>.
    /// </summary>
    public static byte[] RAR5Marker => RARUtils.RAR5Marker.ToArray();

    private readonly Stream _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    private readonly BinaryReader _reader = new(stream, Encoding.UTF8, leaveOpen: true);

    /// <summary>
    /// Checks if the stream starts with RAR 5.0 marker.
    /// </summary>
    /// <param name="stream">
    /// The stream to check.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the stream starts with a RAR 5.0 marker.
    /// </returns>
    public static bool IsRAR5(Stream stream) => IsRAR5(stream, 0);

    /// <summary>
    /// Checks if the stream contains a RAR 5.0 marker at the specified offset.
    /// </summary>
    /// <param name="stream">
    /// The stream to check.
    /// </param>
    /// <param name="offset">
    /// Byte offset to check at.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if a RAR 5.0 marker is found at the offset.
    /// </returns>
    public static bool IsRAR5(Stream stream, long offset)
    {
        if (stream.Length - offset < 8)
        {
            return false;
        }

        long pos = stream.Position;
        stream.Position = offset;
        byte[] marker = new byte[8];
        stream.ReadExactly(marker, 0, 8);
        stream.Position = pos;

        for (int i = 0; i < 8; i++)
        {
            if (marker[i] != RARUtils.RAR5Marker[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks if there are enough bytes remaining to read a base header.
    /// </summary>
    public bool CanReadBaseHeader => _stream.Position + 4 <= _stream.Length;

    /// <summary>
    /// Peeks at the next block type without advancing the stream position.
    /// Returns null if not enough data or if it looks like an SRR block.
    /// </summary>
    /// <returns>
    /// The block type byte, or <see langword="null"/> if insufficient data.
    /// </returns>
    public byte? PeekBlockType()
    {
        if (_stream.Position + 6 > _stream.Length)
        {
            return null;
        }

        long pos = _stream.Position;

        // Skip CRC32 (4 bytes)
        _stream.Seek(4, SeekOrigin.Current);

        // Read header size vint
        _ = ReadVInt();

        // Read type vint
        ulong headerType = ReadVInt();

        // Restore position
        _stream.Position = pos;

        return (byte)headerType;
    }

    /// <summary>
    /// Reads a variable-length integer (vint) from the stream.
    /// </summary>
    /// <returns>
    /// The decoded variable-length integer value.
    /// </returns>
    public ulong ReadVInt()
    {
        ulong result = 0;
        int shift = 0;

        while (true)
        {
            byte b = _reader.ReadByte();
            result |= (ulong)(b & RAR5Format.VIntDataMask) << shift;

            if ((b & RAR5Format.VIntContinuationBit) == 0)
            {
                break;
            }

            shift += RAR5Format.VIntShiftStep;
            if (shift > RAR5Format.VIntMaxShift)
            {
                throw new InvalidDataException("VInt too large");
            }
        }

        return result;
    }

    /// <summary>
    /// Reads a RAR 5.0 block header.
    /// </summary>
    /// <returns>
    /// The parsed block result, or <see langword="null"/> if no more blocks.
    /// </returns>
    public RAR5BlockReadResult? ReadBlock()
    {
        if (_stream.Position + 4 > _stream.Length)
        {
            return null;
        }

        _ = _stream.Position;
        uint crc = _reader.ReadUInt32();

        long headerSizePosition = _stream.Position;

        // Read header size - this is size starting from header type field
        ulong headerSize = ReadVInt();

        // Header content starts here (after header size vint)
        long headerContentStart = _stream.Position;

        if (headerContentStart + (long)headerSize > _stream.Length)
        {
            return null;
        }

        // Read header type
        ulong headerType = ReadVInt();

        // Read header flags
        ulong flags = ReadVInt();

        var result = new RAR5BlockReadResult
        {
            BlockType = (RAR5BlockType)headerType,
            Flags = flags,
            HeaderSize = headerSize,
            BlockPosition = headerContentStart,  // Position where header content starts
            HeaderCRC = crc
        };

        // Read extra area size if flag set
        if ((flags & (ulong)RAR5HeaderFlags.ExtraArea) != 0)
        {
            result.ExtraAreaSize = ReadVInt();
        }

        // Read data size if flag set
        if ((flags & (ulong)RAR5HeaderFlags.DataArea) != 0)
        {
            result.DataSize = ReadVInt();
        }

        // Set split flags from header flags
        bool isSplitBefore = (flags & (ulong)RAR5HeaderFlags.SplitBefore) != 0;
        bool isSplitAfter = (flags & (ulong)RAR5HeaderFlags.SplitAfter) != 0;

        // Parse type-specific content
        long headerEnd = headerContentStart + (long)headerSize;
        switch (result.BlockType)
        {
            case RAR5BlockType.Main:
                result.ArchiveInfo = ParseArchiveBlock(headerEnd);
                break;
            case RAR5BlockType.File:
                result.FileInfo = ParseFileBlock(headerEnd, isSplitBefore, isSplitAfter);
                break;
            case RAR5BlockType.Service:
                result.ServiceBlockInfo = ParseServiceBlock(headerEnd);
                break;
        }

        // Validate CRC - CRC covers from header size field to end of header
        long currentPos = _stream.Position;
        long crcDataSize = headerContentStart + (long)headerSize - headerSizePosition;
        if (crcDataSize is <= 0 or > int.MaxValue)
        {
            return result;
        }

        _stream.Position = headerSizePosition;
        byte[] headerData = _reader.ReadBytes((int)crcDataSize);
        uint calculatedCRC = Force.Crc32.Crc32Algorithm.Compute(headerData);
        result.CRCValid = crc == calculatedCRC;
        _stream.Position = currentPos;

        return result;
    }

    /// <summary>
    /// The raw file/service header fields shared by RAR5 FILE (0x02) and SERVICE (0x03)
    /// blocks, read in their on-disk order. Optional fields (governed by
    /// <see cref="RAR5FileFlags"/>) are read identically for both block kinds.
    /// </summary>
    private readonly record struct RAR5FileFields(
        ulong FileFlags,
        ulong UnpackedSize,
        ulong Attributes,
        uint? ModificationTime,
        uint? FileCRC,
        ulong CompressionInfo,
        ulong HostOS,
        string Name);

    /// <summary>
    /// Reads the eight common RAR5 file-block fields (flags, unpacked size, attributes,
    /// mtime, CRC, compression info, host OS, and name) in order. Callers map the raw
    /// values and perform their own compression-bit unpacking / type checks.
    /// </summary>
    private RAR5FileFields ReadRAR5FileFields(long headerEnd)
    {
        ulong fileFlags = ReadVInt();

        // Unpacked size (unless UNKNOWN_SIZE flag is set)
        ulong unpackedSize = 0;
        if ((fileFlags & (ulong)RAR5FileFlags.UnknownSize) == 0)
        {
            unpackedSize = ReadVInt();
        }

        // File attributes
        ulong attributes = ReadVInt();

        // mtime if present — stays null when the flag is clear (modern RAR5 often carries it
        // in the FHEXTRA extra area instead), so callers don't record a bogus 1970 timestamp.
        uint? mtime = null;
        if ((fileFlags & (ulong)RAR5FileFlags.TimePresent) != 0)
        {
            mtime = _reader.ReadUInt32();
        }

        // CRC if present — stays null when the flag is clear (don't record a bogus 00000000).
        uint? fileCRC = null;
        if ((fileFlags & (ulong)RAR5FileFlags.CRC32Present) != 0)
        {
            fileCRC = _reader.ReadUInt32();
        }

        // Compression info
        ulong compressionInfo = ReadVInt();

        // Host OS
        ulong hostOS = ReadVInt();

        // Name length and name
        string name = string.Empty;
        ulong nameLen = ReadVInt();
        if (nameLen > 0 && _stream.Position + (long)nameLen <= headerEnd)
        {
            byte[] nameBytes = _reader.ReadBytes((int)nameLen);
            name = Encoding.UTF8.GetString(nameBytes);
        }

        return new RAR5FileFields(fileFlags, unpackedSize, attributes, mtime, fileCRC, compressionInfo, hostOS, name);
    }

    private RAR5ServiceBlockInfo? ParseServiceBlock(long headerEnd)
    {
        RAR5FileFields fields = ReadRAR5FileFields(headerEnd);

        var info = new RAR5ServiceBlockInfo
        {
            FileFlags = fields.FileFlags,
            UnpackedSize = fields.UnpackedSize,
            CompressionVersion = (int)(fields.CompressionInfo & RAR5Format.CompInfoVersionMask),
            CompressionMethod = (int)((fields.CompressionInfo >> RAR5Format.CompInfoMethodShift) & RAR5Format.CompInfoMethodMask),
            DictSize = (int)((fields.CompressionInfo >> RAR5Format.CompInfoDictShift) & RAR5Format.CompInfoDictMask),
            SubType = fields.Name
        };
        info.IsStored = info.CompressionMethod == 0;

        // Check for CMT type
        if (info.SubType == "CMT" || info.SubType.StartsWith("CMT", StringComparison.Ordinal))
        {
            info.ServiceDataType = (ulong)RAR5ServiceType.Comment;
        }

        return info;
    }

    private RAR5ArchiveInfo ParseArchiveBlock(long headerEnd)
    {
        var info = new RAR5ArchiveInfo
        {
            // Read archive flags
            ArchiveFlags = ReadVInt()
        };

        // Read volume number if present
        if (info.HasVolumeNumber && _stream.Position < headerEnd)
        {
            info.VolumeNumber = ReadVInt();
        }

        return info;
    }

    private RAR5FileInfo ParseFileBlock(long headerEnd, bool isSplitBefore, bool isSplitAfter)
    {
        RAR5FileFields fields = ReadRAR5FileFields(headerEnd);

        return new RAR5FileInfo
        {
            IsSplitBefore = isSplitBefore,
            IsSplitAfter = isSplitAfter,
            FileFlags = fields.FileFlags,
            UnpackedSize = fields.UnpackedSize,
            Attributes = fields.Attributes,
            ModificationTime = fields.ModificationTime,
            FileCRC = fields.FileCRC,
            CompressionInfo = fields.CompressionInfo,
            HostOS = fields.HostOS,
            FileName = fields.Name
        };
    }

    /// <summary>
    /// Skips to the end of the current block.
    /// </summary>
    /// <param name="block">
    /// The block to skip past.
    /// </param>
    public void SkipBlock(RAR5BlockReadResult block)
    {
        // Move past the header
        long target = block.BlockPosition + (long)block.HeaderSize;

        // Include data area if present
        if ((block.Flags & (ulong)RAR5HeaderFlags.DataArea) != 0)
        {
            target += (long)block.DataSize;
        }

        if (target > _stream.Length)
        {
            target = _stream.Length;
        }

        _stream.Position = target;
    }

    /// <summary>
    /// Reads the data portion of a service block.
    /// </summary>
    /// <param name="block">
    /// The service block to read data from.
    /// </param>
    /// <returns>
    /// The raw service block data, or <see langword="null"/> if not a service block.
    /// </returns>
    public byte[]? ReadServiceBlockData(RAR5BlockReadResult block)
    {
        if (block.BlockType != RAR5BlockType.Service || block.ServiceBlockInfo == null)
        {
            return null;
        }

        if ((block.Flags & (ulong)RAR5HeaderFlags.DataArea) == 0 || block.DataSize == 0)
        {
            return null;
        }

        long dataStart = block.BlockPosition + (long)block.HeaderSize;
        if (dataStart + (long)block.DataSize > _stream.Length)
        {
            return null;
        }

        if (block.DataSize > int.MaxValue)
        {
            return null;
        }

        _stream.Position = dataStart;
        return _reader.ReadBytes((int)block.DataSize);
    }
}
