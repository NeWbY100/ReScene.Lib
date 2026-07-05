namespace ReScene.SRS;

/// <summary>
/// Result of SRS file creation.
/// </summary>
public class SRSCreationResult
{
    /// <summary>
    /// Whether SRS creation completed successfully.
    /// </summary>
    public bool Success
    {
        get; set;
    }

    /// <summary>
    /// Path to the created SRS file.
    /// </summary>
    public string? OutputPath
    {
        get; set;
    }

    /// <summary>
    /// Error message if creation failed.
    /// </summary>
    public string? ErrorMessage
    {
        get; set;
    }

    /// <summary>
    /// Detected container type of the sample file.
    /// </summary>
    public SRSContainerType ContainerType
    {
        get; set;
    }

    /// <summary>
    /// Number of tracks found in the sample file.
    /// </summary>
    public int TrackCount
    {
        get; set;
    }

    /// <summary>
    /// Size of the created SRS file in bytes.
    /// </summary>
    public long SRSFileSize
    {
        get; set;
    }

    /// <summary>
    /// CRC32 checksum of the original sample file.
    /// </summary>
    public uint SampleCRC32
    {
        get; set;
    }

    /// <summary>
    /// Size of the original sample file in bytes.
    /// </summary>
    public long SampleSize
    {
        get; set;
    }

    /// <summary>
    /// Non-fatal warnings encountered during creation.
    /// </summary>
    public IList<string> Warnings { get; } = [];
}
