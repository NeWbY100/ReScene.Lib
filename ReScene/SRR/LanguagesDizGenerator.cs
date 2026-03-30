using System.Text;
using ReScene.RAR;

namespace ReScene.SRR;

/// <summary>
/// Generates a languages.diz file by extracting language metadata from VobSub .idx files
/// found inside RAR archives. This follows the pyrescene convention of embedding subtitle
/// language information as a stored file in the SRR.
/// </summary>
internal static class LanguagesDizGenerator
{
    /// <summary>
    /// Scans RAR volumes for VobSub .idx files and extracts language lines.
    /// Returns the languages.diz content as UTF-8 bytes, or null if no .idx files were found.
    /// </summary>
    /// <param name="rarVolumePaths">The paths to the RAR volume files to scan.</param>
    /// <returns>The languages.diz content as UTF-8 bytes, or <see langword="null"/> if no .idx files were found.</returns>
    public static byte[]? Generate(IReadOnlyList<string> rarVolumePaths)
    {
        if (rarVolumePaths.Count == 0)
        {
            return null;
        }

        string firstVolume = rarVolumePaths[0];

        // Find all .idx files by parsing RAR headers across all volumes
        var idxFileNames = FindIdxFiles(rarVolumePaths);
        if (idxFileNames.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();

        foreach (string idxName in idxFileNames)
        {
            var languageLines = ReadLanguageLines(firstVolume, idxName);
            if (languageLines.Count == 0)
            {
                continue;
            }

            // Comment header with the .idx filename (just the filename, no path)
            sb.AppendLine($"# {Path.GetFileName(idxName)}");
            foreach (string line in languageLines)
            {
                sb.AppendLine(line);
            }
        }

        if (sb.Length == 0)
        {
            return null;
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Parses RAR headers across all volumes to find archived files with .idx extension.
    /// </summary>
    private static List<string> FindIdxFiles(IReadOnlyList<string> volumePaths)
    {
        var idxFiles = new List<string>();

        foreach (string volumePath in volumePaths)
        {
            try
            {
                using var fs = new FileStream(volumePath, FileMode.Open, FileAccess.Read, FileShare.Read);

                bool isRar5 = IsRar5(fs);

                if (isRar5)
                {
                    FindIdxFilesRar5(fs, idxFiles);
                }
                else
                {
                    FindIdxFilesRar4(fs, idxFiles);
                }
            }
            catch
            {
                // If header parsing fails for a volume, continue with the next
            }
        }

        return idxFiles;
    }

    private static void FindIdxFilesRar4(FileStream fs, List<string> idxFiles)
    {
        fs.Position = 0;
        var reader = new RARHeaderReader(fs);

        while (reader.CanReadBaseHeader)
        {
            var block = reader.ReadBlock(parseContents: true);
            if (block is null)
            {
                break;
            }

            if (block.FileHeader is { } fh &&
                fh.FileName.EndsWith(".idx", StringComparison.OrdinalIgnoreCase) &&
                !fh.IsDirectory &&
                !idxFiles.Contains(fh.FileName))
            {
                idxFiles.Add(fh.FileName);
            }

            // Skip past the block — manually seek like RarStream does,
            // because SkipBlock intentionally does not skip data for FileHeader blocks.
            long target = block.BlockPosition + block.HeaderSize;
            if (block.BlockType is RAR4BlockType.FileHeader or RAR4BlockType.Service)
            {
                target += block.AddSize;
            }
            else if ((block.Flags & (ushort)RARFileFlags.LongBlock) != 0)
            {
                target += block.AddSize;
            }

            fs.Position = Math.Min(target, fs.Length);
        }
    }

    private static void FindIdxFilesRar5(FileStream fs, List<string> idxFiles)
    {
        fs.Position = 8; // Skip RAR5 marker
        var reader = new RAR5HeaderReader(fs);

        while (reader.CanReadBaseHeader)
        {
            var block = reader.ReadBlock();
            if (block is null)
            {
                break;
            }

            if (block.FileInfo is { } fi &&
                fi.FileName.EndsWith(".idx", StringComparison.OrdinalIgnoreCase) &&
                !fi.IsDirectory &&
                !idxFiles.Contains(fi.FileName))
            {
                idxFiles.Add(fi.FileName);
            }

            // Advance past the block header and data area
            reader.SkipBlock(block);
        }
    }

    /// <summary>
    /// Reads an .idx file from the RAR archive using RarStream and extracts "id: " lines.
    /// </summary>
    private static List<string> ReadLanguageLines(string firstVolumePath, string idxFileName)
    {
        var lines = new List<string>();

        try
        {
            using var stream = new RarStream(firstVolumePath, idxFileName);

            // Safety limit: don't read .idx files larger than 1 MB
            if (stream.Length > 1024 * 1024)
            {
                return lines;
            }

            using var sr = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            string? line;
            while ((line = sr.ReadLine()) is not null)
            {
                if (line.StartsWith("id: ", StringComparison.Ordinal))
                {
                    lines.Add(line);
                }
            }
        }
        catch
        {
            // If file can't be read (compressed, corrupt, etc.), skip it
        }

        return lines;
    }

    private static bool IsRar5(FileStream fs)
    {
        if (fs.Length < 8)
        {
            return false;
        }

        long pos = fs.Position;
        Span<byte> marker = stackalloc byte[8];
        int read = fs.Read(marker);
        fs.Position = pos;

        if (read < 8)
        {
            return false;
        }

        ReadOnlySpan<byte> rar5Marker = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];
        return marker.SequenceEqual(rar5Marker);
    }
}
