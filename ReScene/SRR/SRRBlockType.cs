namespace ReScene.SRR;

/// <summary>
/// SRR-specific block types (0x69-0x71).
/// </summary>
public enum SRRBlockType : byte
{
    /// <summary>
    /// SRR file header block.
    /// </summary>
    Header = 0x69,

    /// <summary>
    /// Stored file block.
    /// </summary>
    StoredFile = 0x6A,

    /// <summary>
    /// OSO hash block.
    /// </summary>
    OSOHash = 0x6B,

    /// <summary>
    /// RAR padding block.
    /// </summary>
    RARPadding = 0x6C,

    /// <summary>
    /// RAR file reference block, followed by embedded RAR headers.
    /// </summary>
    RARFile = 0x71
}
