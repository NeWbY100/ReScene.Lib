using ReScene.Core.IO;

namespace ReScene.Core;

/// <summary>
/// Provides data for brute-force status change events, indicating when the operation state changes
/// (e.g., from running to completed or cancelled).
/// </summary>
public class BruteForceStatusChangedEventArgs : OperationStatusChangedEventArgs
{
    /// <summary>
    /// Initializes a new instance with the specified new status.
    /// </summary>
    public BruteForceStatusChangedEventArgs(OperationStatus newStatus) : base(newStatus)
    {
    }

    /// <summary>
    /// Initializes a new instance with the specified old and new statuses.
    /// </summary>
    public BruteForceStatusChangedEventArgs(OperationStatus? oldStatus, OperationStatus newStatus) : base(oldStatus, newStatus)
    {
    }

    /// <summary>
    /// Initializes a new instance with the specified new status and completion status.
    /// </summary>
    public BruteForceStatusChangedEventArgs(OperationStatus newStatus, OperationCompletionStatus? completionStatus) : base(newStatus, completionStatus)
    {
    }

    /// <summary>
    /// Initializes a new instance with the specified old status, new status, and completion status.
    /// </summary>
    public BruteForceStatusChangedEventArgs(OperationStatus? oldStatus, OperationStatus newStatus, OperationCompletionStatus? completionStatus) : base(oldStatus, newStatus, completionStatus)
    {
    }
}
