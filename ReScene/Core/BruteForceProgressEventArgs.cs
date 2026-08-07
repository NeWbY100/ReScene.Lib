using ReScene.Core.IO;

namespace ReScene.Core;

/// <summary>
/// Provides data for brute-force progress events, including the current RAR version and command-line arguments being tested.
/// </summary>
/// <param name="releaseDirectoryPath">
/// The path to the release directory being processed.
/// </param>
/// <param name="rarVersionDirectoryPath">
/// The path to the RAR version directory being tested.
/// </param>
/// <param name="rarCommandLineArguments">
/// The command-line arguments being used for the current test.
/// </param>
/// <param name="operationSize">
/// The total number of operations to perform.
/// </param>
/// <param name="operationProgressed">
/// The number of operations completed so far.
/// </param>
/// <param name="startDateTime">
/// The date and time when the operation started.
/// </param>
public class BruteForceProgressEventArgs(string releaseDirectoryPath, string rarVersionDirectoryPath, string rarCommandLineArguments, long operationSize, long operationProgressed, DateTime startDateTime) : OperationProgressEventArgs(operationSize, operationProgressed, startDateTime)
{
    /// <summary>
    /// Gets the path to the release directory being processed.
    /// </summary>
    public string ReleaseDirectoryPath { get; private set; } = releaseDirectoryPath;

    /// <summary>
    /// Gets the path to the RAR version directory currently being tested.
    /// </summary>
    public string RARVersionDirectoryPath { get; private set; } = rarVersionDirectoryPath;

    /// <summary>
    /// Gets the command-line arguments being used for the current RAR test.
    /// </summary>
    public string RARCommandLineArguments { get; private set; } = rarCommandLineArguments;

    /// <summary>
    /// Gets the description of the current brute-force phase (e.g., "Phase 1: Comment Block Filtering").
    /// </summary>
    public string PhaseDescription { get; init; } = "";

    /// <summary>
    /// True when the engine could not process this combination — most commonly because the RAR
    /// console binary failed to launch (e.g. a *nix binary without the execute bit, a DOS-era build
    /// that can't start on 64-bit Windows, or an AV block). Consumers mark the combination's row as an
    /// error rather than finalising it as a clean "No Match"; the full reason is in the Phase 2 log.
    /// </summary>
    public bool CombinationFailed { get; init; }

    /// <summary>
    /// Working directory of this combination's rar invocation — the run's prepared input-files copy.
    /// Empty for events that do not describe a concrete Phase-2 invocation (e.g. Phase-1 comment
    /// filtering), in which case consumers fall back to a switches-only command-line rendering.
    /// </summary>
    public string InputDirectoryPath { get; init; } = "";

    /// <summary>Output archive path of this combination's rar invocation; empty when unknown.</summary>
    public string OutputFilePath { get; init; } = "";

    /// <summary>
    /// The arguments the rar process is actually invoked with, space-joined with whole-token quoting
    /// for tokens containing spaces (so a shell re-splitting the string reconstructs the engine's argv
    /// — relevant for -z&lt;commentfile&gt; under a path with spaces) — the display form plus any
    /// engine-added switches (-cfg- ignore user rar config, -ma4 for RAR 5.50–6.x, -vn old volume
    /// naming, -z&lt;commentfile&gt;).
    /// Empty when unknown. Consumers rendering a runnable command line must prefer this over
    /// <see cref="RARCommandLineArguments"/> (the display form), or the pasted command can silently
    /// produce a different archive (e.g. RAR5 format where the run forced RAR4).
    /// </summary>
    public string ExecutedArguments { get; init; } = "";
}
