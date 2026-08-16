namespace ReScene.RAR;

/// <summary>
/// Reads RAR 4.x headers from a stream.
/// </summary>
internal class RARHeaderReader
{
    private readonly BinaryReader _reader;
    private readonly Stream _stream;

    /// <summary>
    /// Creates a new RAR header reader.
    /// </summary>
    /// <param name="stream">
    /// Stream to read from
    /// </param>
    public RARHeaderReader(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
    }

    /// <summary>
    /// Creates a new RAR header reader using an existing BinaryReader.
    /// </summary>
    /// <param name="reader">
    /// BinaryReader to use
    /// </param>
    public RARHeaderReader(BinaryReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _stream = reader.BaseStream;
    }

    /// <summary>
    /// Checks if there are enough bytes remaining to read a base header.
    /// </summary>
    public bool CanReadBaseHeader => _stream.Position + RAR4HeaderLayout.BaseHeaderSize <= _stream.Length;

    /// <summary>
    /// Peeks at the next block type without advancing the stream position.
    /// </summary>
    /// <returns>
    /// Block type byte, or null if not enough data
    /// </returns>
    public byte? PeekBlockType()
    {
        if (_stream.Position + 3 > _stream.Length)
        {
            return null;
        }

        long pos = _stream.Position;
        _stream.Seek(2, SeekOrigin.Current); // Skip CRC
        byte type = _reader.ReadByte();
        _stream.Seek(pos, SeekOrigin.Begin);
        return type;
    }

