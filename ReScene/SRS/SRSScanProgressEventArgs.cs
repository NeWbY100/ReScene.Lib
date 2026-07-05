namespace ReScene.SRS;

/// <summary>
/// Progress data for signature scanning operations.
/// </summary>
public class SRSScanProgressEventArgs : EventArgs
{
    /// <summary>
    /// Gets the current phase description.
    /// </summary>
    public string Phase { get; init; } = string.Empty;

    /// <summary>
    /// Gets the bytes scanned so far.
    /// </summary>
    public long BytesScanned { get; init; }

    /// <summary>
    /// Gets the total bytes to scan.
    /// </summary>
    public long BytesTotal { get; init; }

    /// <summary>
    /// Gets the scan progress percentage (0-100).
    /// </summary>
    public int Percent { get; init; }
}
