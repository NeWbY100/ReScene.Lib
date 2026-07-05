namespace ReScene.RAR;

/// <summary>
/// RAR 4.x file header flags (LHD_*) from unrar headers.hpp
/// </summary>
[Flags]
public enum RARFileFlags : ushort
{
    /// <summary>
    /// No flags set.
    /// </summary>
    None = 0x0000,

    /// <summary>
    /// File continued from previous volume (LHD_SPLIT_BEFORE).
    /// </summary>
    SplitBefore = 0x0001,

    /// <summary>
    /// File continues in next volume (LHD_SPLIT_AFTER).
    /// </summary>
    SplitAfter = 0x0002,

    /// <summary>
    /// File is encrypted (LHD_PASSWORD).
    /// </summary>
    Password = 0x0004,

    /// <summary>
    /// File comment present (LHD_COMMENT).
    /// </summary>
    Comment = 0x0008,

    /// <summary>
    /// Solid flag for files (LHD_SOLID).
    /// </summary>
    Solid = 0x0010,

    // Dictionary size encoded in bits 5-7 (mask 0x00E0)

    /// <summary>
    /// 64 KB dictionary (LHD_WINDOW64).
    /// </summary>
    DictSize64 = 0x0000,

    /// <summary>
    /// 128 KB dictionary (LHD_WINDOW128).
    /// </summary>
    DictSize128 = 0x0020,

    /// <summary>
    /// 256 KB dictionary (LHD_WINDOW256).
    /// </summary>
    DictSize256 = 0x0040,

    /// <summary>
    /// 512 KB dictionary (LHD_WINDOW512).
    /// </summary>
    DictSize512 = 0x0060,

    /// <summary>
    /// 1 MB dictionary (LHD_WINDOW1024).
    /// </summary>
    DictSize1024 = 0x0080,

    /// <summary>
    /// 2 MB dictionary (LHD_WINDOW2048).
    /// </summary>
    DictSize2048 = 0x00A0,

    /// <summary>
    /// 4 MB dictionary (LHD_WINDOW4096).
    /// </summary>
    DictSize4096 = 0x00C0,

    /// <summary>
    /// Entry is a directory (LHD_DIRECTORY).
    /// </summary>
    Directory = 0x00E0,

    /// <summary>
    /// 64-bit file sizes for files larger than 2 GB, RAR 2.6+ (LHD_LARGE).
    /// </summary>
    Large = 0x0100,

    /// <summary>
    /// Unicode filename, RAR 3.0+ (LHD_UNICODE).
    /// </summary>
    Unicode = 0x0200,

    /// <summary>
    /// Salt for encryption (LHD_SALT).
    /// </summary>
    Salt = 0x0400,

    /// <summary>
    /// File version present (LHD_VERSION).
    /// </summary>
    Version = 0x0800,

    /// <summary>
    /// Extended time fields, RAR 2.0+ (LHD_EXTTIME).
    /// </summary>
    ExtTime = 0x1000,

    // Generic block flags

    /// <summary>
    /// Skip if block type is unknown (SKIP_IF_UNKNOWN).
    /// </summary>
    SkipIfUnknown = 0x4000,

    /// <summary>
    /// ADD_SIZE field present (LONG_BLOCK).
    /// </summary>
    LongBlock = 0x8000
}
