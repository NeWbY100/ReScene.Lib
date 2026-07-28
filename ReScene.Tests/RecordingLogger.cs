using ReScene.Core;

namespace ReScene.Tests;

/// <summary>
/// A minimal <see cref="IReSceneLogger"/> test double that records messages by severity instead of
/// discarding or printing them, so tests can assert on what was logged (e.g. that a rollback
/// failure is actually reported, not silently swallowed, or that a lifecycle test's expected
/// marker line has appeared before proceeding).
/// </summary>
public sealed class RecordingLogger : IReSceneLogger
{
    /// <summary>One recorded log call: its severity, target log panel, and message text.</summary>
    public readonly record struct LogEntry(string Level, LogTarget Target, string Message);

    private readonly List<string> _debugMessages = [];
    private readonly List<string> _informationMessages = [];
    private readonly List<string> _warningMessages = [];
    private readonly List<string> _errorMessages = [];
    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<string> DebugMessages => _debugMessages;
    public IReadOnlyList<string> InformationMessages => _informationMessages;
    public IReadOnlyList<string> WarningMessages => _warningMessages;
    public IReadOnlyList<string> ErrorMessages => _errorMessages;

    /// <summary>Every recorded call, across all severities, in call order.</summary>
    public IReadOnlyList<LogEntry> Entries => _entries;

    public void Debug(object? sender, string message, LogTarget target = LogTarget.System)
    {
        _debugMessages.Add(message);
        _entries.Add(new LogEntry("Debug", target, message));
    }

    public void Information(object? sender, string message, LogTarget target = LogTarget.System)
    {
        _informationMessages.Add(message);
        _entries.Add(new LogEntry("Information", target, message));
    }

    public void Warning(object? sender, string message, LogTarget target = LogTarget.System)
    {
        _warningMessages.Add(message);
        _entries.Add(new LogEntry("Warning", target, message));
    }

    public void Error(object? sender, string message, LogTarget target = LogTarget.System)
    {
        _errorMessages.Add(message);
        _entries.Add(new LogEntry("Error", target, message));
    }

    public void Error(object? sender, Exception exception, string message, LogTarget target = LogTarget.System)
    {
        string combined = $"{message}: {exception.Message}";
        _errorMessages.Add(combined);
        _entries.Add(new LogEntry("Error", target, combined));
    }

    public void Verbose(object? sender, string message)
    {
        // Not asserted on by any current test; intentionally discarded.
    }

    /// <summary>Counts recorded entries (any severity) whose message contains <paramref name="substring"/>.</summary>
    public int Count(string substring) => _entries.Count(e => e.Message.Contains(substring, StringComparison.Ordinal));
}
