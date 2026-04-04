namespace ReScene.Core.IO;

/// <summary>
/// Provides data for file compression status change events, including the path to the compressed file.
/// </summary>
public class FileCompressionOperationStatusChangedEventArgs : OperationStatusChangedEventArgs
{
    /// <summary>
    /// Gets the path to the compressed file associated with this status change.
    /// </summary>
    public string CompressedFilePath
    {
        get;
    }

    /// <summary>
    /// Initializes a new instance with the specified new status and compressed file path.
    /// </summary>
    /// <param name="newStatus">The new operation status.</param>
    /// <param name="compressedFilePath">The path to the compressed file.</param>
    public FileCompressionOperationStatusChangedEventArgs(OperationStatus newStatus, string compressedFilePath) : base(newStatus)
    {
        CompressedFilePath = compressedFilePath;
    }

    /// <summary>
    /// Initializes a new instance with the specified old status, new status, and compressed file path.
    /// </summary>
    /// <param name="oldStatus">The previous operation status.</param>
    /// <param name="newStatus">The new operation status.</param>
    /// <param name="compressedFilePath">The path to the compressed file.</param>
    public FileCompressionOperationStatusChangedEventArgs(OperationStatus? oldStatus, OperationStatus newStatus, string compressedFilePath) : base(oldStatus, newStatus)
    {
        CompressedFilePath = compressedFilePath;
    }

    /// <summary>
    /// Initializes a new instance with the specified new status, completion status, and compressed file path.
    /// </summary>
    /// <param name="newStatus">The new operation status.</param>
    /// <param name="completionStatus">The completion outcome, if applicable.</param>
    /// <param name="compressedFilePath">The path to the compressed file.</param>
    public FileCompressionOperationStatusChangedEventArgs(OperationStatus newStatus, OperationCompletionStatus? completionStatus, string compressedFilePath) : base(newStatus, completionStatus)
    {
        CompressedFilePath = compressedFilePath;
    }

    /// <summary>
    /// Initializes a new instance with the specified old status, new status, completion status, and compressed file path.
    /// </summary>
    /// <param name="oldStatus">The previous operation status.</param>
    /// <param name="newStatus">The new operation status.</param>
    /// <param name="completionStatus">The completion outcome, if applicable.</param>
    /// <param name="compressedFilePath">The path to the compressed file.</param>
    public FileCompressionOperationStatusChangedEventArgs(OperationStatus? oldStatus, OperationStatus newStatus, OperationCompletionStatus? completionStatus, string compressedFilePath) : base(oldStatus, newStatus, completionStatus)
    {
        CompressedFilePath = compressedFilePath;
    }
}
