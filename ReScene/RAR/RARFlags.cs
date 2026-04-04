namespace ReScene.RAR;

/// <summary>
/// RAR 4.x main archive header flags (MHD_*) from unrar headers.hpp
/// </summary>
[Flags]
public enum RARArchiveFlags : ushort
{
    /// <summary>No flags set.</summary>
    None = 0x0000,
    /// <summary>Multi-volume archive (MHD_VOLUME).</summary>
    Volume = 0x0001,
    /// <summary>Archive comment present (MHD_COMMENT).</summary>
    Comment = 0x0002,
    /// <summary>Archive is locked (MHD_LOCK).</summary>
    Lock = 0x0004,
    /// <summary>Solid archive (MHD_SOLID).</summary>
    Solid = 0x0008,
    /// <summary>New volume naming scheme, RAR 2.9+ (MHD_NEWNUMBERING).</summary>
    NewNumbering = 0x0010,
    /// <summary>Authenticity info present (MHD_AV).</summary>
    AuthInfo = 0x0020,
    /// <summary>Has recovery record (MHD_PROTECT).</summary>
    Protected = 0x0040,
    /// <summary>Encrypted headers (MHD_PASSWORD).</summary>
    Password = 0x0080,
    /// <summary>First volume, RAR 3.0+ (MHD_FIRSTVOLUME).</summary>
    FirstVolume = 0x0100
}

/// <summary>
/// RAR 4.x file header flags (LHD_*) from unrar headers.hpp
/// </summary>
[Flags]
public enum RARFileFlags : ushort
{
    /// <summary>No flags set.</summary>
    None = 0x0000,
    /// <summary>File continued from previous volume (LHD_SPLIT_BEFORE).</summary>
    SplitBefore = 0x0001,
    /// <summary>File continues in next volume (LHD_SPLIT_AFTER).</summary>
    SplitAfter = 0x0002,
    /// <summary>File is encrypted (LHD_PASSWORD).</summary>
    Password = 0x0004,
    /// <summary>File comment present (LHD_COMMENT).</summary>
    Comment = 0x0008,
    /// <summary>Solid flag for files (LHD_SOLID).</summary>
    Solid = 0x0010,

    // Dictionary size encoded in bits 5-7 (mask 0x00E0)
    /// <summary>64 KB dictionary (LHD_WINDOW64).</summary>
    DictSize64 = 0x0000,
    /// <summary>128 KB dictionary (LHD_WINDOW128).</summary>
    DictSize128 = 0x0020,
    /// <summary>256 KB dictionary (LHD_WINDOW256).</summary>
    DictSize256 = 0x0040,
    /// <summary>512 KB dictionary (LHD_WINDOW512).</summary>
    DictSize512 = 0x0060,
    /// <summary>1 MB dictionary (LHD_WINDOW1024).</summary>
    DictSize1024 = 0x0080,
    /// <summary>2 MB dictionary (LHD_WINDOW2048).</summary>
    DictSize2048 = 0x00A0,
    /// <summary>4 MB dictionary (LHD_WINDOW4096).</summary>
    DictSize4096 = 0x00C0,
    /// <summary>Entry is a directory (LHD_DIRECTORY).</summary>
    Directory = 0x00E0,

    /// <summary>64-bit file sizes for files larger than 2 GB, RAR 2.6+ (LHD_LARGE).</summary>
    Large = 0x0100,
    /// <summary>Unicode filename, RAR 3.0+ (LHD_UNICODE).</summary>
    Unicode = 0x0200,
    /// <summary>Salt for encryption (LHD_SALT).</summary>
    Salt = 0x0400,
    /// <summary>File version present (LHD_VERSION).</summary>
    Version = 0x0800,
    /// <summary>Extended time fields, RAR 2.0+ (LHD_EXTTIME).</summary>
    ExtTime = 0x1000,

    // Generic block flags
    /// <summary>Skip if block type is unknown (SKIP_IF_UNKNOWN).</summary>
    SkipIfUnknown = 0x4000,
    /// <summary>ADD_SIZE field present (LONG_BLOCK).</summary>
    LongBlock = 0x8000
}

/// <summary>
/// RAR 4.x end archive flags (EARC_*) from unrar headers.hpp
/// </summary>
[Flags]
public enum RAREndArchiveFlags : ushort
{
    /// <summary>No flags set.</summary>
    None = 0x0000,
    /// <summary>Not the last volume (EARC_NEXT_VOLUME).</summary>
    NextVolume = 0x0001,
    /// <summary>Data CRC present (EARC_DATACRC).</summary>
    DataCrc = 0x0002,
    /// <summary>Reserved space present (EARC_REVSPACE).</summary>
    RevSpace = 0x0004,
    /// <summary>Volume number present (EARC_VOLNUMBER).</summary>
    VolNumber = 0x0008
}

/// <summary>
/// Mask constants for extracting flag values
/// </summary>
public static class RARFlagMasks
{
    /// <summary>
    /// Mask for dictionary size bits (bits 5-7)
    /// </summary>
    public const ushort DictionarySizeMask = 0x00E0;

    /// <summary>
    /// Shift amount for dictionary size bits
    /// </summary>
    public const int DictionarySizeShift = 5;

    /// <summary>
    /// Salt length in bytes
    /// </summary>
    public const int SaltLength = 8;
}

/// <summary>
/// Timestamp precision levels for RAR -tsm/-tsc/-tsa options.
/// Maps directly to the RAR command-line option suffixes (0-4).
/// </summary>
public enum TimestampPrecision : byte
{
    /// <summary>
    /// Time not saved (ts*0, -ts*-)
    /// </summary>
    NotSaved = 0,

    /// <summary>
    /// 1 second precision (ts*1, DOS time only)
    /// </summary>
    OneSecond = 1,

    /// <summary>
    /// ~0.0065536 second precision (ts*2, 1 extra byte)
    /// </summary>
    HighPrecision1 = 2,

    /// <summary>
    /// ~0.0000256 second precision (ts*3, 2 extra bytes)
    /// </summary>
    HighPrecision2 = 3,

    /// <summary>
    /// NTFS 100-nanosecond precision (ts*4, 3 extra bytes)
    /// </summary>
    NtfsPrecision = 4
}
