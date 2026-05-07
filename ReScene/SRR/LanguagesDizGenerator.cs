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
    private const long MaxUnpackedSize = 8L * 1024 * 1024;

    /// <summary>
    /// Result of a languages.diz generation pass.
    /// </summary>
    /// <param name="Data">
    /// The languages.diz UTF-8 content, or <see langword="null"/> when no language lines were extracted.
    /// </param>
    /// <param name="IdxFileNames">
    /// All .idx file names discovered in the archive (independent of whether their content was usable).
    /// </param>
    /// <param name="Warnings">
    /// Per-file warnings for .idx entries that were discovered but skipped (solid archive, split, decompression failure, etc.).
    /// </param>
    internal record Result(byte[]? Data, IReadOnlyList<string> IdxFileNames, IReadOnlyList<string> Warnings);

    /// <summary>
    /// Scans RAR volumes for VobSub .idx files and extracts language lines, decompressing
    /// non-stored entries through <see cref="RARArchive"/> when possible.
    /// </summary>
    /// <param name="rarVolumePaths">
    /// The paths to the RAR volume files to scan.
    /// </param>
    /// <returns>
    /// A <see cref="Result"/> describing the generated content, the discovered .idx names,
    /// and any per-file skip warnings.
    /// </returns>
    public static Result Generate(IReadOnlyList<string> rarVolumePaths)
    {
        var warnings = new List<string>();

        if (rarVolumePaths.Count == 0)
        {
            return new Result(null, [], warnings);
        }

        using RARArchive archive = RARArchive.Open(rarVolumePaths);

        var idxEntries = archive.Files
            .Where(e => e.FileName.EndsWith(".idx", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var idxNames = idxEntries.Select(e => e.FileName).ToList();
        if (idxEntries.Count == 0)
        {
            return new Result(null, idxNames, warnings);
        }

        var sb = new StringBuilder();

        foreach (RAREntry entry in idxEntries)
        {
            byte[]? rawBytes = archive.TryReadAllBytes(entry, MaxUnpackedSize, out string? skipReason);
            if (rawBytes is null)
            {
                warnings.Add($"languages.diz: skipped '{Path.GetFileName(entry.FileName)}' ({skipReason}).");
                continue;
            }

            List<string> languageLines = ParseLanguageLines(rawBytes);
            if (languageLines.Count == 0)
            {
                continue;
            }

            sb.AppendLine($"# {Path.GetFileName(entry.FileName)}");
            foreach (string line in languageLines)
            {
                sb.AppendLine(line);
            }
        }

        if (sb.Length == 0)
        {
            return new Result(null, idxNames, warnings);
        }

        return new Result(Encoding.UTF8.GetBytes(sb.ToString()), idxNames, warnings);
    }

    private static List<string> ParseLanguageLines(byte[] data)
    {
        var lines = new List<string>();

        using var ms = new MemoryStream(data, writable: false);
        using var sr = new StreamReader(ms, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        string? line;
        while ((line = sr.ReadLine()) is not null)
        {
            if (line.StartsWith("id: ", StringComparison.Ordinal))
            {
                lines.Add(line);
            }
        }

        return lines;
    }
}
