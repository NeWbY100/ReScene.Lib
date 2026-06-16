using System.Text;
using ReScene.Core.Cryptography;

namespace ReScene.Core;

/// <summary>
/// Prepares the working "input" directory for a brute-force run: validating that the required SRR
/// files are present, copying the release (or just the SRR-listed entries) into the output's
/// <c>input</c> subdirectory, verifying CRC32s, applying file/directory timestamps, and writing the
/// archive comment file. Extracted from <see cref="Manager"/>.
/// </summary>
/// <remarks>
/// All cross-cutting concerns are supplied explicitly so the preparer never reaches back into
/// <see cref="Manager"/> state: the logger and the original log source (so emitted log events are
/// indistinguishable), the run's cancellation token, and the progress/preservation callbacks that
/// re-raise Manager's events.
/// </remarks>
internal sealed class InputDirectoryPreparer(
    IReSceneLogger logger,
    object logSource,
    Action<FileCopyProgressEventArgs> fireFileCopyProgress,
    Action<CRCValidationProgressEventArgs> fireCrcValidationProgress,
    Action<string, string> fireTimestampPreservationFailed,
    CancellationToken cancellationToken)
{
    private readonly IReSceneLogger _logger = logger;
    private readonly object _logSource = logSource;
    private readonly CancellationToken _cancellationToken = cancellationToken;
    private readonly Action<FileCopyProgressEventArgs> _fireFileCopyProgress = fireFileCopyProgress;
    private readonly Action<CRCValidationProgressEventArgs> _fireCrcValidationProgress = fireCrcValidationProgress;
    private readonly Action<string, string> _fireTimestampPreservationFailed = fireTimestampPreservationFailed;

    /// <summary>The result of preparing the input directory.</summary>
    /// <param name="InputFilesDir">The path of the prepared input directory.</param>
    /// <param name="CommentFilePath">
    /// The path of the written comment file, or <see langword="null"/> if no comment was written.
    /// </param>
    public readonly record struct PrepareResult(string InputFilesDir, string? CommentFilePath);

    /// <summary>
    /// Validates that all required input files from the SRR exist in the release directory
    /// and that their CRC32 values match. Runs before any brute-forcing begins.
    /// </summary>
    public bool ValidateInputFiles(BruteForceOptions options)
    {
        RAROptions opts = options.RAROptions;
        string releaseDir = options.ReleaseDirectoryPath;

        _logger.Information(_logSource, $"=== Validating Input Files ({opts.ArchiveFilePaths.Count} files, {opts.ArchiveDirectoryPaths.Count} directories) ===", LogTarget.System);

        foreach (string file in opts.ArchiveFilePaths.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            _logger.Information(_logSource, $"  Required: {file}", LogTarget.System);
        }

        List<string> missing = [];

        foreach (string file in opts.ArchiveFilePaths)
        {
            string filePath = Path.Combine(releaseDir, file);
            if (!File.Exists(filePath))
            {
                missing.Add(file);
                _logger.Error(_logSource, $"  MISSING: {file}", LogTarget.System);
                continue;
            }

            // CRC validation is deferred to ValidateArchiveFileCrcs (after copy)
            // which has progress UI support
        }

        if (missing.Count > 0)
        {
            _logger.Error(_logSource, $"Input validation failed: {missing.Count} file(s) missing.", LogTarget.System);
            return false;
        }

        _logger.Information(_logSource, $"Input validation passed: all {opts.ArchiveFilePaths.Count} file(s) present.", LogTarget.System);
        return true;
    }

    /// <summary>
    /// Cleans and recreates the input directory, copies the release files into it, verifies SRR
    /// CRC32s, applies timestamps, and writes the archive comment file (if any). Returns the input
    /// directory and the written comment file path.
    /// </summary>
    public PrepareResult PrepareInputDirectory(BruteForceOptions options)
    {
        string inputFilesDir = Path.Combine(options.OutputDirectoryPath, "input");
        if (Directory.Exists(inputFilesDir))
        {
            _logger.Information(_logSource, $"Cleaning existing input directory: {inputFilesDir}");
            Directory.Delete(inputFilesDir, true);
        }

        _logger.Information(_logSource, $"Creating input directory and copying files from {options.ReleaseDirectoryPath} to {inputFilesDir}");
        Directory.CreateDirectory(inputFilesDir);
        if (options.RAROptions.HasArchiveFileList)
        {
            _logger.Information(_logSource, $"Using SRR file list: {options.RAROptions.ArchiveFilePaths.Count} files, {options.RAROptions.ArchiveDirectoryPaths.Count} dirs");
            CopySelectedEntries(options.ReleaseDirectoryPath, inputFilesDir, options.RAROptions.ArchiveFilePaths, options.RAROptions.ArchiveDirectoryPaths);
        }
        else
        {
            CopyDirectory(options.ReleaseDirectoryPath, inputFilesDir);
        }

        _logger.Information(_logSource, $"Finished copying {Directory.GetFiles(inputFilesDir, "*.*", SearchOption.AllDirectories).Length} files to input directory");

        if (options.RAROptions.ArchiveFileCrcs.Count > 0)
        {
            _logger.Information(_logSource, $"Validating SRR CRC32 entries: {options.RAROptions.ArchiveFileCrcs.Count} file(s)");
            ValidateArchiveFileCrcs(inputFilesDir, options.RAROptions.ArchiveFileCrcs);
        }

        int fileTimestampCount = options.RAROptions.FileTimestamps.Count;
        int fileCreationCount = options.RAROptions.FileCreationTimes.Count;
        int fileAccessCount = options.RAROptions.FileAccessTimes.Count;
        // Apply file timestamps before directory timestamps to keep directory times authoritative.
        if (fileTimestampCount + fileCreationCount + fileAccessCount > 0)
        {
            _logger.Information(_logSource, $"Applying file timestamps: mtime {fileTimestampCount}, ctime {fileCreationCount}, atime {fileAccessCount}");
            ApplyFileTimestamps(inputFilesDir, options.RAROptions.FileTimestamps, options.RAROptions.FileCreationTimes, options.RAROptions.FileAccessTimes);
        }

        int dirTimestampCount = options.RAROptions.DirectoryTimestamps.Count;
        int dirCreationCount = options.RAROptions.DirectoryCreationTimes.Count;
        int dirAccessCount = options.RAROptions.DirectoryAccessTimes.Count;
        if (dirTimestampCount + dirCreationCount + dirAccessCount > 0)
        {
            _logger.Information(_logSource, $"Applying directory timestamps: mtime {dirTimestampCount}, ctime {dirCreationCount}, atime {dirAccessCount}");
            ApplyDirectoryTimestamps(inputFilesDir, options.RAROptions.DirectoryTimestamps, options.RAROptions.DirectoryCreationTimes, options.RAROptions.DirectoryAccessTimes);
        }

        // Create comment file if archive has a comment
        string? commentFilePath = null;
        if (options.RAROptions.ArchiveCommentBytes is { Length: > 0 } archiveCommentBytes)
        {
            // Use raw bytes for exact reconstruction
            string commentPath = Path.Combine(options.OutputDirectoryPath, "comment.txt");
            try
            {
                File.WriteAllBytes(commentPath, archiveCommentBytes.ToArray());
                commentFilePath = commentPath;
                _logger.Information(_logSource, $"Created comment file: {commentPath} ({archiveCommentBytes.Length} bytes)");
            }
            catch (Exception ex)
            {
                _logger.Warning(_logSource, $"Failed to create comment file: {ex.Message}");
            }
        }
        else if (!string.IsNullOrEmpty(options.RAROptions.ArchiveComment))
        {
            // Fallback to string (for manually entered comments)
            string commentPath = Path.Combine(options.OutputDirectoryPath, "comment.txt");
            try
            {
                // Use UTF-8 without BOM
                File.WriteAllText(commentPath, options.RAROptions.ArchiveComment, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                commentFilePath = commentPath;
                _logger.Information(_logSource, $"Created comment file: {commentPath} ({options.RAROptions.ArchiveComment.Length} chars, fallback)");
            }
            catch (Exception ex)
            {
                _logger.Warning(_logSource, $"Failed to create comment file: {ex.Message}");
            }
        }

        return new PrepareResult(inputFilesDir, commentFilePath);
    }

    private void ValidateArchiveFileCrcs(string inputDirectory, Dictionary<string, string> expectedCrcs)
    {
        if (expectedCrcs.Count == 0)
        {
            return;
        }

        // Pre-calculate total bytes across all files
        long totalBytes = 0;
        foreach (KeyValuePair<string, string> entry in expectedCrcs)
        {
            string filePath = Path.Combine(inputDirectory, entry.Key);
            if (File.Exists(filePath))
            {
                totalBytes += new FileInfo(filePath).Length;
            }
        }

        List<string> missing = [];
        List<string> mismatched = [];
        int filesVerified = 0;
        long cumulativeBytesVerified = 0;

        foreach (KeyValuePair<string, string> entry in expectedCrcs)
        {
            _cancellationToken.ThrowIfCancellationRequested();

            string relativePath = entry.Key;
            string expectedCRC = entry.Value;
            string filePath = Path.Combine(inputDirectory, relativePath);

            if (!File.Exists(filePath))
            {
                missing.Add(relativePath);
                continue;
            }

            long fileSize = new FileInfo(filePath).Length;
            long baseBytes = cumulativeBytesVerified;

            _fireCrcValidationProgress(new CRCValidationProgressEventArgs
            {
                FileName = relativePath,
                FilesVerified = filesVerified,
                TotalFiles = expectedCrcs.Count,
                BytesVerified = baseBytes,
                TotalBytes = totalBytes
            });

            string actualCRC = CRC32.Calculate(filePath, bytesRead =>
            {
                _fireCrcValidationProgress(new CRCValidationProgressEventArgs
                {
                    FileName = relativePath,
                    FilesVerified = filesVerified,
                    TotalFiles = expectedCrcs.Count,
                    BytesVerified = baseBytes + bytesRead,
                    TotalBytes = totalBytes
                });
            }, _cancellationToken);

            cumulativeBytesVerified += fileSize;
            filesVerified++;

            if (!string.Equals(actualCRC, expectedCRC, StringComparison.OrdinalIgnoreCase))
            {
                mismatched.Add($"{relativePath} (expected {expectedCRC}, got {actualCRC})");
            }
        }

        // Fire final 100% event
        _fireCrcValidationProgress(new CRCValidationProgressEventArgs
        {
            FileName = "",
            FilesVerified = expectedCrcs.Count,
            TotalFiles = expectedCrcs.Count,
            BytesVerified = totalBytes,
            TotalBytes = totalBytes
        });

        if (missing.Count == 0 && mismatched.Count == 0)
        {
            _logger.Information(_logSource, $"SRR CRC32 validation passed for {expectedCrcs.Count} file(s).");
            return;
        }

        foreach (string entry in missing)
        {
            _logger.Error(_logSource, $"SRR CRC32 validation missing file: {entry}");
        }

        foreach (string entry in mismatched)
        {
            _logger.Error(_logSource, $"SRR CRC32 validation mismatch: {entry}");
        }

        int issueCount = missing.Count + mismatched.Count;
        throw new InvalidDataException($"SRR CRC32 validation failed for {issueCount} file(s).");
    }

    private void CopyDirectory(string sourceDir, string destDir)
        => FileOperations.CopyDirectory(sourceDir, destDir, _cancellationToken, _fireFileCopyProgress, _logger, _fireTimestampPreservationFailed);

    private void CopySelectedEntries(string sourceDir, string destDir, HashSet<string> filePaths, HashSet<string> directoryPaths)
        => FileOperations.CopySelectedEntries(sourceDir, destDir, filePaths, directoryPaths, _cancellationToken, _fireFileCopyProgress, _logger, _fireTimestampPreservationFailed);

    private void ApplyFileTimestamps(string inputDirectory, Dictionary<string, DateTime> modifiedTimes, Dictionary<string, DateTime> creationTimes, Dictionary<string, DateTime> accessTimes)
        => FileOperations.ApplyFileTimestamps(inputDirectory, modifiedTimes, creationTimes, accessTimes, _logger);

    private void ApplyDirectoryTimestamps(string inputDirectory, Dictionary<string, DateTime> modifiedTimes, Dictionary<string, DateTime> creationTimes, Dictionary<string, DateTime> accessTimes)
        => FileOperations.ApplyDirectoryTimestamps(inputDirectory, modifiedTimes, creationTimes, accessTimes, _logger);
}
