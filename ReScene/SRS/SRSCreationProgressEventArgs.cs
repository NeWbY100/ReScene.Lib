namespace ReScene.SRS;

/// <summary>
/// Progress event args for SRS creation.
/// </summary>
public class SRSCreationProgressEventArgs : EventArgs
{
    /// <summary>
    /// Gets or sets the progress message describing the current creation step.
    /// </summary>
    public string Message { get; set; } = string.Empty;
}
