using ReScene.Core.IO;

namespace ReScene.Core.Diagnostics;

/// <summary>
/// Provides data for RAR process status change events, including the associated RAR process.
/// </summary>
public class RARProcessStatusChangedEventArgs : OperationStatusChangedEventArgs
{
    /// <summary>
    /// Gets the RAR process associated with this status change.
    /// </summary>
    public RARProcess Process { get; private set; }

    /// <summary>
    /// Initializes a new instance with the specified process and new status.
    /// </summary>
    public RARProcessStatusChangedEventArgs(RARProcess process, OperationStatus newStatus) : base(newStatus)
    {
        Process = process;
    }

    public RARProcessStatusChangedEventArgs(RARProcess process, OperationStatus? oldStatus, OperationStatus newStatus)
        : base(oldStatus, newStatus)
    {
        Process = process;
    }

    public RARProcessStatusChangedEventArgs(RARProcess process, OperationStatus newStatus, OperationCompletionStatus completionStatus)
        : base(newStatus, completionStatus)
    {
        Process = process;
    }

    public RARProcessStatusChangedEventArgs(RARProcess process, OperationStatus? oldStatus, OperationStatus newStatus, OperationCompletionStatus? completionStatus)
        : base(oldStatus, newStatus, completionStatus)
    {
        Process = process;
    }
}
