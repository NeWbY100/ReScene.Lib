namespace ReScene.Core.IO;

/// <summary>
/// Specifies the current state of a long-running operation.
/// </summary>
public enum OperationStatus
{
    /// <summary>
    /// The operation is currently executing.
    /// </summary>
    Running,

    /// <summary>
    /// The operation is temporarily paused.
    /// </summary>
    Paused,

    /// <summary>
    /// The operation has finished (check <see cref="OperationCompletionStatus"/> for outcome).
    /// </summary>
    Completed
}
