namespace ReScene.Core;

/// <summary>
/// Locates the RAR file(s) a brute-force attempt actually produced (which may be volume sets with
/// various naming schemes) and moves a matched archive to its final output path. Extracted from
/// <see cref="Manager"/> to group the output-file discovery and placement helpers.
/// </summary>
internal static class MatchedRarWriter
{
    /// <summary>
    /// Moves a matched RAR file to its final path. Returns <see langword="true"/> when the file
    /// ends up at <paramref name="destinationPath"/> — either moved there, or already there
    /// because no rename was needed. Returns <see langword="false"/> when a different file
    /// already occupies the destination (the source is left untouched).
    /// </summary>
    public static bool MoveMatchedFile(string sourcePath, string destinationPath)
    {
        if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (File.Exists(destinationPath))
        {
            return false;
        }

        File.Move(sourcePath, destinationPath);
        return true;
    }

    /// <summary>
    /// Locates the first-volume RAR file produced for the given expected output path, handling the
    /// non-volume case and the various volume naming schemes (partNN.rar, partN.rar, .rar/.r00).
    /// Returns <see langword="null"/> when no produced file is found.
    /// </summary>
    public static string? FindCreatedRARFile(string expectedRarFilePath)
    {
        // Check if the expected file exists (non-volume case)
        if (File.Exists(expectedRarFilePath))
        {
            return expectedRarFilePath;
        }

        // Check for volume files
        string directory = Path.GetDirectoryName(expectedRarFilePath) ?? string.Empty;
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(expectedRarFilePath);

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
