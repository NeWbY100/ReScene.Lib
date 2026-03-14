using ReScene.Core.IO;

namespace ReScene.Core.Diagnostics;

public class ProcessStatusChangedEventArgs : OperationStatusChangedEventArgs
{
    public RARProcess Process { get; private set; }

    public ProcessStatusChangedEventArgs(RARProcess process, OperationStatus newStatus)
        : base(newStatus)
    {
        Process = process;
    }

    public ProcessStatusChangedEventArgs(RARProcess process, OperationStatus? oldStatus, OperationStatus newStatus)
        : base(oldStatus, newStatus)
    {
        Process = process;
    }

    public ProcessStatusChangedEventArgs(RARProcess process, OperationStatus newStatus, OperationCompletionStatus completionStatus)
        : base(newStatus, completionStatus)
    {
        Process = process;
    }

    public ProcessStatusChangedEventArgs(RARProcess process, OperationStatus? oldStatus, OperationStatus newStatus, OperationCompletionStatus? completionStatus)
        : base(oldStatus, newStatus, completionStatus)
    {
        Process = process;
    }
}
