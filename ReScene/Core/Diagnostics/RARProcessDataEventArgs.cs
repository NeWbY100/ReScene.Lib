namespace ReScene.Core.Diagnostics;

/// <summary>
/// Provides data for RAR process output events, including the associated RAR process.
/// </summary>
public class RARProcessDataEventArgs : ProcessDataEventArgs
{
    /// <summary>
    /// Gets the RAR process that produced the output.
    /// </summary>
    public RARProcess Process
    {
        get; private set;
    }

    /// <summary>
    /// Initializes a new instance with the specified process and output data.
    /// </summary>
    /// <param name="process">The RAR process that produced the output.</param>
    /// <param name="data">The output data text.</param>
    public RARProcessDataEventArgs(RARProcess process, string? data) : base(data)
    {
        Process = process;
    }

    /// <summary>
    /// Initializes a new instance with the specified process, output data, and error flag.
    /// </summary>
    /// <param name="process">The RAR process that produced the output.</param>
    /// <param name="data">The output data text.</param>
    /// <param name="error">Whether this data came from the error stream.</param>
    public RARProcessDataEventArgs(RARProcess process, string? data, bool error) : base(data, error)
    {
        Process = process;
    }
}
