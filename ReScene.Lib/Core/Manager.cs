using System.Text;
using System.Text.RegularExpressions;
using RAR;
using Core.Cryptography;
using Core.Diagnostics;
using Core.IO;

namespace Core;

public partial class Manager(IReSceneLogger? logger = null)
{
    public event EventHandler<RARProcessDataEventArgs>? RARProcessOutput;

    public event EventHandler<RARProcessStatusChangedEventArgs>? RARProcessStatusChanged;

    public event EventHandler<RARCompressionProgressEventArgs>? RARCompressionProgress;

    public event EventHandler<RARCompressionStatusChangedEventArgs>? RARCompressionStatusChanged;

    public event EventHandler<BruteForceProgressEventArgs>? BruteForceProgress;

    public event EventHandler<BruteForceStatusChangedEventArgs>? BruteForceStatusChanged;

    public event EventHandler<FileCopyProgressEventArgs>? FileCopyProgress;

    public event EventHandler<CrcValidationProgressEventArgs>? CrcValidationProgress;

    public BruteForceOptions? BruteForceOptions { get; private set; }

    private readonly CancellationTokenSource CancellationTokenSource = new();

    private readonly Dictionary<RARProcess, StreamWriter> ProcessLogWriters = [];
    private readonly HashSet<RARProcess> ActiveProcesses = [];
    private readonly object ProcessLock = new();
    private string? CommentFilePath = null;

    private readonly IReSceneLogger _logger = logger ?? NullReSceneLogger.Instance;

    [GeneratedRegex("(?:win)?(?:rar|wr)(?:-x64|-x32)?-?(\\d+)(?:b\\d+)?", RegexOptions.IgnoreCase)]
    private static partial Regex GeneratedRARVersionRegex();
    private readonly static Regex RARVersionRegex = GeneratedRARVersionRegex();

    public static int ParseRARVersion(string rarVersionDirectoryName)
    {
        Match versionMatch = RARVersionRegex.Match(rarVersionDirectoryName);
        if (!versionMatch.Success)
        {
            throw new FormatException($"WinRAR version not found in directory name:{Environment.NewLine}{rarVersionDirectoryName}");
        }

        string versionNumberStr = versionMatch.Groups[1].Value;
        if (!int.TryParse(versionNumberStr, out int versionNumber))
        {
            throw new InvalidDataException($"WinRAR version found in directory name is invalid:{Environment.NewLine}{versionNumberStr}");
        }

        return versionNumber switch
        {
            < 100 => versionNumber * 10,
            _ => versionNumber
        };
    }

    public static RARArchiveVersion ParseRARArchiveVersion(RARCommandLineArgument[] commandLineArguments, int version)
    {
        RARCommandLineArgument? archiveVersionCommandLine = commandLineArguments.FirstOrDefault(a => a.Argument == "-ma4" || a.Argument == "-ma5");
        if (archiveVersionCommandLine != null)
        {
            return archiveVersionCommandLine.Argument switch
            {
                "-ma4" => RARArchiveVersion.RAR4,
                "-ma5" => RARArchiveVersion.RAR5,
                _ => throw new IndexOutOfRangeException($"RAR archive version command line argument out of range: {archiveVersionCommandLine.Argument}")
            };
        }

        return version switch
        {
            < 500 => RARArchiveVersion.RAR4,
            < 700 => RARArchiveVersion.RAR5,
            >= 700 => RARArchiveVersion.RAR7
        };
    }

    public async Task<bool> BruteForceRARVersionAsync(BruteForceOptions options)
    {
        _logger.Information(this, $"=== Starting Brute-Force ===", LogTarget.System);
        _logger.Information(this, $"Release: {options.ReleaseDirectoryPath}", LogTarget.System);
        _logger.Information(this, $"Output: {options.OutputDirectoryPath}", LogTarget.System);
        _logger.Information(this, $"Expected {options.HashType}: {string.Join(", ", options.Hashes)}", LogTarget.System);

        // Log all settings
        LogBruteForceSettings(options);

        BruteForceOptions = options;

        DateTime bruteForceStartDateTime = DateTime.Now;

        BruteForceStatusChangedEventArgs status = new(OperationStatus.Running);
        FireBruteForceStatusChanged(status);

        // === DIRECT RECONSTRUCTION (Custom Packer) ===
        if (options.RAROptions.CustomPackerDetected != SRR.CustomPackerType.None
            && !string.IsNullOrEmpty(options.RAROptions.SrrFilePath))
        {
            _logger.Information(this, $"Custom packer detected ({options.RAROptions.CustomPackerDetected}) — using direct SRR reconstruction", LogTarget.System);

            var reconstructor = new SRRReconstructor(_logger);
            reconstructor.Progress += (s, e) => FireBruteForceProgress(e);

            bool result = await reconstructor.ReconstructAsync(
                options.RAROptions.SrrFilePath,
                options.ReleaseDirectoryPath,
                options.OutputDirectoryPath,
                options.RAROptions.OriginalRarFileNames,
                options.Hashes,
                options.HashType,
                CancellationTokenSource.Token);

            var completionStatus = result ? OperationCompletionStatus.Success : OperationCompletionStatus.Error;
            status = new BruteForceStatusChangedEventArgs(OperationStatus.Running, OperationStatus.Completed, completionStatus);
            FireBruteForceStatusChanged(status);
            return result;
        }

        string[] rarVersionDirectories = Directory.GetDirectories(options.RARInstallationsDirectoryPath);
        _logger.Debug(this, $"Found {rarVersionDirectories.Length} RAR version directories in {options.RARInstallationsDirectoryPath}");

        if (rarVersionDirectories.Length == 0)
        {
            _logger.Warning(this, "No RAR executables found in WinRAR directory or sub directories");
            return false;
        }

        // Get all valid RAR directories first
        var allValidRarDirectories = GetValidRarDirectories(rarVersionDirectories, options);
        _logger.Information(this, $"Found {allValidRarDirectories.Count} valid RAR versions matching configured version ranges");

        // Validate input files before any brute-forcing
        if (options.RAROptions.HasArchiveFileList && !ValidateInputFiles(options))
        {
            return false;
        }

        // === PHASE 1: Comment Block Brute-Force ===
        // If CMT compressed data is available, first brute-force the comment to narrow down versions
        List<(string Path, int Version)> versionsToUse;
        if (options.RAROptions.CanUseCommentPhase)
        {
            versionsToUse = await BruteForceCommentPhaseAsync(options, allValidRarDirectories);
            _logger.Information(this, $"Phase 1 complete: {versionsToUse.Count} matching version(s)", LogTarget.System);
            _logger.Information(this, $"=== PHASE 2: Full RAR Brute-Force with {versionsToUse.Count} version(s) ===", LogTarget.Phase2);
        }
        else
        {
            versionsToUse = allValidRarDirectories;
            _logger.Information(this, "Phase 1 skipped (no CMT data)", LogTarget.System);
            _logger.Information(this, "Phase 1 skipped (no CMT data) - using all versions for brute-force", LogTarget.Phase1);
        }

        string inputFilesDir = await Task.Run(() => PrepareInputDirectory(options));

        int totalProgressSize = CalculateBruteForceProgressSize(options, versionsToUse.Count, allValidRarDirectories.Count);
        int currentProgress = 0;

        DirectoryInfo directoryInfo = new(inputFilesDir);
        FileInfo[] fileInfos = directoryInfo.GetFiles("*.*", SearchOption.AllDirectories);

        // Save file attributes
        Dictionary<FileInfo, FileAttributes> fileInfoAttributes = fileInfos.Select(f => new KeyValuePair<FileInfo, FileAttributes>(f, f.Attributes)).ToDictionary(f => f.Key, f => f.Value);

        // Save file hash
        HashSet<string> fileHashes = [];

        bool found = false;
        bool stopOnFirstMatch = options.RAROptions.StopOnFirstMatch;
        for (int a = 0; a < (options.RAROptions.SetFileArchiveAttribute == TriState.Checked ? 2 : 1) && !(found && stopOnFirstMatch); a++)
        {
            if (options.RAROptions.SetFileArchiveAttribute != TriState.Unchecked)
            {
                if (a == 0)
                {
                    // Set archive attribute on first run
                    SetFileAttributes(fileInfos, FileAttributes.Archive, true);
                }
                else
                {
                    // Remove archive attribute on second run
                    SetFileAttributes(fileInfos, FileAttributes.Archive, false);
                }
            }

            for (int b = 0; b < (options.RAROptions.SetFileNotContentIndexedAttribute == TriState.Checked ? 2 : 1) && !(found && stopOnFirstMatch); b++)
            {
                if (options.RAROptions.SetFileNotContentIndexedAttribute != TriState.Unchecked)
                {
                    if (b == 0)
                    {
                        // Set not content indexed attribute on first run
                        SetFileAttributes(fileInfos, FileAttributes.NotContentIndexed, true);
                    }
                    else
                    {
                        // Remove not content indexed attribute on second run
                        SetFileAttributes(fileInfos, FileAttributes.NotContentIndexed, false);
                    }
                }

                // Use versions filtered by Phase 1 (or all versions if Phase 1 was skipped)
                foreach (var (rarVersionDirectoryPath, version) in versionsToUse)
                {
                    if (CancellationTokenSource.IsCancellationRequested)
                    {
                        break;
                    }

                    var (foundCombination, newProgress) = await TryProcessCommandLinesAsync(options, version, rarVersionDirectoryPath, inputFilesDir, totalProgressSize, currentProgress, bruteForceStartDateTime, fileHashes, a, b);
                    currentProgress = newProgress;
                    if (foundCombination)
                    {
                        found = true;
                        if (stopOnFirstMatch)
                        {
                            _logger.Information(this, "Match found - stopping brute force (StopOnFirstMatch is enabled)", LogTarget.Phase2);
                            break;
                        }
                        else
                        {
                            _logger.Information(this, "Match found - continuing to test remaining versions (StopOnFirstMatch is disabled)", LogTarget.Phase2);
                        }
                    }

                }
            }
        }

        if (options.RAROptions.SetFileArchiveAttribute != TriState.Unchecked ||
            options.RAROptions.SetFileNotContentIndexedAttribute != TriState.Unchecked)
        {
            // Restore file attributes
            foreach (FileInfo fileInfo in fileInfos)
            {
                fileInfo.Attributes = fileInfoAttributes[fileInfo];
            }
        }

        // Log completion summary to System tab
        var elapsed = DateTime.Now - bruteForceStartDateTime;
        if (CancellationTokenSource.IsCancellationRequested)
        {
            _logger.Information(this, $"=== Brute-force CANCELLED after {elapsed.TotalSeconds:F1}s ===", LogTarget.System);
        }
        else if (found)
        {
            _logger.Information(this, $"=== Brute-force SUCCESS in {elapsed.TotalSeconds:F1}s ===", LogTarget.System);
        }
        else
        {
            _logger.Warning(this, $"=== Brute-force FAILED - no match found after {elapsed.TotalSeconds:F1}s ===", LogTarget.System);
        }

        status = new(OperationStatus.Running, OperationStatus.Completed, CancellationTokenSource.IsCancellationRequested ? OperationCompletionStatus.Cancelled : OperationCompletionStatus.Success);
        FireBruteForceStatusChanged(status);
        return found;
    }

