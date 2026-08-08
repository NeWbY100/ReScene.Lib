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

            // Anything that isn't RAR5 was previously ASSUMED to be RAR4 — a non-RAR file whose
            // headers happen to parse as structurally plausible RAR4 blocks could then surface a
            // wrong-but-plausible name. Require the actual RAR4 marker bytes (mirrors
            // RARDetailedParser.HasValidRARSignature) before ever walking as RAR4.
            if (!isRAR5 && !HasValidRAR4Marker(fs))
            {
                return null;
            }

            fs.Position = isRAR5 ? RARUtils.RAR5Marker.Length : RARUtils.RAR4Marker.Length; // skip marker

            return isRAR5 ? WalkRAR5(fs) : WalkRAR4(fs);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Validates the 7-byte RAR4 marker at the start of <paramref name="fs"/>. Stream position is
    /// preserved, matching <see cref="RARUtils.IsRAR5Marker"/>'s convention.
    /// </summary>
    private static bool HasValidRAR4Marker(FileStream fs)
    {
        if (fs.Length < RARUtils.RAR4Marker.Length)
        {
            return false;
        }

        long savedPosition = fs.Position;
        fs.Position = 0;
        byte[] marker = new byte[RARUtils.RAR4Marker.Length];
        fs.ReadExactly(marker, 0, marker.Length);
        fs.Position = savedPosition;

        return marker.AsSpan().SequenceEqual(RARUtils.RAR4Marker);
    }

    /// <summary>
    /// Walk shape mirrors <see cref="RARStream.ValidateFirstVolume"/>'s RAR5 branch, plus two
    /// safety checks neither ValidateFirstVolume nor RARStream need: directories are skipped
    /// (this probe wants the first DATA-bearing entry, mirroring RARArchive's established
    /// walkers' <c>IsDirectory</c> filter) and a header whose CRC doesn't validate aborts the
    /// whole probe — a corrupted header can't be trusted for its parsed name OR for the
    /// HeaderSize/DataSize this walk needs to find the next block, so continuing past it risks a
    /// desynced read rather than a clean "no file header found".
    /// </summary>
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

            if (!block.CRCValid)
            {
                return null;
            }

            if (block.BlockType == RAR5BlockType.File)
            {
                RAR5FileInfo? fileInfo = block.FileInfo;
                if (fileInfo == null)
                {
                    return null;
                }

                if (!fileInfo.IsDirectory)
                {
                    return string.IsNullOrEmpty(fileInfo.FileName) ? null : NormalizePathSeparator(fileInfo.FileName);
                }
            }

            reader.SkipBlock(block);
        }

        return null;
    }

    /// <summary>
    /// Walk shape mirrors <see cref="RARStream.ValidateFirstVolume"/>'s RAR4 branch, plus the
    /// same directory-skip and CRC-validity gate as <see cref="WalkRAR5"/> (see its remarks).
    /// </summary>
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

            if (!block.CRCValid)
            {
                return null;
            }

            if (block.BlockType == RAR4BlockType.FileHeader)
            {
                RARFileHeader? fileHeader = block.FileHeader;
                if (fileHeader == null)
                {
                    return null;
                }

                if (!fileHeader.IsDirectory)
                {
                    return string.IsNullOrEmpty(fileHeader.FileName) ? null : NormalizePathSeparator(fileHeader.FileName);
                }
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
