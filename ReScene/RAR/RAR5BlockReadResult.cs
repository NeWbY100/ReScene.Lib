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