    public void Stop()
    {
        _logger.Information(this, "Stopping brute force operation and cancelling all RAR processes");
        CancellationTokenSource.Cancel();

        // CliWrap will automatically kill processes when the cancellation token is cancelled
        // The processes will clean themselves up in Process_ProcessStatusChanged

        lock (ProcessLock)
        {
            _logger.Information(this, $"Active processes count: {ActiveProcesses.Count}");
            ActiveProcesses.Clear();
        }
    }

    private static int CalculateBruteForceProgressSize(BruteForceOptions options, int filteredVersionCount = 0, int totalVersionCount = 0)
    {
        int size = 0;

        DirectoryInfo directoryInfo = new(options.ReleaseDirectoryPath);
        string[] rarVersionDirectories = Directory.GetDirectories(options.RARInstallationsDirectoryPath);
        for (int a = 0; a < (options.RAROptions.SetFileArchiveAttribute == TriState.Checked ? 2 : 1); a++)
        {
            for (int b = 0; b < (options.RAROptions.SetFileNotContentIndexedAttribute == TriState.Checked ? 2 : 1); b++)
            {
                Parallel.ForEach(rarVersionDirectories, (rarVersionDirectoryPath, s, i) =>
                {
                    string rarExeFilePath = Path.Combine(rarVersionDirectoryPath, "rar.exe");
                    if (!File.Exists(rarExeFilePath))
                    {
                        return;
                    }

                    string rarVersionDirectoryName = Path.GetFileName(rarVersionDirectoryPath);
                    int version = ParseRARVersion(rarVersionDirectoryName);
                    if (!options.RAROptions.RARVersions.Any(r => r.InRange(version)))
                    {
                        return;
                    }

                    Parallel.ForEach(options.RAROptions.CommandLineArguments, (commandLineArguments, s2, j) =>
                    {
                        RARArchiveVersion archiveVersion = ParseRARArchiveVersion(commandLineArguments, version);

                        // Filter arguments by RAR version and RAR archive version
                        IEnumerable<string> filteredArguments = commandLineArguments.Where(
                            a => version >= a.MinimumVersion &&
                            (!a.MaximumVersion.HasValue || version <= a.MaximumVersion.Value) &&
                            (!a.ArchiveVersion.HasValue || a.ArchiveVersion.Value.HasFlag(archiveVersion))
                        ).Select(a => a.Argument);

                        // Apply same RAR 6.x timestamp skip as the main loop
                        bool hasTimestampOptions = filteredArguments.Any(arg => arg.StartsWith("-ts"));
                        bool isRAR4Format = archiveVersion == RARArchiveVersion.RAR4 ||
                                           (version >= 550 && version < 700 && !filteredArguments.Contains("-ma5"));
                        if (version >= 600 && version < 700 && isRAR4Format && hasTimestampOptions)
                        {
                            return;
                        }

                        Interlocked.Increment(ref size);
                    });
                });
            }
        }

        // If Phase 1 filtered the versions, scale the progress accordingly
        if (filteredVersionCount > 0 && totalVersionCount > 0 && filteredVersionCount < totalVersionCount)
        {
            // Scale the size based on the ratio of filtered versions to total versions
            size = (int)((long)size * filteredVersionCount / totalVersionCount);
        }

        return size;
    }

    private async Task<int> RARCompressDirectoryAsync(string rarExeFilePath, string inputDirectory, string outputFilePath, IEnumerable<string> commandLineOptions, CancellationToken cancellationToken)
    {
        RARProcess process = new(rarExeFilePath, inputDirectory, outputFilePath, commandLineOptions, _logger)
        {
            LogTarget = LogTarget.Phase2
        };

        // Initialize streaming log writer for this process
        if (BruteForceOptions != null)
        {
            // Create log file path
            string logsDir = Path.Combine(BruteForceOptions.OutputDirectoryPath, "logs");
            Directory.CreateDirectory(logsDir);

            string logFileName = $"{Path.GetFileNameWithoutExtension(outputFilePath)}_{DateTime.Now:yyyyMMdd_HHmmss}.log";
            string logFilePath = Path.Combine(logsDir, logFileName);

            // Open StreamWriter with AutoFlush enabled for immediate writes
            StreamWriter writer = new(logFilePath, append: false)
            {
                AutoFlush = true
            };
            ProcessLogWriters[process] = writer;

            _logger.Information(this, $"Opened log file for streaming: {logFilePath}", LogTarget.Phase2);
        }

        process.ProcessStatusChanged += Process_ProcessStatusChanged;
        process.ProcessOutput += Process_ProcessOutput;
        process.CompressionStatusChanged += Process_CompressionStatusChanged;
        process.CompressionProgress += Process_CompressionProgress;

        // Create a linked cancellation token for early termination
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Start monitoring for second volume (for early termination optimization)
        Task monitorTask = MonitorForSecondVolumeAsync(outputFilePath, linkedCts);

        // Run the RAR process
        Task<int> processTask = process.RunAsync(linkedCts.Token);

        // Wait for either process completion or early termination
        await Task.WhenAny(processTask, monitorTask);

        // If monitoring detected second volume, cancel the process
        if (monitorTask.IsCompleted && !processTask.IsCompleted)
        {
            _logger.Debug(this, $"Second volume detected, terminating RAR process early for: {outputFilePath}", LogTarget.Phase2);
            linkedCts.Cancel();
            // Wait a bit for graceful cancellation
            await Task.WhenAny(processTask, Task.Delay(1000, cancellationToken));
        }

        // Return the exit code if available, otherwise return success (0) since we terminated early
        return processTask.IsCompleted ? await processTask : 0;
    }

