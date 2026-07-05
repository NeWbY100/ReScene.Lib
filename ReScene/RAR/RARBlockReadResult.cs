namespace ReScene.RAR;

/// <summary>
/// Result of reading a RAR block header.
/// </summary>
internal class RARBlockReadResult
{
    /// <summary>
    /// Block type (RAR 4.x).
    /// </summary>
    public RAR4BlockType BlockType
    {
        get; set;
    }

    /// <summary>
    /// Raw flags value.
    /// </summary>
    public ushort Flags
    {
        get; set;
    }

    /// <summary>
    /// Header size in bytes.
    /// </summary>
    public ushort HeaderSize
    {
        get; set;
    }

    /// <summary>
    /// Additional data size (from LONG_BLOCK or file headers).
    /// </summary>
    public uint AddSize
    {
        get; set;
    }

    /// <summary>
    /// Position where the block starts.
    /// </summary>
    public long BlockPosition
    {
        get; set;
    }

    /// <summary>
    /// Header CRC value.
    /// </summary>
    public ushort HeaderCRC
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
    /// Parsed archive header (if BlockType is ArchiveHeader).
    /// </summary>
    public RARArchiveHeader? ArchiveHeader
    {
        get; set;
    }

    /// <summary>
    /// Parsed file header (if BlockType is FileHeader).
    /// </summary>
    public RARFileHeader? FileHeader
    {
        get; set;
    }

    /// <summary>
    /// Parsed service block info (if BlockType is Service).
    /// </summary>
    public RARServiceBlockInfo? ServiceBlockInfo
    {
        get; set;
    }

    /// <summary>
    /// Full 64-bit size of the data area that follows this block's header. File and service
    /// blocks report their parsed <c>PackedSize</c> (which folds in HIGH_PACK_SIZE when the
    /// LARGE flag is set); any other block type falls back to the 32-bit <see cref="AddSize"/>.
    /// Only meaningful when the block was read with <c>parseContents: true</c>. Walkers must use
    /// this (not <see cref="AddSize"/>) to skip past file/service data, otherwise a &gt;= 4 GiB
    /// packed entry is under-skipped by whole multiples of 4 GiB.
    /// </summary>
    public long DataSize => FileHeader is { } fh
        ? (long)fh.PackedSize
        : ServiceBlockInfo is { } sb
            ? (long)sb.PackedSize
            : AddSize;
}
