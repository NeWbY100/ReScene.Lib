namespace ReScene.SRR;

/// <summary>
/// Indicates the type of custom RAR packer anomaly detected in file headers.
/// </summary>
public enum CustomPackerType
{
    /// <summary>
    /// No custom packer detected.
    /// </summary>
    None,

    /// <summary>
    /// UNP_SIZE = 0xFFFFFFFFFFFFFFFF (both low and high 32-bit fields are all ones).
    /// Known groups: RELOADED, HI2U, 0x0007, 0x0815.
    /// </summary>
    AllOnesWithLargeFlag,

    /// <summary>
    /// UNP_SIZE = 0xFFFFFFFF without LARGE flag (raw 32-bit field maxed out).
    /// Known group: QCF.
    /// </summary>
    MaxUint32WithoutLargeFlag
}

/// <summary>
/// SRR-specific block types (0x69-0x71).
/// </summary>
public enum SRRBlockType : byte
{
    /// <summary>SRR file header block.</summary>
    Header = 0x69,
    /// <summary>Stored file block.</summary>
    StoredFile = 0x6A,
    /// <summary>OSO hash block.</summary>
    OsoHash = 0x6B,
    /// <summary>RAR padding block.</summary>
    RarPadding = 0x6C,
    /// <summary>RAR file reference block, followed by embedded RAR headers.</summary>
    RarFile = 0x71
}

/// <summary>
/// SRR header block flags.
/// </summary>
[Flags]
public enum SRRHeaderFlags : ushort
{
    /// <summary>No flags set.</summary>
    None = 0x0000,
    /// <summary>Application name is present in the header.</summary>
    AppNamePresent = 0x0001
}

/// <summary>
/// Generic SRR block flags.
/// </summary>
[Flags]
public enum SRRBlockFlags : ushort
{
    /// <summary>No flags set.</summary>
    None = 0x0000,
    /// <summary>Skip this block if the type is unknown.</summary>
    SkipIfUnknown = 0x4000,
    /// <summary>Block has an additional size field (long block).</summary>
    LongBlock = 0x8000
}

/// <summary>
/// Base class for SRR blocks.
/// </summary>
public class SRRBlock
{
    /// <summary>
    /// Gets or sets the block CRC value.
    /// </summary>
    public ushort Crc { get; set; }

    /// <summary>
    /// Gets or sets the block type.
    /// </summary>
    public SRRBlockType BlockType { get; set; }

    /// <summary>
    /// Gets or sets the block flags.
    /// </summary>
    public ushort Flags { get; set; }

    /// <summary>
    /// Gets or sets the header size in bytes.
    /// </summary>
    public ushort HeaderSize { get; set; }

    /// <summary>
    /// Gets or sets the block position in the stream.
    /// </summary>
    public long BlockPosition { get; set; }

    /// <summary>
    /// Gets or sets the additional data size following the header.
    /// </summary>
    public uint AddSize { get; set; }
}

/// <summary>
/// SRR RAR file reference block (0x71).
/// Contains the RAR filename and is followed by embedded RAR headers.
/// </summary>
public class SrrRarFileBlock : SRRBlock
{
    /// <summary>
    /// Gets or sets the RAR filename referenced by this block.
    /// </summary>
    public string FileName { get; set; } = string.Empty;
}

/// <summary>
/// SRR stored file block (0x6A).
/// Contains a file embedded within the SRR.
/// </summary>
public class SrrStoredFileBlock : SRRBlock
{
    /// <summary>
    /// Gets or sets the stored filename.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the length of the stored file data in bytes.
    /// </summary>
    public uint FileLength { get; set; }

    /// <summary>
    /// Gets or sets the offset in the stream where file data begins.
    /// </summary>
    public long DataOffset { get; set; }
}

/// <summary>
/// SRR header block (0x69).
/// The first block in an SRR file, contains app name if present.
/// </summary>
public class SrrHeaderBlock : SRRBlock
{
    /// <summary>
    /// Gets or sets the application name that created this SRR file.
    /// </summary>
    public string? AppName { get; set; }

    /// <summary>
    /// Gets a value indicating whether the app name is present in the header.
    /// </summary>
    public bool HasAppName => (Flags & (ushort)SRRHeaderFlags.AppNamePresent) != 0;
}

/// <summary>
/// SRR OSO hash block (0x6B).
/// Contains OSO hash information for OpenSubtitles matching.
/// </summary>
public class SrrOsoHashBlock : SRRBlock
{
    /// <summary>
    /// Gets or sets the filename associated with this hash.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file size in bytes.
    /// </summary>
    public ulong FileSize { get; set; }

    /// <summary>
    /// Gets or sets the 8-byte OSO hash value.
    /// </summary>
    public byte[] OsoHash { get; set; } = [];
}

/// <summary>
/// SRR RAR padding block (0x6C).
/// Contains padding information for RAR reconstruction.
/// </summary>
public class SrrRarPaddingBlock : SRRBlock
{
    /// <summary>
    /// Gets or sets the RAR filename this padding applies to.
    /// </summary>
    public string RarFileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the padding size in bytes.
    /// </summary>
    public uint PaddingSize { get; set; }
}
