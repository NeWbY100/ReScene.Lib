namespace RARLib.Decompression;

/// <summary>
/// Huffman decode table for RAR decompression.
/// Ported from unrar unpack.hpp DecodeTable struct.
/// </summary>
public class DecodeTable
{
    /// <summary>
    /// Real size of DecodeNum table (alphabet size).
    /// </summary>
    public int MaxNum { get; set; }

    /// <summary>
    /// Left aligned start and upper limit codes defining code space ranges for bit lengths.
    /// DecodeLen[BitLength-1] defines the start of range for bit length and
    /// DecodeLen[BitLength] defines next code after the end of range.
    /// </summary>
    public uint[] DecodeLen { get; } = new uint[16];

    /// <summary>
    /// Every item contains the sum of all preceding items.
    /// Contains the start position in code list for every bit length.
    /// </summary>
    public uint[] DecodePos { get; } = new uint[16];

    /// <summary>
    /// Number of compressed bits processed in quick mode.
    /// Must not exceed MaxQuickDecodeBits (9).
    /// </summary>
    public int QuickBits { get; set; }

    /// <summary>
    /// Translates compressed bits (up to QuickBits length) to bit length in quick mode.
    /// </summary>
    public byte[] QuickLen { get; } = new byte[1 << PackDef.MaxQuickDecodeBits];

    /// <summary>
    /// Translates compressed bits (up to QuickBits length) to position in alphabet in quick mode.
    /// </summary>
    public ushort[] QuickNum { get; } = new ushort[1 << PackDef.MaxQuickDecodeBits];

    /// <summary>
    /// Translate the position in code list to position in alphabet.
    /// Used when compressed bit field is too long for QuickLen based translation.
    /// </summary>
    public ushort[] DecodeNum { get; } = new ushort[PackDef.LargestTableSize];

    /// <summary>
    /// Creates a new decode table.
    /// </summary>
    public DecodeTable()
    {
        Reset();
    }

    /// <summary>
    /// Resets the table to initial state.
    /// </summary>
    public void Reset()
    {
        MaxNum = 0;
        QuickBits = 0;
        Array.Clear(DecodeLen);
        Array.Clear(DecodePos);
        Array.Clear(QuickLen);
        Array.Clear(QuickNum);
        Array.Clear(DecodeNum);
    }
}

/// <summary>
/// Collection of decode tables used during unpacking.
/// </summary>
public class UnpackBlockTables
{
    /// <summary>Decode literals.</summary>
    public DecodeTable LD { get; } = new();

    /// <summary>Decode distances.</summary>
    public DecodeTable DD { get; } = new();

    /// <summary>Decode lower bits of distances.</summary>
    public DecodeTable LDD { get; } = new();

    /// <summary>Decode repeating distances.</summary>
    public DecodeTable RD { get; } = new();

    /// <summary>Decode bit lengths in Huffman table.</summary>
    public DecodeTable BD { get; } = new();
}
