namespace ReScene.Core;

/// <summary>
/// Defines logging methods for the ReScene library at various severity levels.
/// </summary>
public interface IReSceneLogger
{
    /// <summary>
    /// Logs a debug-level message.
    /// </summary>
    void Debug(object? sender, string message, LogTarget target = LogTarget.System);

    /// <summary>
    /// Logs an informational message.
    /// </summary>
    void Information(object? sender, string message, LogTarget target = LogTarget.System);

    /// <summary>
    /// Logs a warning message.
    /// </summary>
    void Warning(object? sender, string message, LogTarget target = LogTarget.System);

    /// <summary>
    /// Logs an error message.
    /// </summary>
    void Error(object? sender, string message, LogTarget target = LogTarget.System);

    /// <summary>
    /// Logs an error message with an associated exception.
    /// </summary>
    void Error(object? sender, Exception exception, string message, LogTarget target = LogTarget.System);

    /// <summary>
    /// Logs a verbose-level message to the system log.
    /// </summary>
    void Verbose(object? sender, string message);
}
