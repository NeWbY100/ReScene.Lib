using ReScene.Core.Cryptography;
using ReScene.Core.Diagnostics;
using ReScene.Core.IO;
using ReScene.RAR;

namespace ReScene.Core;

/// <summary>
/// Orchestrates brute-force RAR reconstruction by testing RAR version and argument combinations
/// against expected hash values until a match is found.
/// </summary>
public partial class Manager
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Manager"/> class.
    /// </summary>
    /// <param name="logger">
    /// The logger to use, or <see langword="null"/> to discard log output.
    /// </param>
    public Manager(IReSceneLogger? logger = null)
    {
        _logger = logger ?? NullReSceneLogger.Instance;
        _processLogManager = new ProcessLogManager(_logger, this);
    }

    /// <summary>
    /// Occurs when a RAR process writes output.
    /// </summary>
    internal event EventHandler<RARProcessDataEventArgs>? RARProcessOutput;

    /// <summary>
    /// Occurs when a RAR process status changes.
    /// </summary>
    internal event EventHandler<RARProcessStatusChangedEventArgs>? RARProcessStatusChanged;

    /// <summary>
    /// Occurs when RAR compression progress updates.
    /// </summary>
    internal event EventHandler<RARCompressionProgressEventArgs>? RARCompressionProgress;

    /// <summary>
    /// Occurs when RAR compression status changes.
    /// </summary>
    internal event EventHandler<RARCompressionStatusChangedEventArgs>? RARCompressionStatusChanged;

    /// <summary>
    /// Occurs when brute-force progress updates (version/argument combination being tested).
    /// </summary>
    public event EventHandler<BruteForceProgressEventArgs>? BruteForceProgress;

    /// <summary>
    /// Occurs when the brute-force operation status changes (running, completed, cancelled).
    /// </summary>
    public event EventHandler<BruteForceStatusChangedEventArgs>? BruteForceStatusChanged;

    /// <summary>
    /// Occurs when file copy progress updates during input directory preparation.
    /// </summary>
    public event EventHandler<FileCopyProgressEventArgs>? FileCopyProgress;

    /// <summary>
    /// Occurs when CRC validation progress updates during input file verification.
    /// </summary>
    public event EventHandler<CRCValidationProgressEventArgs>? CRCValidationProgress;

    /// <summary>
    /// Occurs when preserving a source file's timestamps onto its copied
    /// destination fails (e.g. denied by ACLs). The packed RAR's File Time
    /// (DOS) for that file will reflect the copy time, not the source mtime,
    /// unless the SRR carries explicit timestamps that override this.
    /// The event argument is the destination file path.
    /// </summary>
    public event EventHandler<TimestampPreservationFailedEventArgs>? TimestampPreservationFailed;

    /// <summary>
    /// Gets the current brute-force options, or null if no operation is in progress.
    /// </summary>
    public BruteForceOptions? BruteForceOptions
    {
        get; private set;
    }

    // Reassigned at the start of each brute-force run so it can be linked to the caller's
    // cancellation token (see BruteForceRARVersionAsync). Stop() cancels it directly.
    private CancellationTokenSource _cts = new();

    private string? _commentFilePath = null;

    private readonly IReSceneLogger _logger;

    // Owns the per-process streaming log writers (open/write/close), keeping that
    // concurrency-safe bookkeeping out of the orchestrator. Manager forwards its process
    // callbacks to it. A single instance for the lifetime of this Manager.
    private readonly ProcessLogManager _processLogManager;

    /// <summary>
    /// Parses the RAR version number from a directory name (e.g., "winrar-560" returns 560).
    /// </summary>
    /// <param name="rarVersionDirectoryName">
    /// The WinRAR version directory name.
    /// </param>
    /// <returns>
    /// The parsed version number, normalized to three digits.
    /// </returns>
    public static int ParseRARVersion(string rarVersionDirectoryName)
        => RarVersionSelector.ParseRARVersion(rarVersionDirectoryName);

    /// <summary>
    /// Determines the RAR archive format version from command-line arguments and the RAR version number.
    /// </summary>
    /// <param name="commandLineArguments">
    /// The RAR command-line arguments to check.
    /// </param>
    /// <param name="version">
    /// The RAR version number.
    /// </param>
    /// <returns>
    /// The detected archive format version.
    /// </returns>
    public static RARArchiveVersion ParseRARArchiveVersion(RARCommandLineArgument[] commandLineArguments, int version)
        => RarVersionSelector.ParseRARArchiveVersion(commandLineArguments, version);

    /// <summary>
    /// Runs the brute-force RAR reconstruction, testing version and argument combinations until a hash match is found.
    /// </summary>
    /// <param name="options">
    /// The brute-force configuration options.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the operation; the internal source is linked to it so cancellation reaches the
    /// running RAR processes.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if a matching RAR archive was found.
    /// </returns>
    public async Task<bool> BruteForceRARVersionAsync(BruteForceOptions options, CancellationToken cancellationToken = default)
    {
        // Link the internal cancellation source to the caller's token so the UI's Cancel
        // (which cancels that token) actually reaches the running RAR processes, not just
        // Stop(). The field-initialized source is replaced and disposed here.
        _cts.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

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
            && !string.IsNullOrEmpty(options.RAROptions.SRRFilePath))
        {
            _logger.Information(this, $"Custom packer detected ({options.RAROptions.CustomPackerDetected}) — using direct SRR reconstruction", LogTarget.System);

            var reconstructor = new SRRReconstructor(_logger);
            reconstructor.Progress += (s, e) => FireBruteForceProgress(e);

            bool result = await reconstructor.ReconstructAsync(
                options.RAROptions.SRRFilePath,
                options.ReleaseDirectoryPath,
                options.OutputDirectoryPath,
                options.RAROptions.OriginalRarFileNames,
                options.Hashes,
                options.HashType,
                _cts.Token);

            OperationCompletionStatus completionStatus = result ? OperationCompletionStatus.Success : OperationCompletionStatus.Error;
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
        List<(string Path, int Version)> allValidRarDirectories = GetValidRarDirectories(rarVersionDirectories, options);
        _logger.Information(this, $"Found {allValidRarDirectories.Count} valid RAR versions matching configured version ranges");

        // Prepares the working input directory and validates the SRR file list. Constructed after
        // _cts was (re)linked above so it observes this run's cancellation token, and given the
        // event-firing callbacks so its progress/preservation events fire from Manager as before.
        var inputDirectoryPreparer = new InputDirectoryPreparer(
            _logger, this, FireFileCopyProgress, FireCRCValidationProgress, FireTimestampPreservationFailed, _cts.Token);

        // Validate input files before any brute-forcing
        if (options.RAROptions.HasArchiveFileList && !inputDirectoryPreparer.ValidateInputFiles(options))
        {
            return false;
        }

        // === PHASE 1: Comment Block Brute-Force ===
        // If CMT compressed data is available, first brute-force the comment to narrow down versions
        List<(string Path, int Version)> versionsToUse;
        if (options.RAROptions.CanUseCommentPhase)
        {
            var commentPhaseBruteForcer = new CommentPhaseBruteForcer(_logger, this, FireBruteForceProgress, _cts.Token);
            versionsToUse = await commentPhaseBruteForcer.BruteForceCommentPhaseAsync(options, allValidRarDirectories);
            _logger.Information(this, $"Phase 1 complete: {versionsToUse.Count} matching version(s)", LogTarget.System);
            _logger.Information(this, $"=== PHASE 2: Full RAR Brute-Force with {versionsToUse.Count} version(s) ===", LogTarget.Phase2);
        }
        else
        {
            versionsToUse = allValidRarDirectories;
            _logger.Information(this, "Phase 1 skipped (no CMT data)", LogTarget.System);
            _logger.Information(this, "Phase 1 skipped (no CMT data) - using all versions for brute-force", LogTarget.Phase1);
        }

        InputDirectoryPreparer.PrepareResult prepareResult = await Task.Run(() => inputDirectoryPreparer.PrepareInputDirectory(options));
        string inputFilesDir = prepareResult.InputFilesDir;
        _commentFilePath = prepareResult.CommentFilePath;

        int totalProgressSize = BruteForceProgressCalculator.CalculateBruteForceProgressSize(options, versionsToUse.Count, allValidRarDirectories.Count);
        int currentProgress = 0;

        DirectoryInfo directoryInfo = new(inputFilesDir);
        FileInfo[] fileInfos = directoryInfo.GetFiles("*.*", SearchOption.AllDirectories);

        // Save file attributes
        var fileInfoAttributes = fileInfos.Select(f => new KeyValuePair<FileInfo, FileAttributes>(f, f.Attributes)).ToDictionary(f => f.Key, f => f.Value);

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
                foreach ((string? rarVersionDirectoryPath, int version) in versionsToUse)
                {
                    if (_cts.IsCancellationRequested)
                    {
                        break;
                    }

                    (bool foundCombination, int newProgress) = await TryProcessCommandLinesAsync(options, version, rarVersionDirectoryPath, inputFilesDir, totalProgressSize, currentProgress, bruteForceStartDateTime, fileHashes, a, b);
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
        TimeSpan elapsed = DateTime.Now - bruteForceStartDateTime;
        if (_cts.IsCancellationRequested)
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

        status = new(OperationStatus.Running, OperationStatus.Completed, _cts.IsCancellationRequested ? OperationCompletionStatus.Cancelled : OperationCompletionStatus.Success);
        FireBruteForceStatusChanged(status);
        return found;
    }

    /// <summary>
    /// Cancels the brute-force operation and terminates all active RAR processes.
    /// </summary>
    public void Stop()
    {
        _logger.Information(this, "Stopping brute force operation and cancelling all RAR processes");
        _cts.Cancel();

        // CliWrap automatically kills the running processes when the token is cancelled;
        // each process then closes its log writer in Process_ProcessStatusChanged.
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
            _processLogManager.OpenLog(process, BruteForceOptions.OutputDirectoryPath, outputFilePath);
        }

        process.ProcessStatusChanged += Process_ProcessStatusChanged;
        process.ProcessOutput += Process_ProcessOutput;
        process.CompressionStatusChanged += Process_CompressionStatusChanged;
        process.CompressionProgress += Process_CompressionProgress;

        // Create a linked cancellation token for early termination
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

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

        // When process completes, close and dispose the log writer.
        if (e.NewStatus == OperationStatus.Completed)
        {
            _processLogManager.CloseLog(process);
        }
    }

    private void Process_ProcessOutput(object? sender, ProcessDataEventArgs e)
    {
        if (sender is not RARProcess process)
        {
            return;
        }

        // Stream output directly to the log file (auto-flushed) before re-raising the event.
        _processLogManager.WriteOutput(process, e.Data);

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

    private void FireCRCValidationProgress(CRCValidationProgressEventArgs e)
        => CRCValidationProgress?.Invoke(this, e);

    private void SetFileAttributes(IEnumerable<FileInfo> files, FileAttributes attribute, bool add)
        => FileOperations.SetFileAttributes(files, attribute, add, _logger);

    private List<(string Path, int Version)> GetValidRarDirectories(string[] directories, BruteForceOptions options)
        => RarVersionSelector.GetValidRarDirectories(directories, options, _logger, this);


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
            if (_cts.IsCancellationRequested)
            {
                return (false, currentProgress);
            }

            RARArchiveVersion archiveVersion = ParseRARArchiveVersion(commandLineArguments, version);
            List<string> filteredArguments = RarVersionSelector.FilterArgumentsForVersion(commandLineArguments, version, archiveVersion);

            string joinedArguments = string.Join("", filteredArguments);
            string displayArguments = string.Join(" ", filteredArguments);

            // RAR 6.x doesn't honor timestamp options (-tsc0/-tsa0) for RAR4 format archives, so
            // skip the combination to avoid creating archives with wrong extended-time flags.
            if (RarVersionSelector.ShouldSkipRar6TimestampCombination(version, archiveVersion, filteredArguments))
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

            FireBruteForceProgress(new(options.ReleaseDirectoryPath, rarVersionDirectoryPath, displayArguments, totalProgressSize, currentProgress, bruteForceStartDateTime)
            {
                PhaseDescription = "Phase 2: Full RAR Creation"
            });

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

            if (!string.IsNullOrEmpty(_commentFilePath))
            {
                // Add comment option: -z<commentfile>
                finalArguments.Add($"-z{_commentFilePath}");
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
                    processCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);

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
                    using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    Task monitorTask = MonitorForSecondVolumeAsync(rarFilePath, monitorCts);
                    await Task.WhenAny(runningProcessTask, monitorTask);

                    // Clean up monitor if process finished before second volume appeared
                    if (!monitorTask.IsCompleted)
                    {
                        monitorCts.Cancel();
                    }
                }
                else
                {
                    // Standard: run with early termination (kills RAR after first volume is complete)
                    await RARCompressDirectoryAsync(rarExeFilePath, inputFilesDir, rarFilePath, finalArguments, _cts.Token);
                }

                currentProgress++;
                FireBruteForceProgress(new(options.ReleaseDirectoryPath, rarVersionDirectoryPath, displayArguments, totalProgressSize, currentProgress, bruteForceStartDateTime)
                {
                    PhaseDescription = "Phase 2: Full RAR Creation"
                });

                // Check if RAR file or volume files were created
                string? actualRarFilePath = MatchedRarWriter.FindCreatedRARFile(rarFilePath);
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
                    RAROptions opts = options.RAROptions;

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
                            {
                                cmtParts.Add($"Host OS -> {RARPatcher.GetHostOSName(opts.DetectedCmtHostOS.Value)} (0x{opts.DetectedCmtHostOS.Value:X2})");
                            }

                            if (opts.DetectedCmtFileTime.HasValue)
                            {
                                cmtParts.Add($"File Time -> 0x{opts.DetectedCmtFileTime.Value:X8}");
                            }

                            if (opts.DetectedCmtFileAttributes.HasValue)
                            {
                                cmtParts.Add($"Attributes -> 0x{opts.DetectedCmtFileAttributes.Value:X8}");
                            }

                            _logger.Information(this, $"  CMT:     {string.Join(", ", cmtParts)}", LogTarget.System);
                        }
                    }

                    if (opts.NeedsLargePatching)
                    {
                        _logger.Information(this, $"  LARGE:   {(opts.DetectedLargeFlag == true ? "Added" : "Removed")} (HIGH_PACK=0x{opts.DetectedHighPackSize ?? 0:X8}, HIGH_UNP=0x{opts.DetectedHighUnpSize ?? 0:X8})", LogTarget.System);
                    }

                    _logger.Information(this, "  Note:    RAR output was patched post-creation to match original headers", LogTarget.System);
                }

                // Rename the matched file(s) to their final name inside the "output" subdirectory
                string baseName = Path.GetFileNameWithoutExtension(rarFilePath);
                string patchedBaseName = options.RAROptions.NeedsPatching ? baseName + "-patched" : baseName;
                IReadOnlyList<string> originalNames = options.RAROptions.OriginalRarFileNames;
                bool useOriginalNames = options.RAROptions.RenameToOriginalNames &&
                                        options.RAROptions.StopOnFirstMatch &&
                                        originalNames.Count > 0;

                if (options.RAROptions.CompleteAllVolumes)
                {
                    // Re-find all volumes now that RAR has completed
                    string? completedRarFilePath = MatchedRarWriter.FindCreatedRARFile(rarFilePath);
                    if (completedRarFilePath != null)
                    {
                        // Patch remaining volumes (first volume already patched - will be no-op for it)
                        if (options.RAROptions.NeedsPatching)
                        {
                            PatchRARFilesHostOS(completedRarFilePath, options.RAROptions);
                        }

                        // Rename all volumes to their final names inside the "output" subdirectory
                        List<string> allVolumes = MatchedRarWriter.GetAllVolumeFiles(completedRarFilePath);

                        for (int i = 0; i < allVolumes.Count; i++)
                        {
                            string outputFileName = useOriginalNames && i < originalNames.Count
                                ? Path.GetFileName(originalNames[i])
                                : Path.GetFileName(allVolumes[i]).Replace(baseName, patchedBaseName, StringComparison.Ordinal);
                            string outputPath = Path.Combine(rarOutputDir, outputFileName);
                            if (MatchedRarWriter.MoveMatchedFile(allVolumes[i], outputPath))
                            {
                                _logger.Information(this, $"  Volume: {outputFileName}", LogTarget.System);
                            }
                            else
                            {
                                _logger.Warning(this, $"  Volume NOT written (a different file already occupies '{outputFileName}'); left at '{allVolumes[i]}'", LogTarget.System);
                            }
                        }

                        _logger.Information(this, $"  Completed {allVolumes.Count} volume(s)", LogTarget.System);
                    }
                }
                else
                {
                    // Standard behavior: just rename the first .rar file
                    string outputFileName = useOriginalNames
                        ? Path.GetFileName(originalNames[0])
                        : Path.GetFileName(actualRarFilePath).Replace(baseName, patchedBaseName, StringComparison.Ordinal);
                    string outputPath = Path.Combine(rarOutputDir, outputFileName);
                    if (!MatchedRarWriter.MoveMatchedFile(actualRarFilePath, outputPath))
                    {
                        _logger.Warning(this, $"Matched archive NOT written (a different file already occupies '{outputFileName}'); left at '{actualRarFilePath}'", LogTarget.System);
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

    private void DeleteRARFileAndVolumes(string rarFilePath)
        => FileOperations.DeleteRARFileAndVolumes(rarFilePath, _logger);

    private void FireTimestampPreservationFailed(string destPath, string errorMessage)
        => TimestampPreservationFailed?.Invoke(this, new TimestampPreservationFailedEventArgs
        {
            DestinationPath = destPath,
            ErrorMessage = errorMessage
        });

    private void PatchRARFilesHostOS(string rarFilePath, RAROptions rarOptions, bool allVolumes = true)
    {
        if (!rarOptions.NeedsPatching)
        {
            return;
        }

        try
        {
            // Collect files to patch (all volumes or just the specified file)
            List<string> filesToPatch = allVolumes ? MatchedRarWriter.GetAllVolumeFiles(rarFilePath) : [rarFilePath];

            if (rarOptions.NeedsHostOSPatching)
            {
                string hostOSName = RARPatcher.GetHostOSName(rarOptions.DetectedFileHostOS!.Value);
                _logger.Information(this, $"Patching to match SRR: Host OS={hostOSName} (0x{rarOptions.DetectedFileHostOS.Value:X2}), Attrs=0x{rarOptions.DetectedFileAttributes ?? 0:X8} for {filesToPatch.Count} file(s)", LogTarget.Phase2);
            }

            if (rarOptions.NeedsLargePatching)
            {
                _logger.Information(this, $"Patching LARGE flag: {(rarOptions.DetectedLargeFlag == true ? "adding" : "removing")} for {filesToPatch.Count} file(s)", LogTarget.Phase2);
            }

            if (rarOptions.NeedsMtimePatching)
            {
                _logger.Information(this, $"Patching mtime (DOS FTIME + EXT_TIME remainder) for {rarOptions.FileTimestamps.Count} file(s) across {filesToPatch.Count} volume(s)", LogTarget.Phase2);
            }

            // Build patch options
            var patchOptions = new PatchOptions
            {
                // LARGE flag patching
                SetLargeFlag = rarOptions.DetectedLargeFlag,
                HighPackSize = rarOptions.DetectedHighPackSize ?? 0,
                HighUnpSize = rarOptions.DetectedHighUnpSize ?? 0
            };

            // Per-file mtime overrides — sidesteps file-system / WinRAR precision quirks.
            if (rarOptions.NeedsMtimePatching)
            {
                patchOptions.FileModifiedTimes = rarOptions.FileTimestamps;
            }

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
                    List<PatchResult> results = RARPatcher.PatchFile(filePath, patchOptions);
                    totalPatched += results.Count;

                    foreach (PatchResult result in results)
                    {
                        string blockDesc = result.BlockType == RAR4BlockType.Service
                            ? $"Service ({result.FileName ?? "?"})"
                            : $"File ({result.FileName ?? "?"})";
                        _logger.Debug(this, $"Patched {blockDesc}: Host OS 0x{result.OriginalHostOS:X2} -> 0x{result.NewHostOS:X2}, Attrs 0x{result.OriginalAttributes:X8} -> 0x{result.NewAttributes:X8}, CRC 0x{result.OriginalCRC:X4} -> 0x{result.NewCRC:X4}", LogTarget.Phase2);
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

    /// <summary>
    /// Logs all brute-force settings for debugging and tracking purposes.
    /// </summary>
    private void LogBruteForceSettings(BruteForceOptions options)
    {
        RAROptions opts = options.RAROptions;

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
            string versionRanges = string.Join(", ", opts.RARVersions.Select(v =>
                v.End > v.Start ? $"{v.Start}-{v.End}" : v.Start.ToString()));
            _logger.Information(this, $"  RAR version ranges: {versionRanges}", LogTarget.System);
        }
        else
        {
            _logger.Information(this, "  RAR version ranges: All versions", LogTarget.System);
        }

        // Command line arguments
        _logger.Information(this, $"  Command line combinations: {opts.CommandLineArguments.Count}", LogTarget.System);
        if (opts.CommandLineArguments.Count is > 0 and <= 10)
        {
            foreach (RARCommandLineArgument[] args in opts.CommandLineArguments)
            {
                string argStr = string.Join(" ", args.Select(a => a.Argument));
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
