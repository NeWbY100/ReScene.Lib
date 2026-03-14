namespace RARLib;

/// <summary>
/// RAR 4.x main archive header flags (MHD_*) from unrar headers.hpp
/// </summary>
[Flags]
public enum RARArchiveFlags : ushort
{
    None = 0x0000,
    Volume = 0x0001,            // MHD_VOLUME - Multi-volume archive
    Comment = 0x0002,           // MHD_COMMENT - Archive comment present
    Lock = 0x0004,              // MHD_LOCK - Archive locked
    Solid = 0x0008,             // MHD_SOLID - Solid archive
    NewNumbering = 0x0010,      // MHD_NEWNUMBERING - New volume naming (RAR 2.9+)
    AuthInfo = 0x0020,          // MHD_AV - Authenticity info present
    Protected = 0x0040,         // MHD_PROTECT - Has recovery record
    Password = 0x0080,          // MHD_PASSWORD - Encrypted headers
    FirstVolume = 0x0100,       // MHD_FIRSTVOLUME - First volume (RAR 3.0+)
}

/// <summary>
/// RAR 4.x file header flags (LHD_*) from unrar headers.hpp
/// </summary>
[Flags]
public enum RARFileFlags : ushort
{
    None = 0x0000,
    SplitBefore = 0x0001,       // LHD_SPLIT_BEFORE - File continued from previous volume
    SplitAfter = 0x0002,        // LHD_SPLIT_AFTER - File continues in next volume
    Password = 0x0004,          // LHD_PASSWORD - File encrypted
    Comment = 0x0008,           // LHD_COMMENT - File comment present
    Solid = 0x0010,             // LHD_SOLID - Solid flag (for files)

    // Dictionary size encoded in bits 5-7 (mask 0x00E0)
    DictSize64 = 0x0000,        // LHD_WINDOW64 - 64KB dictionary
    DictSize128 = 0x0020,       // LHD_WINDOW128 - 128KB dictionary
    DictSize256 = 0x0040,       // LHD_WINDOW256 - 256KB dictionary
    DictSize512 = 0x0060,       // LHD_WINDOW512 - 512KB dictionary
    DictSize1024 = 0x0080,      // LHD_WINDOW1024 - 1MB dictionary
    DictSize2048 = 0x00A0,      // LHD_WINDOW2048 - 2MB dictionary
    DictSize4096 = 0x00C0,      // LHD_WINDOW4096 - 4MB dictionary
    Directory = 0x00E0,         // LHD_DIRECTORY - Entry is a directory

    Large = 0x0100,             // LHD_LARGE - 64-bit file sizes (files >2GB, RAR 2.6+)
    Unicode = 0x0200,           // LHD_UNICODE - Unicode filename (RAR 3.0+)
    Salt = 0x0400,              // LHD_SALT - Salt for encryption
    Version = 0x0800,           // LHD_VERSION - File version present
    ExtTime = 0x1000,           // LHD_EXTTIME - Extended time fields (RAR 2.0+)

    // Generic block flags
    SkipIfUnknown = 0x4000,     // SKIP_IF_UNKNOWN
    LongBlock = 0x8000,         // LONG_BLOCK - ADD_SIZE field present
}

/// <summary>
/// RAR 4.x end archive flags (EARC_*) from unrar headers.hpp
/// </summary>
[Flags]
public enum RAREndArchiveFlags : ushort
{
    None = 0x0000,
    NextVolume = 0x0001,        // EARC_NEXT_VOLUME - Not last volume
    DataCrc = 0x0002,           // EARC_DATACRC - Data CRC present
    RevSpace = 0x0004,          // EARC_REVSPACE - Reserved space present
    VolNumber = 0x0008,         // EARC_VOLNUMBER - Volume number present
}

/// <summary>
/// Mask constants for extracting flag values
/// </summary>
public static class RARFlagMasks
{
    /// <summary>Mask for dictionary size bits (bits 5-7)</summary>
    public const ushort DictionarySizeMask = 0x00E0;

    /// <summary>Shift amount for dictionary size bits</summary>
    public const int DictionarySizeShift = 5;

    /// <summary>Salt length in bytes</summary>
    public const int SaltLength = 8;
}

/// <summary>
/// Timestamp precision levels for RAR -tsm/-tsc/-tsa options.
/// Maps directly to the RAR command-line option suffixes (0-4).
/// </summary>
public enum TimestampPrecision : byte
{
    /// <summary>Time not saved (ts*0, -ts*-)</summary>
    NotSaved = 0,

    /// <summary>1 second precision (ts*1, DOS time only)</summary>
    OneSecond = 1,

    /// <summary>~0.0065536 second precision (ts*2, 1 extra byte)</summary>
    HighPrecision1 = 2,

    /// <summary>~0.0000256 second precision (ts*3, 2 extra bytes)</summary>
    HighPrecision2 = 3,

    /// <summary>NTFS 100-nanosecond precision (ts*4, 3 extra bytes)</summary>
    NtfsPrecision = 4
}
