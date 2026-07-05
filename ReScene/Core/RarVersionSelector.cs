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
    /// Tries to parse the RAR version number from a directory name (e.g., "winrar-560" → 560).
    /// </summary>
    /// <param name="rarVersionDirectoryName">The WinRAR version directory name.</param>
    /// <param name="version">When this method returns <see langword="true"/>, the normalised version number; otherwise 0.</param>
    /// <returns><see langword="true"/> if the version was successfully parsed; otherwise <see langword="false"/>.</returns>
    public static bool TryParseRARVersion(string rarVersionDirectoryName, out int version)
        => TryParseRARVersion(rarVersionDirectoryName, out version, out _);

    /// <summary>
    /// Tries to parse the RAR version number from a directory name, also returning the variant tag —
    /// the remainder of the name after the version digits, trimmed of leading separators (e.g.,
    /// "winrar-250-beta1" → 250 + "beta1"; "winrar-250" → 250 + ""). Distinguishes folders that
    /// parse to the same version (betas, locale builds, …).
    /// </summary>
    /// <param name="rarVersionDirectoryName">The WinRAR version directory name.</param>
    /// <param name="version">When this method returns <see langword="true"/>, the normalised version number; otherwise 0.</param>
    /// <param name="variantTag">When this method returns <see langword="true"/>, the variant tag (empty when none); otherwise empty.</param>
    /// <returns><see langword="true"/> if the version was successfully parsed; otherwise <see langword="false"/>.</returns>
    public static bool TryParseRARVersion(string rarVersionDirectoryName, out int version, out string variantTag)
    {
        version = 0;
        variantTag = string.Empty;
        Match versionMatch = _rarVersionRegex.Match(rarVersionDirectoryName);
        if (!versionMatch.Success || !int.TryParse(versionMatch.Groups[1].Value, out int versionNumber))
        {
            return false;
        }

        version = versionNumber < 100 ? versionNumber * 10 : versionNumber;

        // The tag is everything after the version capture (not the whole match — the regex may also
        // consume a "b<digits>" beta suffix, which we want to keep in the tag).
        Group versionGroup = versionMatch.Groups[1];
        variantTag = rarVersionDirectoryName[(versionGroup.Index + versionGroup.Length)..]
            .TrimStart('-', '_', '.', ' ');
        return true;
    }

    /// <summary>
    /// Parses the RAR version number from a directory name (e.g., "winrar-560" returns 560).
    /// </summary>
    /// <param name="rarVersionDirectoryName">
    /// The WinRAR version directory name.
    /// </param>
    /// <returns>
    /// The parsed version number, normalized to three digits.
    /// </returns>
    /// <exception cref="FormatException">Thrown when the version cannot be parsed from <paramref name="rarVersionDirectoryName"/>.</exception>
    public static int ParseRARVersion(string rarVersionDirectoryName)
    {
        if (!TryParseRARVersion(rarVersionDirectoryName, out int version))
        {
            throw new FormatException(
                $"WinRAR version not found in directory name:{Environment.NewLine}{rarVersionDirectoryName}");
        }

        return version;
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
            < RarVersionThresholds.Rar5FormatMinimum => RARArchiveVersion.RAR4,
            < RarVersionThresholds.Rar7FormatMinimum => RARArchiveVersion.RAR5,
            >= RarVersionThresholds.Rar7FormatMinimum => RARArchiveVersion.RAR7
        };
    }

    /// <summary>
    /// Filters candidate RAR command-line arguments down to those applicable to the given RAR
    /// version and archive format — honoring each argument's minimum/maximum version and its
    /// required archive version — and returns the argument strings.
    /// </summary>
    public static List<string> FilterArgumentsForVersion(IEnumerable<RARCommandLineArgument> commandLineArguments, int version, RARArchiveVersion archiveVersion)
        => [.. commandLineArguments
            .Where(a => version >= a.MinimumVersion
                && (!a.MaximumVersion.HasValue || version <= a.MaximumVersion.Value)
                && (!a.ArchiveVersion.HasValue || a.ArchiveVersion.Value.HasFlag(archiveVersion)))
            .Select(a => a.Argument)];

    /// <summary>
    /// RAR 6.x does not honor timestamp options (-tsc/-tsa) when producing RAR4-format archives,
    /// so those combinations must be skipped to avoid wrong extended-time header flags. RAR 7.x is
    /// excluded because it only creates RAR7 archives and handles timestamps natively.
    /// </summary>
    public static bool ShouldSkipRar6TimestampCombination(int version, RARArchiveVersion archiveVersion, IReadOnlyList<string> filteredArguments)
    {
        bool hasTimestampOptions = filteredArguments.Any(a => a.StartsWith("-ts", StringComparison.Ordinal));
        bool isRAR4Format = archiveVersion == RARArchiveVersion.RAR4
            || (version >= 550 && version < RarVersionThresholds.Rar7FormatMinimum && !filteredArguments.Contains("-ma5"));
        return version >= 600 && version < RarVersionThresholds.Rar7FormatMinimum && isRAR4Format && hasTimestampOptions;
    }

    /// <summary>
    /// Filters the given RAR version directories down to those containing a <c>rar.exe</c> and
    /// whose parsed version falls within one of the configured version ranges, returning each
    /// directory paired with its parsed version.
    /// </summary>
    public static List<(string Path, int Version)> GetValidRarDirectories(string[] directories, BruteForceOptions options, IReSceneLogger logger, object logSource)
    {
        var validDirectories = new List<(string Path, int Version)>();

        // Folder-name allow-list applied ON TOP OF the version ranges: when non-empty, only these
        // folders run — so same-version variant folders (e.g. winrar-390 vs winrar-390-beta1, both
        // version 390) can be excluded individually. Empty means no folder filter.
        HashSet<string>? allowedFolders = options.RAROptions.AllowedVersionFolders.Count > 0
            ? new HashSet<string>(options.RAROptions.AllowedVersionFolders, StringComparer.OrdinalIgnoreCase)
            : null;

        foreach (string dir in directories)
        {
            string rarExeFilePath = Path.Combine(dir, "rar.exe");
            if (!File.Exists(rarExeFilePath))
            {
                logger.Information(logSource, $"rar.exe not found in {dir}");
                continue;
            }

            string dirName = Path.GetFileName(dir);
            if (!TryParseRARVersion(dirName, out int version))
            {
                logger.Information(logSource, $"Unrecognised WinRAR version folder name: {dir}");
                continue;
            }

            if (!options.RAROptions.RARVersions.Any(r => r.InRange(version)))
            {
                continue;
            }

            if (allowedFolders is not null && !allowedFolders.Contains(dirName))
            {
                logger.Information(logSource, $"WinRAR version folder not in selection: {dirName}");
                continue;
            }

            validDirectories.Add((dir, version));
        }

        return validDirectories;
    }
}
