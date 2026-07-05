namespace ReScene.SRR;

/// <summary>
/// SRR stored file block (0x6A).
/// Contains a file embedded within the SRR.
/// </summary>
public class SRRStoredFileBlock : SRRBlock
{
    /// <summary>
    /// Gets or sets the stored filename.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the length of the stored file data in bytes.
    /// </summary>
    public uint FileLength
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the offset in the stream where file data begins.
    /// </summary>
    public long DataOffset
    {
        get; set;
    }
}
