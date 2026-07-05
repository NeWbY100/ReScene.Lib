namespace ReScene.RAR.Decompression;

/// <summary>
/// Collection of decode tables used during unpacking.
/// </summary>
internal class UnpackBlockTables
{
    /// <summary>
    /// Decode literals.
    /// </summary>
    public DecodeTable LD { get; } = new();

    /// <summary>
    /// Decode distances.
    /// </summary>
    public DecodeTable DD { get; } = new();

    /// <summary>
    /// Decode lower bits of distances.
    /// </summary>
    public DecodeTable LDD { get; } = new();

    /// <summary>
    /// Decode repeating distances.
    /// </summary>
    public DecodeTable RD { get; } = new();

    /// <summary>
    /// Decode bit lengths in Huffman table.
    /// </summary>
    public DecodeTable BD { get; } = new();
}
