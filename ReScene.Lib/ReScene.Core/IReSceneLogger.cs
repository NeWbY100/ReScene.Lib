namespace ReScene.Core;

public interface IReSceneLogger
{
    void Debug(object? sender, string message, LogTarget target = LogTarget.System);
    void Information(object? sender, string message, LogTarget target = LogTarget.System);
    void Warning(object? sender, string message, LogTarget target = LogTarget.System);
    void Error(object? sender, string message, LogTarget target = LogTarget.System);
    void Error(object? sender, Exception exception, string message, LogTarget target = LogTarget.System);
    void Verbose(object? sender, string message);
}
