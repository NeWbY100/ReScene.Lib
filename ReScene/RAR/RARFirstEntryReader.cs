namespace ReScene.RAR;

/// <summary>
/// Best-effort diagnostic probe that reads the archived name of the first packed-file entry from
/// a RAR4 or RAR5 volume. Used to detect when a produced archive packs files in a different order
/// than the release it is being assembled against (see the SRR-guided-assembly quick gate in
/// <c>ReScene.Core.Manager</c>). This is a diagnostic, not a structural validator: it never throws
/// — any parse or I/O failure degrades to <see langword="null"/> rather than interrupting a
/// brute-force run.
/// </summary>
internal static class RARFirstEntryReader
{
    /// <summary>
    /// Returns the archived name of the first file header found in <paramref name="volumePath"/>
    /// (RAR4 or RAR5), with path separators normalized to backslash — matching <see
    /// cref="RARStream"/>'s convention. Returns <see langword="null"/> when the path is missing or
    /// empty, is not a parseable RAR volume, contains no file header, or any other parse/IO error
    /// occurs.
    /// </summary>
    internal static string? TryGetFirstFileName(string volumePath)
    {
        try
        {
            using var fs = new FileStream(volumePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            bool isRAR5 = RAR5HeaderReader.IsRAR5(fs);
            fs.Position = isRAR5 ? RARUtils.RAR5Marker.Length : RARUtils.RAR4Marker.Length; // skip marker

            return isRAR5 ? WalkRAR5(fs) : WalkRAR4(fs);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Walk shape mirrors <see cref="RARStream.ValidateFirstVolume"/>'s RAR5 branch.</summary>
    private static string? WalkRAR5(FileStream fs)
    {
        var reader = new RAR5HeaderReader(fs);
        while (fs.Position < fs.Length)
        {
            RAR5BlockReadResult? block = reader.ReadBlock();
            if (block == null)
            {
                break;
            }

            if (block.BlockType == RAR5BlockType.File)
            {
                return block.FileInfo != null ? NormalizePathSeparator(block.FileInfo.FileName) : null;
            }

            reader.SkipBlock(block);
        }

        return null;
    }

    /// <summary>Walk shape mirrors <see cref="RARStream.ValidateFirstVolume"/>'s RAR4 branch.</summary>
    private static string? WalkRAR4(FileStream fs)
    {
        var reader = new RARHeaderReader(fs);
        while (fs.Position < fs.Length)
        {
            RARBlockReadResult? block = reader.ReadBlock(parseContents: true);
            if (block == null)
            {
                break;
            }

            if (block.BlockType == RAR4BlockType.FileHeader)
            {
                return block.FileHeader != null ? NormalizePathSeparator(block.FileHeader.FileName) : null;
            }

            // Skip past the block (header + data). Use the 64-bit DataSize so a LARGE service
            // block (>= 4 GiB) ahead of the first file header is skipped in full.
            long target = block.BlockPosition + block.HeaderSize + block.DataSize;
            fs.Position = Math.Min(target, fs.Length);
        }

        return null;
    }

    /// <summary>
    /// Normalizes path separators to backslash (RAR internal format), matching <see
    /// cref="RARStream"/>'s identical convention.
    /// </summary>
    private static string NormalizePathSeparator(string path) => path.Replace('/', '\\');
}
