namespace RARLib.Decompression;

/// <summary>
/// Pack/unpack constants and lookup tables from unrar compress.hpp.
/// </summary>
public static class PackDef
{
    #region Shared Lookup Tables

    /// <summary>Length decode values (RAR 2.x/3.x).</summary>
    public static readonly byte[] LDecode =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 20, 24, 28,
            32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224
    ];

    /// <summary>Length extra bits (RAR 2.x/3.x).</summary>
    public static readonly byte[] LBits =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2,
            3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5
    ];

    /// <summary>Short distance decode values (RAR 2.x/3.x).</summary>
    public static readonly byte[] SDDecode = [0, 4, 8, 16, 32, 64, 128, 192];

    /// <summary>Short distance extra bits (RAR 2.x/3.x).</summary>
    public static readonly byte[] SDBits = [2, 2, 3, 4, 5, 6, 6, 6];

    #endregion
    /// <summary>Maximum LZ match length for short distances.</summary>
    public const int MaxLzMatch = 0x1001;

    /// <summary>Maximum incremented LZ match for longer distances.</summary>
    public const int MaxIncLzMatch = MaxLzMatch + 3;

    /// <summary>Maximum match length for RAR v3.</summary>
    public const int Max3LzMatch = 0x101;

    /// <summary>Maximum incremented match length for RAR v3.</summary>
    public const int Max3IncLzMatch = Max3LzMatch + 3;

    /// <summary>Low distance repeat count.</summary>
    public const int LowDistRepCount = 16;

    // RAR 5.x constants
    /// <summary>Alphabet size for RAR 5.x literals/lengths.</summary>
    public const int NC = 306;

    /// <summary>Base distance codes up to 4 GB.</summary>
    public const int DCB = 64;

    /// <summary>Extended distance codes up to 1 TB.</summary>
    public const int DCX = 80;

    /// <summary>Low distance codes.</summary>
    public const int LDC = 16;

    /// <summary>Repeat distance codes.</summary>
    public const int RC = 44;

    /// <summary>Huffman table size (base).</summary>
    public const int HuffTableSizeB = NC + DCB + RC + LDC;

    /// <summary>Huffman table size (extended).</summary>
    public const int HuffTableSizeX = NC + DCX + RC + LDC;

    /// <summary>Bit length codes.</summary>
    public const int BC = 20;

    // RAR 3.x constants
    /// <summary>Alphabet size for RAR 3.x literals/lengths.</summary>
    public const int NC30 = 299;

    /// <summary>Distance codes for RAR 3.x.</summary>
    public const int DC30 = 60;

    /// <summary>Low distance codes for RAR 3.x.</summary>
    public const int LDC30 = 17;

    /// <summary>Repeat codes for RAR 3.x.</summary>
    public const int RC30 = 28;

    /// <summary>Bit length codes for RAR 3.x.</summary>
    public const int BC30 = 20;

    /// <summary>Huffman table size for RAR 3.x.</summary>
    public const int HuffTableSize30 = NC30 + DC30 + RC30 + LDC30;

    // RAR 2.x constants
    /// <summary>Alphabet size for RAR 2.x.</summary>
    public const int NC20 = 298;

    /// <summary>Distance codes for RAR 2.x.</summary>
    public const int DC20 = 48;

    /// <summary>Repeat codes for RAR 2.x.</summary>
    public const int RC20 = 28;

    /// <summary>Bit length codes for RAR 2.x.</summary>
    public const int BC20 = 19;

    /// <summary>Multimedia codes for RAR 2.x.</summary>
    public const int MC20 = 257;

    /// <summary>Largest alphabet size among all versions.</summary>
    public const int LargestTableSize = 306;

    /// <summary>Maximum bits for quick decode mode.</summary>
    public const int MaxQuickDecodeBits = 9;
}
