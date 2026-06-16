using System.Text;

namespace ReScene.RAR;

/// <summary>
/// Result of reading a RAR 5.0 block header.
/// </summary>
internal class RAR5BlockReadResult
{
    /// <summary>
    /// Block type (RAR 5.0).
    /// </summary>
    public RAR5BlockType BlockType
    {
        get; set;
    }

    /// <summary>
    /// Raw header flags value.
    /// </summary>
    public ulong Flags
    {
        get; set;
    }

    /// <summary>
    /// Header size in bytes (excluding CRC).
    /// </summary>
    public ulong HeaderSize
    {
        get; set;
    }

    /// <summary>
    /// Extra area size (if present).
    /// </summary>
    public ulong ExtraAreaSize
    {
        get; set;
    }

    /// <summary>
    /// Data size (if present).
    /// </summary>
    public ulong DataSize
    {
        get; set;
    }

    /// <summary>
    /// Position where the block starts (after CRC).
    /// </summary>
    public long BlockPosition
    {
        get; set;
    }

    /// <summary>
    /// Header CRC32 value.
    /// </summary>
    public uint HeaderCRC
    {
        get; set;
    }

    /// <summary>
    /// True if header CRC is valid.
    /// </summary>
    public bool CRCValid
    {
        get; set;
    }

    /// <summary>
    /// Parsed archive header info (if BlockType is Main).
    /// </summary>
    public RAR5ArchiveInfo? ArchiveInfo
    {
        get; set;
    }

    /// <summary>
    /// Parsed file header info (if BlockType is File).
    /// </summary>
    public RAR5FileInfo? FileInfo
    {
        get; set;
    }

    /// <summary>
    /// Parsed service block info (if BlockType is Service).
    /// </summary>
    public RAR5ServiceBlockInfo? ServiceBlockInfo
    {
        get; set;
    }
}

/// <summary>
/// RAR 5.0 main archive header info.
/// </summary>
public class RAR5ArchiveInfo
{
    /// <summary>
    /// Archive flags.
    /// </summary>
    public ulong ArchiveFlags
    {
        get; set;
    }

    /// <summary>
    /// Volume number (if present).
    /// </summary>
    public ulong? VolumeNumber
    {
        get; set;
    }

    /// <summary>
    /// True if this is a multi-volume archive.
    /// </summary>
    public bool IsVolume => (ArchiveFlags & 0x0001) != 0;

    /// <summary>
    /// True if volume number field is present.
    /// </summary>
    public bool HasVolumeNumber => (ArchiveFlags & 0x0002) != 0;

    /// <summary>
    /// True if this is a solid archive.
    /// </summary>
    public bool IsSolid => (ArchiveFlags & 0x0004) != 0;

    /// <summary>
    /// True if archive has recovery record.
    /// </summary>
    public bool HasRecoveryRecord => (ArchiveFlags & 0x0008) != 0;

    /// <summary>
    /// True if archive headers are locked.
    /// </summary>
    public bool IsLocked => (ArchiveFlags & 0x0010) != 0;
}

/// <summary>
/// RAR 5.0 file header info.
/// </summary>
public class RAR5FileInfo
{
    /// <summary>
    /// File flags.
    /// </summary>
    public ulong FileFlags
    {
        get; set;
    }

    /// <summary>
    /// Unpacked size.
    /// </summary>
    public ulong UnpackedSize
    {
        get; set;
    }

    /// <summary>
    /// File attributes.
    /// </summary>
    public ulong Attributes
    {
        get; set;
    }

    /// <summary>
    /// Modification time (Unix timestamp).
    /// </summary>
    public uint? ModificationTime
    {
        get; set;
    }

    /// <summary>
    /// File CRC32.
    /// </summary>
    public uint? FileCRC
    {
        get; set;
    }

    /// <summary>
    /// Compression info (version, solid, method, dict size).
    /// </summary>
    public ulong CompressionInfo
    {
        get; set;
    }

    /// <summary>
    /// Host OS.
    /// </summary>
    public ulong HostOS
    {
        get; set;
    }

    /// <summary>
    /// File name.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// True if this is a directory.
    /// </summary>
    public bool IsDirectory => (FileFlags & (ulong)RAR5FileFlags.Directory) != 0;

    /// <summary>
    /// True if data is stored uncompressed.
    /// </summary>
    public bool IsStored => CompressionMethod == 0;

    /// <summary>
    /// Compression method (0-5).
    /// </summary>
    public int CompressionMethod => (int)((CompressionInfo >> 7) & 0x07);

    /// <summary>
    /// Dictionary size as power of 2 (bits 10-13 of CompInfo for RAR5).
    /// </summary>
    public int DictSizePower => (int)((CompressionInfo >> 10) & 0x0F);

    /// <summary>
    /// Dictionary size in KB (base 128KB shifted by DictSizePower).
    /// </summary>
    public int DictionarySizeKB => 128 << DictSizePower;

    /// <summary>
    /// True if file continues from previous volume.
    /// </summary>
    public bool IsSplitBefore
    {
        get; set;
    }

    /// <summary>
    /// True if file continues in next volume.
    /// </summary>
    public bool IsSplitAfter
    {
        get; set;
    }
}

/// <summary>
/// Parsed service block info for RAR 5.0.
/// </summary>
internal class RAR5ServiceBlockInfo
{
    /// <summary>
    /// Service data type (e.g., 0x03 for CMT comment).
    /// </summary>
    public ulong ServiceDataType
    {
        get; set;
    }

