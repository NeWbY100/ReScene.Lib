namespace ReScene.SRR;

/// <summary>
/// SRR OSO hash block (0x6B).
/// Contains OSO hash information for OpenSubtitles matching.
/// </summary>
public class SRROSOHashBlock : SRRBlock
{
    /// <summary>
    /// Gets or sets the filename associated with this hash.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file size in bytes.
    /// </summary>
    public ulong FileSize
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the 8-byte OSO hash value.
    /// </summary>
    public ReadOnlyMemory<byte> OSOHash { get; set; }
}
