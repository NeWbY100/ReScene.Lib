using ReScene.Core.IO;

namespace ReScene.Core;

/// <summary>
/// Provides data for brute-force progress events, including the current RAR version and command-line arguments being tested.
/// </summary>
/// <param name="releaseDirectoryPath">The path to the release directory being processed.</param>
/// <param name="rarVersionDirectoryPath">The path to the RAR version directory being tested.</param>
/// <param name="rarCommandLineArguments">The command-line arguments being used for the current test.</param>
/// <param name="operationSize">The total number of operations to perform.</param>
/// <param name="operationProgressed">The number of operations completed so far.</param>
/// <param name="startDateTime">The date and time when the operation started.</param>
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
}
