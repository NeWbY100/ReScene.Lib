using ReScene.RAR;

namespace ReScene.Core;

/// <summary>
/// Provides static file system utility methods extracted from <see cref="Manager"/>
/// for copying, deleting, timestamping, and attribute management of files and directories.
/// </summary>
internal static class FileOperations
{
    /// <summary>
    /// Returns all RAR volume files belonging to the same archive set as the specified first volume.
    /// Handles both new-style (part01.rar, part02.rar) and old-style (.rar, .r00, .r01) naming.
    /// </summary>
    /// <param name="firstVolumePath">
    /// Path to any volume in the set (typically the first).
    /// </param>
    /// <returns>
    /// An ordered list of all volume file paths.
    /// </returns>
    public static List<string> GetAllVolumeFiles(string firstVolumePath)
    {
        string directory = Path.GetDirectoryName(firstVolumePath) ?? string.Empty;
        string baseName = RARVolumeNaming.GetBaseName(Path.GetFileName(firstVolumePath));

        List<string> files = RARVolumeNaming.EnumerateVolumes(directory, baseName);

        // If no volumes found, just return the original file
        if (files.Count == 0 && File.Exists(firstVolumePath))
        {
            files.Add(firstVolumePath);
        }

        return files;
    }

    /// <summary>
    /// Resolves an SRR entry path to a safe relative path under the given base directory.
    /// Returns false if the path is empty, traverses outside the base, or resolves to ".".
    /// </summary>
    /// <param name="baseFullPath">
    /// The base directory that the relative path must stay within.
    /// </param>
    /// <param name="entryPath">
    /// The raw entry path from the SRR file.
    /// </param>
    /// <param name="relativePath">
    /// When successful, the normalized relative path; otherwise empty.
    /// </param>
    /// <returns>
    /// True if the path resolved safely; false otherwise.
    /// </returns>
    public static bool TryResolveRelativePath(string baseFullPath, string entryPath, out string relativePath)
    {
        relativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(entryPath))
        {
            return false;
        }

