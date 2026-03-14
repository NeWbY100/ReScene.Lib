using ReScene.Core.IO;

namespace ReScene.Core.Diagnostics;

public class RARProcessStatusChangedEventArgs : OperationStatusChangedEventArgs
{
    public RARProcess Process { get; private set; }

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
