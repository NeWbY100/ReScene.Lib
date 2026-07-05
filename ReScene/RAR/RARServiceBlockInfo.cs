using ReScene.RAR.Decompression;

namespace ReScene.RAR;

/// <summary>
/// Parsed service sub-block info (0x7A blocks like CMT, RR, etc.)
/// </summary>
internal class RARServiceBlockInfo
{
    /// <summary>
    /// Sub-block type name (e.g., "CMT", "RR", "AV").
    /// </summary>
    public string SubType { get; set; } = string.Empty;

    /// <summary>
    /// Packed (compressed) size of data.
    /// </summary>
    public ulong PackedSize
    {
        get; set;
    }

    /// <summary>
    /// Unpacked size of data.
    /// </summary>
    public ulong UnpackedSize
    {
        get; set;
    }

    /// <summary>
    /// Compression method (0x30=Store, 0x33=Normal, etc.).
    /// </summary>
    public byte CompressionMethod
    {
        get; set;
    }

    /// <summary>
    /// Data CRC.
    /// </summary>
    public uint DataCRC
    {
        get; set;
    }

    /// <summary>
    /// Offset where the data starts (relative to block start).
    /// </summary>
    public int DataOffset
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

    /// <summary>
    /// For CMT blocks: raw comment data (may be compressed).
    /// </summary>
    public byte[]? RawData
    {
        get; set;
    }

    /// <summary>
    /// True if comment is stored (uncompressed), false if compressed.
    /// </summary>
    public bool IsStored => CompressionMethod == (byte)RARMethod.Store;

    /// <summary>
    /// Host operating system (0=MS-DOS, 1=OS/2, 2=Windows, 3=Unix, etc.).
    /// </summary>
    public byte HostOS
    {
        get; set;
    }

    /// <summary>
    /// Raw DOS file time value (0 indicates no timestamp/zeroed).
    /// </summary>
    public uint FileTimeDOS
    {
        get; set;
    }

    /// <summary>
    /// File attributes.
    /// </summary>
    public uint FileAttributes
    {
        get; set;
    }

    /// <summary>
    /// RAR version needed to unpack.
    /// </summary>
    public byte UnpackVersion
    {
        get; set;
    }

    /// <summary>
    /// True if file time is zeroed (0x00000000).
    /// </summary>
    public bool HasZeroedFileTime => FileTimeDOS == 0;

    /// <summary>
    /// Modification time precision level (maps to -tsm0 through -tsm4).
    /// </summary>
    public TimestampPrecision MtimePrecision
    {
        get; set;
    }

    /// <summary>
    /// Creation time precision level (maps to -tsc0 through -tsc4).
    /// </summary>
    public TimestampPrecision CtimePrecision
    {
        get; set;
    }

    /// <summary>
    /// Access time precision level (maps to -tsa0 through -tsa4).
    /// </summary>
    public TimestampPrecision AtimePrecision
    {
        get; set;
    }
}
