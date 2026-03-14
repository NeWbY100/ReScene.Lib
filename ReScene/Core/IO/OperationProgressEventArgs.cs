namespace ReScene.Core.IO;

/// <summary>
/// Provides data for operation progress events, including timing and completion estimates.
/// </summary>
public class OperationProgressEventArgs : EventArgs
{
    /// <summary>
    /// Gets the total number of units in the operation.
    /// </summary>
    public long OperationSize { get; private set; }

    /// <summary>
    /// Gets the number of units completed so far.
    /// </summary>
    public long OperationProgressed { get; private set; }

    /// <summary>
    /// Gets the number of units remaining to complete.
    /// </summary>
    public long OperationRemaining { get; private set; }

    /// <summary>
    /// Gets the date and time when the operation started.
    /// </summary>
    public DateTime StartDateTime { get; private set; }

    /// <summary>
    /// Gets the time elapsed since the operation started.
    /// </summary>
    public TimeSpan TimeElapsed { get; private set; }

    /// <summary>
    /// Gets the estimated time remaining to complete the operation.
    /// </summary>
    public TimeSpan TimeRemaining { get; private set; }

    /// <summary>
    /// Gets the average operation speed in units per second.
    /// </summary>
    public long OperationSpeed { get; private set; }

    /// <summary>
    /// Gets the estimated date and time when the operation will complete.
    /// </summary>
    public DateTime EstimatedFinishDateTime { get; private set; }

    /// <summary>
    /// Gets the completion percentage (0-100).
    /// </summary>
    public double Progress { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="OperationProgressEventArgs"/> class.
    /// </summary>
    /// <param name="operationSize">The total number of units in the operation.</param>
    /// <param name="operationProgressed">The number of units completed so far.</param>
    /// <param name="startDateTime">The date and time when the operation started.</param>
    public OperationProgressEventArgs(long operationSize, long operationProgressed, DateTime startDateTime)
    {
        if (operationSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(operationSize));
        }

        if (operationProgressed > operationSize)
        {
            throw new ArgumentOutOfRangeException(nameof(operationProgressed));
        }

        OperationSize = operationSize;
        OperationProgressed = operationProgressed;
        OperationRemaining = OperationSize - OperationProgressed;
        StartDateTime = startDateTime;
        TimeElapsed = DateTime.Now.Subtract(StartDateTime);
        TimeRemaining = OperationProgressed < 1 ? default : TimeSpan.FromSeconds(TimeElapsed.TotalSeconds / OperationProgressed * OperationRemaining);
        OperationSpeed = TimeElapsed.TotalSeconds < 1 ? OperationProgressed : (long)(OperationProgressed / TimeElapsed.TotalSeconds);
        EstimatedFinishDateTime = DateTime.Now.Add(TimeRemaining);
        Progress = 100.0 / OperationSize * OperationProgressed;
    }
}
