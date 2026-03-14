namespace ReScene.Core;

/// <summary>
/// A no-op logger implementation used as default when no logger is provided.
/// </summary>
public sealed class NullReSceneLogger : IReSceneLogger
{
    public static readonly NullReSceneLogger Instance = new();

    private NullReSceneLogger() { }

    public void Debug(object? sender, string message, LogTarget target = LogTarget.System) { }
    public void Information(object? sender, string message, LogTarget target = LogTarget.System) { }
    public void Warning(object? sender, string message, LogTarget target = LogTarget.System) { }
    public void Error(object? sender, string message, LogTarget target = LogTarget.System) { }
    public void Error(object? sender, Exception exception, string message, LogTarget target = LogTarget.System) { }
    public void Verbose(object? sender, string message) { }
}
