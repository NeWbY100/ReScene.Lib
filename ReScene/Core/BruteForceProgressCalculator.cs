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
    public static int CalculateBruteForceProgressSize(BruteForceOptions options, int filteredVersionCount = 0, int totalVersionCount = 0)
    {
        int size = 0;

        DirectoryInfo directoryInfo = new(options.ReleaseDirectoryPath);
        string[] rarVersionDirectories = Directory.GetDirectories(options.RARInstallationsDirectoryPath);
        for (int a = 0; a < (options.RAROptions.SetFileArchiveAttribute == TriState.Checked ? 2 : 1); a++)
        {
            for (int b = 0; b < (options.RAROptions.SetFileNotContentIndexedAttribute == TriState.Checked ? 2 : 1); b++)
            {
                Parallel.ForEach(rarVersionDirectories, (rarVersionDirectoryPath, s, i) =>
                {
                    string rarExeFilePath = Path.Combine(rarVersionDirectoryPath, "rar.exe");
                    if (!File.Exists(rarExeFilePath))
                    {
                        return;
                    }

                    string rarVersionDirectoryName = Path.GetFileName(rarVersionDirectoryPath);
                    int version = RarVersionSelector.ParseRARVersion(rarVersionDirectoryName);
                    if (!options.RAROptions.RARVersions.Any(r => r.InRange(version)))
                    {
                        return;
                    }

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
