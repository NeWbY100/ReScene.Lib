namespace ReScene.RAR;

/// <summary>
/// Mask constants for extracting flag values
/// </summary>
internal static class RARFlagMasks
{
    /// <summary>
    /// Mask for dictionary size bits (bits 5-7)
    /// </summary>
    public const ushort DictionarySizeMask = 0x00E0;

    /// <summary>
    /// Shift amount for dictionary size bits
    /// </summary>
    public const int DictionarySizeShift = 5;

    /// <summary>
    /// Salt length in bytes
    /// </summary>
    public const int SaltLength = 8;
}
