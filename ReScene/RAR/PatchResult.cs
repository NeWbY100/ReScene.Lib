namespace ReScene.RAR;

/// <summary>
/// Result of a patching operation on a single block.
/// </summary>
internal sealed record PatchResult
{
    /// <summary>
    /// Byte offset of the block within the RAR file.
    /// </summary>
    public long BlockPosition
    {
        get; init;
    }

    /// <summary>
    /// RAR 4.x block type (FileHeader or Service).
    /// </summary>
    public RAR4BlockType BlockType
    {
        get; init;
    }

    /// <summary>
    /// File name from the block header, if available.
    /// </summary>
    public string? FileName
    {
        get; init;
    }

    /// <summary>
    /// Host OS value before patching.
    /// </summary>
    public byte OriginalHostOS
    {
        get; init;
    }

    /// <summary>
    /// Host OS value after patching.
    /// </summary>
    public byte NewHostOS
    {
        get; init;
    }

    /// <summary>
    /// File attributes value before patching.
    /// </summary>
    public uint OriginalAttributes
    {
        get; init;
    }

    /// <summary>
    /// File attributes value after patching.
    /// </summary>
    public uint NewAttributes
    {
        get; init;
    }

    /// <summary>
    /// Header CRC before patching.
    /// </summary>
    public ushort OriginalCRC
    {
        get; init;
    }

    /// <summary>
    /// Header CRC after patching (0 in analysis mode).
    /// </summary>
    public ushort NewCRC
    {
        get; init;
    }
}
