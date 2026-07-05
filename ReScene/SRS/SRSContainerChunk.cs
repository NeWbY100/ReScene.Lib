namespace ReScene.SRS;

/// <summary>
/// Non-SRS container element (for tree display).
/// </summary>
public class SRSContainerChunk
{
    /// <summary>
    /// Absolute position in the file.
    /// </summary>
    public long BlockPosition
    {
        get; set;
    }

    /// <summary>
    /// Total size of the chunk (header + payload).
    /// </summary>
    public long BlockSize
    {
        get; set;
    }

    /// <summary>
    /// Display label (e.g. "RIFF AVI", "LIST movi").
    /// </summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Raw chunk ID/tag (e.g. "RIFF", "LIST", GUID bytes).
    /// </summary>
    public string ChunkId { get; set; } = string.Empty;

    /// <summary>
    /// Size of the chunk header.
    /// </summary>
    public int HeaderSize
    {
        get; set;
    }

    /// <summary>
    /// Size of the payload (excluding header).
    /// </summary>
    public long PayloadSize
    {
        get; set;
    }
}
