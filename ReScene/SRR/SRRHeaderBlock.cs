namespace ReScene.SRR;

/// <summary>
/// SRR header block (0x69).
/// The first block in an SRR file, contains app name if present.
/// </summary>
public class SRRHeaderBlock : SRRBlock
{
    /// <summary>
    /// Gets or sets the application name that created this SRR file.
    /// </summary>
    public string? AppName
    {
        get; set;
    }

    /// <summary>
    /// Gets a value indicating whether the app name is present in the header.
    /// </summary>
    public bool HasAppName => (Flags & (ushort)SRRHeaderFlags.AppNamePresent) != 0;
}
