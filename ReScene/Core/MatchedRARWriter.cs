namespace ReScene.Core;

/// <summary>
/// Locates the RAR file(s) a brute-force attempt actually produced (which may be volume sets with
/// various naming schemes) and moves a matched archive to its final output path. Extracted from
/// <see cref="Manager"/> to group the output-file discovery and placement helpers.
/// </summary>
internal static class MatchedRARWriter
{
    /// <summary>
    /// Moves a matched RAR file to its final path. Returns <see langword="true"/> when the file
    /// ends up at <paramref name="destinationPath"/> — either moved there, or already there
    /// because no rename was needed. Returns <see langword="false"/> when a different file
    /// already occupies the destination (the source is left untouched), or when the move did not
    /// actually leave a file at the destination.
    /// </summary>
    public static bool MoveMatchedFile(string sourcePath, string destinationPath)
    {
        if (PathsEqual(sourcePath, destinationPath))
        {
            // No rename needed — the file is already at its final path (a very common case: an
            // unpatched, not-renamed-to-release-names run's produced volume already sits at the
            // name RenameMatchedOutput would compute). Still verify it's actually there.
            return File.Exists(destinationPath);
        }

        if (File.Exists(destinationPath))
        {
            return false;
        }

        File.Move(sourcePath, destinationPath);

        // Defensive post-condition: a caller must never be told a move succeeded when the
        // destination doesn't actually hold a file afterward.
        return File.Exists(destinationPath);
    }

    /// <summary>
    /// Filesystem-correct equality for two paths, used to short-circuit the source==destination
    /// no-op case: resolves both to their full, normalized form (so differing relative segments,
    /// redundant <c>.</c> components, or separator style referring to the same file compare equal
    /// — a raw string compare of the unresolved paths would miss this) and compares
    /// case-insensitively on Windows/macOS (their default file systems) or case-sensitively on
    /// Linux.
    /// </summary>
    internal static bool PathsEqual(string left, string right)
    {
        StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
    }

    /// <summary>
    /// Locates the first-volume RAR file produced for the given expected output path, handling the
    /// non-volume case and the various volume naming schemes (partNN.rar, partN.rar, .rar/.r00).
    /// Returns <see langword="null"/> when no produced file is found.
    /// </summary>
    public static string? FindCreatedRARFile(string expectedRARFilePath)
    {
        // Check if the expected file exists (non-volume case)
        if (File.Exists(expectedRARFilePath))
        {
            return expectedRARFilePath;
        }

        // Check for volume files
        string directory = Path.GetDirectoryName(expectedRARFilePath) ?? string.Empty;
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(expectedRARFilePath);

        // Check for RAR5 volume format with zero-padded numbers: filename.part01.rar, filename.part02.rar, etc.
        string part01File = Path.Combine(directory, $"{fileNameWithoutExtension}.part01.rar");
        if (File.Exists(part01File))
        {
            return part01File;
        }

        // Check for RAR5 volume format without zero-padding: filename.part1.rar, filename.part2.rar, etc.
        string part1File = Path.Combine(directory, $"{fileNameWithoutExtension}.part1.rar");
        if (File.Exists(part1File))
        {
            return part1File;
        }

        // Check for older RAR volume formats: filename.rar + filename.r00, filename.r01, etc.
        // In this case, the first volume keeps the .rar extension
        string firstVolumeOldFormat = Path.Combine(directory, $"{fileNameWithoutExtension}.rar");
        string secondVolumeOldFormat = Path.Combine(directory, $"{fileNameWithoutExtension}.r00");
        if (File.Exists(firstVolumeOldFormat) && File.Exists(secondVolumeOldFormat))
        {
            return firstVolumeOldFormat;
        }

        // Check if only the first volume exists (very small archive that fits in one volume)
        if (File.Exists(firstVolumeOldFormat))
        {
            return firstVolumeOldFormat;
        }

        // No RAR file found
        return null;
    }

    /// <summary>
    /// Returns all RAR volume files belonging to the same archive set as the specified first volume.
    /// </summary>
    public static List<string> GetAllVolumeFiles(string firstVolumePath)
        => FileOperations.GetAllVolumeFiles(firstVolumePath);
}
