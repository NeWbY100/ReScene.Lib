namespace ReScene.Core.Diagnostics;

/// <summary>
/// Provides data for process output events, including the output text and whether it is an error.
/// </summary>
public class ProcessDataEventArgs(string? data) : EventArgs
{
    /// <summary>
    /// Gets the output data text.
    /// </summary>
    public string? Data { get; private set; } = data;

    /// <summary>
    /// Gets whether this data came from the error stream.
    /// </summary>
    public bool Error
    {
        get; private set;
    }

    /// <summary>
    /// Initializes a new instance with the specified data and error flag.
    /// </summary>
    /// <param name="data">The output data text.</param>
    /// <param name="error">Whether this data came from the error stream.</param>
    public ProcessDataEventArgs(string? data, bool error) : this(data)
    {
        Error = error;
    }
}
