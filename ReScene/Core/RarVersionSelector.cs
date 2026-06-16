using System.Text.RegularExpressions;
using ReScene.Core.Diagnostics;

namespace ReScene.Core;

/// <summary>
/// Discovers and filters the RAR version directories and command-line argument combinations used
/// by the brute-force orchestrator: parsing the version number from a directory name, mapping
/// arguments to an archive format, filtering arguments by version/format applicability, and the
/// RAR 6.x timestamp-skip rule. Extracted from <see cref="Manager"/>.
/// </summary>
internal static partial class RarVersionSelector
{
    [GeneratedRegex("(?:win)?(?:rar|wr)(?:-x64|-x32)?-?(\\d+)(?:b\\d+)?", RegexOptions.IgnoreCase)]
    private static partial Regex Generated_rarVersionRegex();

    private static readonly Regex _rarVersionRegex = Generated_rarVersionRegex();

    /// <summary>
    /// Parses the RAR version number from a directory name (e.g., "winrar-560" returns 560).
    /// </summary>
    /// <param name="rarVersionDirectoryName">
    /// The WinRAR version directory name.
    /// </param>
    /// <returns>
    /// The parsed version number, normalized to three digits.
    /// </returns>
    public static int ParseRARVersion(string rarVersionDirectoryName)
    {
        Match versionMatch = _rarVersionRegex.Match(rarVersionDirectoryName);
        if (!versionMatch.Success)
        {
            throw new FormatException($"WinRAR version not found in directory name:{Environment.NewLine}{rarVersionDirectoryName}");
        }

        string versionNumberStr = versionMatch.Groups[1].Value;
        if (!int.TryParse(versionNumberStr, out int versionNumber))
        {
            throw new InvalidDataException($"WinRAR version found in directory name is invalid:{Environment.NewLine}{versionNumberStr}");
        }

        return versionNumber switch
        {
            < 100 => versionNumber * 10,
            _ => versionNumber
        };
    }

    /// <summary>
    /// Determines the RAR archive format version from command-line arguments and the RAR version number.
    /// </summary>
    /// <param name="commandLineArguments">
    /// The RAR command-line arguments to check.
    /// </param>
    /// <param name="version">
    /// The RAR version number.
    /// </param>
    /// <returns>
    /// The detected archive format version.
    /// </returns>
    public static RARArchiveVersion ParseRARArchiveVersion(RARCommandLineArgument[] commandLineArguments, int version)
    {
        RARCommandLineArgument? archiveVersionCommandLine = commandLineArguments.FirstOrDefault(a => a.Argument is "-ma4" or "-ma5");
        if (archiveVersionCommandLine != null)
        {
            return archiveVersionCommandLine.Argument switch
            {
                "-ma4" => RARArchiveVersion.RAR4,
                "-ma5" => RARArchiveVersion.RAR5,
                _ => throw new IndexOutOfRangeException($"RAR archive version command line argument out of range: {archiveVersionCommandLine.Argument}")
            };
        }

        return version switch
        {
            < 500 => RARArchiveVersion.RAR4,
            < 700 => RARArchiveVersion.RAR5,
            >= 700 => RARArchiveVersion.RAR7
        };
    }

    /// <summary>
    /// Filters candidate RAR command-line arguments down to those applicable to the given RAR
    /// version and archive format — honoring each argument's minimum/maximum version and its
    /// required archive version — and returns the argument strings.
    /// </summary>
    public static List<string> FilterArgumentsForVersion(IEnumerable<RARCommandLineArgument> commandLineArguments, int version, RARArchiveVersion archiveVersion)
        => commandLineArguments
            .Where(a => version >= a.MinimumVersion
                && (!a.MaximumVersion.HasValue || version <= a.MaximumVersion.Value)
                && (!a.ArchiveVersion.HasValue || a.ArchiveVersion.Value.HasFlag(archiveVersion)))
            .Select(a => a.Argument)
            .ToList();

    /// <summary>
    /// RAR 6.x does not honor timestamp options (-tsc/-tsa) when producing RAR4-format archives,
    /// so those combinations must be skipped to avoid wrong extended-time header flags. RAR 7.x is
    /// excluded because it only creates RAR7 archives and handles timestamps natively.
    /// </summary>
    public static bool ShouldSkipRar6TimestampCombination(int version, RARArchiveVersion archiveVersion, IReadOnlyList<string> filteredArguments)
    {
        bool hasTimestampOptions = filteredArguments.Any(a => a.StartsWith("-ts", StringComparison.Ordinal));
        bool isRAR4Format = archiveVersion == RARArchiveVersion.RAR4
            || (version >= 550 && version < 700 && !filteredArguments.Contains("-ma5"));
        return version >= 600 && version < 700 && isRAR4Format && hasTimestampOptions;
    }

    /// <summary>
    /// Filters the given RAR version directories down to those containing a <c>rar.exe</c> and
    /// whose parsed version falls within one of the configured version ranges, returning each
    /// directory paired with its parsed version.
    /// </summary>
    public static List<(string Path, int Version)> GetValidRarDirectories(string[] directories, BruteForceOptions options, IReSceneLogger logger, object logSource)
    {
        var validDirectories = new List<(string Path, int Version)>();

        foreach (string dir in directories)
        {
            string rarExeFilePath = Path.Combine(dir, "rar.exe");
            if (!File.Exists(rarExeFilePath))
            {
                logger.Information(logSource, $"rar.exe not found in {dir}");
                continue;
            }

            string dirName = Path.GetFileName(dir);
            int version = ParseRARVersion(dirName);

            if (options.RAROptions.RARVersions.Any(r => r.InRange(version)))
            {
                validDirectories.Add((dir, version));
            }
        }

        return validDirectories;
    }
}
