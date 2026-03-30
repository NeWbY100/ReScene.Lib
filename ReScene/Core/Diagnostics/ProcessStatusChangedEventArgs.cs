using ReScene.Core.IO;

namespace ReScene.Core.Diagnostics;

/// <summary>
/// Provides data for process status change events, including the associated RAR process.
/// </summary>
public class ProcessStatusChangedEventArgs : OperationStatusChangedEventArgs
{
    /// <summary>
    /// Gets the RAR process associated with this status change.
    /// </summary>
    public RARProcess Process { get; private set; }

    /// <summary>
    /// Initializes a new instance with the specified process and new status.
    /// </summary>
    /// <param name="process">The RAR process.</param>
    /// <param name="newStatus">The new operation status.</param>
    public ProcessStatusChangedEventArgs(RARProcess process, OperationStatus newStatus)
        : base(newStatus)
    {
        Process = process;
    }

    /// <summary>
    /// Initializes a new instance with the specified process, old status, and new status.
    /// </summary>
    /// <param name="process">The RAR process.</param>
    /// <param name="oldStatus">The previous operation status.</param>
    /// <param name="newStatus">The new operation status.</param>
    public ProcessStatusChangedEventArgs(RARProcess process, OperationStatus? oldStatus, OperationStatus newStatus)
        : base(oldStatus, newStatus)
    {
        Process = process;
    }

    /// <summary>
    /// Initializes a new instance with the specified process, new status, and completion status.
    /// </summary>
    /// <param name="process">The RAR process.</param>
    /// <param name="newStatus">The new operation status.</param>
    /// <param name="completionStatus">The completion outcome.</param>
    public ProcessStatusChangedEventArgs(RARProcess process, OperationStatus newStatus, OperationCompletionStatus completionStatus)
        : base(newStatus, completionStatus)
    {
        Process = process;
    }

    /// <summary>
    /// Initializes a new instance with the specified process, old status, new status, and completion status.
    /// </summary>
    /// <param name="process">The RAR process.</param>
    /// <param name="oldStatus">The previous operation status.</param>
    /// <param name="newStatus">The new operation status.</param>
    /// <param name="completionStatus">The completion outcome, if applicable.</param>
    public ProcessStatusChangedEventArgs(RARProcess process, OperationStatus? oldStatus, OperationStatus newStatus, OperationCompletionStatus? completionStatus)
        : base(oldStatus, newStatus, completionStatus)
    {
        Process = process;
    }
}
