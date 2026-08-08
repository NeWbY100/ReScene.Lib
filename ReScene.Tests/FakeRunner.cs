using ReScene.Core;
using ReScene.Core.Diagnostics;

namespace ReScene.Tests;

/// <summary>
/// Test double for <see cref="IRARProcessRunner"/>: records every launch and lets the test decide
/// exactly when (and how) each one "exits", instead of ever running a real rar process. This is
/// what makes the producer-observation invariant provable — a real process's completion timing
/// can't be held open on demand.
/// </summary>
internal sealed class FakeRunner : IRARProcessRunner
{
    /// <summary>One recorded call to <see cref="RunAsync"/>.</summary>
    public sealed class Launch(string outputFilePath, IReadOnlyList<string> arguments, IReadOnlyList<string>? inputPaths)
    {
        public string OutputFilePath { get; } = outputFilePath;

        /// <summary>The switches this launch was called with (pre-RARProcess mask/tail composition) —
        /// e.g. proves/disproves <c>-ds</c> for the ordered-input engine tests.</summary>
        public IReadOnlyList<string> Arguments { get; } = arguments;

        /// <summary>The <c>inputPaths</c> this launch was called with — <see langword="null"/> for a
        /// mask run, or the SRR-ordered explicit file list Manager composed for this candidate.</summary>
        public IReadOnlyList<string>? InputPaths { get; } = inputPaths;

        // Exit is completed ONLY by the test — cancellation must NOT complete it, or every
        // Cancel() path would hand Manager an already-finished task and the latch could not
        // distinguish observation from abandonment.
        public TaskCompletionSource<int> Exit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Resolves when Manager cancels this launch's token — the signal a test awaits
        /// to know Manager has reached (and started) its cleanup/observation for this launch.</summary>
        public TaskCompletionSource CancellationRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Every launch made so far, in order.</summary>
    public List<Launch> Launches { get; } = [];

    /// <summary>Invoked synchronously from <see cref="RunAsync"/> for every launch, before it
    /// returns — the test writes carrier volume file(s) here, ahead of the (possibly held)
    /// <see cref="Launch.Exit"/>.</summary>
    public Action<Launch>? OnLaunch { get; set; }

    public Task<int> RunAsync(string rarExePath, string inputDirectory, string outputFilePath,
        IEnumerable<string> arguments, LogTarget logTarget,
        Action<RARProcess>? onCreated, CancellationToken cancellationToken,
        IReadOnlyList<string>? inputPaths = null)
    {
        var launch = new Launch(outputFilePath, [.. arguments], inputPaths);
        cancellationToken.Register(() => launch.CancellationRequested.TrySetResult());
        Launches.Add(launch);
        OnLaunch?.Invoke(launch);
        return launch.Exit.Task;
    }
}
