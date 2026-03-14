namespace ReScene.Core.IO;

public class FileCompressionOperationProgressEventArgs(long operationSize, long operationProgressed, DateTime startDateTime, string filePath) : OperationProgressEventArgs(operationSize, operationProgressed, startDateTime)
{
    public string FilePath { get; } = filePath;

    public bool Cancelled { get; private set; }

    public void Cancel()
    {
        Cancelled = true;
    }
}
