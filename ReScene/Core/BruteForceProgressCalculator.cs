using ReScene.Core.Diagnostics;

namespace ReScene.Core;

/// <summary>
/// Computes the total number of RAR version/argument combinations the brute-force loop will test,
/// used to drive progress reporting. Mirrors the iteration and skip rules of the main loop so the
/// denominator matches the work actually performed. Extracted from <see cref="Manager"/>.
/// </summary>
internal static class BruteForceProgressCalculator
{
    /// <summary>
    /// Calculates the brute-force progress size (total combinations to test), optionally scaled
    /// down when Phase 1 narrowed the candidate versions.
    /// </summary>
    /// <param name="options">
    /// The brute-force options (attribute toggles and command-line argument sets).
    /// </param>
    /// <param name="allValidRarDirectories">
    /// The full set of valid RAR directories already discovered by the Manager (rar.exe present and
    /// in a configured version range). This MUST be the unfiltered list — passing the Phase-1
    /// filtered list would make the scaling below double-count.
    /// </param>
    /// <param name="filteredVersionCount">
    /// The number of versions remaining after Phase 1 (0 when Phase 1 was skipped).
    /// </param>
    /// <param name="totalVersionCount">
    /// The total number of valid versions before Phase 1 (0 when Phase 1 was skipped).
    /// </param>
    public static int CalculateBruteForceProgressSize(
        BruteForceOptions options,
        IReadOnlyList<(string Path, int Version)> allValidRarDirectories,
        int filteredVersionCount = 0,
        int totalVersionCount = 0)
    {
        int size = 0;

        for (int a = 0; a < (options.RAROptions.SetFileArchiveAttribute == TriState.Checked ? 2 : 1); a++)
        {
            for (int b = 0; b < (options.RAROptions.SetFileNotContentIndexedAttribute == TriState.Checked ? 2 : 1); b++)
            {
                Parallel.ForEach(allValidRarDirectories, (rarVersionDirectory, s, i) =>
                {
                    int version = rarVersionDirectory.Version;

                    Parallel.ForEach(options.RAROptions.CommandLineArguments, (commandLineArguments, s2, j) =>
                    {
                        RARArchiveVersion archiveVersion = RarVersionSelector.ParseRARArchiveVersion(commandLineArguments, version);
                        List<string> filteredArguments = RarVersionSelector.FilterArgumentsForVersion(commandLineArguments, version, archiveVersion);

                        // Apply same RAR 6.x timestamp skip as the main loop
                        if (RarVersionSelector.ShouldSkipRar6TimestampCombination(version, archiveVersion, filteredArguments))
                        {
                            return;
                        }

                        Interlocked.Increment(ref size);
                    });
                });
            }
        }

        // If Phase 1 filtered the versions, scale the progress accordingly
        if (filteredVersionCount > 0 && totalVersionCount > 0 && filteredVersionCount < totalVersionCount)
        {
            // Scale the size based on the ratio of filtered versions to total versions
            size = (int)((long)size * filteredVersionCount / totalVersionCount);
        }

        return size;
    }
}
