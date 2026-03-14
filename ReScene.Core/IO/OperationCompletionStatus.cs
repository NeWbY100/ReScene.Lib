namespace ReScene.Core.IO;

/// <summary>
/// Specifies the outcome of a completed operation.
/// </summary>
public enum OperationCompletionStatus
{
    /// <summary>
    /// The operation was cancelled by the user.
    /// </summary>
    Cancelled,

    /// <summary>
    /// The operation failed due to an error.
    /// </summary>
    Error,

    /// <summary>
    /// The operation completed successfully.
    /// </summary>
    Success
}
