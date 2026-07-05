namespace ReScene.RAR;

/// <summary>
/// Parsed service block info for RAR 5.0.
/// </summary>
internal class RAR5ServiceBlockInfo
{
    /// <summary>
    /// Service data type (e.g., 0x03 for CMT comment).
    /// </summary>
    public ulong ServiceDataType
    {
        get; set;
    }

    /// <summary>
    /// Sub-type name (e.g., "CMT").
    /// </summary>
    public string SubType { get; set; } = string.Empty;

    /// <summary>
    /// Unpacked data size.
    /// </summary>
    public ulong UnpackedSize
    {
        get; set;
    }

    /// <summary>
    /// File flags.
    /// </summary>
    public ulong FileFlags
    {
        get; set;
    }

    /// <summary>
    /// True if data is stored uncompressed.
    /// </summary>
    public bool IsStored
    {
        get; set;
    }

    /// <summary>
    /// Compression version.
    /// </summary>
    public int CompressionVersion
    {
        get; set;
    }

    /// <summary>
    /// Compression method (0-5).
    /// </summary>
    public int CompressionMethod
    {
        get; set;
    }

    /// <summary>
    /// Dictionary size as power of 2.
    /// </summary>
    public int DictSize
    {
        get; set;
    }

    /// <summary>
    /// For CMT blocks: the comment text if extracted.
    /// </summary>
    public string? CommentText
    {
        get; set;
    }
}
