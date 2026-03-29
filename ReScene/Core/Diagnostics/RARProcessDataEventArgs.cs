namespace ReScene.Core.Diagnostics;

/// <summary>
/// Provides data for RAR process output events, including the associated RAR process.
/// </summary>
public class RARProcessDataEventArgs : ProcessDataEventArgs
{
    /// <summary>
    /// Gets the RAR process that produced the output.
    /// </summary>
    public RARProcess Process { get; private set; }

    /// <summary>
    /// Initializes a new instance with the specified process and output data.
    /// </summary>
    public RARProcessDataEventArgs(RARProcess process, string? data) : base(data)
    {
        Process = process;
    }

    public RARProcessDataEventArgs(RARProcess process, string? data, bool error) : base(data, error)
    {
        Process = process;
    }
}
