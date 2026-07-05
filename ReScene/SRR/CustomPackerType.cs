namespace ReScene.SRR;

/// <summary>
/// Indicates the type of custom RAR packer anomaly detected in file headers.
/// </summary>
public enum CustomPackerType
{
    /// <summary>
    /// No custom packer detected.
    /// </summary>
    None,

    /// <summary>
    /// UNP_SIZE = 0xFFFFFFFFFFFFFFFF (both low and high 32-bit fields are all ones).
    /// Known groups: RELOADED, HI2U, 0x0007, 0x0815.
    /// </summary>
    AllOnesWithLargeFlag,

    /// <summary>
    /// UNP_SIZE = 0xFFFFFFFF without LARGE flag (raw 32-bit field maxed out).
    /// Known group: QCF.
    /// </summary>
    MaxUint32WithoutLargeFlag
}
