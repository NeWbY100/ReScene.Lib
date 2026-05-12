namespace ReScene.Core;

/// <summary>
/// Provides data for the <see cref="Manager.TimestampPreservationFailed"/> event,
/// raised when copying the source file's mtime/ctime/atime onto the destination
/// file in the input working directory fails.
/// </summary>
public class TimestampPreservationFailedEventArgs : EventArgs
{
    /// <summary>
    /// The destination path whose timestamps could not be set.
    /// </summary>
    public string DestinationPath { get; init; } = "";

    /// <summary>
    /// The exception message from the failed timestamp call.
    /// </summary>
    public string ErrorMessage { get; init; } = "";
}
