using ReScene.Core.Diagnostics;

namespace ReScene.Core;

/// <summary>
/// One brute-force candidate's immutable identity and composed command line: the version under
/// test, the paths it reads and writes, and the exact arguments it will run with. Composed once at
/// the top of a candidate iteration and passed to every phase, so no phase recomputes a path or a
/// joined argument string — notably the assembly output directory, which the pre-refactor code
/// derived identically in two places, and the candidate slug, which it derived late.
/// <para>
/// Every collection member is exposed as <see cref="IReadOnlyList{T}"/> so the "frozen" claim holds
/// against the phases this record is handed to: the loop still builds them as concrete lists, but a
/// helper receiving a context cannot append to a candidate's argument set.
/// </para>
/// </summary>
/// <param name="Version">The rar version under test (e.g. 560).</param>
/// <param name="VersionDirectoryPath">The version's installation directory.</param>
/// <param name="VersionDirectoryName">That directory's leaf name, used in log lines and output names.</param>
/// <param name="RarExeFilePath">The resolved rar executable for this version.</param>
/// <param name="InputFilesDir">The prepared input directory the candidate packs from.</param>
/// <param name="RarOutputDir">The run's output subdirectory that receives produced archives.</param>
/// <param name="RarFilePath">This candidate's intended output archive path.</param>
/// <param name="CandidateSlug">The output archive's file name without extension.</param>
/// <param name="AssemblyDir">Where SRR-guided assembly writes this candidate's assembled set.</param>
/// <param name="CommandLineArguments">The unfiltered switch combination this candidate represents.</param>
/// <param name="FilteredArguments">Those switches after version/format filtering.</param>
/// <param name="DisplayArguments">The filtered switches joined for display and log lines.</param>
/// <param name="FinalArguments">The actual argument list, including engine-added switches.</param>
/// <param name="ExecutedArguments">Those final arguments joined and quoted for a runnable command line.</param>
/// <param name="InputTail">Explicit input operands, or <see langword="null"/> for rar's own mask.</param>
/// <param name="InputFileArguments">The input tail rendered for progress events; empty for a mask run.</param>
/// <param name="TotalProgressSize">The run's progress denominator, carried so progress rows need no extra parameter.</param>
/// <param name="BruteForceStartDateTime">The run's start instant, carried for the same reason.</param>
internal sealed record CandidateContext(
    int Version,
    string VersionDirectoryPath,
    string VersionDirectoryName,
    string RarExeFilePath,
    string InputFilesDir,
    string RarOutputDir,
    string RarFilePath,
    string CandidateSlug,
    string AssemblyDir,
    IReadOnlyList<RARCommandLineArgument> CommandLineArguments,
    IReadOnlyList<string> FilteredArguments,
    string DisplayArguments,
    IReadOnlyList<string> FinalArguments,
    string ExecutedArguments,
    IReadOnlyList<string>? InputTail,
    string InputFileArguments,
    int TotalProgressSize,
    DateTime BruteForceStartDateTime);