    private async Task MonitorForSecondVolumeAsync(string expectedRarFilePath, CancellationTokenSource cts)
    {
        try
        {
            string directory = Path.GetDirectoryName(expectedRarFilePath) ?? string.Empty;
            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(expectedRarFilePath);

            // Determine which second volume filename to look for
            // Check .part02.rar (zero-padded)
            string secondVolumePart02 = Path.Combine(directory, $"{fileNameWithoutExtension}.part02.rar");
            // Check .part2.rar (non-padded)
            string secondVolumePart2 = Path.Combine(directory, $"{fileNameWithoutExtension}.part2.rar");
            // Check .r00 (old format)
            string secondVolumeR00 = Path.Combine(directory, $"{fileNameWithoutExtension}.r00");

            // Poll for second volume existence
            while (!cts.Token.IsCancellationRequested)
            {
                if (File.Exists(secondVolumePart02) || File.Exists(secondVolumePart2) || File.Exists(secondVolumeR00))
                {
                    // Second volume detected! Return to trigger early termination
                    return;
                }

                // Wait a bit before checking again (100ms polling interval)
                await Task.Delay(100, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation, ignore
        }
        catch (ObjectDisposedException)
        {
            // CTS was disposed after the process completed, ignore
        }
        catch (Exception ex)
        {
            _logger.Debug(this, $"Error monitoring for second volume: {ex.Message}", LogTarget.Phase2);
        }
    }

    private void Process_ProcessStatusChanged(object? sender, OperationStatusChangedEventArgs e)
    {
        if (sender is not RARProcess process)
        {
            return;
        }

        RARProcessStatusChanged?.Invoke(this, new(process, e.OldStatus, e.NewStatus, e.CompletionStatus));

        // When process completes, close and dispose the log writer
        if (e.NewStatus == OperationStatus.Completed)
        {
            if (ProcessLogWriters.TryGetValue(process, out StreamWriter? writer))
            {
                try
                {
                    writer.Close();
                    writer.Dispose();
                    _logger.Information(this, $"Process log file closed", LogTarget.Phase2);
                }
                catch (Exception ex)
                {
                    _logger.Information(this, $"Failed to close process log writer: {ex.Message}", LogTarget.Phase2);
                }
                finally
                {
                    // Clean up tracking dictionary
                    ProcessLogWriters.Remove(process);
                }
            }
        }
    }

    private void Process_ProcessOutput(object? sender, ProcessDataEventArgs e)
    {
        if (sender is not RARProcess process)
        {
            return;
        }

        // Stream output directly to log file (auto-flushed)
        if (ProcessLogWriters.TryGetValue(process, out StreamWriter? writer))
        {
            try
            {
                writer.WriteLine(e.Data);
                // AutoFlush is enabled, so data is immediately written to disk
            }
            catch (Exception ex)
            {
                _logger.Information(this, $"Failed to write to process log: {ex.Message}", LogTarget.Phase2);
            }
        }

        RARProcessOutput?.Invoke(this, new(process, e.Data));
    }

    private void Process_CompressionStatusChanged(object? sender, OperationStatusChangedEventArgs e)
    {
        if (sender is not RARProcess process)
        {
            return;
        }

        RARCompressionStatusChanged?.Invoke(this, new(process, e.OldStatus, e.NewStatus, e.CompletionStatus));
    }

    private void Process_CompressionProgress(object? sender, FileCompressionOperationProgressEventArgs e)
    {
        if (sender is not RARProcess process)
        {
            return;
        }

        RARCompressionProgress?.Invoke(this, new(process, e.OperationSize, e.OperationProgressed, e.StartDateTime, e.FilePath));
    }

    private void FireBruteForceProgress(BruteForceProgressEventArgs e)
        => BruteForceProgress?.Invoke(this, e);

    private void FireBruteForceStatusChanged(BruteForceStatusChangedEventArgs e)
        => BruteForceStatusChanged?.Invoke(this, e);

    private void FireFileCopyProgress(FileCopyProgressEventArgs e)
        => FileCopyProgress?.Invoke(this, e);

    private void CopyFileWithProgress(string sourcePath, string destPath, string displayName,
        ref long bytesCopied, ref int filesCopied, int totalFiles, long totalBytes,
        string sourceDir, string destDir)
    {
        byte[] buffer = new byte[1048576 * 32]; // 32MB buffer

        using (FileStream sourceStream = File.OpenRead(sourcePath))
        using (FileStream destStream = new(destPath, FileMode.Create, FileAccess.Write))
        {
            int bytesRead;
            while ((bytesRead = sourceStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                CancellationTokenSource.Token.ThrowIfCancellationRequested();
                destStream.Write(buffer, 0, bytesRead);
                bytesCopied += bytesRead;

                FireFileCopyProgress(new FileCopyProgressEventArgs
                {
                    FileName = displayName,
                    FilesCopied = filesCopied,
                    TotalFiles = totalFiles,
                    BytesCopied = bytesCopied,
                    TotalBytes = totalBytes,
                    SourceDirectory = sourceDir,
                    DestinationDirectory = destDir,
                });
            }
        }

        filesCopied++;

        // Fire final event for this file with updated count
        FireFileCopyProgress(new FileCopyProgressEventArgs
        {
            FileName = displayName,
            FilesCopied = filesCopied,
            TotalFiles = totalFiles,
            BytesCopied = bytesCopied,
            TotalBytes = totalBytes,
            SourceDirectory = sourceDir,
            DestinationDirectory = destDir,
        });
    }

    private void FireCrcValidationProgress(CrcValidationProgressEventArgs e)
        => CrcValidationProgress?.Invoke(this, e);

    private void SetFileAttributes(IEnumerable<FileInfo> files, FileAttributes attribute, bool add)
    {
        foreach (FileInfo fileInfo in files)
        {
            if (add)
            {
                fileInfo.Attributes |= attribute;
                _logger.Information(this, $"Added {attribute} attribute to {fileInfo}", LogTarget.Phase2);
            }
            else
            {
                fileInfo.Attributes &= ~attribute;
                _logger.Information(this, $"Removed {attribute} attribute from {fileInfo}", LogTarget.Phase2);
            }
        }
    }

    private List<(string Path, int Version)> GetValidRarDirectories(string[] directories, BruteForceOptions options)
    {
        var validDirectories = new List<(string Path, int Version)>();

        foreach (var dir in directories)
        {
            string rarExeFilePath = Path.Combine(dir, "rar.exe");
            if (!File.Exists(rarExeFilePath))
            {
                _logger.Information(this, $"rar.exe not found in {dir}");
                continue;
            }

            string dirName = Path.GetFileName(dir);
            int version = ParseRARVersion(dirName);

            if (options.RAROptions.RARVersions.Any(r => r.InRange(version)))
            {
                validDirectories.Add((dir, version));
            }
        }

        return validDirectories;
    }


    private async Task<(bool Found, int NewProgress)> TryProcessCommandLinesAsync(
        BruteForceOptions options,
        int version,
        string rarVersionDirectoryPath,
        string inputFilesDir,
        int totalProgressSize,
        int currentProgress,
        DateTime bruteForceStartDateTime,
        HashSet<string> fileHashes,
        int archiveAttributeIteration,
        int notContentAttributeIteration)
    {
        string rarExeFilePath = Path.Combine(rarVersionDirectoryPath, "rar.exe");
        string rarVersionDirectoryName = Path.GetFileName(rarVersionDirectoryPath);

        // Create subdirectory structure:
        // - inputFilesDir: Contains copy of input files (working directory for RAR)
        // - rarOutputDir: Contains generated RAR files
        string rarOutputDir = Path.Combine(options.OutputDirectoryPath, "output");

        _logger.Debug(this, $"Input files directory: {inputFilesDir}", LogTarget.Phase2);
        _logger.Debug(this, $"RAR output directory: {rarOutputDir}", LogTarget.Phase2);

        if (!Directory.Exists(rarOutputDir))
        {
            Directory.CreateDirectory(rarOutputDir);
        }

        bool loggedRAR6TimestampSkip = false; // Only log RAR 6.x timestamp skip once per version

        for (int j = 0; j < options.RAROptions.CommandLineArguments.Count; j++)
        {
            RARCommandLineArgument[] commandLineArguments = options.RAROptions.CommandLineArguments[j];
            if (CancellationTokenSource.IsCancellationRequested)
            {
                return (false, currentProgress);
            }

            RARArchiveVersion archiveVersion = ParseRARArchiveVersion(commandLineArguments, version);

            // Filter arguments by RAR version and RAR archive version
            IEnumerable<string> filteredArguments = commandLineArguments.Where(
                a => version >= a.MinimumVersion &&
                (!a.MaximumVersion.HasValue || version <= a.MaximumVersion.Value) &&
                (!a.ArchiveVersion.HasValue || a.ArchiveVersion.Value.HasFlag(archiveVersion))
            ).Select(a => a.Argument);

            string joinedArguments = string.Join("", filteredArguments);
            string displayArguments = string.Join(" ", filteredArguments);

            // RAR 6.x doesn't honor timestamp options (-tsc0/-tsa0) for RAR4 format archives
            // Skip this combination to avoid creating archives with wrong extended time flags
            // RAR 7.x is excluded: it only creates RAR7 format and handles timestamps natively
            bool hasTimestampOptions = filteredArguments.Any(a => a.StartsWith("-ts"));
            bool isRAR4Format = archiveVersion == RARArchiveVersion.RAR4 ||
                               (version >= 550 && version < 700 && !filteredArguments.Contains("-ma5"));
            if (version >= 600 && version < 700 && isRAR4Format && hasTimestampOptions)
            {
                if (!loggedRAR6TimestampSkip)
                {
                    _logger.Debug(this, $"Skipping RAR {version} with timestamp options for RAR4 format (known issue)", LogTarget.Phase2);
                    loggedRAR6TimestampSkip = true;
                }
                continue;
            }
            string archiveAttribute = options.RAROptions.SetFileArchiveAttribute != TriState.Unchecked && archiveAttributeIteration == 0 ? "archived-" : string.Empty;
            string notContentIndexedAttribute = options.RAROptions.SetFileNotContentIndexedAttribute != TriState.Unchecked && notContentAttributeIteration == 0 ? "notcontentindexed-" : string.Empty;
            // Output RAR file to the rarOutputDir subdirectory
            string rarFilePath = Path.Combine(rarOutputDir, $"{archiveAttribute}{notContentIndexedAttribute}{rarVersionDirectoryName}-{joinedArguments}.rar");

            if (File.Exists(rarFilePath))
            {
                // Throw error? Overwrite?
                _logger.Debug(this, $"RAR file already exists, skipping: {rarFilePath}", LogTarget.Phase2);
                continue;
            }

            FireBruteForceProgress(new(options.ReleaseDirectoryPath, rarVersionDirectoryPath, displayArguments, totalProgressSize, currentProgress, bruteForceStartDateTime) { PhaseDescription = "Phase 2: Full RAR Creation" });

            // Build final arguments list, including comment option if available
            List<string> finalArguments = [.. filteredArguments];

            // Auto-add -ma4 for RAR 5.50-6.x to force RAR4 format (unless -ma5 was explicitly requested)
            // RAR 7.x doesn't accept -ma4/-ma5 flags
            if (version >= 550 && version < 700 && !finalArguments.Contains("-ma4") && !finalArguments.Contains("-ma5"))
            {
                finalArguments.Insert(0, "-ma4");
            }

            // Add -vn for old volume naming if enabled (available since RAR 3.00, removed in RAR 7.x)
            if (options.RAROptions.UseOldVolumeNaming && version >= 300 && version < 700 && !finalArguments.Contains("-vn"))
            {
                finalArguments.Add("-vn");
            }

            if (!string.IsNullOrEmpty(CommentFilePath))
            {
                // Add comment option: -z<commentfile>
                finalArguments.Add($"-z{CommentFilePath}");
            }

            // ---- Execute RAR ----
            // When CompleteAllVolumes is enabled, we start RAR without auto-kill and check
            // the CRC while it's still running. If the first volume matches, we let RAR
            // finish creating all volumes. If it doesn't match, we kill RAR immediately.
            Task<int>? runningProcessTask = null;
            CancellationTokenSource? processCts = null;

            try
            {
                if (options.RAROptions.CompleteAllVolumes)
                {
                    // Start RAR without automatic early termination
                    processCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationTokenSource.Token);

                    RARProcess process = new(rarExeFilePath, inputFilesDir, rarFilePath, finalArguments, _logger)
                    {
                        LogTarget = LogTarget.Phase2
                    };
                    process.ProcessStatusChanged += Process_ProcessStatusChanged;
                    process.ProcessOutput += Process_ProcessOutput;
                    process.CompressionStatusChanged += Process_CompressionStatusChanged;
                    process.CompressionProgress += Process_CompressionProgress;

                    runningProcessTask = process.RunAsync(processCts.Token);

                    // Wait for first volume to complete (second volume appearing means first is done)
                    using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationTokenSource.Token);
                    Task monitorTask = MonitorForSecondVolumeAsync(rarFilePath, monitorCts);
                    await Task.WhenAny(runningProcessTask, monitorTask);

                    // Clean up monitor if process finished before second volume appeared
                    if (!monitorTask.IsCompleted)
                        monitorCts.Cancel();
                }
                else
                {
                    // Standard: run with early termination (kills RAR after first volume is complete)
                    await RARCompressDirectoryAsync(rarExeFilePath, inputFilesDir, rarFilePath, finalArguments, CancellationTokenSource.Token);
                }

                currentProgress++;
                FireBruteForceProgress(new(options.ReleaseDirectoryPath, rarVersionDirectoryPath, displayArguments, totalProgressSize, currentProgress, bruteForceStartDateTime) { PhaseDescription = "Phase 2: Full RAR Creation" });

                // Check if RAR file or volume files were created
                string? actualRarFilePath = FindCreatedRARFile(rarFilePath);
                if (actualRarFilePath == null)
                {
                    _logger.Information(this, $"RAR file was not created: {rarFilePath}", LogTarget.Phase2);
                    if (runningProcessTask != null && !runningProcessTask.IsCompleted)
                    {
                        processCts!.Cancel();
                        await Task.WhenAny(runningProcessTask, Task.Delay(1000));
                    }
                    continue;
                }

                // Log what file was actually created (may be different from expected if volumes were created)
                if (actualRarFilePath != rarFilePath)
                {
                    _logger.Debug(this, $"Actual file created: {actualRarFilePath} (expected: {Path.GetFileName(rarFilePath)})", LogTarget.Phase2);
                }

                // Apply patching to first volume only (other volumes may still be in progress)
                if (options.RAROptions.NeedsPatching)
                {
                    PatchRARFilesHostOS(actualRarFilePath, options.RAROptions, allVolumes: false);
                }

                string hash = options.HashType switch
                {
                    HashType.SHA1 => SHA1.Calculate(actualRarFilePath),
                    HashType.CRC32 => CRC32.Calculate(actualRarFilePath),
                    _ => throw new IndexOutOfRangeException(nameof(options.HashType))
                };

                _logger.Information(this, $"Hash for {actualRarFilePath}: {hash} (match: {options.Hashes.Contains(hash)})", LogTarget.Phase2);

                // Track if we've seen this hash before (to avoid keeping duplicates)
                bool isDuplicateHash = fileHashes.Contains(hash);
                fileHashes.Add(hash);

                if (!options.Hashes.Contains(hash))
                {
                    // No match - kill background RAR process if still running
                    if (runningProcessTask != null && !runningProcessTask.IsCompleted)
                    {
                        processCts!.Cancel();
                        await Task.WhenAny(runningProcessTask, Task.Delay(1000));
                    }

                    if (options.RAROptions.DeleteRARFiles)
                    {
                        // Delete all non-matching files
                        DeleteRARFileAndVolumes(actualRarFilePath);
                    }
                    else if (options.RAROptions.DeleteDuplicateCRCFiles && isDuplicateHash)
                    {
                        // Delete duplicates to save disk space (only keep unique CRC files)
                        _logger.Debug(this, $"Deleting duplicate hash file: {actualRarFilePath} (hash: {hash})", LogTarget.Phase2);
                        DeleteRARFileAndVolumes(actualRarFilePath);
                    }
                    // If DeleteRARFiles is false and (DeleteDuplicateCRCFiles is false or not a duplicate), keep for debugging

                    continue;
                }

                // ---- MATCH FOUND ----

                // If RAR is still running (CompleteAllVolumes), let it finish creating all volumes
                if (runningProcessTask != null && !runningProcessTask.IsCompleted)
                {
                    _logger.Information(this, "Match found, completing all volumes...", LogTarget.System);
                    await runningProcessTask;
                }

                // Log match to System tab for visibility
                string patchedNote = options.RAROptions.NeedsPatching ? " (patched)" : "";
                _logger.Information(this, $"*** MATCH FOUND{patchedNote}! ***", LogTarget.System);
                _logger.Information(this, $"  Version: {rarVersionDirectoryName}", LogTarget.System);
                _logger.Information(this, $"  Params:  {displayArguments}", LogTarget.System);
                _logger.Information(this, $"  Hash:    {hash}", LogTarget.System);
                _logger.Information(this, $"  RAR:     {actualRarFilePath}", LogTarget.System);

                if (options.RAROptions.NeedsPatching)
                {
                    var opts = options.RAROptions;

                    if (opts.NeedsHostOSPatching)
                    {
                        string hostOS = opts.DetectedFileHostOS.HasValue
                            ? $"{RARPatcher.GetHostOSName(opts.DetectedFileHostOS.Value)} (0x{opts.DetectedFileHostOS.Value:X2})"
                            : "N/A";
                        _logger.Information(this, $"  Patched: Host OS -> {hostOS}, Attributes -> 0x{opts.DetectedFileAttributes ?? 0:X8}", LogTarget.System);

                        if (opts.DetectedCmtHostOS.HasValue || opts.DetectedCmtFileTime.HasValue || opts.DetectedCmtFileAttributes.HasValue)
                        {
                            var cmtParts = new List<string>();
                            if (opts.DetectedCmtHostOS.HasValue)
                                cmtParts.Add($"Host OS -> {RARPatcher.GetHostOSName(opts.DetectedCmtHostOS.Value)} (0x{opts.DetectedCmtHostOS.Value:X2})");
                            if (opts.DetectedCmtFileTime.HasValue)
                                cmtParts.Add($"File Time -> 0x{opts.DetectedCmtFileTime.Value:X8}");
                            if (opts.DetectedCmtFileAttributes.HasValue)
                                cmtParts.Add($"Attributes -> 0x{opts.DetectedCmtFileAttributes.Value:X8}");
                            _logger.Information(this, $"  CMT:     {string.Join(", ", cmtParts)}", LogTarget.System);
                        }
                    }

                    if (opts.NeedsLargePatching)
                    {
                        _logger.Information(this, $"  LARGE:   {(opts.DetectedLargeFlag == true ? "Added" : "Removed")} (HIGH_PACK=0x{opts.DetectedHighPackSize ?? 0:X8}, HIGH_UNP=0x{opts.DetectedHighUnpSize ?? 0:X8})", LogTarget.System);
                    }

                    _logger.Information(this, "  Note:    RAR output was patched post-creation to match original headers", LogTarget.System);
                }

                // Move files to output directory
                string baseName = Path.GetFileNameWithoutExtension(rarFilePath);
                string patchedBaseName = options.RAROptions.NeedsPatching ? baseName + "-patched" : baseName;
                var originalNames = options.RAROptions.OriginalRarFileNames;
                bool useOriginalNames = options.RAROptions.RenameToOriginalNames &&
                                        options.RAROptions.StopOnFirstMatch &&
                                        originalNames.Count > 0;

                if (options.RAROptions.CompleteAllVolumes)
                {
                    // Re-find all volumes now that RAR has completed
                    string? completedRarFilePath = FindCreatedRARFile(rarFilePath);
                    if (completedRarFilePath != null)
                    {
                        // Patch remaining volumes (first volume already patched - will be no-op for it)
                        if (options.RAROptions.NeedsPatching)
                        {
                            PatchRARFilesHostOS(completedRarFilePath, options.RAROptions);
                        }

                        // Move all volumes to output directory
                        List<string> allVolumes = GetAllVolumeFiles(completedRarFilePath);

                        for (int i = 0; i < allVolumes.Count; i++)
                        {
                            string outputFileName = useOriginalNames && i < originalNames.Count
                                ? Path.GetFileName(originalNames[i])
                                : Path.GetFileName(allVolumes[i]).Replace(baseName, patchedBaseName);
                            string outputPath = Path.Combine(options.OutputDirectoryPath, outputFileName);
                            if (!File.Exists(outputPath))
                            {
                                File.Move(allVolumes[i], outputPath);
                                _logger.Information(this, $"  Volume: {outputFileName}", LogTarget.System);
                            }
                        }

                        _logger.Information(this, $"  Completed {allVolumes.Count} volume(s)", LogTarget.System);
                    }
                }
                else
                {
                    // Standard behavior: just move the first .rar file
                    string outputFileName = useOriginalNames
                        ? Path.GetFileName(originalNames[0])
                        : Path.GetFileName(actualRarFilePath).Replace(baseName, patchedBaseName);
                    string outputPath = Path.Combine(options.OutputDirectoryPath, outputFileName);
                    if (!File.Exists(outputPath))
                    {
                        File.Move(actualRarFilePath, outputPath);
                    }
                }

                return (true, currentProgress);
            }
            finally
            {
                processCts?.Dispose();
            }
        }

        return (false, currentProgress);
    }

    private static string? FindCreatedRARFile(string expectedRarFilePath)
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

    private void DeleteRARFileAndVolumes(string rarFilePath)
    {
        try
        {
            string directory = Path.GetDirectoryName(rarFilePath) ?? string.Empty;
            string fileName = Path.GetFileName(rarFilePath);
            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(rarFilePath);

            // Determine the base name (remove .partXX.rar or .rXX suffix if present)
            string baseName = fileNameWithoutExtension;

            // Check if this is a volume file and extract base name
            if (fileName.Contains(".part") && fileName.EndsWith(".rar"))
            {
                // Format: filename.part01.rar - remove .part01
                int partIndex = fileName.IndexOf(".part");
                if (partIndex > 0)
                {
                    baseName = fileName[..partIndex];
                }
            }

            // Delete all related volume files using pattern matching
            // Pattern 1: filename.partXX.rar (zero-padded)
            string[] partFiles = Directory.GetFiles(directory, $"{baseName}.part*.rar");
            foreach (string file in partFiles)
            {
                try
                {
                    File.Delete(file);
                    _logger.Debug(this, $"Deleted volume file: {file}", LogTarget.Phase2);
                }
                catch (Exception ex)
                {
                    _logger.Information(this, $"Failed to delete volume file {file}: {ex.Message}", LogTarget.Phase2);
                }
            }

            // Pattern 2: filename.rXX (old format - r00, r01, r02, etc.)
            string[] rxxFiles = Directory.GetFiles(directory, $"{baseName}.r??");
            foreach (string file in rxxFiles)
            {
                try
                {
                    File.Delete(file);
                    _logger.Debug(this, $"Deleted volume file: {file}", LogTarget.Phase2);
                }
                catch (Exception ex)
                {
                    _logger.Information(this, $"Failed to delete volume file {file}: {ex.Message}", LogTarget.Phase2);
                }
            }

            // Also delete the main .rar file if it exists (old format first volume)
            string mainRarFile = Path.Combine(directory, $"{baseName}.rar");
            if (File.Exists(mainRarFile))
            {
                try
                {
                    File.Delete(mainRarFile);
                    _logger.Debug(this, $"Deleted main RAR file: {mainRarFile}", LogTarget.Phase2);
                }
                catch (Exception ex)
                {
                    _logger.Information(this, $"Failed to delete main RAR file {mainRarFile}: {ex.Message}", LogTarget.Phase2);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Information(this, $"Failed to delete RAR file and volumes: {rarFilePath}{Environment.NewLine}{ex.Message}", LogTarget.Phase2);
        }
    }

    private void CopyDirectory(string sourceDir, string destDir)
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
            CancellationTokenSource.Token.ThrowIfCancellationRequested();

            string relative = Path.GetRelativePath(sourceDir, file);
            string destFile = Path.Combine(destDir, relative);

            // Ensure parent directory exists (for top-level files)
            string? destParent = Path.GetDirectoryName(destFile);
            if (!string.IsNullOrEmpty(destParent))
                Directory.CreateDirectory(destParent);

            CopyFileWithProgress(file, destFile, Path.GetFileName(file), ref bytesCopied, ref filesCopied,
                allFiles.Length, totalBytes, sourceDir, destDir);
        }
    }

    private void CopySelectedEntries(string sourceDir, string destDir, HashSet<string> filePaths, HashSet<string> directoryPaths)
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
                continue;
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
            CancellationTokenSource.Token.ThrowIfCancellationRequested();

            if (!TryResolveRelativePath(sourceRoot, file, out string relativeFile))
            {
                skippedEntries++;
                continue;
            }

            string sourcePath = Path.Combine(sourceRoot, relativeFile);
            if (!File.Exists(sourcePath))
            {
                missingFiles++;
                _logger.Warning(this, $"SRR entry not found on disk: {relativeFile}");
                continue;
            }

            string destPath = Path.Combine(destRoot, relativeFile);
            string? destDirectory = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDirectory))
            {
                Directory.CreateDirectory(destDirectory);
            }

            CopyFileWithProgress(sourcePath, destPath, Path.GetFileName(relativeFile), ref bytesCopied, ref filesCopied,
                totalFiles, totalBytes, sourceDir, destDir);
        }

        if (skippedEntries > 0)
        {
            _logger.Warning(this, $"Skipped {skippedEntries} SRR entries due to invalid paths.");
        }

        if (missingFiles > 0)
        {
            throw new FileNotFoundException($"{missingFiles} file(s) from the SRR archive list are missing in the release directory.");
        }
    }

    private static bool TryResolveRelativePath(string baseFullPath, string entryPath, out string relativePath)
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
    /// Validates that all required input files from the SRR exist in the release directory
    /// and that their CRC32 values match. Runs before any brute-forcing begins.
    /// </summary>
    private bool ValidateInputFiles(BruteForceOptions options)
    {
        var opts = options.RAROptions;
        string releaseDir = options.ReleaseDirectoryPath;

        _logger.Information(this, $"=== Validating Input Files ({opts.ArchiveFilePaths.Count} files, {opts.ArchiveDirectoryPaths.Count} directories) ===", LogTarget.System);

        foreach (string file in opts.ArchiveFilePaths.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            _logger.Information(this, $"  Required: {file}", LogTarget.System);
        }

        List<string> missing = [];

        foreach (string file in opts.ArchiveFilePaths)
        {
            string filePath = Path.Combine(releaseDir, file);
            if (!File.Exists(filePath))
            {
                missing.Add(file);
                _logger.Error(this, $"  MISSING: {file}", LogTarget.System);
                continue;
            }

            // CRC validation is deferred to ValidateArchiveFileCrcs (after copy)
            // which has progress UI support
        }

        if (missing.Count > 0)
        {
            _logger.Error(this, $"Input validation failed: {missing.Count} file(s) missing.", LogTarget.System);
            return false;
        }

        _logger.Information(this, $"Input validation passed: all {opts.ArchiveFilePaths.Count} file(s) present.", LogTarget.System);
        return true;
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
                totalBytes += new FileInfo(filePath).Length;
        }

        List<string> missing = [];
        List<string> mismatched = [];
        int filesVerified = 0;
        long cumulativeBytesVerified = 0;

        foreach (KeyValuePair<string, string> entry in expectedCrcs)
        {
            CancellationTokenSource.Token.ThrowIfCancellationRequested();

            string relativePath = entry.Key;
            string expectedCrc = entry.Value;
            string filePath = Path.Combine(inputDirectory, relativePath);

            if (!File.Exists(filePath))
            {
                missing.Add(relativePath);
                continue;
            }

            long fileSize = new FileInfo(filePath).Length;
            long baseBytes = cumulativeBytesVerified;

            FireCrcValidationProgress(new CrcValidationProgressEventArgs
            {
                FileName = relativePath,
                FilesVerified = filesVerified,
                TotalFiles = expectedCrcs.Count,
                BytesVerified = baseBytes,
                TotalBytes = totalBytes
            });

            string actualCrc = CRC32.Calculate(filePath, bytesRead =>
            {
                FireCrcValidationProgress(new CrcValidationProgressEventArgs
                {
                    FileName = relativePath,
                    FilesVerified = filesVerified,
                    TotalFiles = expectedCrcs.Count,
                    BytesVerified = baseBytes + bytesRead,
                    TotalBytes = totalBytes
                });
            }, CancellationTokenSource.Token);

            cumulativeBytesVerified += fileSize;
            filesVerified++;

            if (!string.Equals(actualCrc, expectedCrc, StringComparison.OrdinalIgnoreCase))
            {
                mismatched.Add($"{relativePath} (expected {expectedCrc}, got {actualCrc})");
            }
        }

        // Fire final 100% event
        FireCrcValidationProgress(new CrcValidationProgressEventArgs
        {
            FileName = "",
            FilesVerified = expectedCrcs.Count,
            TotalFiles = expectedCrcs.Count,
            BytesVerified = totalBytes,
            TotalBytes = totalBytes
        });

        if (missing.Count == 0 && mismatched.Count == 0)
        {
            _logger.Information(this, $"SRR CRC32 validation passed for {expectedCrcs.Count} file(s).");
            return;
        }

        foreach (string entry in missing)
        {
            _logger.Error(this, $"SRR CRC32 validation missing file: {entry}");
        }

        foreach (string entry in mismatched)
        {
            _logger.Error(this, $"SRR CRC32 validation mismatch: {entry}");
        }

        int issueCount = missing.Count + mismatched.Count;
        throw new InvalidDataException($"SRR CRC32 validation failed for {issueCount} file(s).");
    }

    private string PrepareInputDirectory(BruteForceOptions options)
    {
        string inputFilesDir = Path.Combine(options.OutputDirectoryPath, "input");
        if (Directory.Exists(inputFilesDir))
        {
            _logger.Information(this, $"Cleaning existing input directory: {inputFilesDir}");
            Directory.Delete(inputFilesDir, true);
        }

        _logger.Information(this, $"Creating input directory and copying files from {options.ReleaseDirectoryPath} to {inputFilesDir}");
        Directory.CreateDirectory(inputFilesDir);
        if (options.RAROptions.HasArchiveFileList)
        {
            _logger.Information(this, $"Using SRR file list: {options.RAROptions.ArchiveFilePaths.Count} files, {options.RAROptions.ArchiveDirectoryPaths.Count} dirs");
            CopySelectedEntries(options.ReleaseDirectoryPath, inputFilesDir, options.RAROptions.ArchiveFilePaths, options.RAROptions.ArchiveDirectoryPaths);
        }
        else
        {
            CopyDirectory(options.ReleaseDirectoryPath, inputFilesDir);
        }
        _logger.Information(this, $"Finished copying {Directory.GetFiles(inputFilesDir, "*.*", SearchOption.AllDirectories).Length} files to input directory");

        if (options.RAROptions.ArchiveFileCrcs.Count > 0)
        {
            _logger.Information(this, $"Validating SRR CRC32 entries: {options.RAROptions.ArchiveFileCrcs.Count} file(s)");
            ValidateArchiveFileCrcs(inputFilesDir, options.RAROptions.ArchiveFileCrcs);
        }

        int fileTimestampCount = options.RAROptions.FileTimestamps.Count;
        int fileCreationCount = options.RAROptions.FileCreationTimes.Count;
        int fileAccessCount = options.RAROptions.FileAccessTimes.Count;
        // Apply file timestamps before directory timestamps to keep directory times authoritative.
        if (fileTimestampCount + fileCreationCount + fileAccessCount > 0)
        {
            _logger.Information(this, $"Applying file timestamps: mtime {fileTimestampCount}, ctime {fileCreationCount}, atime {fileAccessCount}");
            ApplyFileTimestamps(inputFilesDir, options.RAROptions.FileTimestamps, options.RAROptions.FileCreationTimes, options.RAROptions.FileAccessTimes);
        }

        int dirTimestampCount = options.RAROptions.DirectoryTimestamps.Count;
        int dirCreationCount = options.RAROptions.DirectoryCreationTimes.Count;
        int dirAccessCount = options.RAROptions.DirectoryAccessTimes.Count;
        if (dirTimestampCount + dirCreationCount + dirAccessCount > 0)
        {
            _logger.Information(this, $"Applying directory timestamps: mtime {dirTimestampCount}, ctime {dirCreationCount}, atime {dirAccessCount}");
            ApplyDirectoryTimestamps(inputFilesDir, options.RAROptions.DirectoryTimestamps, options.RAROptions.DirectoryCreationTimes, options.RAROptions.DirectoryAccessTimes);
        }

        // Create comment file if archive has a comment
        CommentFilePath = null;
        if (options.RAROptions.ArchiveCommentBytes != null && options.RAROptions.ArchiveCommentBytes.Length > 0)
        {
            // Use raw bytes for exact reconstruction
            string commentFilePath = Path.Combine(options.OutputDirectoryPath, "comment.txt");
            try
            {
                File.WriteAllBytes(commentFilePath, options.RAROptions.ArchiveCommentBytes);
                CommentFilePath = commentFilePath;
                _logger.Information(this, $"Created comment file: {commentFilePath} ({options.RAROptions.ArchiveCommentBytes.Length} bytes)");
            }
            catch (Exception ex)
            {
                _logger.Warning(this, $"Failed to create comment file: {ex.Message}");
            }
        }
        else if (!string.IsNullOrEmpty(options.RAROptions.ArchiveComment))
        {
            // Fallback to string (for manually entered comments)
            string commentFilePath = Path.Combine(options.OutputDirectoryPath, "comment.txt");
            try
            {
                // Use UTF-8 without BOM
                File.WriteAllText(commentFilePath, options.RAROptions.ArchiveComment, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                CommentFilePath = commentFilePath;
                _logger.Information(this, $"Created comment file: {commentFilePath} ({options.RAROptions.ArchiveComment.Length} chars, fallback)");
            }
            catch (Exception ex)
            {
                _logger.Warning(this, $"Failed to create comment file: {ex.Message}");
            }
        }

        return inputFilesDir;
    }

    private void ApplyFileTimestamps(string inputDirectory, Dictionary<string, DateTime> modifiedTimes, Dictionary<string, DateTime> creationTimes, Dictionary<string, DateTime> accessTimes)
    {
        // Order matters: set creation/access first so modified time ends up as the final write.
        ApplyFileTimestampEntries(inputDirectory, creationTimes, File.SetCreationTime, "creation");
        ApplyFileTimestampEntries(inputDirectory, accessTimes, File.SetLastAccessTime, "access");
        ApplyFileTimestampEntries(inputDirectory, modifiedTimes, File.SetLastWriteTime, "modified");
    }

    private void ApplyFileTimestampEntries(string inputDirectory, Dictionary<string, DateTime> timestamps, Action<string, DateTime> setter, string label)
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
                _logger.Warning(this, $"Failed to set {label} timestamp for file {relativePath}: {ex.Message}");
            }
        }
    }

    private void ApplyDirectoryTimestamps(string inputDirectory, Dictionary<string, DateTime> modifiedTimes, Dictionary<string, DateTime> creationTimes, Dictionary<string, DateTime> accessTimes)
    {
        // Order matters: set creation/access first so modified time ends up as the final write.
        ApplyDirectoryTimestampEntries(inputDirectory, creationTimes, Directory.SetCreationTime, "creation");
        ApplyDirectoryTimestampEntries(inputDirectory, accessTimes, Directory.SetLastAccessTime, "access");
        ApplyDirectoryTimestampEntries(inputDirectory, modifiedTimes, Directory.SetLastWriteTime, "modified");
    }

    private void ApplyDirectoryTimestampEntries(string inputDirectory, Dictionary<string, DateTime> timestamps, Action<string, DateTime> setter, string label)
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
                _logger.Warning(this, $"Failed to set {label} timestamp for directory {relativePath}: {ex.Message}");
            }
        }
    }

    private void PatchRARFilesHostOS(string rarFilePath, RAROptions rarOptions, bool allVolumes = true)
    {
        if (!rarOptions.NeedsPatching)
        {
            return;
        }

        try
        {
            // Collect files to patch (all volumes or just the specified file)
            List<string> filesToPatch = allVolumes ? GetAllVolumeFiles(rarFilePath) : [rarFilePath];

            if (rarOptions.NeedsHostOSPatching)
            {
                string hostOSName = RARPatcher.GetHostOSName(rarOptions.DetectedFileHostOS!.Value);
                _logger.Information(this, $"Patching to match SRR: Host OS={hostOSName} (0x{rarOptions.DetectedFileHostOS.Value:X2}), Attrs=0x{rarOptions.DetectedFileAttributes ?? 0:X8} for {filesToPatch.Count} file(s)", LogTarget.Phase2);
            }

            if (rarOptions.NeedsLargePatching)
            {
                _logger.Information(this, $"Patching LARGE flag: {(rarOptions.DetectedLargeFlag == true ? "adding" : "removing")} for {filesToPatch.Count} file(s)", LogTarget.Phase2);
            }

            // Build patch options
            var patchOptions = new PatchOptions
            {
                // LARGE flag patching
                SetLargeFlag = rarOptions.DetectedLargeFlag,
                HighPackSize = rarOptions.DetectedHighPackSize ?? 0,
                HighUnpSize = rarOptions.DetectedHighUnpSize ?? 0
            };

            // Set Host OS options if Host OS differs from current platform
            if (rarOptions.NeedsHostOSPatching)
            {
                patchOptions.FileHostOS = rarOptions.DetectedFileHostOS;
                patchOptions.PatchServiceBlocks = true;
                patchOptions.ServiceBlockHostOS = rarOptions.DetectedCmtHostOS ?? rarOptions.DetectedFileHostOS;
                patchOptions.ServiceBlockFileTime = rarOptions.DetectedCmtFileTime;
            }

            // Set attribute options if detected (attributes can differ even when Host OS matches)
            if (rarOptions.NeedsAttributePatching)
            {
                patchOptions.FileAttributes = rarOptions.DetectedFileAttributes;
                patchOptions.PatchServiceBlocks = true;
                patchOptions.ServiceBlockAttributes = rarOptions.DetectedCmtFileAttributes ?? rarOptions.DetectedFileAttributes;
            }

            int totalPatched = 0;
            foreach (string filePath in filesToPatch)
            {
                try
                {
                    // LARGE patching must run first (structural change) before in-place patching
                    if (rarOptions.NeedsLargePatching)
                    {
                        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                        bool largeModified = RARPatcher.PatchLargeFlags(stream, patchOptions);
                        if (largeModified)
                        {
                            _logger.Debug(this, $"LARGE flag patched in: {filePath}", LogTarget.Phase2);
                        }
                    }

                    // In-place patching (Host OS, Attributes, File Time, CRC)
                    var results = RARPatcher.PatchFile(filePath, patchOptions);
                    totalPatched += results.Count;

                    foreach (var result in results)
                    {
                        string blockDesc = result.BlockType == RAR4BlockType.Service
                            ? $"Service ({result.FileName ?? "?"})"
                            : $"File ({result.FileName ?? "?"})";
                        _logger.Debug(this, $"Patched {blockDesc}: Host OS 0x{result.OriginalHostOS:X2} -> 0x{result.NewHostOS:X2}, Attrs 0x{result.OriginalAttributes:X8} -> 0x{result.NewAttributes:X8}, CRC 0x{result.OriginalCrc:X4} -> 0x{result.NewCrc:X4}", LogTarget.Phase2);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning(this, $"Failed to patch {filePath}: {ex.Message}", LogTarget.Phase2);
                }
            }

            _logger.Information(this, $"Patched {totalPatched} block(s) in {filesToPatch.Count} file(s)", LogTarget.Phase2);
        }
        catch (Exception ex)
        {
            _logger.Warning(this, $"Patching failed: {ex.Message}", LogTarget.Phase2);
        }
    }

    private static List<string> GetAllVolumeFiles(string firstVolumePath)
    {
        var files = new List<string>();
        string directory = Path.GetDirectoryName(firstVolumePath) ?? string.Empty;
        string fileName = Path.GetFileName(firstVolumePath);
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(firstVolumePath);

        // Determine the base name (remove .partXX if present)
        string baseName = fileNameWithoutExtension;
        if (fileName.Contains(".part") && fileName.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
        {
            int partIndex = fileName.IndexOf(".part", StringComparison.OrdinalIgnoreCase);
            if (partIndex > 0)
            {
                baseName = fileName[..partIndex];
            }
        }

        // Check for partXX.rar format volumes
        string[] partFiles = Directory.GetFiles(directory, $"{baseName}.part*.rar");
        if (partFiles.Length > 0)
        {
            files.AddRange(partFiles.OrderBy(f => f));
            return files;
        }

        // Check for .rar + .rXX format volumes
        string mainRar = Path.Combine(directory, $"{baseName}.rar");
        if (File.Exists(mainRar))
        {
            files.Add(mainRar);
        }

        // .r?? matches .r00-.r99 but also .rar - exclude .rar to avoid duplicates
        string[] rxxFiles = Directory.GetFiles(directory, $"{baseName}.r??");
        if (rxxFiles.Length > 0)
        {
            files.AddRange(rxxFiles
                .Where(f => !f.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f));
        }

        // If no volumes found, just return the original file
        if (files.Count == 0 && File.Exists(firstVolumePath))
        {
            files.Add(firstVolumePath);
        }

        return files;
    }

    /// <summary>
    /// Extracts the CMT block compressed data from a RAR file.
    /// </summary>
    private static byte[]? ExtractCmtCompressedData(string rarFilePath)
    {
        try
        {
            using var fs = new FileStream(rarFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(fs);

            // Skip RAR signature (7 bytes for RAR 4.x)
            if (fs.Length < 7)
                return null;

            fs.Position = 7;

            while (fs.Position + 7 <= fs.Length)
            {
                long blockStart = fs.Position;

                // Read base header
                ushort crc = reader.ReadUInt16();
                byte blockType = reader.ReadByte();
                ushort flags = reader.ReadUInt16();
                ushort headerSize = reader.ReadUInt16();

                if (headerSize < 7 || blockStart + headerSize > fs.Length)
                    break;

                // Check if this is a service block (0x7A)
                if (blockType == 0x7A && headerSize >= 32)
                {
                    // Read ADD_SIZE (4 bytes at offset 7)
                    uint addSize = reader.ReadUInt32();

                    // Read to get the sub-type name
                    // Skip: UnpSize(4), HostOS(1), FileCRC(4), FileTime(4), UnpVer(1), Method(1), NameSize(2), Attr(4) = 21 bytes
                    fs.Position = blockStart + 7 + 4 + 4 + 1 + 4 + 4 + 1 + 1;
                    ushort nameSize = reader.ReadUInt16();

                    // Skip Attr (4 bytes)
                    fs.Position += 4;

                    // Read the sub-type name
                    if (nameSize > 0 && fs.Position + nameSize <= fs.Length)
                    {
                        byte[] nameBytes = reader.ReadBytes(nameSize);
                        string subType = Encoding.ASCII.GetString(nameBytes);

                        if (string.Equals(subType, "CMT", StringComparison.OrdinalIgnoreCase))
                        {
                            // Found CMT block - read the compressed data
                            long dataStart = blockStart + headerSize;
                            if (dataStart + addSize <= fs.Length && addSize > 0)
                            {
                                fs.Position = dataStart;
                                byte[] data = reader.ReadBytes((int)addSize);
                                return data;
                            }
                        }
                    }

                    // Skip this block
                    fs.Position = blockStart + headerSize + addSize;
                }
                else
                {
                    // Skip this block
                    bool hasAddSize = (flags & 0x8000) != 0 || blockType == 0x74 || blockType == 0x7A;
                    uint addSize = 0;
                    if (hasAddSize)
                    {
                        fs.Position = blockStart + 7;
                        if (fs.Position + 4 <= fs.Length)
                        {
                            addSize = reader.ReadUInt32();
                        }
                    }
                    fs.Position = blockStart + headerSize + addSize;
                }

                // Safety check
                if (fs.Position <= blockStart)
                    break;
            }
        }
        catch
        {
            // Failed to extract CMT data
        }

        return null;
    }

    /// <summary>
    /// Phase 1: Brute-force RAR versions using only the comment block.
    /// Returns a list of (directoryPath, version) tuples that produced matching CMT blocks.
    /// </summary>
    private async Task<List<(string Path, int Version)>> BruteForceCommentPhaseAsync(
        BruteForceOptions options,
        List<(string Path, int Version)> allRarDirectories)
    {
        var matchedVersions = new List<(string Path, int Version)>();
        byte[]? expectedCmtData = options.RAROptions.CmtCompressedData;

        if (expectedCmtData == null || expectedCmtData.Length == 0)
        {
            _logger.Information(this, "Phase 1 skipped: No CMT compressed data available", LogTarget.Phase1);
            return allRarDirectories; // Return all versions if no CMT data
        }

        _logger.Information(this, $"=== PHASE 1: Comment Block Brute-Force ===", LogTarget.Phase1);
        _logger.Information(this, $"Expected CMT compressed data: {expectedCmtData.Length} bytes", LogTarget.Phase1);

        // Create a temporary directory for Phase 1 testing
        string phase1Dir = Path.Combine(options.OutputDirectoryPath, "phase1_temp");
        if (Directory.Exists(phase1Dir))
        {
            Directory.Delete(phase1Dir, true);
        }
        Directory.CreateDirectory(phase1Dir);

        // Create a small dummy file for testing
        string dummyInputDir = Path.Combine(phase1Dir, "input");
        Directory.CreateDirectory(dummyInputDir);
        string dummyFilePath = Path.Combine(dummyInputDir, "dummy.txt");
        File.WriteAllText(dummyFilePath, "dummy");

        // Create comment file
        string commentFilePath = Path.Combine(phase1Dir, "comment.txt");
        if (options.RAROptions.ArchiveCommentBytes != null)
        {
            File.WriteAllBytes(commentFilePath, options.RAROptions.ArchiveCommentBytes);
        }
        else if (!string.IsNullOrEmpty(options.RAROptions.ArchiveComment))
        {
            File.WriteAllText(commentFilePath, options.RAROptions.ArchiveComment, new UTF8Encoding(false));
        }

        string outputDir = Path.Combine(phase1Dir, "output");
        Directory.CreateDirectory(outputDir);

        int totalTests = allRarDirectories.Count;
        int currentTest = 0;
        int matchCount = 0;
        DateTime phase1StartTime = DateTime.Now;

        // For Phase 1, use the CMT compression method (not file compression method)
        // CMT CompressionMethod is stored as raw 0x30-0x35, convert to 0-5 for -m flag
        string cmtMethodArg;
        if (options.RAROptions.CmtCompressionMethod.HasValue)
        {
            byte rawMethod = options.RAROptions.CmtCompressionMethod.Value;
            int cmtMethod = rawMethod >= 0x30 ? rawMethod - 0x30 : rawMethod;
            if (cmtMethod >= 0 && cmtMethod <= 5)
            {
                cmtMethodArg = $"-m{cmtMethod}";
            }
            else
            {
                cmtMethodArg = "-m3"; // Default to normal
            }
        }
        else
        {
            cmtMethodArg = "-m3"; // Default to normal compression
        }

        // For Phase 1, always use -md64k (comments always use 64KB dictionary window)
        string cmtDictArg = "-md64k";

        _logger.Information(this, $"CMT compression method: {cmtMethodArg}", LogTarget.Phase1);
        _logger.Information(this, $"CMT dictionary size: {cmtDictArg}", LogTarget.Phase1);

        foreach (var (rarVersionDir, version) in allRarDirectories)
        {
            if (CancellationTokenSource.IsCancellationRequested)
                break;

            string rarExePath = Path.Combine(rarVersionDir, "rar.exe");
            string versionName = Path.GetFileName(rarVersionDir);

            currentTest++;

            // Fire progress event for Phase 1
            FireBruteForceProgress(new(options.ReleaseDirectoryPath, rarVersionDir, $"{cmtMethodArg} {cmtDictArg}", totalTests, currentTest, phase1StartTime) { PhaseDescription = "Phase 1: Comment Block Filtering" });

            // Build arguments using CMT-specific compression method and dictionary
            List<string> args = ["a", "-r", cmtMethodArg, cmtDictArg, $"-z{commentFilePath}"];

            // Add -ma4 for RAR 5.50-6.x to create RAR4 format (RAR 7.x doesn't accept -ma4)
            if (version >= 550 && version < 700)
            {
                args.Add("-ma4");
            }

            // Add timestamp options if RAR version supports them (3.20+)
            // For CMT blocks, we typically disable ctime and atime
            if (version >= 320)
            {
                args.Add("-tsc-");
                args.Add("-tsa-");
            }

            string testRarPath = Path.Combine(outputDir, $"test_{versionName}_{cmtMethodArg}_{cmtDictArg}.rar"
                .Replace("-", "").Replace("/", ""));

            try
            {
                // Create the test RAR
                var process = new RARProcess(rarExePath, dummyInputDir, testRarPath, args, _logger)
                {
                    LogTarget = LogTarget.Phase1
                };
                await process.RunAsync(CancellationTokenSource.Token);

                if (!File.Exists(testRarPath))
                {
                    continue;
                }

                // Extract CMT data from generated RAR
                byte[]? generatedCmtData = ExtractCmtCompressedData(testRarPath);

                // Clean up test file
                try { File.Delete(testRarPath); } catch { }

                if (generatedCmtData == null)
                {
                    continue;
                }

                // Compare CMT compressed data
                if (generatedCmtData.SequenceEqual(expectedCmtData))
                {
                    matchCount++;
                    if (!matchedVersions.Any(v => v.Path == rarVersionDir))
                    {
                        matchedVersions.Add((rarVersionDir, version));
                        _logger.Information(this, $"Phase 1 MATCH: {versionName} {cmtMethodArg} {cmtDictArg}", LogTarget.Phase1);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Debug(this, $"Phase 1 test failed for {versionName}: {ex.Message}", LogTarget.Phase1);
            }
        }

        // Clean up Phase 1 temp directory
        try
        {
            Directory.Delete(phase1Dir, true);
        }
        catch { }

        _logger.Information(this, $"Phase 1 complete: {totalTests} tests, {matchCount} matches, {matchedVersions.Count} unique versions", LogTarget.Phase1);

        if (matchedVersions.Count == 0)
        {
            _logger.Warning(this, "Phase 1 found no matches - falling back to all versions for Phase 2", LogTarget.Phase1);
            return allRarDirectories;
        }

        return matchedVersions;
    }

    /// <summary>
    /// Logs all brute-force settings for debugging and tracking purposes.
    /// </summary>
    private void LogBruteForceSettings(BruteForceOptions options)
    {
        var opts = options.RAROptions;

        _logger.Information(this, "=== Settings ===", LogTarget.System);

        // General settings
        _logger.Information(this, $"  Stop on first match: {opts.StopOnFirstMatch}", LogTarget.System);
        _logger.Information(this, $"  Delete non-matching RAR files: {opts.DeleteRARFiles}", LogTarget.System);
        _logger.Information(this, $"  Delete duplicate CRC files: {opts.DeleteDuplicateCRCFiles}", LogTarget.System);

        // File attributes
        _logger.Information(this, $"  Set Archive attribute: {opts.SetFileArchiveAttribute}", LogTarget.System);
        _logger.Information(this, $"  Set NotContentIndexed attribute: {opts.SetFileNotContentIndexedAttribute}", LogTarget.System);

        // Version ranges
        if (opts.RARVersions.Count > 0)
        {
            var versionRanges = string.Join(", ", opts.RARVersions.Select(v =>
                v.End > v.Start ? $"{v.Start}-{v.End}" : v.Start.ToString()));
            _logger.Information(this, $"  RAR version ranges: {versionRanges}", LogTarget.System);
        }
        else
        {
            _logger.Information(this, "  RAR version ranges: All versions", LogTarget.System);
        }

        // Command line arguments
        _logger.Information(this, $"  Command line combinations: {opts.CommandLineArguments.Count}", LogTarget.System);
        if (opts.CommandLineArguments.Count > 0 && opts.CommandLineArguments.Count <= 10)
        {
            foreach (var args in opts.CommandLineArguments)
            {
                var argStr = string.Join(" ", args.Select(a => a.Argument));
                _logger.Debug(this, $"    Args: {argStr}", LogTarget.System);
            }
        }

        // Archive comment
        _logger.Information(this, $"  Has archive comment: {!string.IsNullOrEmpty(opts.ArchiveComment)}", LogTarget.System);
        _logger.Information(this, $"  Can use Phase 1 (CMT): {opts.CanUseCommentPhase}", LogTarget.System);
        if (opts.CmtCompressionMethod.HasValue)
        {
            string methodName = opts.CmtCompressionMethod.Value switch
            {
                0x30 => "Store",
                0x31 => "Fastest",
                0x32 => "Fast",
                0x33 => "Normal",
                0x34 => "Good",
                0x35 => "Best",
                _ => $"0x{opts.CmtCompressionMethod.Value:X2}"
            };
            _logger.Information(this, $"  CMT compression method: {methodName}", LogTarget.System);
        }

        // Volume naming
        _logger.Information(this, $"  Use old volume naming (-vn): {opts.UseOldVolumeNaming}", LogTarget.System);

        // Host OS patching
        _logger.Information(this, $"  Enable Host OS patching: {opts.EnableHostOSPatching}", LogTarget.System);
        if (opts.DetectedFileHostOS.HasValue)
        {
            string hostOSName = opts.DetectedFileHostOS.Value switch
            {
                0 => "MS-DOS",
                1 => "OS/2",
                2 => "Windows",
                3 => "Unix",
                4 => "Mac OS",
                5 => "BeOS",
                _ => $"Unknown ({opts.DetectedFileHostOS.Value})"
            };
            _logger.Information(this, $"  Detected file Host OS: {hostOSName} (0x{opts.DetectedFileHostOS.Value:X2})", LogTarget.System);
        }
        if (opts.DetectedFileAttributes.HasValue)
        {
            _logger.Information(this, $"  Detected file attributes: 0x{opts.DetectedFileAttributes.Value:X8}", LogTarget.System);
        }
        if (opts.DetectedCmtHostOS.HasValue)
        {
            _logger.Information(this, $"  Detected CMT Host OS: 0x{opts.DetectedCmtHostOS.Value:X2}", LogTarget.System);
        }
        if (opts.DetectedCmtFileTime.HasValue)
        {
            _logger.Information(this, $"  Detected CMT file time: 0x{opts.DetectedCmtFileTime.Value:X8}", LogTarget.System);
        }
        if (opts.DetectedCmtFileAttributes.HasValue)
        {
            _logger.Information(this, $"  Detected CMT attributes: 0x{opts.DetectedCmtFileAttributes.Value:X8}", LogTarget.System);
        }
        _logger.Information(this, $"  Needs Host OS patching: {opts.NeedsHostOSPatching}", LogTarget.System);
        _logger.Information(this, $"  Needs attribute patching: {opts.NeedsAttributePatching}", LogTarget.System);

        // LARGE flag
        if (opts.DetectedLargeFlag.HasValue)
        {
            _logger.Information(this, $"  Detected LARGE flag: {opts.DetectedLargeFlag.Value}", LogTarget.System);
            if (opts.DetectedLargeFlag.Value)
            {
                _logger.Information(this, $"  Detected HIGH_PACK_SIZE: 0x{opts.DetectedHighPackSize ?? 0:X8}", LogTarget.System);
                _logger.Information(this, $"  Detected HIGH_UNP_SIZE: 0x{opts.DetectedHighUnpSize ?? 0:X8}", LogTarget.System);
            }
        }
        _logger.Information(this, $"  Needs LARGE patching: {opts.NeedsLargePatching}", LogTarget.System);

        // File/directory counts
        _logger.Information(this, $"  File timestamps to apply: {opts.FileTimestamps.Count}", LogTarget.System);
        _logger.Information(this, $"  File creation times to apply: {opts.FileCreationTimes.Count}", LogTarget.System);
        _logger.Information(this, $"  File access times to apply: {opts.FileAccessTimes.Count}", LogTarget.System);
        _logger.Information(this, $"  Directory timestamps to apply: {opts.DirectoryTimestamps.Count}", LogTarget.System);
        _logger.Information(this, $"  Archive file CRCs to verify: {opts.ArchiveFileCrcs.Count}", LogTarget.System);

        if (opts.HasArchiveFileList)
        {
            _logger.Information(this, $"  Archive file paths: {opts.ArchiveFilePaths.Count}", LogTarget.System);
            _logger.Information(this, $"  Archive directory paths: {opts.ArchiveDirectoryPaths.Count}", LogTarget.System);
        }

        _logger.Information(this, "=== End Settings ===", LogTarget.System);
    }
}
