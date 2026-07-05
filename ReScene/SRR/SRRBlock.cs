namespace ReScene.SRR;

/// <summary>
/// Base class for SRR blocks.
/// </summary>
public class SRRBlock
{
    /// <summary>
    /// Gets or sets the block CRC value.
    /// </summary>
    public ushort CRC
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the block type.
    /// </summary>
    public SRRBlockType BlockType
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the block flags.
    /// </summary>
    public ushort Flags
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the header size in bytes.
    /// </summary>
    public ushort HeaderSize
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the block position in the stream.
    /// </summary>
    public long BlockPosition
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the additional data size following the header.
    /// </summary>
    public uint AddSize
    {
        get; set;
    }
}
