using System.Text;

namespace RARLib;

/// <summary>
/// Result of reading a RAR 5.0 block header.
/// </summary>
public class RAR5BlockReadResult
{
    /// <summary>Block type (RAR 5.0).</summary>
    public RAR5BlockType BlockType { get; set; }

    /// <summary>Raw header flags value.</summary>
    public ulong Flags { get; set; }

    /// <summary>Header size in bytes (excluding CRC).</summary>
    public ulong HeaderSize { get; set; }

    /// <summary>Extra area size (if present).</summary>
    public ulong ExtraAreaSize { get; set; }

    /// <summary>Data size (if present).</summary>
    public ulong DataSize { get; set; }

    /// <summary>Position where the block starts (after CRC).</summary>
    public long BlockPosition { get; set; }

    /// <summary>Header CRC32 value.</summary>
    public uint HeaderCrc { get; set; }

    /// <summary>True if header CRC is valid.</summary>
    public bool CrcValid { get; set; }

    /// <summary>Parsed archive header info (if BlockType is Main).</summary>
    public RAR5ArchiveInfo? ArchiveInfo { get; set; }

    /// <summary>Parsed file header info (if BlockType is File).</summary>
    public RAR5FileInfo? FileInfo { get; set; }

    /// <summary>Parsed service block info (if BlockType is Service).</summary>
    public RAR5ServiceBlockInfo? ServiceBlockInfo { get; set; }
}

/// <summary>
/// RAR 5.0 main archive header info.
/// </summary>
public class RAR5ArchiveInfo
{
    /// <summary>Archive flags.</summary>
    public ulong ArchiveFlags { get; set; }

    /// <summary>Volume number (if present).</summary>
    public ulong? VolumeNumber { get; set; }

    /// <summary>True if this is a multi-volume archive.</summary>
    public bool IsVolume => (ArchiveFlags & 0x0001) != 0;

    /// <summary>True if volume number field is present.</summary>
    public bool HasVolumeNumber => (ArchiveFlags & 0x0002) != 0;

    /// <summary>True if this is a solid archive.</summary>
    public bool IsSolid => (ArchiveFlags & 0x0004) != 0;

    /// <summary>True if archive has recovery record.</summary>
    public bool HasRecoveryRecord => (ArchiveFlags & 0x0008) != 0;

    /// <summary>True if archive headers are locked.</summary>
    public bool IsLocked => (ArchiveFlags & 0x0010) != 0;
}

/// <summary>
/// RAR 5.0 file header info.
/// </summary>
public class RAR5FileInfo
{
    /// <summary>File flags.</summary>
    public ulong FileFlags { get; set; }

    /// <summary>Unpacked size.</summary>
    public ulong UnpackedSize { get; set; }

    /// <summary>File attributes.</summary>
    public ulong Attributes { get; set; }

    /// <summary>Modification time (Unix timestamp).</summary>
    public uint? ModificationTime { get; set; }

    /// <summary>File CRC32.</summary>
    public uint? FileCrc { get; set; }

    /// <summary>Compression info (version, solid, method, dict size).</summary>
    public ulong CompressionInfo { get; set; }

    /// <summary>Host OS.</summary>
    public ulong HostOS { get; set; }

    /// <summary>File name.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>True if this is a directory.</summary>
    public bool IsDirectory => (FileFlags & (ulong)RAR5FileFlags.Directory) != 0;

    /// <summary>True if data is stored uncompressed.</summary>
    public bool IsStored => CompressionMethod == 0;

    /// <summary>Compression method (0-5).</summary>
    public int CompressionMethod => (int)((CompressionInfo >> 7) & 0x07);

    /// <summary>Dictionary size as power of 2 (bits 10-13 of CompInfo for RAR5).</summary>
    public int DictSizePower => (int)((CompressionInfo >> 10) & 0x0F);

    /// <summary>Dictionary size in KB (base 128KB shifted by DictSizePower).</summary>
    public int DictionarySizeKB => 128 << DictSizePower;

    /// <summary>True if file continues from previous volume.</summary>
    public bool IsSplitBefore { get; set; }

    /// <summary>True if file continues in next volume.</summary>
    public bool IsSplitAfter { get; set; }
}

