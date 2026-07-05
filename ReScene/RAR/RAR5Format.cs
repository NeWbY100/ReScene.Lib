namespace ReScene.RAR;

/// <summary>
/// Named constants for RAR 5.0 vint and CompInfo bit-field masks.
/// </summary>
internal static class RAR5Format
{
    // Variable-length integer (vint) decode
    /// <summary>Low 7 bits of a vint byte carry data.</summary>
    public const int VIntDataMask = 0x7F;

    /// <summary>High bit signals that more vint bytes follow.</summary>
    public const int VIntContinuationBit = 0x80;

    /// <summary>Number of data bits added per vint byte.</summary>
    public const int VIntShiftStep = 7;

    /// <summary>Maximum shift before the vint value would overflow a ulong.</summary>
    public const int VIntMaxShift = 63;

    // CompInfo field bit-field masks and shifts
    /// <summary>Bits 0-5: compression version.</summary>
    public const ulong CompInfoVersionMask = 0x3F;

    /// <summary>Bit 6: solid flag within CompInfo.</summary>
    public const ulong CompInfoSolidBit = 0x40;

    /// <summary>Bits 7-9: compression method (shift amount).</summary>
    public const int CompInfoMethodShift = 7;

    /// <summary>Mask applied after shifting to extract the 3-bit method.</summary>
    public const ulong CompInfoMethodMask = 0x07;

    /// <summary>Bits 10-13: dictionary size power (shift amount).</summary>
    public const int CompInfoDictShift = 10;

    /// <summary>Mask applied after shifting to extract the 4-bit dict power.</summary>
    public const ulong CompInfoDictMask = 0x0F;

    /// <summary>Base dictionary size in KB; actual size is 128 KB shifted left by DictSizePower.</summary>
    public const int CompInfoDictBaseKB = 128;
}
