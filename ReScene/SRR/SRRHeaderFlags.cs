namespace ReScene.SRR;

/// <summary>
/// SRR header block flags.
/// </summary>
[Flags]
internal enum SRRHeaderFlags : ushort
{
    /// <summary>
    /// No flags set.
    /// </summary>
    None = 0x0000,

    /// <summary>
    /// Application name is present in the header.
    /// </summary>
    AppNamePresent = 0x0001
}
