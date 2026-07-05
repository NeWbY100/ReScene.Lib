namespace ReScene.SRR;

/// <summary>
/// Generic SRR block flags.
/// </summary>
[Flags]
public enum SRRBlockFlags : ushort
{
    /// <summary>
    /// No flags set.
    /// </summary>
    None = 0x0000,

    /// <summary>
    /// Skip this block if the type is unknown.
    /// </summary>
    SkipIfUnknown = 0x4000,

    /// <summary>
    /// Block has an additional size field (long block).
    /// </summary>
    LongBlock = 0x8000
}
