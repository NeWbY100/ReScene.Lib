namespace RARLib;

/// <summary>
/// Represents parsed data from a RAR 4.x file header block.
/// </summary>
public class RARFileHeader
{
    /// <summary>Position of the header block in the stream.</summary>
    public long BlockPosition { get; set; }

    /// <summary>Header CRC value.</summary>
    public ushort HeaderCrc { get; set; }

    /// <summary>Header size in bytes.</summary>
    public ushort HeaderSize { get; set; }

    /// <summary>Raw flags from the header.</summary>
    public RARFileFlags Flags { get; set; }

    /// <summary>Packed (compressed) size in bytes.</summary>
    public ulong PackedSize { get; set; }

    /// <summary>Unpacked (original) size in bytes.</summary>
    public ulong UnpackedSize { get; set; }

    /// <summary>Host operating system.</summary>
    public byte HostOS { get; set; }

    /// <summary>File CRC32.</summary>
    public uint FileCrc { get; set; }

    /// <summary>RAR version needed to unpack (e.g., 29 = RAR 2.9).</summary>
    public byte UnpackVersion { get; set; }

    /// <summary>Compression method (0 = store, 1-5 = compression levels).</summary>
    public byte CompressionMethod { get; set; }

    /// <summary>Dictionary size in KB (64, 128, 256, 512, 1024, 2048, 4096).</summary>
    public int DictionarySizeKB { get; set; }

    /// <summary>File attributes.</summary>
    public uint FileAttributes { get; set; }

    /// <summary>File name (decoded).</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>True if this entry represents a directory.</summary>
    public bool IsDirectory { get; set; }

    /// <summary>File modification time.</summary>
    public DateTime? ModifiedTime { get; set; }

    /// <summary>File creation time (from extended time fields).</summary>
    public DateTime? CreationTime { get; set; }

    /// <summary>File access time (from extended time fields).</summary>
    public DateTime? AccessTime { get; set; }

    /// <summary>Raw DOS file time value (4 bytes at offset 20).</summary>
    public uint FileTimeDOS { get; set; }

    /// <summary>Modification time precision level (maps to -tsm0 through -tsm4).</summary>
    public TimestampPrecision MtimePrecision { get; set; }

    /// <summary>Creation time precision level (maps to -tsc0 through -tsc4).</summary>
    public TimestampPrecision CtimePrecision { get; set; }

    /// <summary>Access time precision level (maps to -tsa0 through -tsa4).</summary>
    public TimestampPrecision AtimePrecision { get; set; }

    /// <summary>True if header CRC validation passed.</summary>
    public bool CrcValid { get; set; }

    // Convenience properties for common flag checks

    /// <summary>True if file is split from previous volume.</summary>
    public bool IsSplitBefore => (Flags & RARFileFlags.SplitBefore) != 0;

    /// <summary>True if file continues in next volume.</summary>
    public bool IsSplitAfter => (Flags & RARFileFlags.SplitAfter) != 0;

    /// <summary>True if file is encrypted.</summary>
    public bool IsEncrypted => (Flags & RARFileFlags.Password) != 0;

    /// <summary>True if file has Unicode name.</summary>
    public bool HasUnicodeName => (Flags & RARFileFlags.Unicode) != 0;

    /// <summary>True if file has extended time fields.</summary>
    public bool HasExtendedTime => (Flags & RARFileFlags.ExtTime) != 0;

    /// <summary>True if file uses 64-bit sizes.</summary>
    public bool HasLargeSize => (Flags & RARFileFlags.Large) != 0;

    /// <summary>Upper 32 bits of packed size (when LARGE flag set).</summary>
    public uint HighPackSize { get; set; }

    /// <summary>Upper 32 bits of unpacked size (when LARGE flag set).</summary>
    public uint HighUnpSize { get; set; }
}

/// <summary>
/// Represents parsed data from a RAR 4.x main archive header block.
/// </summary>
public class RARArchiveHeader
{
    /// <summary>Position of the header block in the stream.</summary>
    public long BlockPosition { get; set; }

    /// <summary>Header CRC value.</summary>
    public ushort HeaderCrc { get; set; }

    /// <summary>Header size in bytes.</summary>
    public ushort HeaderSize { get; set; }

    /// <summary>Raw flags from the header.</summary>
    public RARArchiveFlags Flags { get; set; }

    /// <summary>True if header CRC validation passed.</summary>
    public bool CrcValid { get; set; }

    // Convenience properties for common flag checks

    /// <summary>True if this is a multi-volume archive.</summary>
    public bool IsVolume => (Flags & RARArchiveFlags.Volume) != 0;

    /// <summary>True if this is a solid archive.</summary>
    public bool IsSolid => (Flags & RARArchiveFlags.Solid) != 0;

    /// <summary>True if archive has recovery record.</summary>
    public bool HasRecoveryRecord => (Flags & RARArchiveFlags.Protected) != 0;

    /// <summary>True if archive uses new volume naming (RAR 2.9+).</summary>
    public bool HasNewVolumeNaming => (Flags & RARArchiveFlags.NewNumbering) != 0;

    /// <summary>True if this is the first volume (RAR 3.0+).</summary>
    public bool IsFirstVolume => (Flags & RARArchiveFlags.FirstVolume) != 0;

    /// <summary>True if archive headers are encrypted.</summary>
    public bool HasEncryptedHeaders => (Flags & RARArchiveFlags.Password) != 0;

    /// <summary>True if archive is locked.</summary>
    public bool IsLocked => (Flags & RARArchiveFlags.Lock) != 0;
}
