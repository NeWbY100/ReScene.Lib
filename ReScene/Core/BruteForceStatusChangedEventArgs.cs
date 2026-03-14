using ReScene.Core.IO;

namespace ReScene.Core;

/// <summary>
/// Provides data for brute-force status change events, indicating when the operation state changes
/// (e.g., from running to completed or cancelled).
/// </summary>
public class BruteForceStatusChangedEventArgs : OperationStatusChangedEventArgs
{
    public BruteForceStatusChangedEventArgs(OperationStatus newStatus) : base(newStatus)
    {
    }

    public BruteForceStatusChangedEventArgs(OperationStatus? oldStatus, OperationStatus newStatus) : base(oldStatus, newStatus)
    {
    }

    public BruteForceStatusChangedEventArgs(OperationStatus newStatus, OperationCompletionStatus? completionStatus) : base(newStatus, completionStatus)
    {
    }

    public BruteForceStatusChangedEventArgs(OperationStatus? oldStatus, OperationStatus newStatus, OperationCompletionStatus? completionStatus) : base(oldStatus, newStatus, completionStatus)
    {
    }
}