        string normalized = entryPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        while (normalized.StartsWith("." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        normalized = normalized.TrimStart(Path.DirectorySeparatorChar);

        if (normalized.Length == 0)
        {
            return false;
        }

        string basePath = Path.GetFullPath(baseFullPath);
        string fullPath = Path.GetFullPath(Path.Combine(basePath, normalized));

        string basePrefix = basePath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? basePath
            : basePath + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        relativePath = Path.GetRelativePath(basePath, fullPath);
        return !string.IsNullOrWhiteSpace(relativePath) && relativePath != ".";
    }

    /// <summary>
    /// Copies a single file from source to destination with chunked progress reporting.
    /// Updates the running byte/file counters via ref parameters.
    /// </summary>
    /// <param name="sourcePath">
    /// Source file path.
    /// </param>
    /// <param name="destPath">
    /// Destination file path.
    /// </param>
    /// <param name="displayName">
    /// Display name for progress reporting.
    /// </param>
    /// <param name="bytesCopied">
    /// Running total of bytes copied (updated in-place).
    /// </param>
    /// <param name="filesCopied">
    /// Running count of files copied (updated in-place).
    /// </param>
    /// <param name="totalFiles">
    /// Total number of files to copy.
    /// </param>
    /// <param name="totalBytes">
    /// Total bytes to copy across all files.
    /// </param>
    /// <param name="sourceDir">
    /// Source directory (for progress event context).
    /// </param>
    /// <param name="destDir">
    /// Destination directory (for progress event context).
    /// </param>
    /// <param name="ct">
    /// Cancellation token.
    /// </param>
    /// <param name="onProgress">
    /// Optional callback invoked after each buffer write and after file completion.
    /// </param>
    /// <param name="logger">
    /// Optional logger used to warn about timestamp-preservation failures.
    /// </param>
    /// <param name="onTimestampFailure">
    /// Optional callback invoked with (destPath, errorMessage) when copying the
    /// source file's timestamps onto the destination fails.
    /// </param>
    public static void CopyFileWithProgress(
        string sourcePath, string destPath, string displayName,
        ref long bytesCopied, ref int filesCopied, int totalFiles, long totalBytes,
        string sourceDir, string destDir,
        CancellationToken ct,
        Action<FileCopyProgressEventArgs>? onProgress = null,
        IReSceneLogger? logger = null,
        Action<string, string>? onTimestampFailure = null)
    {
        // 1 MiB copy buffer: large enough for full streaming throughput, but small enough to
        // stay off the Large Object Heap (the previous 32 MiB buffer wasted LOH memory with no
        // measurable throughput gain).
        byte[] buffer = new byte[1024 * 1024];

        using (FileStream sourceStream = File.OpenRead(sourcePath))
        using (FileStream destStream = new(destPath, FileMode.Create, FileAccess.Write))
        {
            int bytesRead;
            while ((bytesRead = sourceStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                destStream.Write(buffer, 0, bytesRead);
                bytesCopied += bytesRead;

                onProgress?.Invoke(new FileCopyProgressEventArgs
                {
                    FileName = displayName,
                    FilesCopied = filesCopied,
                    TotalFiles = totalFiles,
                    BytesCopied = bytesCopied,
                    TotalBytes = totalBytes,
                    SourceDirectory = sourceDir,
                    DestinationDirectory = destDir
                });
            }
        }

        // Preserve the source file's timestamps. When the SRR carries archived
        // timestamps, ApplyFileTimestamps will later override these — same end
        // result. When the SRR has none, these preserved values are what
        // WinRAR ends up packing into FILE_HEAD's FTIME (instead of "now").
        try
        {
            File.SetCreationTime(destPath, File.GetCreationTime(sourcePath));
            File.SetLastAccessTime(destPath, File.GetLastAccessTime(sourcePath));
            File.SetLastWriteTime(destPath, File.GetLastWriteTime(sourcePath));
        }
        catch (Exception ex)
        {
            // Non-fatal — leave whatever timestamps the OS assigned, but warn
            // the caller so the UI can surface this. The packed RAR's FTIME
            // will end up reflecting the copy completion time instead of the
            // source's mtime.
            logger?.Warning(null,
                $"Failed to preserve timestamps on {destPath}: {ex.Message}. " +
                $"The reconstructed RAR's File Time (DOS) may not match the original.");
            onTimestampFailure?.Invoke(destPath, ex.Message);
        }

        filesCopied++;

        // Fire final event for this file with updated count
        onProgress?.Invoke(new FileCopyProgressEventArgs
        {
            FileName = displayName,
            FilesCopied = filesCopied,
            TotalFiles = totalFiles,
            BytesCopied = bytesCopied,
            TotalBytes = totalBytes,
            SourceDirectory = sourceDir,
            DestinationDirectory = destDir
        });
    }

    /// <summary>
    /// Copies an entire directory tree from source to destination with progress reporting.
    /// </summary>
    /// <param name="sourceDir">
    /// Source directory path.
    /// </param>
    /// <param name="destDir">
    /// Destination directory path.
    /// </param>
    /// <param name="ct">
    /// Cancellation token.
    /// </param>
    /// <param name="onProgress">
    /// Optional callback invoked during file copy progress.
    /// </param>
    /// <param name="logger">
    /// Optional logger used to warn about timestamp-preservation failures.
    /// </param>
    /// <param name="onTimestampFailure">
    /// Optional callback invoked with (destPath, errorMessage) when copying the
    /// source file's timestamps onto the destination fails.
    /// </param>
    public static void CopyDirectory(
        string sourceDir, string destDir,
        CancellationToken ct,
        Action<FileCopyProgressEventArgs>? onProgress = null,
        IReSceneLogger? logger = null,
        Action<string, string>? onTimestampFailure = null)
    {
        // Enumerate all files upfront for progress tracking
        string[] allFiles = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
        long totalBytes = 0;
        foreach (string file in allFiles)
        {
            totalBytes += new FileInfo(file).Length;
        }

        int filesCopied = 0;
        long bytesCopied = 0;

        // Create all directories first
        foreach (string dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceDir, dir);
            Directory.CreateDirectory(Path.Combine(destDir, relative));
        }

        Directory.CreateDirectory(destDir);

        // Copy files with progress
        foreach (string file in allFiles)
        {
            ct.ThrowIfCancellationRequested();

            string relative = Path.GetRelativePath(sourceDir, file);
            string destFile = Path.Combine(destDir, relative);

            // Ensure parent directory exists (for top-level files)
            string? destParent = Path.GetDirectoryName(destFile);
            if (!string.IsNullOrEmpty(destParent))
            {
                Directory.CreateDirectory(destParent);
            }

            CopyFileWithProgress(file, destFile, Path.GetFileName(file), ref bytesCopied, ref filesCopied,
                allFiles.Length, totalBytes, sourceDir, destDir, ct, onProgress, logger, onTimestampFailure);
        }
    }

    /// <summary>
    /// Copies selected files and directories from the SRR file list to a destination directory.
    /// </summary>
    /// <param name="sourceDir">
    /// Source release directory.
    /// </param>
    /// <param name="destDir">
    /// Destination input directory.
    /// </param>
    /// <param name="filePaths">
    /// Set of file paths from the SRR archive list.
    /// </param>
    /// <param name="directoryPaths">
    /// Set of directory paths from the SRR archive list.
    /// </param>
    /// <param name="ct">
    /// Cancellation token.
    /// </param>
    /// <param name="onProgress">
    /// Optional callback invoked during file copy progress.
    /// </param>
    /// <param name="logger">
    /// Optional logger for warnings.
    /// </param>
    /// <param name="onTimestampFailure">
    /// Optional callback invoked with (destPath, errorMessage) when copying the
    /// source file's timestamps onto the destination fails.
    /// </param>
    public static void CopySelectedEntries(
        string sourceDir, string destDir,
        HashSet<string> filePaths, HashSet<string> directoryPaths,
        CancellationToken ct,
        Action<FileCopyProgressEventArgs>? onProgress = null,
        IReSceneLogger? logger = null,
        Action<string, string>? onTimestampFailure = null)
    {
        string sourceRoot = Path.GetFullPath(sourceDir);
        string destRoot = Path.GetFullPath(destDir);
        int missingFiles = 0;
        int skippedEntries = 0;

        foreach (string directory in directoryPaths)
        {
            if (!TryResolveRelativePath(sourceRoot, directory, out string relativeDir))
            {
                skippedEntries++;
                continue;
            }

            string destPath = Path.Combine(destRoot, relativeDir);
            Directory.CreateDirectory(destPath);
        }

        // Pre-calculate totals for progress
        int totalFiles = 0;
        long totalBytes = 0;
        foreach (string file in filePaths)
        {
            if (!TryResolveRelativePath(sourceRoot, file, out string relativeFile))
            {
                continue;
            }

            string sourcePath = Path.Combine(sourceRoot, relativeFile);
            if (File.Exists(sourcePath))
            {
                totalFiles++;
                totalBytes += new FileInfo(sourcePath).Length;
            }
        }

        int filesCopied = 0;
        long bytesCopied = 0;

        foreach (string file in filePaths)
        {
            ct.ThrowIfCancellationRequested();

            if (!TryResolveRelativePath(sourceRoot, file, out string relativeFile))
            {
                skippedEntries++;
                continue;
            }

            string sourcePath = Path.Combine(sourceRoot, relativeFile);
            if (!File.Exists(sourcePath))
            {
                missingFiles++;
                logger?.Warning(null, $"SRR entry not found on disk: {relativeFile}");
                continue;
            }

            string destPath = Path.Combine(destRoot, relativeFile);
            string? destDirectory = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDirectory))
            {
                Directory.CreateDirectory(destDirectory);
            }

            CopyFileWithProgress(sourcePath, destPath, Path.GetFileName(relativeFile), ref bytesCopied, ref filesCopied,
                totalFiles, totalBytes, sourceDir, destDir, ct, onProgress, logger, onTimestampFailure);
        }

