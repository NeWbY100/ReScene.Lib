namespace ReScene.RAR;

/// <summary>
/// Represents a complete RAR header block with all its fields parsed in detail.
/// </summary>
public class RARDetailedBlock
{
    /// <summary>
    /// Block type name.
    /// </summary>
    public string BlockType { get; set; } = string.Empty;

    /// <summary>
    /// Block type value.
    /// </summary>
    public byte BlockTypeValue
    {
        get; set;
    }

    /// <summary>
    /// Start offset of this block.
    /// </summary>
    public long StartOffset
    {
        get; set;
    }

    /// <summary>
    /// Total block size (header + data).
    /// </summary>
    public long TotalSize
    {
        get; set;
    }

    /// <summary>
    /// Header size only.
    /// </summary>
    public int HeaderSize
    {
        get; set;
    }

    /// <summary>
    /// All fields in this block header.
    /// </summary>
    public IList<RARHeaderField> Fields { get; } = [];

    /// <summary>
    /// True if this block has associated data after the header.
    /// </summary>
    public bool HasData
    {
        get; set;
    }

    /// <summary>
    /// Size of data after header.
    /// </summary>
    public long DataSize
    {
        get; set;
    }

    /// <summary>
    /// For file/service blocks: the item name.
    /// </summary>
    public string? ItemName
    {
        get; set;
    }
}
