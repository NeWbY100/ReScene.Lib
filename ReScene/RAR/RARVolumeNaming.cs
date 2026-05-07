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
