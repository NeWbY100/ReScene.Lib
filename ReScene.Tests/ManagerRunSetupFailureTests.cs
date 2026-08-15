using ReScene.Core;
using ReScene.Core.IO;

namespace ReScene.Tests;

/// <summary>
/// Tests for <see cref="Manager.BruteForceRARVersionAsync"/>'s run-setup failure paths — the
/// early exits before any rar.exe launches (installations root missing, installations root
/// empty, input-file validation failure). Each must return a failed
/// <see cref="BruteForceRunResult"/> rather than throw, AND fire a terminal
/// <see cref="Manager.BruteForceStatusChanged"/> event: callers key their busy state off that
/// event, so a setup failure that returns (or throws) without one strands them at
/// <see cref="OperationStatus.Running"/>.
/// </summary>
public class ManagerRunSetupFailureTests : TempDirTestBase
{
    private readonly string _releaseDir;
    private readonly string _outputDir;

    public ManagerRunSetupFailureTests()
    {
        _releaseDir = Path.Combine(TempDir, "release");
        _outputDir = Path.Combine(TempDir, "output");
        Directory.CreateDirectory(_releaseDir);
        Directory.CreateDirectory(_outputDir);
    }

    private static List<BruteForceStatusChangedEventArgs> RecordStatusEvents(Manager manager)
    {
        List<BruteForceStatusChangedEventArgs> events = [];
        manager.BruteForceStatusChanged += (_, e) => events.Add(e);
        return events;
    }

    // The full transition, not just "a terminal event exists": exactly two events — the initial
    // Running (no completion yet), then the terminal Running→Completed(Error) — the same shape
    // the custom-packer and preflight-failure paths already produce.
    private static void AssertSingleTerminalErrorStatus(IReadOnlyList<BruteForceStatusChangedEventArgs> events)
    {
        Assert.Equal(2, events.Count);

        Assert.Equal(OperationStatus.Running, events[0].NewStatus);
        Assert.Null(events[0].CompletionStatus);

        BruteForceStatusChangedEventArgs terminal = events[1];
        Assert.Equal(OperationStatus.Running, terminal.OldStatus);
        Assert.Equal(OperationStatus.Completed, terminal.NewStatus);
        Assert.Equal(OperationCompletionStatus.Error, terminal.CompletionStatus);
    }

    [Fact]
    public async Task BruteForceRARVersionAsync_MissingInstallationsRoot_FailsWithTerminalStatusInsteadOfThrowing()
    {
        string missingRoot = Path.Combine(TempDir, "no-such-winrar-root");
        var options = new BruteForceOptions(missingRoot, _releaseDir, _outputDir);

        using var manager = new Manager();
        List<BruteForceStatusChangedEventArgs> events = RecordStatusEvents(manager);

        BruteForceRunResult result = await manager.BruteForceRARVersionAsync(options);

        Assert.False(result.Success);
        Assert.Null(result.Combo);
        AssertSingleTerminalErrorStatus(events);
    }

    [Fact]
    public async Task BruteForceRARVersionAsync_EmptyInstallationsRoot_FailsWithTerminalStatus()
    {
        string emptyRoot = Path.Combine(TempDir, "winrar-root-empty");
        Directory.CreateDirectory(emptyRoot);
        var options = new BruteForceOptions(emptyRoot, _releaseDir, _outputDir);

        using var manager = new Manager();
        List<BruteForceStatusChangedEventArgs> events = RecordStatusEvents(manager);

        BruteForceRunResult result = await manager.BruteForceRARVersionAsync(options);

        Assert.False(result.Success);
        AssertSingleTerminalErrorStatus(events);
    }

    [Fact]
    public async Task BruteForceRARVersionAsync_InputFileValidationFailure_FailsWithTerminalStatus()
    {
        // One (invalid) version folder gets past the empty-root check; the SRR's archive file
        // list names a file the release directory does not contain, so validation fails.
        string root = Path.Combine(TempDir, "winrar-root");
        Directory.CreateDirectory(Path.Combine(root, "winrar-500"));
        var options = new BruteForceOptions(root, _releaseDir, _outputDir);
        options.RAROptions.ArchiveFilePaths.Add("missing-volume.rar");

        using var manager = new Manager();
        List<BruteForceStatusChangedEventArgs> events = RecordStatusEvents(manager);

        BruteForceRunResult result = await manager.BruteForceRARVersionAsync(options);

        Assert.False(result.Success);
        AssertSingleTerminalErrorStatus(events);
    }
}
