using System.Text.RegularExpressions;

namespace ReScene.RAR;

/// <summary>
/// Computes RAR volume successor paths for both old-style (.rar/.r00/.r01)
/// and new-style (.partNN.rar) naming conventions.
/// </summary>
internal static partial class RARVolumeNaming
{
    /// <summary>
    /// Computes the path of the next volume in the set, or <see langword="null"/>
    /// when the input path doesn't match a recognized volume pattern.
    /// </summary>
    public static string? GetNextVolumePath(string currentPath, bool isOldNaming)
    {
        return isOldNaming ? GetNextOldStyleVolume(currentPath) : GetNextNewStyleVolume(currentPath);
    }

    /// <summary>
    /// Extracts the archive base name from a volume file name by stripping a
    /// <c>.partNN</c> segment when the file is a <c>.partNN.rar</c> volume. For all
    /// other names (including old-style <c>.rNN</c> volumes and plain <c>.rar</c>)
    /// the file name without its extension is returned.
    /// </summary>
    /// <param name="fileName">
    /// A file name (not a full path), e.g. <c>release.part01.rar</c> or <c>release.rar</c>.
    /// </param>
    public static string GetBaseName(string fileName)
    {
        if (fileName.Contains(".part", StringComparison.OrdinalIgnoreCase)
            && fileName.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
        {
            int partIndex = fileName.IndexOf(".part", StringComparison.OrdinalIgnoreCase);
            if (partIndex > 0)
            {
                return fileName[..partIndex];
            }
        }

        return Path.GetFileNameWithoutExtension(fileName);
    }

    /// <summary>
    /// Enumerates all RAR volume files belonging to the archive set with the given
    /// <paramref name="baseName"/> in <paramref name="directory"/>, ordered by name.
    /// Handles both new-style (<c>base.partNN.rar</c>) and old-style
    /// (<c>base.rar</c> + <c>base.rNN</c>) naming. Returns an empty list when none exist.
    /// </summary>
    public static List<string> EnumerateVolumes(string directory, string baseName)
    {
        var files = new List<string>();

        // New-style: base.partXX.rar
        string[] partFiles = Directory.GetFiles(directory, $"{baseName}.part*.rar");
        if (partFiles.Length > 0)
        {
            files.AddRange(partFiles.OrderBy(f => f));
            return files;
        }

        // Old-style: base.rar + base.rXX
        string mainRar = Path.Combine(directory, $"{baseName}.rar");
        if (File.Exists(mainRar))
        {
            files.Add(mainRar);
        }

        // .r?? matches .r00-.r99 but also .rar - exclude .rar to avoid duplicates
        string[] rxxFiles = Directory.GetFiles(directory, $"{baseName}.r??");
        if (rxxFiles.Length > 0)
        {
            files.AddRange(rxxFiles
                .Where(f => !f.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f));
        }

        return files;
    }

    /// <summary>
    /// Returns the candidate second-volume file paths to probe when monitoring an
    /// in-progress compression for early termination. Uses candidate-path probing
    /// (not globbing) so the 100ms poll stays cheap and never matches in-progress
    /// volume writes. Covers zero-padded (<c>.part02.rar</c>, <c>.part002.rar</c>),
    /// non-padded (<c>.part2.rar</c>) and old-style (<c>.r00</c>) second volumes.
    /// </summary>
    /// <param name="directory">
    /// The output directory.
    /// </param>
    /// <param name="baseName">
    /// The expected output base name without extension (not stripped of <c>.part</c>).
    /// </param>
    public static string[] SecondVolumeCandidates(string directory, string baseName) =>
    [
        Path.Combine(directory, $"{baseName}.part02.rar"),  // zero-padded, 2 digits
        Path.Combine(directory, $"{baseName}.part002.rar"), // zero-padded, 3 digits
        Path.Combine(directory, $"{baseName}.part2.rar"),   // non-padded
        Path.Combine(directory, $"{baseName}.r00"),         // old format
    ];

    /// <summary>
    /// Old-style naming: .rar -> .r00 -> .r01 -> ... -> .r99 -> .s00 -> ...
    /// Also handles: .001 -> .002 -> ...
    /// </summary>
    private static string? GetNextOldStyleVolume(string currentPath)
    {
        string ext = Path.GetExtension(currentPath);

        if (ext.Equals(".rar", StringComparison.OrdinalIgnoreCase))
        {
            string basePath = currentPath[..^ext.Length];
            char prefix = char.IsUpper(ext[1]) ? 'R' : 'r';
            return basePath + "." + prefix + "00";
        }

        if (ext.Length == 4 && (ext[1] == 'r' || ext[1] == 'R' || ext[1] == 's' || ext[1] == 'S' ||
                                ext[1] == 't' || ext[1] == 'T' || ext[1] == 'u' || ext[1] == 'U' ||
                                ext[1] == 'v' || ext[1] == 'V' || ext[1] == 'w' || ext[1] == 'W' ||
                                ext[1] == 'x' || ext[1] == 'X' || ext[1] == 'y' || ext[1] == 'Y' ||
                                ext[1] == 'z' || ext[1] == 'Z') &&
            char.IsDigit(ext[2]) && char.IsDigit(ext[3]))
        {
            char prefix = ext[1];
            int num = (ext[2] - '0') * 10 + (ext[3] - '0');
            num++;

            const int maxVolumeNumberPerLetter = 99;
            if (num > maxVolumeNumberPerLetter)
            {
                num = 0;
                prefix = (char)(prefix + 1);
            }

            string basePath = currentPath[..^ext.Length];
            return basePath + "." + prefix + num.ToString("D2");
        }

        if (ext.Length >= 2 && ext[1..].All(char.IsDigit))
        {
            int num = int.Parse(ext[1..]);
            num++;
            string basePath = currentPath[..^ext.Length];
            return basePath + "." + num.ToString($"D{ext.Length - 1}");
        }

        return null;
    }

    /// <summary>
    /// New-style naming: .part1.rar -> .part2.rar, .part01.rar -> .part02.rar, etc.
    /// </summary>
    private static string? GetNextNewStyleVolume(string currentPath)
    {
        Match match = NewStylePartRegex().Match(currentPath);
        if (match.Success)
        {
            string numStr = match.Groups[1].Value;
            string suffix = match.Groups[2].Value;

            int num = int.Parse(numStr) + 1;
            string newNumStr = num.ToString($"D{numStr.Length}");
            return currentPath[..match.Groups[1].Index] + newNumStr + suffix;
        }

        return null;
    }

    [GeneratedRegex(@"\.part(\d+)(\.rar)$", RegexOptions.IgnoreCase)]
    private static partial Regex NewStylePartRegex();
}
