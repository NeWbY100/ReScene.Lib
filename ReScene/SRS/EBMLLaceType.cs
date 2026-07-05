namespace ReScene.SRS;

/// <summary>
/// Lacing mode for MKV Block/SimpleBlock elements.
/// Values correspond to the 2-bit lacing field in the block flags byte (bits 1-2).
/// </summary>
internal enum EBMLLaceType : byte
{
    /// <summary>
    /// No lacing - single frame per block.
    /// </summary>
    None = 0,

    /// <summary>
    /// Xiph lacing - 0xFF-terminated sizes.
    /// </summary>
    Xiph = 2,

    /// <summary>
    /// Fixed-size lacing - all frames are equal size.
    /// </summary>
    Fixed = 4,

    /// <summary>
    /// EBML lacing - delta-encoded sizes using EBML VINTs.
    /// </summary>
    EBML = 6
}