    /// <summary>
    /// Reads a RAR block header and optionally parses its contents.
    /// </summary>
    /// <param name="parseContents">
    /// If true, parse archive/file header contents
    /// </param>
    /// <returns>
    /// Block read result, or null if not enough data
    /// </returns>
    public RARBlockReadResult? ReadBlock(bool parseContents = true)
    {
        if (!CanReadBaseHeader)
        {
            return null;
        }

        long blockStart = _stream.Position;

        // Read base header (7 bytes)
        ushort crc = _reader.ReadUInt16();
        byte typeRaw = _reader.ReadByte();
        ushort flags = _reader.ReadUInt16();
        ushort headerSize = _reader.ReadUInt16();

        if (headerSize < RAR4HeaderLayout.BaseHeaderSize || blockStart + headerSize > _stream.Length)
        {
            return null;
        }

        var result = new RARBlockReadResult
        {
            BlockType = (RAR4BlockType)typeRaw,
            Flags = flags,
            HeaderSize = headerSize,
            BlockPosition = blockStart,
            HeaderCRC = crc
        };

        // Validate CRC by reading entire header
        long currentPos = _stream.Position;
        _stream.Seek(blockStart, SeekOrigin.Begin);
        byte[] headerBytes = _reader.ReadBytes(headerSize);
        result.CRCValid = RARUtils.ValidateHeaderCRC(crc, headerBytes);
        _stream.Seek(currentPos, SeekOrigin.Begin);

        // Read ADD_SIZE for file headers and service blocks (always present even without LONG_BLOCK flag)
        bool hasAddSize = (flags & (ushort)RARFileFlags.LongBlock) != 0 ||
                          result.BlockType == RAR4BlockType.FileHeader ||
                          result.BlockType == RAR4BlockType.Service;

        if (hasAddSize)
        {
            if (_stream.Position + 4 > _stream.Length)
            {
                return null;
            }

            result.AddSize = _reader.ReadUInt32();
        }

        if (parseContents)
        {
            long headerEnd = blockStart + headerSize;

            switch (result.BlockType)
            {
                case RAR4BlockType.ArchiveHeader:
                    result.ArchiveHeader = ParseArchiveHeader(result);
                    break;
                case RAR4BlockType.FileHeader:
                    result.FileHeader = ParseFileHeader(result, headerEnd);
                    break;
                case RAR4BlockType.Service:
                    result.ServiceBlockInfo = ParseServiceBlock(result, headerEnd);
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// Skips to the end of the current block (header only, not data).
    /// For file blocks in SRR files, data is not present.
    /// </summary>
    /// <param name="block">
    /// Block to skip
    /// </param>
    /// <param name="includeData">
    /// If true, also skip ADD_SIZE bytes (for non-file blocks)
    /// </param>
    public void SkipBlock(RARBlockReadResult block, bool includeData = false)
    {
        long target = block.BlockPosition + block.HeaderSize;
        if (includeData && block.BlockType != RAR4BlockType.FileHeader)
        {
            target += block.AddSize;
        }

        if (target <= block.BlockPosition || target > _stream.Length)
        {
            _stream.Seek(_stream.Length, SeekOrigin.Begin);
        }
        else
        {
            _stream.Seek(target, SeekOrigin.Begin);
        }
    }

    private static RARArchiveHeader ParseArchiveHeader(RARBlockReadResult block)
    {
        return new RARArchiveHeader
        {
            BlockPosition = block.BlockPosition,
            HeaderCRC = block.HeaderCRC,
            HeaderSize = block.HeaderSize,
            Flags = (RARArchiveFlags)block.Flags,
            CRCValid = block.CRCValid
        };
    }

    private RARFileHeader? ParseFileHeader(RARBlockReadResult block, long headerEnd)
    {
        // UNP_SIZE(4) + HOST_OS(1) + CRC(4) + TIME(4) + VER(1) + METHOD(1) + NAME_SIZE(2) + ATTR(4)
        const int fileFieldsSize = 4 + 1 + 4 + 4 + 1 + 1 + 2 + 4;
        const int minFileHeaderSize = RAR4HeaderLayout.BaseHeaderSize + RAR4HeaderLayout.AddSizeFieldLength + fileFieldsSize;

        if (block.HeaderSize < minFileHeaderSize)
        {
            return null;
        }

        if (_stream.Position + fileFieldsSize > headerEnd)
        {
            return null;
        }

        var flags = (RARFileFlags)block.Flags;

        // PACK_SIZE is already in AddSize for file headers
        uint packSize = block.AddSize;

        // Read remaining fields
        uint unpSize = _reader.ReadUInt32();
        byte hostOS = _reader.ReadByte();
        uint fileCRC = _reader.ReadUInt32();
        uint fileTime = _reader.ReadUInt32();
        byte unpVer = _reader.ReadByte();

        // Method is stored as ASCII '0'-'6', subtract AsciiDigitZero to get 0-6
        byte methodRaw = _reader.ReadByte();
        byte method = (byte)(methodRaw >= RAR4HeaderLayout.AsciiDigitZero ? methodRaw - RAR4HeaderLayout.AsciiDigitZero : methodRaw);

        // Read filename
        string? fileName = TryReadFileName(headerEnd, flags, out bool isDirectory, out uint fileAttributes, out uint highPackSize, out uint highUnpSize);

        // Handle 64-bit sizes if LHD_LARGE is set
        ulong packedSize = packSize | ((ulong)highPackSize << 32);
        ulong unpackedSize = unpSize | ((ulong)highUnpSize << 32);

        // Parse timestamps
        DateTime? modifiedTime = RARUtils.DosDateToDateTime(fileTime);
        DateTime? creationTime = null;
        DateTime? accessTime = null;

        // Default precisions - assume not saved unless we find data
        TimestampPrecision mtimePrecision = TimestampPrecision.NotSaved;
        TimestampPrecision ctimePrecision = TimestampPrecision.NotSaved;
        TimestampPrecision atimePrecision = TimestampPrecision.NotSaved;

        // If DOS time is present and non-zero, mtime has at least 1-second precision
        if (fileTime != 0)
        {
            mtimePrecision = TimestampPrecision.OneSecond;
        }

        // Skip salt if present
        SkipOptionalSalt(headerEnd, flags);

        // Read extended times and detect precision
        ReadExtendedTimes(headerEnd, flags, fileTime,
            ref modifiedTime, ref creationTime, ref accessTime,
            ref mtimePrecision, ref ctimePrecision, ref atimePrecision);

        return new RARFileHeader
        {
            BlockPosition = block.BlockPosition,
            HeaderCRC = block.HeaderCRC,
            HeaderSize = block.HeaderSize,
            Flags = flags,
            PackedSize = packedSize,
            UnpackedSize = unpackedSize,
            HostOS = hostOS,
            FileCRC = fileCRC,
            UnpackVersion = unpVer,
            CompressionMethod = method,
            DictionarySizeKB = RARUtils.GetDictionarySize(flags),
            FileAttributes = fileAttributes,
            FileName = fileName ?? string.Empty,
            IsDirectory = isDirectory,
            ModifiedTime = modifiedTime,
            CreationTime = creationTime,
            AccessTime = accessTime,
            FileTimeDOS = fileTime,
            MtimePrecision = mtimePrecision,
            CtimePrecision = ctimePrecision,
            AtimePrecision = atimePrecision,
            CRCValid = block.CRCValid,
            HighPackSize = highPackSize,
            HighUnpSize = highUnpSize
        };
    }

    private string? TryReadFileName(long headerEnd, RARFileFlags flags, out bool isDirectory, out uint fileAttributes, out uint highPackSize, out uint highUnpSize)
    {
        isDirectory = RARUtils.IsDirectory(flags);
        fileAttributes = 0;
        highPackSize = 0;
        highUnpSize = 0;

        if (_stream.Position + 2 + 4 > headerEnd)
        {
            return null;
        }

        ushort nameSize = _reader.ReadUInt16();
        fileAttributes = _reader.ReadUInt32();

        // Read HIGH_PACK_SIZE and HIGH_UNP_SIZE if LHD_LARGE is set (64-bit sizes)
        if ((flags & RARFileFlags.Large) != 0)
        {
            if (_stream.Position + 8 > headerEnd)
            {
                return null;
            }

            highPackSize = _reader.ReadUInt32();
            highUnpSize = _reader.ReadUInt32();
        }

        if (nameSize == 0)
        {
            return null;
        }

        if (_stream.Position + nameSize > headerEnd)
        {
            return null;
        }

        byte[] nameBytes = _reader.ReadBytes(nameSize);
        string? name = RARUtils.DecodeFileName(nameBytes, (flags & RARFileFlags.Unicode) != 0);

        // Check for trailing slash indicating directory
        if (!string.IsNullOrEmpty(name) &&
            (name.EndsWith('\\') || name.EndsWith('/')))
        {
            isDirectory = true;
        }

        return name;
    }

    private void SkipOptionalSalt(long headerEnd, RARFileFlags flags)
    {
        if ((flags & RARFileFlags.Salt) == 0)
        {
            return;
        }

        TrySkipBytes(headerEnd, RARFlagMasks.SaltLength);
    }

    private void ReadExtendedTimes(long headerEnd, RARFileFlags flags, uint baseFileTime,
        ref DateTime? modifiedTime, ref DateTime? creationTime, ref DateTime? accessTime,
        ref TimestampPrecision mtimePrecision, ref TimestampPrecision ctimePrecision, ref TimestampPrecision atimePrecision)
    {
        if ((flags & RARFileFlags.ExtTime) == 0)
        {
            return;
        }

        if (!TryReadUInt16(headerEnd, out ushort extFlags))
        {
            return;
        }

        for (int i = 0; i < RAR4HeaderLayout.ExtTimeFieldCount; i++)
        {
            int rmode = (extFlags >> ((RAR4HeaderLayout.ExtTimeFieldCount - 1 - i) * 4)) & RAR4HeaderLayout.ExtTimeNibbleMask;

            // Determine precision for this time type
            // rmode & ExtTimePresentBit = time present flag
            // rmode & ExtTimePrecisionMask = number of extra precision bytes (0-3)
            TimestampPrecision precision;
            if ((rmode & RAR4HeaderLayout.ExtTimePresentBit) == 0)
            {
                // Time not present in extended time structure
                // For mtime, keep existing precision (from DOS time)
                // For ctime/atime, they remain NotSaved
                continue;
            }
            else
            {
                // Time is present - precision based on extra byte count
                precision = PrecisionFromExtraBytes(rmode & RAR4HeaderLayout.ExtTimePrecisionMask);
            }

            // mtime uses base DOS time; ctime/atime have their own DOS time
            uint dosTime = baseFileTime;
            if (i != 0 && !TryReadUInt32(headerEnd, out dosTime))
            {
                return;
            }

            DateTime? time = RARUtils.DosDateToDateTime(dosTime);
            if ((rmode & RAR4HeaderLayout.ExtTimeRoundUpBit) != 0 && time.HasValue)
            {
                time = time.Value.AddSeconds(1);
            }

            int count = rmode & RAR4HeaderLayout.ExtTimePrecisionMask;
            if (!TryReadRemainder(headerEnd, count, out int remainder))
            {
                return;
            }

            // Remainder is in 100ns units, which map directly to DateTime ticks
            if (time.HasValue && remainder != 0)
            {
                time = time.Value.AddTicks(remainder);
            }

            switch (i)
            {
                case 0:
                    modifiedTime = time;
                    mtimePrecision = precision;
                    break;
                case 1:
                    creationTime = time;
                    ctimePrecision = precision;
                    break;
                case 2:
                    accessTime = time;
                    atimePrecision = precision;
                    break;
            }
        }
    }

    private bool TryReadRemainder(long headerEnd, int count, out int remainder)
    {
        remainder = 0;
        if (count <= 0)
        {
            return true;
        }

        if (!TryReadBytes(headerEnd, count, out byte[] bytes))
        {
            return false;
        }

        for (int j = 0; j < count; j++)
        {
            remainder |= bytes[j] << ((j + 3 - count) * 8);
        }

        return true;
    }

    private bool TryReadUInt16(long headerEnd, out ushort value)
    {
        value = 0;
        if (_stream.Position + 2 > headerEnd)
        {
            return false;
        }

        value = _reader.ReadUInt16();
        return true;
    }

    private bool TryReadUInt32(long headerEnd, out uint value)
    {
        value = 0;
        if (_stream.Position + 4 > headerEnd)
        {
            return false;
        }

        value = _reader.ReadUInt32();
        return true;
    }

    private bool TryReadBytes(long headerEnd, int count, out byte[] bytes)
    {
        bytes = [];
        if (count < 0)
        {
            return false;
        }

        if (_stream.Position + count > headerEnd)
        {
            return false;
        }

        bytes = _reader.ReadBytes(count);
        return bytes.Length == count;
    }

    private bool TrySkipBytes(long headerEnd, int count)
    {
        if (count <= 0)
        {
            return true;
        }

        long target = _stream.Position + count;
        if (target > headerEnd || target < 0)
        {
            return false;
        }

        _stream.Seek(count, SeekOrigin.Current);
        return true;
    }

    /// <summary>
    /// Parses a service sub-block (0x7A) to extract sub-type and data.
    /// </summary>
    private RARServiceBlockInfo? ParseServiceBlock(RARBlockReadResult block, long headerEnd)
    {
        // Service blocks have a structure similar to file headers:
        // Base header: CRC (2) + Type (1) + Flags (2) + HeaderSize (2) = 7
        // ADD_SIZE (4) = PACK_SIZE (included in HeaderSize)
        // UNP_SIZE (4) + HOST_OS (1) + DATA_CRC (4) + FILE_TIME (4) +
        // UNP_VER (1) + METHOD (1) + NAME_SIZE (2) + ATTR (4) = 21 bytes
        // + NAME (variable, minimum 1 byte)

        const int serviceFieldsSize = 21; // UNP_SIZE(4) + HOST_OS(1) + CRC(4) + TIME(4) + VER(1) + METHOD(1) + NAME_SIZE(2) + ATTR(4)
        const int minServiceHeaderSize = RAR4HeaderLayout.BaseHeaderSize + RAR4HeaderLayout.AddSizeFieldLength + serviceFieldsSize + 1; // base + ADD_SIZE + fields + min name

        if (block.HeaderSize < minServiceHeaderSize)
        {
            return null;
        }

        if (_stream.Position + serviceFieldsSize > headerEnd)
        {
            return null;
        }

        var flags = (RARFileFlags)block.Flags;

        // PACK_SIZE is in AddSize for service blocks
        uint packSize = block.AddSize;

        // Read service block fields (same layout as file header)
        uint unpSize = _reader.ReadUInt32();
        byte hostOS = _reader.ReadByte();
        uint dataCRC = _reader.ReadUInt32();
        uint subTime = _reader.ReadUInt32();
        byte unpVer = _reader.ReadByte();
        byte method = _reader.ReadByte();
        ushort nameSize = _reader.ReadUInt16();
        uint subAttr = _reader.ReadUInt32();

        // Handle 64-bit sizes if LHD_LARGE is set
        ulong packedSize = packSize;
        ulong unpackedSize = unpSize;

        if ((flags & RARFileFlags.Large) != 0)
        {
            if (_stream.Position + 8 > headerEnd)
            {
                return null;
            }

            uint highPackSize = _reader.ReadUInt32();
            uint highUnpSize = _reader.ReadUInt32();
            packedSize = packSize | ((ulong)highPackSize << 32);
            unpackedSize = unpSize | ((ulong)highUnpSize << 32);
        }

        if (nameSize == 0 || _stream.Position + nameSize > headerEnd)
        {
            return null;
        }

        // Read sub-type name (e.g., "CMT", "RR", "AV")
        byte[] nameBytes = _reader.ReadBytes(nameSize);
        string subType = System.Text.Encoding.ASCII.GetString(nameBytes);

        // Determine timestamp precision from DOS time and extended time flags
        // For service blocks: if FileTimeDOS is 0, time not saved; otherwise basic DOS precision
        TimestampPrecision mtimePrecision = subTime == 0
            ? TimestampPrecision.NotSaved
            : TimestampPrecision.OneSecond;
        TimestampPrecision ctimePrecision = TimestampPrecision.NotSaved;
        TimestampPrecision atimePrecision = TimestampPrecision.NotSaved;

        // Check for extended time data in service blocks
        if ((flags & RARFileFlags.ExtTime) != 0)
        {
            // Service blocks can have extended time - try to read precision
            // Skip salt if present
            SkipOptionalSalt(headerEnd, flags);

            // Try to read extended time flags to get precision
            if (_stream.Position + 2 <= headerEnd)
            {
                ushort extFlags = _reader.ReadUInt16();

                // mtime is at position 0 (bits 12-15)
                int mtimeRmode = (extFlags >> 12) & RAR4HeaderLayout.ExtTimeNibbleMask;
                if ((mtimeRmode & RAR4HeaderLayout.ExtTimePresentBit) != 0)
                {
                    mtimePrecision = PrecisionFromExtraBytes(mtimeRmode & RAR4HeaderLayout.ExtTimePrecisionMask);
                }

                // ctime is at position 1 (bits 8-11)
                int ctimeRmode = (extFlags >> 8) & RAR4HeaderLayout.ExtTimeNibbleMask;
                if ((ctimeRmode & RAR4HeaderLayout.ExtTimePresentBit) != 0)
                {
                    ctimePrecision = PrecisionFromExtraBytes(ctimeRmode & RAR4HeaderLayout.ExtTimePrecisionMask);
                }

                // atime is at position 2 (bits 4-7)
                int atimeRmode = (extFlags >> 4) & RAR4HeaderLayout.ExtTimeNibbleMask;
                if ((atimeRmode & RAR4HeaderLayout.ExtTimePresentBit) != 0)
                {
                    atimePrecision = PrecisionFromExtraBytes(atimeRmode & RAR4HeaderLayout.ExtTimePrecisionMask);
                }
            }
        }

        var result = new RARServiceBlockInfo
        {
            SubType = subType,
            PackedSize = packedSize,
            UnpackedSize = unpackedSize,
            CompressionMethod = method,
            DataCRC = dataCRC,
            DataOffset = block.HeaderSize,
            HostOS = hostOS,
            FileTimeDOS = subTime,
            FileAttributes = subAttr,
            UnpackVersion = unpVer,
            MtimePrecision = mtimePrecision,
            CtimePrecision = ctimePrecision,
            AtimePrecision = atimePrecision
        };

        return result;
    }

    /// <summary>
    /// Maps the RAR extended-time "extra byte" count (the low two bits of an rmode nibble) to a
    /// <see cref="TimestampPrecision"/>. Callers must already have verified the time is present
    /// (the <c>0x8</c> rmode bit) before calling.
    /// </summary>
    private static TimestampPrecision PrecisionFromExtraBytes(int extraBytes) => extraBytes switch
    {
        0 => TimestampPrecision.OneSecond,
        1 => TimestampPrecision.HighPrecision1,
        2 => TimestampPrecision.HighPrecision2,
        3 => TimestampPrecision.NtfsPrecision,
        _ => TimestampPrecision.OneSecond
    };

    /// <summary>
    /// Reads the data portion of a service block.
    /// Call this after ReadBlock to get the raw data.
    /// </summary>
    /// <param name="block">
    /// The service block to read data from.
    /// </param>
    /// <returns>
    /// The raw service block data, or <see langword="null"/> if not a service block.
    /// </returns>
    public byte[]? ReadServiceBlockData(RARBlockReadResult block)
    {
        if (block.BlockType != RAR4BlockType.Service || block.ServiceBlockInfo == null)
        {
            return null;
        }

        long dataStart = block.BlockPosition + block.HeaderSize;

        // ServiceBlockInfo.PackedSize, not block.AddSize. AddSize carries only the LOW 32 bits of
        // the packed size; when the LARGE flag is set the true size combines HIGH_PACK_SIZE, which
        // PackedSize already does. Reading AddSize alone silently returned just the first
        // (size mod 4 GiB) bytes of a large service block and reported that as the whole thing.
        ulong packedSize = block.ServiceBlockInfo.PackedSize;

        if (packedSize == 0 || packedSize > int.MaxValue)
        {
            return null;
        }

        long dataSize = (long)packedSize;
        if (dataStart + dataSize > _stream.Length)
        {
            return null;
        }

        _stream.Seek(dataStart, SeekOrigin.Begin);
        return _reader.ReadBytes((int)dataSize);
    }
}