/// <summary>
/// Parsed service block info for RAR 5.0.
/// </summary>
public class RAR5ServiceBlockInfo
{
    /// <summary>Service data type (e.g., 0x03 for CMT comment).</summary>
    public ulong ServiceDataType { get; set; }

    /// <summary>Sub-type name (e.g., "CMT").</summary>
    public string SubType { get; set; } = string.Empty;

    /// <summary>Unpacked data size.</summary>
    public ulong UnpackedSize { get; set; }

    /// <summary>File flags.</summary>
    public ulong FileFlags { get; set; }

    /// <summary>True if data is stored uncompressed.</summary>
    public bool IsStored { get; set; }

    /// <summary>Compression version.</summary>
    public int CompressionVersion { get; set; }

    /// <summary>Compression method (0-5).</summary>
    public int CompressionMethod { get; set; }

    /// <summary>Dictionary size as power of 2.</summary>
    public int DictSize { get; set; }

    /// <summary>For CMT blocks: the comment text if extracted.</summary>
    public string? CommentText { get; set; }
}

/// <summary>
/// RAR 5.0 common header flags (HFL_*) from unrar headers.hpp
/// </summary>
[Flags]
public enum RAR5HeaderFlags : ulong
{
    ExtraArea = 0x0001,      // HFL_EXTRA - Extra area present
    DataArea = 0x0002,       // HFL_DATA - Data area present
    SkipIfUnknown = 0x0004,  // HFL_SKIPIFUNKNOWN - Skip this header if unknown
    SplitBefore = 0x0008,    // HFL_SPLITBEFORE - Data continued from previous volume
    SplitAfter = 0x0010,     // HFL_SPLITAFTER - Data continues in next volume
    Child = 0x0020,          // HFL_CHILD - Child of preceding file header
    Inherited = 0x0040       // HFL_INHERITED - Preserve host modification
}

/// <summary>
/// RAR 5.0 file/service block flags.
/// </summary>
[Flags]
public enum RAR5FileFlags : ulong
{
    Directory = 0x0001,     // Directory entry
    TimePresent = 0x0002,   // Time field present
    Crc32Present = 0x0004,  // CRC32 field present
    UnknownSize = 0x0008    // Unpacked size unknown
}

/// <summary>
/// RAR 5.0 service data types.
/// </summary>
public enum RAR5ServiceType : ulong
{
    Comment = 0x03          // CMT - Archive comment
}

