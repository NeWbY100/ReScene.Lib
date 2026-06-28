using ReScene.RAR;

namespace ReScene.SRR;

/// <summary>
/// Identifies RAR volume files by their file extension patterns.
/// </summary>
public static class RARVolumeIdentifier
{
    /// <summary>
    /// Computes the archive-set key for a RAR volume path: its directory (normalized to forward
    /// slashes, trimmed) plus the volume base name (extension stripped via
    /// <see cref="RARVolumeNaming.GetBaseName(string)"/>). Volumes in the same set share this key,
    /// distinguishing sets by directory and/or base name (e.g. "DVD1/aln-re4a", or "…cd1" at root).
    /// </summary>
    public static string GetArchiveSetKey(string volumePath)
    {
        string baseName = RARVolumeNaming.GetBaseName(Path.GetFileName(volumePath));
        string? dir = Path.GetDirectoryName(volumePath);
        if (string.IsNullOrEmpty(dir))
        {
            return baseName;
        }

        string normalizedDir = dir.Replace('\\', '/').Trim('/');
        return normalizedDir.Length == 0 ? baseName : $"{normalizedDir}/{baseName}";
    }

    /// <summary>
    /// Determines whether a filename has a RAR volume extension.
    /// Supports .rar, .partN.rar, old-style (.r00, .s00), and numbered (.001, .002).
    /// </summary>
    public static bool IsRarVolume(string fileName)
    {
        string ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(ext))
        {
            return false;
        }

        // .rar (including .partN.rar)
        if (ext.Equals(".rar", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Old-style extensions: .r00, .r01, ..., .r99, .s00, etc.
        if (ext.Length == 4 && ext[0] == '.' &&
            char.IsLetter(ext[1]) && char.IsDigit(ext[2]) && char.IsDigit(ext[3]))
        {
            return true;
        }

        // Extensions like .001, .002 (numbered volumes)
        if (ext.Length == 4 && ext[0] == '.' &&
            char.IsDigit(ext[1]) && char.IsDigit(ext[2]) && char.IsDigit(ext[3]))
        {
            return true;
        }

        return false;
    }
}
