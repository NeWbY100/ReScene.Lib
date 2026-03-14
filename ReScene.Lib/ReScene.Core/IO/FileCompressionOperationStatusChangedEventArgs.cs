namespace ReScene.Core.IO;

public class FileCompressionOperationStatusChangedEventArgs : OperationStatusChangedEventArgs
{
    public string CompressedFilePath { get; }

    public FileCompressionOperationStatusChangedEventArgs(OperationStatus newStatus, string compressedFilePath) : base(newStatus)
    {
        CompressedFilePath = compressedFilePath;
    }

    public FileCompressionOperationStatusChangedEventArgs(OperationStatus? oldStatus, OperationStatus newStatus, string compressedFilePath) : base(oldStatus, newStatus)
    {
        CompressedFilePath = compressedFilePath;
    }

    public FileCompressionOperationStatusChangedEventArgs(OperationStatus newStatus, OperationCompletionStatus? completionStatus, string compressedFilePath) : base(newStatus, completionStatus)
    {
        CompressedFilePath = compressedFilePath;
    }

    public FileCompressionOperationStatusChangedEventArgs(OperationStatus? oldStatus, OperationStatus newStatus, OperationCompletionStatus? completionStatus, string compressedFilePath) : base(oldStatus, newStatus, completionStatus)
    {
        CompressedFilePath = compressedFilePath;
    }
}
