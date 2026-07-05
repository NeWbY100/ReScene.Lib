namespace ReScene.SRS;

/// <summary>
/// Bit-flag constants for the MKV block flags byte (the third byte of the fixed
/// block header, after track-number VINT and 2-byte timecode).
/// </summary>
internal static class MKVBlockFlags
{
    /// <summary>
    /// Mask for the 2-bit lacing field in the block flags byte (bits 1-2).
    /// Apply as <c>(EBMLLaceType)(flagsByte &amp; MKVBlockFlags.LacingMask)</c>.
    /// Results match <see cref="EBMLLaceType"/> values directly:
    /// 0x00 = None, 0x02 = Xiph, 0x04 = Fixed, 0x06 = EBML.
    /// </summary>
    public const int LacingMask = 0x06;
}
