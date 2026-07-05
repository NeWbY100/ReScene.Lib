namespace ReScene.SRS;

/// <summary>
/// Progress event args for SRS reconstruction.
/// </summary>
public class SRSReconstructionProgressEventArgs : EventArgs
{
    /// <summary>
    /// Gets the current phase description (e.g., "Loading SRS", "Rebuilding").
    /// </summary>
    public string Phase { get; init; } = "";

    /// <summary>
    /// Gets the current track number being processed.
    /// </summary>
    public int TrackNumber
    {
        get; init;
    }

    /// <summary>
    /// Gets the total number of tracks to process.
    /// </summary>
    public int TotalTracks
    {
        get; init;
    }

    /// <summary>
    /// Gets the overall progress percentage (0-100).
    /// </summary>
    public double ProgressPercent
    {
        get; init;
    }
}
