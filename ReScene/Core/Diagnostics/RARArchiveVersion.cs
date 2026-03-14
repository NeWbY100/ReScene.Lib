namespace ReScene.Core.Diagnostics;

/// <summary>
/// Specifies the RAR archive format version. Can be combined as flags.
/// </summary>
[Flags]
public enum RARArchiveVersion
{
    /// <summary>
    /// RAR 4.x archive format.
    /// </summary>
    RAR4 = 0x01,

    /// <summary>
    /// RAR 5.0 archive format.
    /// </summary>
    RAR5 = 0x02,

    /// <summary>
    /// RAR 7.0 archive format.
    /// </summary>
    RAR7 = 0x04
}