    /// <summary>
    /// Sub-type name (e.g., "CMT").
    /// </summary>
    public string SubType { get; set; } = string.Empty;

    /// <summary>
    /// Unpacked data size.
    /// </summary>
    public ulong UnpackedSize
    {
        get; set;
    }

    /// <summary>
    /// File flags.
    /// </summary>
    public ulong FileFlags
    {
        get; set;
    }

    /// <summary>
    /// True if data is stored uncompressed.
    /// </summary>
    public bool IsStored
    {
        get; set;
    }

    /// <summary>
    /// Compression version.
    /// </summary>
    public int CompressionVersion
    {
        get; set;
    }

    /// <summary>
    /// Compression method (0-5).
    /// </summary>
    public int CompressionMethod
    {
        get; set;
    }

    /// <summary>
    /// Dictionary size as power of 2.
    /// </summary>
    public int DictSize
    {
        get; set;
    }

    /// <summary>
    /// For CMT blocks: the comment text if extracted.
    /// </summary>
    public string? CommentText
    {
        get; set;
    }
}

/// <summary>
/// RAR 5.0 common header flags (HFL_*) from unrar headers.hpp
/// </summary>
[Flags]
internal enum RAR5HeaderFlags : ulong
{
    /// <summary>
    /// Extra area is present (HFL_EXTRA).
    /// </summary>
    ExtraArea = 0x0001,

    /// <summary>
    /// Data area is present (HFL_DATA).
    /// </summary>
    DataArea = 0x0002,

    /// <summary>
    /// Skip this header if unknown (HFL_SKIPIFUNKNOWN).
    /// </summary>
    SkipIfUnknown = 0x0004,

    /// <summary>
    /// Data continued from previous volume (HFL_SPLITBEFORE).
    /// </summary>
    SplitBefore = 0x0008,

    /// <summary>
    /// Data continues in next volume (HFL_SPLITAFTER).
    /// </summary>
    SplitAfter = 0x0010,

    /// <summary>
    /// Child of preceding file header (HFL_CHILD).
    /// </summary>
    Child = 0x0020,

    /// <summary>
    /// Preserve host modification (HFL_INHERITED).
    /// </summary>
    Inherited = 0x0040
}

/// <summary>
/// RAR 5.0 file/service block flags.
/// </summary>
[Flags]
internal enum RAR5FileFlags : ulong
{
    /// <summary>
    /// Entry is a directory.
    /// </summary>
    Directory = 0x0001,

    /// <summary>
    /// Time field is present.
    /// </summary>
    TimePresent = 0x0002,

    /// <summary>
    /// CRC32 field is present.
    /// </summary>
    CRC32Present = 0x0004,

    /// <summary>
    /// Unpacked size is unknown.
    /// </summary>
    UnknownSize = 0x0008
}

/// <summary>
/// RAR 5.0 service data types.
/// </summary>
internal enum RAR5ServiceType : ulong
{
    /// <summary>
    /// Archive comment (CMT).
    /// </summary>
    Comment = 0x03
}

/// <summary>
/// Reads RAR 5.0 headers from a stream.
/// </summary>
/// <remarks>
/// Creates a new RAR 5.0 header reader.
/// </remarks>
internal class RAR5HeaderReader(Stream stream)
{
    /// <summary>
    /// RAR 5.0 marker bytes.
    /// </summary>
    public static readonly byte[] RAR5Marker = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];

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
            if (marker[i] != RAR5Marker[i])
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
            result |= (ulong)(b & 0x7F) << shift;

            if ((b & 0x80) == 0)
            {
                break;
            }

            shift += 7;
            if (shift > 63)
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
        long crcDataSize = (headerContentStart + (long)headerSize) - headerSizePosition;
        if (crcDataSize is <= 0 or > int.MaxValue)
        {
            return result;
        }

        _stream.Position = headerSizePosition;
        byte[] headerData = _reader.ReadBytes((int)crcDataSize);
        uint calculatedCRC = Force.Crc32.Crc32Algorithm.Compute(headerData);
        result.CRCValid = (crc == calculatedCRC);
        _stream.Position = currentPos;

        return result;
    }

    /// <summary>
    /// The raw file/service header fields shared by RAR5 FILE (0x02) and SERVICE (0x03)
    /// blocks, read in their on-disk order. Optional fields (governed by
    /// <see cref="RAR5FileFlags"/>) are read identically for both block kinds.
    /// </summary>
    private readonly record struct Rar5FileFields(
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
    private Rar5FileFields ReadRar5FileFields(long headerEnd)
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

        return new Rar5FileFields(fileFlags, unpackedSize, attributes, mtime, fileCRC, compressionInfo, hostOS, name);
    }

    private RAR5ServiceBlockInfo? ParseServiceBlock(long headerEnd)
    {
        Rar5FileFields fields = ReadRar5FileFields(headerEnd);

        var info = new RAR5ServiceBlockInfo
        {
            FileFlags = fields.FileFlags,
            UnpackedSize = fields.UnpackedSize,
            CompressionVersion = (int)(fields.CompressionInfo & 0x3F),
            CompressionMethod = (int)((fields.CompressionInfo >> 7) & 0x07),
            DictSize = (int)((fields.CompressionInfo >> 10) & 0x0F),
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
        Rar5FileFields fields = ReadRar5FileFields(headerEnd);

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
