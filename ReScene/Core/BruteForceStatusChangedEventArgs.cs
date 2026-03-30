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
    /// <param name="newStatus">The new operation status.</param>
    public BruteForceStatusChangedEventArgs(OperationStatus newStatus) : base(newStatus)
    {
    }

    /// <summary>
    /// Initializes a new instance with the specified old and new statuses.
    /// </summary>
    /// <param name="oldStatus">The previous operation status.</param>
    /// <param name="newStatus">The new operation status.</param>
    public BruteForceStatusChangedEventArgs(OperationStatus? oldStatus, OperationStatus newStatus) : base(oldStatus, newStatus)
    {
    }

    /// <summary>
    /// Initializes a new instance with the specified new status and completion status.
    /// </summary>
    /// <param name="newStatus">The new operation status.</param>
    /// <param name="completionStatus">The completion outcome, if applicable.</param>
    public BruteForceStatusChangedEventArgs(OperationStatus newStatus, OperationCompletionStatus? completionStatus) : base(newStatus, completionStatus)
    {
    }

    /// <summary>
    /// Initializes a new instance with the specified old status, new status, and completion status.
    /// </summary>
    /// <param name="oldStatus">The previous operation status.</param>
    /// <param name="newStatus">The new operation status.</param>
    /// <param name="completionStatus">The completion outcome, if applicable.</param>
    public BruteForceStatusChangedEventArgs(OperationStatus? oldStatus, OperationStatus newStatus, OperationCompletionStatus? completionStatus) : base(oldStatus, newStatus, completionStatus)
    {
    }
}
