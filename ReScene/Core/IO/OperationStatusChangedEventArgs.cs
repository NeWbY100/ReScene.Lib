namespace ReScene.Core.IO;

/// <summary>
/// Provides data for operation status change events, indicating state transitions and completion status.
/// </summary>
/// <param name="newStatus">The new operation status.</param>
public class OperationStatusChangedEventArgs(OperationStatus newStatus) : EventArgs
{
    /// <summary>
    /// Gets the previous operation status, or <c>null</c> if this is the initial status.
    /// </summary>
    public OperationStatus? OldStatus { get; private set; }

    /// <summary>
    /// Gets the current operation status.
    /// </summary>
    public OperationStatus NewStatus { get; private set; } = newStatus;

    /// <summary>
    /// Gets the completion status if the operation has completed, or <c>null</c> if still in progress.
    /// </summary>
    public OperationCompletionStatus? CompletionStatus { get; private set; }

    public OperationStatusChangedEventArgs(OperationStatus? oldStatus, OperationStatus newStatus)
        : this(newStatus)
    {
        OldStatus = oldStatus;
    }

    public OperationStatusChangedEventArgs(OperationStatus newStatus, OperationCompletionStatus? completionStatus)
        : this(newStatus)
    {
        CompletionStatus = completionStatus;
    }

    public OperationStatusChangedEventArgs(OperationStatus? oldStatus, OperationStatus newStatus, OperationCompletionStatus? completionStatus)
        : this(oldStatus, newStatus)
    {
        CompletionStatus = completionStatus;
    }
}
