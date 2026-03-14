using ReScene.Core.IO;

namespace ReScene.Core.Diagnostics;

public class RARCompressionStatusChangedEventArgs : OperationStatusChangedEventArgs
{
    public RARProcess Process { get; private set; }

    public RARCompressionStatusChangedEventArgs(RARProcess process, OperationStatus newStatus)
        : base(newStatus)
    {
        Process = process;
    }

    public RARCompressionStatusChangedEventArgs(RARProcess process, OperationStatus? oldStatus, OperationStatus newStatus)
        : base(oldStatus, newStatus)
    {
        Process = process;
    }

    public RARCompressionStatusChangedEventArgs(RARProcess process, OperationStatus newStatus, OperationCompletionStatus? completionStatus)
        : base(newStatus, completionStatus)
    {
        Process = process;
    }

    public RARCompressionStatusChangedEventArgs(RARProcess process, OperationStatus? oldStatus, OperationStatus newStatus, OperationCompletionStatus? completionStatus)
        : base(oldStatus, newStatus, completionStatus)
    {
        Process = process;
    }
}