        if (skippedEntries > 0)
        {
            logger?.Warning(null, $"Skipped {skippedEntries} SRR entries due to invalid paths.");
        }

        if (missingFiles > 0)
        {
            throw new FileNotFoundException($"{missingFiles} file(s) from the SRR archive list are missing in the release directory.");
        }
    }

    /// <summary>
    /// Adds or removes a file attribute on a collection of files.
    /// </summary>
    /// <param name="files">
    /// The files to modify.
    /// </param>
    /// <param name="attribute">
    /// The attribute to add or remove.
    /// </param>
    /// <param name="add">
    /// True to add the attribute; false to remove it.
    /// </param>
    /// <param name="logger">
    /// Optional logger for diagnostics.
    /// </param>
    public static void SetFileAttributes(IEnumerable<FileInfo> files, FileAttributes attribute, bool add, IReSceneLogger? logger = null)
    {
        foreach (FileInfo fileInfo in files)
        {
            if (add)
            {
                fileInfo.Attributes |= attribute;
                logger?.Information(null, $"Added {attribute} attribute to {fileInfo}", LogTarget.Phase2);
            }
            else
            {
                fileInfo.Attributes &= ~attribute;
                logger?.Information(null, $"Removed {attribute} attribute from {fileInfo}", LogTarget.Phase2);
            }
        }
    }

    /// <summary>
    /// Applies modified, creation, and access timestamps to files under the specified directory.
    /// Order: creation, access, then modified (so modified time is the final write).
    /// </summary>
    /// <param name="inputDirectory">
    /// The base directory containing the files.
    /// </param>
    /// <param name="modifiedTimes">
    /// Modified time entries keyed by relative path.
    /// </param>
    /// <param name="creationTimes">
    /// Creation time entries keyed by relative path.
    /// </param>
    /// <param name="accessTimes">
    /// Access time entries keyed by relative path.
    /// </param>
    /// <param name="logger">
    /// Optional logger for warnings on failure.
    /// </param>
    public static void ApplyFileTimestamps(
        string inputDirectory,
        Dictionary<string, DateTime> modifiedTimes,
        Dictionary<string, DateTime> creationTimes,
        Dictionary<string, DateTime> accessTimes,
        IReSceneLogger? logger = null)
    {
        // Order matters: set creation/access first so modified time ends up as the final write.
        ApplyFileTimestampEntries(inputDirectory, creationTimes, File.SetCreationTime, "creation", logger);
        ApplyFileTimestampEntries(inputDirectory, accessTimes, File.SetLastAccessTime, "access", logger);
        ApplyFileTimestampEntries(inputDirectory, modifiedTimes, File.SetLastWriteTime, "modified", logger);
    }

    /// <summary>
    /// Applies a single category of timestamp entries to files under the specified directory.
    /// </summary>
    /// <param name="inputDirectory">
    /// The base directory containing the files.
    /// </param>
    /// <param name="timestamps">
    /// Timestamp entries keyed by relative path.
    /// </param>
    /// <param name="setter">
    /// The setter method (e.g., File.SetLastWriteTime).
    /// </param>
    /// <param name="label">
    /// Label for log messages (e.g., "creation", "modified").
    /// </param>
    /// <param name="logger">
    /// Optional logger for warnings on failure.
    /// </param>
    public static void ApplyFileTimestampEntries(
        string inputDirectory,
        Dictionary<string, DateTime> timestamps,
        Action<string, DateTime> setter,
        string label,
        IReSceneLogger? logger = null)
    {
        foreach (KeyValuePair<string, DateTime> entry in timestamps)
        {
            string relativePath = entry.Key;
            string filePath = Path.Combine(inputDirectory, relativePath);
            if (!File.Exists(filePath))
            {
                continue;
            }

            try
            {
                setter(filePath, entry.Value);
            }
            catch (Exception ex)
            {
                logger?.Warning(null, $"Failed to set {label} timestamp for file {relativePath}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Applies modified, creation, and access timestamps to directories under the specified directory.
    /// Order: creation, access, then modified (so modified time is the final write).
    /// </summary>
    /// <param name="inputDirectory">
    /// The base directory containing the subdirectories.
    /// </param>
    /// <param name="modifiedTimes">
    /// Modified time entries keyed by relative path.
    /// </param>
    /// <param name="creationTimes">
    /// Creation time entries keyed by relative path.
    /// </param>
    /// <param name="accessTimes">
    /// Access time entries keyed by relative path.
    /// </param>
    /// <param name="logger">
    /// Optional logger for warnings on failure.
    /// </param>
    public static void ApplyDirectoryTimestamps(
        string inputDirectory,
        Dictionary<string, DateTime> modifiedTimes,
        Dictionary<string, DateTime> creationTimes,
        Dictionary<string, DateTime> accessTimes,
        IReSceneLogger? logger = null)
    {
        // Order matters: set creation/access first so modified time ends up as the final write.
        ApplyDirectoryTimestampEntries(inputDirectory, creationTimes, Directory.SetCreationTime, "creation", logger);
        ApplyDirectoryTimestampEntries(inputDirectory, accessTimes, Directory.SetLastAccessTime, "access", logger);
        ApplyDirectoryTimestampEntries(inputDirectory, modifiedTimes, Directory.SetLastWriteTime, "modified", logger);
    }

    /// <summary>
    /// Applies a single category of timestamp entries to directories under the specified directory.
    /// </summary>
    /// <param name="inputDirectory">
    /// The base directory containing the subdirectories.
    /// </param>
    /// <param name="timestamps">
    /// Timestamp entries keyed by relative path.
    /// </param>
    /// <param name="setter">
    /// The setter method (e.g., Directory.SetLastWriteTime).
    /// </param>
    /// <param name="label">
    /// Label for log messages (e.g., "creation", "modified").
    /// </param>
    /// <param name="logger">
    /// Optional logger for warnings on failure.
    /// </param>
    public static void ApplyDirectoryTimestampEntries(
        string inputDirectory,
        Dictionary<string, DateTime> timestamps,
        Action<string, DateTime> setter,
        string label,
        IReSceneLogger? logger = null)
    {
        foreach (KeyValuePair<string, DateTime> entry in timestamps)
        {
            string relativePath = entry.Key;
            string dirPath = Path.Combine(inputDirectory, relativePath);
            if (!Directory.Exists(dirPath))
            {
                continue;
            }

            try
            {
                setter(dirPath, entry.Value);
            }
            catch (Exception ex)
            {
                logger?.Warning(null, $"Failed to set {label} timestamp for directory {relativePath}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Deletes a RAR file and all its associated volume files (both new and old naming formats).
    /// </summary>
    /// <param name="rarFilePath">
    /// Path to any volume in the set.
    /// </param>
    /// <param name="logger">
    /// Optional logger for diagnostics.
    /// </param>
    public static void DeleteRARFileAndVolumes(string rarFilePath, IReSceneLogger? logger = null)
    {
        try
        {
            string directory = Path.GetDirectoryName(rarFilePath) ?? string.Empty;
            string baseName = RARVolumeNaming.GetBaseName(Path.GetFileName(rarFilePath));

            // Delete every volume in the set (both new-style partNN.rar and
            // old-style .rar + .rNN naming).
            foreach (string file in RARVolumeNaming.EnumerateVolumes(directory, baseName))
            {
                try
                {
                    File.Delete(file);
                    logger?.Debug(null, $"Deleted volume file: {file}", LogTarget.Phase2);
                }
                catch (Exception ex)
                {
                    logger?.Information(null, $"Failed to delete volume file {file}: {ex.Message}", LogTarget.Phase2);
                }
            }
        }
        catch (Exception ex)
        {
            logger?.Information(null, $"Failed to delete RAR file and volumes: {rarFilePath}{Environment.NewLine}{ex.Message}", LogTarget.Phase2);
        }
    }
}
