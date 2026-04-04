namespace ReScene.Core.IO;

/// <summary>
/// Provides data for file compression progress events, including the file being compressed and cancellation support.
/// </summary>
public class FileCompressionOperationProgressEventArgs(long operationSize, long operationProgressed, DateTime startDateTime, string filePath) : OperationProgressEventArgs(operationSize, operationProgressed, startDateTime)
{
    /// <summary>
    /// Gets the path to the file being compressed.
    /// </summary>
    public string FilePath { get; } = filePath;

    /// <summary>
    /// Gets whether cancellation has been requested via <see cref="Cancel"/>.
    /// </summary>
    public bool Cancelled
    {
        get; private set;
    }

    /// <summary>
    /// Requests cancellation of the compression operation.
    /// </summary>
    public void Cancel()
    {
        Cancelled = true;
    }
}
