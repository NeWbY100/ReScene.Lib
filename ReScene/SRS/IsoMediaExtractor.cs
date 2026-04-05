using DiscUtils;
using DiscUtils.Iso9660;
using DiscUtils.Streams;
using DiscUtils.Udf;

namespace ReScene.SRS;

/// <summary>
/// Extracts media files from ISO disc images for SRS reconstruction.
/// Supports both ISO 9660 and UDF file systems.
/// </summary>
public static class IsoMediaExtractor
{
    private static readonly HashSet<string> _mediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".vob", ".m2ts", ".ts", ".mpg", ".mpeg", ".evo", ".m2v",
        ".avi", ".mkv", ".mp4", ".wmv", ".m4v", ".mov",
        ".flac", ".mp3"
    };

    /// <summary>
    /// Lists all media files found inside an ISO image.
    /// </summary>
    /// <param name="isoPath">Path to the ISO file.</param>
    /// <returns>List of file paths inside the ISO.</returns>
    public static List<string> ListMediaFiles(string isoPath)
    {
        var mediaFiles = new List<string>();

        using var isoStream = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using DiscFileSystem? disc = OpenDisc(isoStream);

        if (disc is null)
        {
            return mediaFiles;
        }

        foreach (string file in disc.GetFiles("", "*.*", SearchOption.AllDirectories))
        {
            string ext = Path.GetExtension(file);
            if (_mediaExtensions.Contains(ext))
            {
                // Normalize path separators and remove leading backslash
                string normalized = file.Replace('\\', '/').TrimStart('/');
                mediaFiles.Add(normalized);
            }
        }

        mediaFiles.Sort(StringComparer.OrdinalIgnoreCase);
        return mediaFiles;
    }

    /// <summary>
    /// Extracts a specific file from an ISO image to a destination path.
    /// </summary>
    /// <param name="isoPath">Path to the ISO file.</param>
    /// <param name="innerPath">Path of the file inside the ISO (as returned by ListMediaFiles).</param>
    /// <param name="destPath">Destination path to write the extracted file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="progress">Optional progress callback (0-100).</param>
    public static Task ExtractFileAsync(
        string isoPath,
        string innerPath,
        string destPath,
        Action<int>? progress = null,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            using var isoStream = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using DiscFileSystem? disc = OpenDisc(isoStream);

            if (disc is null)
            {
                throw new InvalidOperationException("Unable to open ISO image. Unsupported format.");
            }

            // DiscUtils uses backslash paths internally
            string discPath = "\\" + innerPath.Replace('/', '\\');

            if (!disc.FileExists(discPath))
            {
                throw new FileNotFoundException($"File not found in ISO: {innerPath}");
            }

            using SparseStream source = disc.OpenFile(discPath, FileMode.Open, FileAccess.Read);
            long totalSize = source.Length;

            string? destDir = Path.GetDirectoryName(destPath);
            if (destDir is not null)
            {
                Directory.CreateDirectory(destDir);
            }

            using var dest = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
            byte[] buffer = new byte[80 * 1024];
            long copied = 0;
            int lastPercent = -1;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                int read = source.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                dest.Write(buffer, 0, read);
                copied += read;

                if (totalSize > 0)
                {
                    int percent = (int)(copied * 100 / totalSize);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress?.Invoke(percent);
                    }
                }
            }
        }, ct);
    }

    /// <summary>
    /// Checks whether the given file path appears to be an ISO image.
    /// </summary>
    /// <param name="filePath">File path to check.</param>
    /// <returns>True if the file extension indicates an ISO image.</returns>
    public static bool IsIsoFile(string filePath)
    {
        string ext = Path.GetExtension(filePath);
        return ext.Equals(".iso", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".img", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Progress data for ISO media extraction operations.
    /// </summary>
    public class IsoProgress
    {
        /// <summary>Gets the current phase description.</summary>
        public string Phase { get; init; } = string.Empty;

        /// <summary>Gets the current file name being processed.</summary>
        public string CurrentFile { get; init; } = string.Empty;

        /// <summary>Gets the current file index (1-based).</summary>
        public int FileIndex { get; init; }

        /// <summary>Gets the total number of files.</summary>
        public int FileCount { get; init; }

        /// <summary>Gets the progress percentage for the current file (0-100).</summary>
        public int CurrentPercent { get; init; }

        /// <summary>Gets the overall progress percentage (0-100).</summary>
        public int OverallPercent { get; init; }

        /// <summary>Gets the total bytes processed so far.</summary>
        public long BytesProcessed { get; init; }

        /// <summary>Gets the total bytes to process.</summary>
        public long BytesTotal { get; init; }

        /// <summary>Gets the bytes processed for the current file.</summary>
        public long CurrentBytesProcessed { get; init; }

        /// <summary>Gets the total bytes for the current file.</summary>
        public long CurrentBytesTotal { get; init; }
    }

    /// <summary>
    /// Finds VOB title sets in the ISO and tries to locate the SRS track signature in each one.
    /// If found, concatenates the matching VOB set to a temp file and returns its path.
    /// Returns null if no matching VOB set is found.
    /// </summary>
    /// <param name="isoPath">Path to the ISO file.</param>
    /// <param name="srsFilePath">Path to the SRS file containing track signatures.</param>
    /// <param name="destPath">Destination path for the concatenated VOB file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="progress">Optional progress callback (0-100).</param>
    /// <returns>True if a matching VOB set was found and extracted.</returns>
    public static Task<bool> ExtractMatchingVobSetAsync(
        string isoPath,
        string srsFilePath,
        string destPath,
        Action<IsoProgress>? progress = null,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            SRSFile srs = SRSFile.Load(srsFilePath);
            if (srs.Tracks.Count == 0 || srs.Tracks[0].Signature.Length == 0)
            {
                return false;
            }

            byte[] signature = srs.Tracks[0].Signature;
            long hintOffset = (long)srs.Tracks[0].MatchOffset;

            using var isoStream = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using DiscFileSystem? disc = OpenDisc(isoStream);

            if (disc is null)
            {
                return false;
            }

            // Find all VOB title sets (e.g., VTS_01_*.VOB, VTS_02_*.VOB)
            List<(string TitlePrefix, List<string> VobFiles)> titleSets = FindVobTitleSets(disc);

            // Build title set sizes
            var titleSetSizes = new List<long>();
            foreach ((string _, List<string> vobFiles) in titleSets)
            {
                long setSize = 0;
                foreach (string vf in vobFiles)
                {
                    using SparseStream sz = disc.OpenFile(vf, FileMode.Open, FileAccess.Read);
                    setSize += sz.Length;
                }

                titleSetSizes.Add(setSize);
            }

            // Total files across all title sets (for overall progress)
            int totalFiles = titleSets.Sum(ts => ts.VobFiles.Count);
            long totalAllBytes = titleSetSizes.Sum();
            int filesProcessed = 0;
            long bytesProcessedAll = 0;

            // Phase 1: Try hint offset first (instant if the offset is correct)
            for (int t = 0; t < titleSets.Count; t++)
            {
                ct.ThrowIfCancellationRequested();
                (string titlePrefix, List<string> vobFiles) = titleSets[t];

                if (hintOffset >= 0 && hintOffset < titleSetSizes[t])
                {
                    progress?.Invoke(new IsoProgress
                    {
                        Phase = "Scanning",
                        CurrentFile = $"Checking {titlePrefix} at hint offset...",
                        FileIndex = filesProcessed + 1,
                        FileCount = totalFiles,
                        CurrentPercent = 0,
                        OverallPercent = 0,
                        BytesProcessed = 0,
                        BytesTotal = totalAllBytes
                    });

                    if (CheckSignatureAtOffset(disc, vobFiles, signature, hintOffset, ct))
                    {
                        // Match found — extract with per-file progress
                        ExtractWithProgress(disc, vobFiles, destPath,
                            totalFiles, totalAllBytes, ref filesProcessed, ref bytesProcessedAll,
                            progress, ct);

                        return true;
                    }
                }
            }

            // Phase 2: Full scan as fallback
            for (int t = 0; t < titleSets.Count; t++)
            {
                ct.ThrowIfCancellationRequested();
                (string titlePrefix, List<string> vobFiles) = titleSets[t];
                long setSize = titleSetSizes[t];

                progress?.Invoke(new IsoProgress
                {
                    Phase = "Scanning",
                    CurrentFile = $"Scanning {titlePrefix}...",
                    FileIndex = filesProcessed + 1,
                    FileCount = totalFiles,
                    CurrentPercent = 0,
                    OverallPercent = totalAllBytes > 0 ? (int)(bytesProcessedAll * 100 / totalAllBytes) : 0,
                    BytesProcessed = bytesProcessedAll,
                    BytesTotal = totalAllBytes
                });

                if (SearchSignatureInVobSet(disc, vobFiles, signature, ct,
                        (scanPercent, scanned, total) =>
                        {
                            progress?.Invoke(new IsoProgress
                            {
                                Phase = "Scanning",
                                CurrentFile = $"Scanning {titlePrefix}...",
                                FileIndex = filesProcessed + 1,
                                FileCount = totalFiles,
                                CurrentPercent = scanPercent,
                                OverallPercent = totalAllBytes > 0
                                    ? (int)((bytesProcessedAll + scanned) * 100 / totalAllBytes) : 0,
                                BytesProcessed = bytesProcessedAll + scanned,
                                BytesTotal = totalAllBytes,
                                CurrentBytesProcessed = scanned,
                                CurrentBytesTotal = total
                            });
                        }))
                {
                    ExtractWithProgress(disc, vobFiles, destPath,
                        totalFiles, totalAllBytes, ref filesProcessed, ref bytesProcessedAll,
                        progress, ct);

                    return true;
                }

                // This title set didn't match — count its bytes as processed
                bytesProcessedAll += setSize;
                filesProcessed += vobFiles.Count;
            }

            return false;
        }, ct);
    }

    /// <summary>
    /// Groups VOB files by title set (e.g., VTS_01, VTS_02) and returns them sorted.
    /// </summary>
    private static List<(string TitlePrefix, List<string> VobFiles)> FindVobTitleSets(DiscFileSystem disc)
    {
        var titleSets = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (string file in disc.GetFiles("", "*.VOB", SearchOption.AllDirectories))
        {
            string fileName = Path.GetFileName(file).ToUpperInvariant();

            // Skip VIDEO_TS.VOB (menu VOB)
            if (fileName.StartsWith("VIDEO_TS", StringComparison.Ordinal))
            {
                continue;
            }

            // Extract title set prefix: VTS_01 from VTS_01_1.VOB
            if (fileName.Length >= 6 && fileName.StartsWith("VTS_", StringComparison.Ordinal))
            {
                string prefix = fileName[..6]; // "VTS_01"
                if (!titleSets.TryGetValue(prefix, out List<string>? files))
                {
                    files = [];
                    titleSets[prefix] = files;
                }

                files.Add(file);
            }
        }

        var result = new List<(string, List<string>)>();
        foreach ((string prefix, List<string> files) in titleSets.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            files.Sort(StringComparer.OrdinalIgnoreCase);
            result.Add((prefix, files));
        }

        return result;
    }

    /// <summary>
    /// Extracts VOB files with per-file progress reporting.
    /// </summary>
    private static void ExtractWithProgress(DiscFileSystem disc, List<string> vobFiles,
        string destPath,
        int totalFiles, long totalAllBytes,
        ref int filesProcessed, ref long bytesProcessedAll,
        Action<IsoProgress>? progress, CancellationToken ct)
    {
        string? destDir = Path.GetDirectoryName(destPath);
        if (destDir is not null)
        {
            Directory.CreateDirectory(destDir);
        }

        using var dest = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        byte[] buffer = new byte[80 * 1024];

        for (int f = 0; f < vobFiles.Count; f++)
        {
            ct.ThrowIfCancellationRequested();
            string vobFile = vobFiles[f];
            string vobName = Path.GetFileName(vobFile);

            using SparseStream source = disc.OpenFile(vobFile, FileMode.Open, FileAccess.Read);
            long fileSize = source.Length;
            long fileCopied = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                int read = source.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                dest.Write(buffer, 0, read);
                fileCopied += read;
                bytesProcessedAll += read;

                int filePercent = fileSize > 0 ? (int)(fileCopied * 100 / fileSize) : 100;
                int overallPercent = totalAllBytes > 0 ? (int)(bytesProcessedAll * 100 / totalAllBytes) : 0;

                progress?.Invoke(new IsoProgress
                {
                    Phase = "Extracting",
                    CurrentFile = $"{vobName} ({f + 1}/{vobFiles.Count})",
                    FileIndex = filesProcessed + f + 1,
                    FileCount = totalFiles,
                    CurrentPercent = filePercent,
                    OverallPercent = overallPercent,
                    BytesProcessed = bytesProcessedAll,
                    BytesTotal = totalAllBytes,
                    CurrentBytesProcessed = fileCopied,
                    CurrentBytesTotal = fileSize
                });
            }

            filesProcessed++;
        }
    }

    /// <summary>
    /// Checks if the signature exists at a specific offset in the concatenated VOB set.
    /// This is O(1) — reads only the signature length bytes at the target position.
    /// </summary>
    private static bool CheckSignatureAtOffset(DiscFileSystem disc, List<string> vobFiles,
        byte[] signature, long offset, CancellationToken ct)
    {
        // Find which VOB file contains the target offset
        long cumulative = 0;
        foreach (string vobFile in vobFiles)
        {
            ct.ThrowIfCancellationRequested();

            using SparseStream vob = disc.OpenFile(vobFile, FileMode.Open, FileAccess.Read);
            long vobLength = vob.Length;

            if (offset < cumulative + vobLength)
            {
                // Target offset is in this VOB
                long localOffset = offset - cumulative;
                vob.Position = localOffset;

                byte[] buffer = new byte[signature.Length];
                int totalRead = 0;

                // Read the signature bytes (may span VOB boundary — handle that)
                int read = vob.Read(buffer, 0, buffer.Length);
                totalRead += read;

                // If we didn't get enough bytes, read from the next VOB
                if (totalRead < signature.Length)
                {
                    int vobIndex = vobFiles.IndexOf(vobFile);
                    if (vobIndex + 1 < vobFiles.Count)
                    {
                        using SparseStream nextVob = disc.OpenFile(vobFiles[vobIndex + 1], FileMode.Open, FileAccess.Read);
                        int remaining = signature.Length - totalRead;
                        totalRead += nextVob.Read(buffer, totalRead, remaining);
                    }
                }

                return totalRead == signature.Length && buffer.AsSpan().SequenceEqual(signature);
            }

            cumulative += vobLength;
        }

        return false;
    }

    /// <summary>
    /// Searches for a byte signature across a concatenated VOB set without extracting.
    /// </summary>
    private static bool SearchSignatureInVobSet(DiscFileSystem disc, List<string> vobFiles,
        byte[] signature, CancellationToken ct, Action<int, long, long>? progress = null)
    {
        // Calculate total size for progress reporting
        long totalSize = 0;
        foreach (string vf in vobFiles)
        {
            using SparseStream s = disc.OpenFile(vf, FileMode.Open, FileAccess.Read);
            totalSize += s.Length;
        }

        // Read chunks from each VOB and scan for the signature
        // Handle the case where the signature spans a VOB boundary
        byte[] buffer = new byte[32 * 1024 * 1024]; // 32 MB buffer for scanning
        byte[] carry = new byte[signature.Length - 1];
        int carryLen = 0;
        long scanned = 0;
        int lastPercent = -1;

        foreach (string vobFile in vobFiles)
        {
            ct.ThrowIfCancellationRequested();

            using SparseStream vob = disc.OpenFile(vobFile, FileMode.Open, FileAccess.Read);
            long remaining = vob.Length;

            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();

                int toRead = (int)Math.Min(buffer.Length, remaining);
                int bytesRead = vob.Read(buffer, 0, toRead);
                if (bytesRead == 0)
                {
                    break;
                }

                remaining -= bytesRead;
                scanned += bytesRead;

                if (totalSize > 0)
                {
                    int percent = (int)(scanned * 100 / totalSize);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress?.Invoke(percent, scanned, totalSize);
                    }
                }

                // Check carry + beginning of buffer for boundary matches
                if (carryLen > 0)
                {
                    byte[] combined = new byte[carryLen + Math.Min(bytesRead, signature.Length)];
                    Array.Copy(carry, 0, combined, 0, carryLen);
                    Array.Copy(buffer, 0, combined, carryLen, combined.Length - carryLen);

                    for (int i = 0; i <= combined.Length - signature.Length; i++)
                    {
                        if (combined.AsSpan(i, signature.Length).SequenceEqual(signature))
                        {
                            return true;
                        }
                    }
                }

                // Scan current buffer
                int searchLimit = bytesRead - signature.Length;
                for (int i = 0; i <= searchLimit; i++)
                {
                    if (buffer.AsSpan(i, signature.Length).SequenceEqual(signature))
                    {
                        return true;
                    }
                }

                // Save carry for next iteration
                carryLen = Math.Min(signature.Length - 1, bytesRead);
                Array.Copy(buffer, bytesRead - carryLen, carry, 0, carryLen);
            }
        }

        return false;
    }

    /// <summary>
    /// Opens an ISO image as a DiscFileSystem, trying UDF first then ISO 9660.
    /// </summary>
    private static DiscFileSystem? OpenDisc(Stream isoStream)
    {
        // Try UDF first (most DVDs use UDF)
        try
        {
            if (UdfReader.Detect(isoStream))
            {
                isoStream.Position = 0;
                return new UdfReader(isoStream);
            }
        }
        catch
        {
            // Fall through to ISO 9660
        }

        // Try ISO 9660
        try
        {
            isoStream.Position = 0;
            return new CDReader(isoStream, true);
        }
        catch
        {
            return null;
        }
    }
}
