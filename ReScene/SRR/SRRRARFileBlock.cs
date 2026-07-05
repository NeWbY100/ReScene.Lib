namespace ReScene.SRR;

/// <summary>
/// SRR RAR file reference block (0x71).
/// Contains the RAR filename and is followed by embedded RAR headers.
/// </summary>
public class SRRRARFileBlock : SRRBlock
{
    /// <summary>
    /// Gets or sets the RAR filename referenced by this block.
    /// </summary>
    public string FileName { get; set; } = string.Empty;
}
