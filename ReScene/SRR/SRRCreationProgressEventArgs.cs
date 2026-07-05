namespace ReScene.SRR;

/// <summary>
/// Progress event args for SRR creation.
/// </summary>
public class SRRCreationProgressEventArgs : EventArgs
{
    /// <summary>
    /// Overall progress percentage (0-100).
    /// </summary>
    public int ProgressPercent
    {
        get; set;
    }

    /// <summary>
    /// Current volume being processed (1-based).
    /// </summary>
    public int CurrentVolume
    {
        get; set;
    }

    /// <summary>
    /// Total number of volumes to process.
    /// </summary>
    public int TotalVolumes
    {
        get; set;
    }

    /// <summary>
    /// Status message describing current operation.
    /// </summary>
    public string Message { get; set; } = string.Empty;
}
