namespace ReScene.SRR;

/// <summary>
/// SRR RAR padding block (0x6C).
/// Contains padding information for RAR reconstruction.
/// </summary>
public class SRRRARPaddingBlock : SRRBlock
{
    /// <summary>
    /// Gets or sets the RAR filename this padding applies to.
    /// </summary>
    public string RARFileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the padding size in bytes.
    /// </summary>
    public uint PaddingSize
    {
        get; set;
    }
}
