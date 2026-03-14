namespace ReScene.Core;

/// <summary>
/// Provides data for log events, including the message text and target log panel.
/// </summary>
/// <param name="message">The log message text.</param>
/// <param name="target">The target log panel. Defaults to <see cref="LogTarget.System"/>.</param>
public class LogEventArgs(string message, LogTarget target = LogTarget.System) : EventArgs
{
    /// <summary>
    /// Gets the log message text.
    /// </summary>
    public string Message { get; } = message;

    /// <summary>
    /// Gets the target log panel for this message.
    /// </summary>
    public LogTarget Target { get; } = target;
}
