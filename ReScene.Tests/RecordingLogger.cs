using ReScene.Core;

namespace ReScene.Tests;

/// <summary>
/// A minimal <see cref="IReSceneLogger"/> test double that records messages by severity instead of
/// discarding or printing them, so tests can assert on what was logged (e.g. that a rollback
/// failure is actually reported, not silently swallowed).
/// </summary>
public sealed class RecordingLogger : IReSceneLogger
{
    private readonly List<string> _debugMessages = [];
    private readonly List<string> _informationMessages = [];
    private readonly List<string> _warningMessages = [];
    private readonly List<string> _errorMessages = [];

    public IReadOnlyList<string> DebugMessages => _debugMessages;
    public IReadOnlyList<string> InformationMessages => _informationMessages;
    public IReadOnlyList<string> WarningMessages => _warningMessages;
    public IReadOnlyList<string> ErrorMessages => _errorMessages;

    public void Debug(object? sender, string message, LogTarget target = LogTarget.System) => _debugMessages.Add(message);

    public void Information(object? sender, string message, LogTarget target = LogTarget.System) => _informationMessages.Add(message);

    public void Warning(object? sender, string message, LogTarget target = LogTarget.System) => _warningMessages.Add(message);

    public void Error(object? sender, string message, LogTarget target = LogTarget.System) => _errorMessages.Add(message);

    public void Error(object? sender, Exception exception, string message, LogTarget target = LogTarget.System) => _errorMessages.Add($"{message}: {exception.Message}");

    public void Verbose(object? sender, string message)
    {
        // Not asserted on by any current test; intentionally discarded.
    }
}