/// <summary>
/// Reads RAR 5.0 headers from a stream.
/// </summary>
/// <remarks>
/// Creates a new RAR 5.0 header reader.
/// </remarks>
public class RAR5HeaderReader(Stream stream)
{
    /// <summary>RAR 5.0 marker bytes.</summary>
    public static readonly byte[] RAR5Marker = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];

    private readonly Stream _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    private readonly BinaryReader _reader = new(stream, Encoding.UTF8, leaveOpen: true);

    /// <summary>
    /// Checks if the stream starts with RAR 5.0 marker.
    /// </summary>
    public static bool IsRAR5(Stream stream)
    {
        if (stream.Length < 8)
            return false;

        long pos = stream.Position;
        byte[] marker = new byte[8];
        stream.ReadExactly(marker, 0, 8);
        stream.Position = pos;

        for (int i = 0; i < 8; i++)
        {
            if (marker[i] != RAR5Marker[i])
                return false;
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
    public byte? PeekBlockType()
    {
        if (_stream.Position + 6 > _stream.Length)
            return null;

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
    public ulong ReadVInt()
    {
        ulong result = 0;
        int shift = 0;

        while (true)
        {
            byte b = _reader.ReadByte();
            result |= (ulong)(b & 0x7F) << shift;

            if ((b & 0x80) == 0)
                break;

            shift += 7;
            if (shift > 63)
                throw new InvalidDataException("VInt too large");
        }

        return result;
    }

    /// <summary>
    /// Reads a RAR 5.0 block header.
    /// </summary>
    public RAR5BlockReadResult? ReadBlock()
    {
        if (_stream.Position + 4 > _stream.Length)
            return null;

        _ = _stream.Position;
        uint crc = _reader.ReadUInt32();

        long headerSizePosition = _stream.Position;

        // Read header size - this is size starting from header type field
        ulong headerSize = ReadVInt();

        // Header content starts here (after header size vint)
        long headerContentStart = _stream.Position;

        if (headerContentStart + (long)headerSize > _stream.Length)
            return null;

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
            HeaderCrc = crc
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
        _stream.Position = headerSizePosition;
        byte[] headerData = _reader.ReadBytes((int)crcDataSize);
        uint calculatedCrc = Force.Crc32.Crc32Algorithm.Compute(headerData);
        result.CrcValid = (crc == calculatedCrc);
        _stream.Position = currentPos;

        return result;
    }

    private RAR5ServiceBlockInfo? ParseServiceBlock(long headerEnd)
    {
        var info = new RAR5ServiceBlockInfo
        {
            // Read file flags
            FileFlags = ReadVInt()
        };

        // Read unpacked size (unless UNKNOWN_SIZE flag is set)
        if ((info.FileFlags & (ulong)RAR5FileFlags.UnknownSize) == 0)
        {
            info.UnpackedSize = ReadVInt();
        }

        // Skip file attributes
        _ = ReadVInt();

        // Skip mtime if present
        if ((info.FileFlags & (ulong)RAR5FileFlags.TimePresent) != 0)
            _reader.ReadUInt32();

        // Skip CRC if present
        if ((info.FileFlags & (ulong)RAR5FileFlags.Crc32Present) != 0)
            _reader.ReadUInt32();

        // Read compression info
        ulong compressionInfo = ReadVInt();
        info.CompressionVersion = (int)(compressionInfo & 0x3F);
        info.CompressionMethod = (int)((compressionInfo >> 7) & 0x07);
        info.DictSize = (int)((compressionInfo >> 10) & 0x0F);
        info.IsStored = info.CompressionMethod == 0;

        // Skip host OS
        _ = ReadVInt();

        // Read name length and name
        ulong nameLen = ReadVInt();
        if (nameLen > 0 && _stream.Position + (long)nameLen <= headerEnd)
        {
            byte[] nameBytes = _reader.ReadBytes((int)nameLen);
            info.SubType = Encoding.UTF8.GetString(nameBytes);
        }

        // Check for CMT type
        if (info.SubType == "CMT" || info.SubType.StartsWith("CMT"))
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
        var info = new RAR5FileInfo
        {
            IsSplitBefore = isSplitBefore,
            IsSplitAfter = isSplitAfter,
            // Read file flags
            FileFlags = ReadVInt()
        };

        // Read unpacked size (unless UNKNOWN_SIZE flag is set)
        if ((info.FileFlags & (ulong)RAR5FileFlags.UnknownSize) == 0)
        {
            info.UnpackedSize = ReadVInt();
        }

        // Read file attributes
        info.Attributes = ReadVInt();

        // Read mtime if present
        if ((info.FileFlags & (ulong)RAR5FileFlags.TimePresent) != 0)
        {
            info.ModificationTime = _reader.ReadUInt32();
        }

        // Read CRC if present
        if ((info.FileFlags & (ulong)RAR5FileFlags.Crc32Present) != 0)
        {
            info.FileCrc = _reader.ReadUInt32();
        }

        // Read compression info
        info.CompressionInfo = ReadVInt();

        // Read host OS
        info.HostOS = ReadVInt();

        // Read name length and name
        ulong nameLen = ReadVInt();
        if (nameLen > 0 && _stream.Position + (long)nameLen <= headerEnd)
        {
            byte[] nameBytes = _reader.ReadBytes((int)nameLen);
            info.FileName = Encoding.UTF8.GetString(nameBytes);
        }

        return info;
    }

    /// <summary>
    /// Skips to the end of the current block.
    /// </summary>
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
            target = _stream.Length;

        _stream.Position = target;
    }

    /// <summary>
    /// Reads the data portion of a service block.
    /// </summary>
    public byte[]? ReadServiceBlockData(RAR5BlockReadResult block)
    {
        if (block.BlockType != RAR5BlockType.Service || block.ServiceBlockInfo == null)
            return null;

        if ((block.Flags & (ulong)RAR5HeaderFlags.DataArea) == 0 || block.DataSize == 0)
            return null;

        long dataStart = block.BlockPosition + (long)block.HeaderSize;
        if (dataStart + (long)block.DataSize > _stream.Length)
            return null;

        _stream.Position = dataStart;
        return _reader.ReadBytes((int)block.DataSize);
    }
}
