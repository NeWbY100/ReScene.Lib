namespace ReScene.SRR;

/// <summary>
/// Result of SRR file creation.
/// </summary>
public class SRRCreationResult
{
    /// <summary>
    /// Whether creation succeeded.
    /// </summary>
    public bool Success
    {
        get; set;
    }

    /// <summary>
    /// Path to the created SRR file.
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
    /// Number of RAR volumes processed.
    /// </summary>
    public int VolumeCount
    {
        get; set;
    }

    /// <summary>
    /// Number of stored files embedded.
    /// </summary>
    public int StoredFileCount
    {
        get; set;
    }

    /// <summary>
    /// Size of the created SRR file in bytes.
    /// </summary>
    public long SRRFileSize
    {
        get; set;
    }

    /// <summary>
    /// Non-fatal warnings encountered during creation.
    /// </summary>
    public IList<string> Warnings { get; } = [];

    /// <summary>
    /// Names of VobSub .idx files discovered when generating languages.diz, in archive order.
    /// </summary>
    public IList<string> LanguagesDizIdxFiles { get; } = [];
}
