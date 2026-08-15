using System.Text;
using ReScene.Core.Cryptography;
using ReScene.Core.Diagnostics;
using ReScene.Core.IO;
using ReScene.RAR;

namespace ReScene.Core;

/// <summary>
/// Orchestrates brute-force RAR reconstruction by testing RAR version and argument combinations
/// against expected hash values until a match is found.
/// </summary>
public class Manager : IDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Manager"/> class.
    /// </summary>
    /// <param name="logger">
    /// The logger to use, or <see langword="null"/> to discard log output.
    /// </param>
    public Manager(IReSceneLogger? logger = null)
        : this(logger, new RealRARProcessRunner(logger))
    {
    }

    /// <summary>
    /// Test seam: injects the rar process runner so the candidate loop (launch sites,
    /// cancellation, and the producer-observation invariant) is exercisable without a real rar
    /// binary. Production code always goes through the public constructor, which wires
    /// <see cref="RealRARProcessRunner"/>.
    /// </summary>
    internal Manager(IReSceneLogger? logger, IRARProcessRunner runner)
    {
        _logger = logger ?? NullReSceneLogger.Instance;
        _processLogManager = new ProcessLogManager(_logger, this);
        _runner = runner;
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

    // 1 while a run is executing — BruteForceRARVersionAsync is single-run-at-a-time (see its
    // doc); Interlocked entry so two racing calls can never both win.
    private int _runActive;

    private string? _commentFilePath = null;

    // Set once per set, before the attribute loop, by the SRR-guided-assembly preflight in
    // BruteForceRARVersionAsync; TryProcessCommandLinesAsync reads it to choose between the
    // assembly and legacy candidate flows.
    private bool _useAssembly;

    // Guards the one-time-per-run "enable Complete all volumes" guidance log: set the first time a
    // non-CAV candidate's quick-gate assembly comes back genuinely inconclusive (SourceExhausted
    // with CompleteAllVolumes off). Reset alongside _useAssembly at the same per-set engagement
    // point below — deliberately deferred from the change that introduced only _useAssembly itself.
    private bool _inconclusiveGuidanceLogged;

    // Guards the one-time-per-run pack-order divergence warning: set the first time a quick-gate
    // mismatch's produced first volume packs its first file under a different name than the
    // assembled (SRR-order) first volume — the signature of a local rar environment (.rarrc, the
    // RAR variable) injecting an order-changing default switch such as -ds. Reset alongside
    // _useAssembly/_inconclusiveGuidanceLogged at the same per-set engagement point below.
    private bool _packOrderGuidanceLogged;

    // Guards the one-time-per-run non-ASCII @listfile fallback warning: the condition is a
    // property of the SET's file list, not of any single candidate, so repeating it for every
    // combination would flood a real brute-force log with thousands of identical lines. Reset
    // alongside the flags above at the same per-set engagement point.
    private bool _nonAsciiOrderFallbackLogged;

    private readonly IReSceneLogger _logger;

    // Owns the per-process streaming log writers (open/write/close), keeping that
    // concurrency-safe bookkeeping out of the orchestrator. Manager forwards its process
    // callbacks to it. A single instance for the lifetime of this Manager.
    private readonly ProcessLogManager _processLogManager;

    // Test seam over rar execution (see IRARProcessRunner); production always resolves to
    // RealRARProcessRunner via the public constructor.
    private readonly IRARProcessRunner _runner;

    /// <summary>
    /// Tries to parse the RAR version number from a directory name (e.g., "winrar-560" → 560).
    /// </summary>
    /// <param name="rarVersionDirectoryName">The WinRAR version directory name.</param>
    /// <param name="version">When this method returns <see langword="true"/>, the normalised version number; otherwise 0.</param>
    /// <returns><see langword="true"/> if the version was successfully parsed; otherwise <see langword="false"/>.</returns>
    public static bool TryParseRARVersion(string rarVersionDirectoryName, out int version)
        => RARVersionSelector.TryParseRARVersion(rarVersionDirectoryName, out version);

    /// <summary>
    /// Tries to parse the RAR version number from a directory name, also returning the variant tag —
    /// the remainder of the name after the version digits, trimmed of leading separators (e.g.,
    /// "winrar-250-beta1" → 250 + "beta1"). Distinguishes folders that parse to the same version.
    /// </summary>
    /// <param name="rarVersionDirectoryName">The WinRAR version directory name.</param>
    /// <param name="version">When this method returns <see langword="true"/>, the normalised version number; otherwise 0.</param>
    /// <param name="variantTag">When this method returns <see langword="true"/>, the variant tag (empty when none); otherwise empty.</param>
    /// <returns><see langword="true"/> if the version was successfully parsed; otherwise <see langword="false"/>.</returns>
    public static bool TryParseRARVersion(string rarVersionDirectoryName, out int version, out string variantTag)
        => RARVersionSelector.TryParseRARVersion(rarVersionDirectoryName, out version, out variantTag);

    /// <summary>
    /// Parses the RAR version number from a directory name (e.g., "winrar-560" returns 560).
    /// </summary>
    /// <param name="rarVersionDirectoryName">
    /// The WinRAR version directory name.
    /// </param>
    /// <returns>
    /// The parsed version number, normalized to three digits.
    /// </returns>
    /// <exception cref="FormatException">Thrown when the version cannot be parsed from <paramref name="rarVersionDirectoryName"/>.</exception>
    public static int ParseRARVersion(string rarVersionDirectoryName)
        => RARVersionSelector.ParseRARVersion(rarVersionDirectoryName);

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
        => RARVersionSelector.ParseRARArchiveVersion(commandLineArguments, version);

    /// <summary>
    /// Builds the expected (volume base filename, CRC) list in volume order from the options'
    /// original names and <see cref="BruteForceOptions.ExpectedVolumeCrcs"/>. Volumes with no
    /// expected CRC are omitted; callers treat a count below the produced-volume count as
    /// not-fully-verifiable.
    /// </summary>
    /// <param name="options">
    /// The brute-force options carrying the original volume names and expected per-volume CRCs.
    /// </param>
    /// <returns>
    /// The ordered list of (volume base filename, expected CRC) pairs for covered volumes.
    /// </returns>
    public static IReadOnlyList<(string Name, string Crc)> BuildExpectedInOrder(BruteForceOptions options)
    {
        var result = new List<(string, string)>();
        foreach (string volume in options.RAROptions.OriginalRARFileNames)
        {
            string name = LastSegment(volume);

            // Look up the canonical directory-qualified key first (so same-basename volumes in
            // different set directories resolve to their own CRC — #9), then fall back to the bare
            // basename (the common flat-SFV case, keyed by basename only). Never returns empty where
            // the basename would have matched. The returned Name stays the bare basename — it's
            // matched positionally and reported, not used as a lookup key.
            if (options.ExpectedVolumeCrcs.TryGetValue(QualifiedKey(volume), out string? crc)
                || options.ExpectedVolumeCrcs.TryGetValue(name, out crc))
            {
                result.Add((name, crc));
            }
        }

        return result;
    }

    /// <summary>
    /// The canonical directory-qualified key for a volume: its relative path with separators
    /// normalized to <c>/</c> and any leading/trailing slashes trimmed. Matches the keys the app's
    /// planner emits, so same-basename volumes in different directories stay distinct (#9).
    /// </summary>
    private static string QualifiedKey(string volumePath)
        => volumePath.Replace('\\', '/').Trim('/');

    private static readonly char[] _pathSegmentSeparators = ['/', '\\'];

    /// <summary>
    /// Returns the last path segment, splitting on BOTH <c>/</c> and <c>\</c> on every platform.
    /// Unlike <see cref="Path.GetFileName(string)"/>, which only splits on the platform separator
    /// (leaving <c>\</c> embedded in SRR-internal volume names on non-Windows), this keeps volume
    /// verification and renaming correct across platforms.
    /// </summary>
    internal static string LastSegment(string path)
    {
        int index = path.LastIndexOfAny(_pathSegmentSeparators);
        return index < 0 ? path : path[(index + 1)..];
    }

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
    /// A <see cref="BruteForceRunResult"/> whose <see cref="BruteForceRunResult.Success"/> is
    /// <see langword="true"/> when a matching RAR archive was found, carrying the winning
    /// version + argument combination (if any) for seeding subsequent archive sets.
    /// </returns>
    /// <remarks>
    /// Single-run-at-a-time per instance: the run mutates per-run instance state (the linked
    /// cancellation source, assembly/once-per-run flags, the options snapshot), so a second
    /// call while one is executing throws <see cref="InvalidOperationException"/> before
    /// touching any of it. The guard releases only after the terminal status event has fired,
    /// so starting the next run from inside a <see cref="BruteForceStatusChanged"/> handler is
    /// also rejected — await the running call instead. <see cref="Stop"/> may be called
    /// concurrently with a run; <see cref="Dispose"/> may not.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a run is already executing on this instance.
    /// </exception>
    public async Task<BruteForceRunResult> BruteForceRARVersionAsync(BruteForceOptions options, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _runActive, 1) != 0)
        {
            throw new InvalidOperationException(
                "A brute-force run is already executing on this Manager instance — await it before starting the next one. " +
                "(Stop() may be called concurrently; starting a new run from a status-event handler is not supported.)");
        }

        try
        {
            return await BruteForceRARVersionCoreAsync(options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _runActive, 0);
        }
    }

    private async Task<BruteForceRunResult> BruteForceRARVersionCoreAsync(BruteForceOptions options, CancellationToken cancellationToken)
    {
        // Link the internal cancellation source to the caller's token so the UI's Cancel
        // (which cancels that token) actually reaches the running RAR processes, not just
        // Stop(). Swap the new source in BEFORE disposing the previous one: Stop() reads _cts
        // un-synchronized, so create-swap-dispose shrinks its race window to "cancelled the
        // previous (finished) run's source" — harmless — and Stop() itself tolerates the
        // remaining disposed-source race outright.
        CancellationTokenSource previousCts = _cts;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        previousCts.Dispose();

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

            using var packedSource = new ReleaseFilePackedSource(options.ReleaseDirectoryPath);
            SRRReconstructionResult reconResult = await reconstructor.ReconstructAsync(
                options.RAROptions.SRRFilePath,
                packedSource,
                options.ReleaseDirectoryPath,
                options.OutputDirectoryPath,
                options.RAROptions.OriginalRARFileNames,
                options.Hashes,
                options.HashType,
                _cts.Token).ConfigureAwait(false);
            bool result = reconResult.Status == SRRReconstructionStatus.Success;
            IReadOnlyList<string> writtenPaths = reconResult.WrittenPaths;
            if (!result && reconResult.Diagnostic is { } diag)
            {
                _logger.Warning(this, $"Direct SRR reconstruction failed ({reconResult.Status}): {diag}", LogTarget.System);
            }

            OperationCompletionStatus completionStatus = result ? OperationCompletionStatus.Success : OperationCompletionStatus.Error;
            status = new BruteForceStatusChangedEventArgs(OperationStatus.Running, OperationStatus.Completed, completionStatus);
            FireBruteForceStatusChanged(status);
            return new BruteForceRunResult(result, null)
            {
                CustomPackerFiles = result ? writtenPaths : []
            };
        }

        // A missing or unreadable installations root must fail the run like every other setup
        // failure — logged error, terminal status, failed result — never throw out of the public
        // API with the status event stranded at Running. DirectoryNotFoundException and
        // PathTooLongException both derive from IOException; ArgumentException covers invalid
        // path characters.
        string[] rarVersionDirectories;
        try
        {
            rarVersionDirectories = Directory.GetDirectories(options.RARInstallationsDirectoryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return FailRunSetup($"Cannot enumerate the WinRAR installations directory '{options.RARInstallationsDirectoryPath}': {ex.Message}");
        }

        _logger.Debug(this, $"Found {rarVersionDirectories.Length} RAR version directories in {options.RARInstallationsDirectoryPath}");

        if (rarVersionDirectories.Length == 0)
        {
            return FailRunSetup("No RAR executables found in WinRAR directory or sub directories");
        }

        // Get all valid RAR directories first
        List<(string Path, int Version)> allValidRARDirectories = GetValidRARDirectories(rarVersionDirectories, options);
        _logger.Information(this, $"Found {allValidRARDirectories.Count} valid RAR versions matching configured version ranges");

        // Prepares the working input directory and validates the SRR file list. Constructed after
        // _cts was (re)linked above so it observes this run's cancellation token, and given the
        // event-firing callbacks so its progress/preservation events fire from Manager as before.
        var inputDirectoryPreparer = new InputDirectoryPreparer(
            _logger, this, FireFileCopyProgress, FireCRCValidationProgress, FireTimestampPreservationFailed, _cts.Token);

        // Validate input files before any brute-forcing
        if (options.RAROptions.HasArchiveFileList && !inputDirectoryPreparer.ValidateInputFiles(options))
        {
            return FailRunSetup("Input file validation failed — see the log above for the missing or mismatched files");
        }

        // === PHASE 1: Comment Block Brute-Force ===
        // If CMT compressed data is available, first brute-force the comment to narrow down versions
        List<(string Path, int Version)> versionsToUse;
        if (options.RAROptions.CanUseCommentPhase)
        {
            var commentPhaseBruteForcer = new CommentPhaseBruteForcer(_logger, this, FireBruteForceProgress, _cts.Token);
            versionsToUse = await commentPhaseBruteForcer.BruteForceCommentPhaseAsync(options, allValidRARDirectories).ConfigureAwait(false);
            _logger.Information(this, $"Phase 1 complete: {versionsToUse.Count} matching version(s)", LogTarget.System);
            _logger.Information(this, $"=== PHASE 2: Full RAR Brute-Force with {versionsToUse.Count} version(s) ===", LogTarget.Phase2);
        }
        else
        {
            versionsToUse = allValidRARDirectories;
            _logger.Information(this, "Phase 1 skipped (no CMT data)", LogTarget.System);
            _logger.Information(this, "Phase 1 skipped (no CMT data) - using all versions for brute-force", LogTarget.Phase1);
        }

        InputDirectoryPreparer.PrepareResult prepareResult = await Task.Run(() => inputDirectoryPreparer.PrepareInputDirectory(options)).ConfigureAwait(false);
        string inputFilesDir = prepareResult.InputFilesDir;
        _commentFilePath = prepareResult.CommentFilePath;

        int totalProgressSize = BruteForceProgressCalculator.CalculateBruteForceProgressSize(options, allValidRARDirectories, versionsToUse.Count, allValidRARDirectories.Count);
        int currentProgress = 0;

        DirectoryInfo directoryInfo = new(inputFilesDir);
        FileInfo[] fileInfos = directoryInfo.GetFiles("*.*", SearchOption.AllDirectories);

        // Save file attributes
        var fileInfoAttributes = fileInfos.Select(f => new KeyValuePair<FileInfo, FileAttributes>(f, f.Attributes)).ToDictionary(f => f.Key, f => f.Value);

        // Save file hash
        HashSet<string> fileHashes = [];

        // === SRR-GUIDED ASSEMBLY: ONCE-PER-SET PREFLIGHT ===
        // Runs once per set, before the attribute loop, so a declined/errored SRR is resolved a
        // single time rather than re-checked per candidate. Three outcomes: Success engages
        // assembly for every candidate in this set; UnsupportedSrr falls back to the legacy
        // candidate loop below (unchanged); Error is a SET failure — an unreadable/malformed SRR
        // must not silently degrade to legacy reconstruction.
        _useAssembly = false;
        _inconclusiveGuidanceLogged = false;
        _packOrderGuidanceLogged = false;
        _nonAsciiOrderFallbackLogged = false;
        if (!string.IsNullOrEmpty(options.RAROptions.SRRFilePath)
            && options.RAROptions.CustomPackerDetected == SRR.CustomPackerType.None)
        {
            SRRReconstructionResult preflight = new SRRReconstructor(_logger)
                .PreflightSet(options.RAROptions.SRRFilePath, options.RAROptions.OriginalRARFileNames);
            switch (preflight.Status)
            {
                case SRRReconstructionStatus.Success:
                    _useAssembly = true;
                    _logger.Information(this, "SRR-guided assembly engaged (headers from SRR, data from rar output)", LogTarget.System);
                    break;
                case SRRReconstructionStatus.UnsupportedSrr:
                    _logger.Information(this, $"SRR-guided assembly unavailable ({preflight.Diagnostic}) — trying legacy reconstruction for this set", LogTarget.System);
                    break;
                default: // Error: unreadable/malformed SRR is a SET failure, not a silent legacy fallback
                    _logger.Error(this, $"SRR could not be read for assembly preflight: {preflight.Diagnostic}", LogTarget.System);
                    status = new BruteForceStatusChangedEventArgs(OperationStatus.Running,
                        OperationStatus.Completed, OperationCompletionStatus.Error);
                    FireBruteForceStatusChanged(status);
                    return new BruteForceRunResult(false, null);
            }
        }

        _logger.Debug(this, $"Assembly engagement for this set: useAssembly={_useAssembly}", LogTarget.System);

        var matchAccumulator = new BruteForceMatchAccumulator();
        bool stopOnFirstMatch = options.RAROptions.StopOnFirstMatch;
        for (int a = 0; a < (options.RAROptions.SetFileArchiveAttribute == TriState.Checked ? 2 : 1) && !(matchAccumulator.Found && stopOnFirstMatch); a++)
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

            for (int b = 0; b < (options.RAROptions.SetFileNotContentIndexedAttribute == TriState.Checked ? 2 : 1) && !(matchAccumulator.Found && stopOnFirstMatch); b++)
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

                    (bool foundCombination, int newProgress, CommittedMatch? match) = await TryProcessCommandLinesAsync(options, version, rarVersionDirectoryPath, inputFilesDir, totalProgressSize, currentProgress, bruteForceStartDateTime, fileHashes, a, b).ConfigureAwait(false);
                    currentProgress = newProgress;
                    matchAccumulator.Record(foundCombination, match);
                    if (foundCombination)
                    {
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
        else if (matchAccumulator.Found)
        {
            _logger.Information(this, $"=== Brute-force SUCCESS in {elapsed.TotalSeconds:F1}s ===", LogTarget.System);
        }
        else
        {
            _logger.Warning(this, $"=== Brute-force FAILED - no match found after {elapsed.TotalSeconds:F1}s ===", LogTarget.System);
        }

        OperationCompletionStatus completion = _cts.IsCancellationRequested
            ? OperationCompletionStatus.Cancelled
            : matchAccumulator.Found
                ? OperationCompletionStatus.Success
                : OperationCompletionStatus.Error;
        status = new(OperationStatus.Running, OperationStatus.Completed, completion);
        FireBruteForceStatusChanged(status);
        return new BruteForceRunResult(matchAccumulator.Found, matchAccumulator.Combo)
        {
            Matches = matchAccumulator.Matches
        };
    }

    /// <summary>
    /// Cancels the brute-force operation and terminates all active RAR processes.
    /// </summary>
    public void Stop()
    {
        _logger.Information(this, "Stopping brute force operation and cancelling all RAR processes");
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Stop() may race run entry (which swaps and disposes the previous source) or land
            // after Dispose(). A cancel aimed at a source that no longer exists has nothing left
            // to stop — swallowing keeps the documented "Stop() is safe to call concurrently"
            // contract honest (the same cancel-vs-dispose tolerance the app side applies).
        }

        // CliWrap automatically kills the running processes when the token is cancelled;
        // each process then closes its log writer in Process_ProcessStatusChanged.
    }

    /// <summary>
    /// Disposes the per-run linked cancellation source. Each brute-force run replaces and disposes
    /// the previous source, so this only releases the final run's source. Call once the Manager is
    /// no longer in use (no run should be active when disposing).
    /// </summary>
    public void Dispose()
    {
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Runs one rar process for a candidate combination, terminating it early once a second
    /// volume appears on disk (the release is provably multi-volume, so further compression by
    /// THIS candidate is no longer needed to test its first-volume hash). Always observes the
    /// process task to real completion before returning — never returns while it is still running
    /// (see <see cref="ObserveProducerQuietlyAsync"/>).
    /// </summary>
    /// <param name="rarExeFilePath">Path to the rar executable.</param>
    /// <param name="inputDirectory">rar's working directory for this run.</param>
    /// <param name="outputFilePath">The output archive path.</param>
    /// <param name="commandLineOptions">The switches to pass, in order.</param>
    /// <param name="cancellationToken">Cancels the running process.</param>
    /// <param name="inputPaths">
    /// Forwarded verbatim to the runner's own <c>inputPaths</c> parameter: the SRR-ordered explicit
    /// file list in place of the platform input mask, or <see langword="null"/> to keep the mask.
    /// </param>
    /// <returns>
    /// The process's real exit code on natural completion. On early termination, the OBSERVED
    /// cancellation exit (normally 1, since <see cref="RARProcess.RunAsync"/> swallows the
    /// cancellation and returns 1) — never a synthetic 0; early termination implies a volume
    /// already exists on disk regardless of the numeric code.
    /// </returns>
    private async Task<int> RARCompressDirectoryAsync(string rarExeFilePath, string inputDirectory, string outputFilePath, IEnumerable<string> commandLineOptions, CancellationToken cancellationToken, IReadOnlyList<string>? inputPaths = null)
    {
        // Create a linked cancellation token for early termination
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Start monitoring for second volume (for early termination optimization)
        Task monitorTask = MonitorForSecondVolumeAsync(outputFilePath, linkedCts);

        // Run the RAR process
        Task<int> processTask = _runner.RunAsync(rarExeFilePath, inputDirectory, outputFilePath, commandLineOptions, LogTarget.Phase2,
            process =>
            {
                // Initialize streaming log writer for this process
                if (BruteForceOptions != null)
                {
                    _processLogManager.OpenLog(process, BruteForceOptions.OutputDirectoryPath, outputFilePath);
                }

                SubscribeToProcessEvents(process);
            },
            linkedCts.Token, inputPaths);

        // Wait for either process completion or early termination
        await Task.WhenAny(processTask, monitorTask).ConfigureAwait(false);

        if (monitorTask.IsCompleted && !processTask.IsCompleted)
        {
            // Monitor-triggered INTENTIONAL early termination: cancel, then QUIET observation (a
            // fault after our own cancel is cleanup noise, not a candidate verdict).
            _logger.Debug(this, $"Second volume detected, terminating RAR process early for: {outputFilePath}", LogTarget.Phase2);
            linkedCts.Cancel();
            int? observed = await ObserveProducerQuietlyAsync(
                new CandidateProducer { ProcessTask = processTask, Cts = linkedCts }, cancelFirst: false).ConfigureAwait(false);
            return observed ?? 1;
        }

        // Natural completion (or fault): PLAIN await — a producer fault propagates to the
        // caller's generic catch exactly as today.
        return await processTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels (when requested) and OBSERVES the producer: awaits the process task to real
    /// completion — no grace-timeout abandonment — swallowing only cancellation or a fault.
    /// Invariant: no finalization, deletion, or next-candidate launch may happen while a
    /// producer task is unobserved.
    /// </summary>
    /// <remarks>
    /// TWO observation modes exist with distinct fault contracts (a single quiet
    /// observer used on winning paths too would silently accept a producer that faulted AFTER the
    /// volume-2 trigger, and could finalize a broken candidate as a match). This method is the
    /// CLEANUP-path observer: it never throws. Use it ONLY for catch blocks and
    /// cancellation/mismatch cleanup, where a producer fault must not abort the run (the run is
    /// already abandoning this candidate). Winning/normal-wait observation is instead a PLAIN
    /// awaited task — a producer fault there MUST propagate into the candidate's generic catch
    /// (one error row, next candidate). Concretely: the CAV "first volume matched, completing all
    /// volumes" await, and this method's own caller's natural-completion return, are both plain
    /// <c>await task.ConfigureAwait(false)</c> calls guarded only by the cancellation filter the
    /// legacy path already uses — never routed through this method.
    /// </remarks>
    /// <param name="producer">This candidate's producer; a no-op when none was launched.</param>
    /// <param name="cancelFirst">Whether to cancel the producer before awaiting it.</param>
    /// <returns>The producer's real exit code, or <see langword="null"/> if it was cancelled, faulted, or never launched.</returns>
    private Task<int?> ObserveProducerQuietlyAsync(CandidateProducer producer, bool cancelFirst)
        => producer.ObserveQuietlyAsync(cancelFirst,
            message => _logger.Debug(this, $"Producer observed faulted during cleanup: {message}", LogTarget.Phase2));

    private async Task MonitorForSecondVolumeAsync(string expectedRARFilePath, CancellationTokenSource cts)
    {
        try
        {
            string directory = Path.GetDirectoryName(expectedRARFilePath) ?? string.Empty;
            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(expectedRARFilePath);

            // Candidate second-volume names (covers .part02/.part002/.part2/.r00).
            string[] secondVolumeCandidates = RARVolumeNaming.SecondVolumeCandidates(directory, fileNameWithoutExtension);

            // Poll for second volume existence
            while (!cts.Token.IsCancellationRequested)
            {
                if (secondVolumeCandidates.Any(File.Exists))
                {
                    // Second volume detected! Return to trigger early termination
                    return;
                }

                // Wait a bit before checking again (100ms polling interval)
                await Task.Delay(100, cts.Token).ConfigureAwait(false);
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

    /// <summary>
    /// True when a rar process that ran to completion did no work: a non-zero exit code with no archive
    /// created (the caller establishes file absence first). Covers e.g. a Linux binary whose dynamic
    /// loader fails on missing shared libraries (exit 127) — the process starts, so no exception is
    /// thrown, but nothing is ever tested. Exit 0 without a file, and an unknown exit (the process was
    /// killed by cleanup before completing), keep the historical no-match treatment. Never a failure
    /// while cancellation is requested: <see cref="RARProcess.RunAsync"/> SWALLOWS the cancellation
    /// exception and returns exit 1, so a user cancel that lands before rar creates its first file
    /// would otherwise be indistinguishable from a genuine failed run.
    /// </summary>
    internal static bool IsCompletedRunFailure(int? completedExitCode, bool cancellationRequested)
        => !cancellationRequested && completedExitCode is not (null or 0);

    /// <summary>
    /// Flattens the executed argument list for the progress events' <c>ExecutedArguments</c>: tokens
    /// containing a space are whole-token quoted so a shell re-splitting the copied command line
    /// reconstructs the engine's argv exactly. Only <c>-z&lt;commentfile&gt;</c> can carry a path
    /// (the other switches are short flags), so e.g. an output folder like <c>D:\My Releases\out</c>
    /// would otherwise split the token and break the pasted line. The wrap assumes tokens contain no
    /// double quote — unreachable on Windows (reserved path character) and vanishingly rare elsewhere;
    /// a correct escape would have to be per-shell and belongs in the per-platform rendering, not here.
    /// </summary>
    internal static string JoinExecutedArguments(IEnumerable<string> finalArguments)
        => string.Join(" ", finalArguments.Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a));

    /// <summary>
    /// Fails a run during setup (before any candidate executes): logs the reason, fires the
    /// terminal <see cref="BruteForceStatusChanged"/> event, and returns the failed result.
    /// Callers key their busy state off that event, so every setup exit must move the status
    /// past <see cref="OperationStatus.Running"/> — returning without it strands them.
    /// </summary>
    private BruteForceRunResult FailRunSetup(string reason)
    {
        _logger.Error(this, reason, LogTarget.System);
        FireBruteForceStatusChanged(new BruteForceStatusChangedEventArgs(
            OperationStatus.Running, OperationStatus.Completed, OperationCompletionStatus.Error));
        return new BruteForceRunResult(false, null);
    }

    private void FireBruteForceStatusChanged(BruteForceStatusChangedEventArgs e)
        => BruteForceStatusChanged?.Invoke(this, e);

    private void FireFileCopyProgress(FileCopyProgressEventArgs e)
        => FileCopyProgress?.Invoke(this, e);

    private void FireCRCValidationProgress(CRCValidationProgressEventArgs e)
        => CRCValidationProgress?.Invoke(this, e);

    private void SetFileAttributes(IEnumerable<FileInfo> files, FileAttributes attribute, bool add)
        => FileOperations.SetFileAttributes(files, attribute, add, _logger);

    private List<(string Path, int Version)> GetValidRARDirectories(string[] directories, BruteForceOptions options)
        => RARVersionSelector.GetValidRARDirectories(directories, options, _logger, this);

    /// <summary>
    /// Wires the Manager's handlers to a RAR process's status/output/progress events.
    /// </summary>
    private void SubscribeToProcessEvents(RARProcess process)
    {
        process.ProcessStatusChanged += Process_ProcessStatusChanged;
        process.ProcessOutput += Process_ProcessOutput;
        process.CompressionStatusChanged += Process_CompressionStatusChanged;
        process.CompressionProgress += Process_CompressionProgress;
    }


    private async Task<(bool Found, int NewProgress, CommittedMatch? Match)> TryProcessCommandLinesAsync(
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
        string rarExeFilePath = RarExecutable.ResolveIn(rarVersionDirectoryPath);
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
                return (false, currentProgress, null);
            }

            RARArchiveVersion archiveVersion = ParseRARArchiveVersion(commandLineArguments, version);
            List<string> filteredArguments = RARVersionSelector.FilterArgumentsForVersion(commandLineArguments, version, archiveVersion);

            string joinedArguments = string.Join("", filteredArguments);
            string displayArguments = string.Join(" ", filteredArguments);

            // RAR 6.x doesn't honor timestamp options (-tsc0/-tsa0) for RAR4 format archives, so
            // skip the combination to avoid creating archives with wrong extended-time flags.
            if (RARVersionSelector.ShouldSkipRAR6TimestampCombination(version, archiveVersion, filteredArguments))
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

            // Decide this candidate's rar input operand (mask, or the SRR-ordered explicit file
            // list) BEFORE building the real switches — BuildFinalArguments needs to know whether to
            // add -ds, and the length guard inside ComposeInputFileArguments runs its OWN trial
            // BuildFinalArguments call to size the guard against the real final command line.
            (IReadOnlyList<string>? inputTail, string inputFileArguments, bool useDs, ListFilePlan? listFile) =
                ComposeInputFileArguments(options, rarExeFilePath, filteredArguments, version, rarFilePath);

            // Materialize the @listfile fallback here — the compose step above only PLANS it —
            // and deliberately before the exists-skip below, at the same point in the candidate
            // sequence the compose method used to write it: the skip's own progress row still
            // references a real file, and the content is run-constant so rewrites are idempotent.
            if (listFile is { } plan)
            {
                File.WriteAllText(plan.Path, plan.Content, Encoding.ASCII);
            }

            // Build the ACTUAL argument list (display args + engine-added -cfg-/-ma4/-vn/-z/-ds) up front —
            // pure composition — so every progress event can carry the executed form for the row's runnable
            // copied command; the display form alone would omit switches that change the output bytes.
            List<string> finalArguments = BuildFinalArguments(filteredArguments, options, version, useDs);
            string executedArguments = JoinExecutedArguments(finalArguments);

            // Everything this candidate is: its identity, paths and composed command line, frozen
            // into one record so no phase below re-derives a path or a joined argument string.
            string candidateSlug = Path.GetFileNameWithoutExtension(rarFilePath);
            var ctx = new CandidateContext(
                version, rarVersionDirectoryPath, rarVersionDirectoryName, rarExeFilePath,
                inputFilesDir, rarOutputDir, rarFilePath, candidateSlug,
                Path.Combine(rarOutputDir, $"assembled-{candidateSlug}"),
                commandLineArguments, filteredArguments, displayArguments,
                finalArguments, executedArguments, inputTail, inputFileArguments,
                totalProgressSize, bruteForceStartDateTime);

            if (File.Exists(ctx.RarFilePath))
            {
                // Different argument combinations can filter to the same output name for a given
                // version; the progress denominator counts each combination, so count this skip too
                // (otherwise the bar/ETA stall well short of 100% for old versions).
                _logger.Debug(this, $"RAR file already exists, skipping: {ctx.RarFilePath}", LogTarget.Phase2);
                currentProgress++;
                FireBruteForceProgress(NewRow(ctx, options.ReleaseDirectoryPath, currentProgress));
                continue;
            }

            FireBruteForceProgress(NewRow(ctx, options.ReleaseDirectoryPath, currentProgress));

            // ---- Execute RAR ----
            // When CompleteAllVolumes is enabled, we start RAR without auto-kill and check
            // the CRC while it's still running. If the first volume matches, we let RAR
            // finish creating all volumes. If it doesn't match, we kill RAR immediately.
            var producer = new CandidateProducer();
            // Guards against double-counting this combination: the success path increments below (once
            // rar has run); a LATE exception (from hashing/verify/rename after that increment) must NOT
            // be counted again in the catch. Only a failure BEFORE the increment (e.g. rar failed to
            // launch) is counted there.
            bool combinationCounted = false;

            // Exit code of a rar process that ran to completion, when known. Null while the process is
            // still running or was killed by the early-termination/cleanup cancels. Used to tell a rar
            // that RAN but did no work (e.g. its loader failed on missing shared libraries: exit 127,
            // no archive) apart from a genuine created-then-unmatched attempt.
            int? completedExitCode = null;

            try
            {
                if (options.RAROptions.CompleteAllVolumes)
                {
                    // Start RAR without automatic early termination
                    producer.Cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);

                    producer.ProcessTask = _runner.RunAsync(ctx.RarExeFilePath, ctx.InputFilesDir, ctx.RarFilePath, ctx.FinalArguments, LogTarget.Phase2,
                        process =>
                        {
                            // Stream this process's full output to its per-attempt log file under
                            // <workRoot>/logs — previously only the standard path opened one, so
                            // Complete-All-Volumes runs (the wizard default) produced no log files at all;
                            // WriteOutput for an unregistered process is a silent no-op. CloseLog fires from
                            // the shared process-status Completed handler, same as the standard path.
                            _processLogManager.OpenLog(process, options.OutputDirectoryPath, ctx.RarFilePath);
                            SubscribeToProcessEvents(process);
                        },
                        producer.Cts.Token, ctx.InputTail);

                    // Wait for first volume to complete (second volume appearing means first is done)
                    using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    Task monitorTask = MonitorForSecondVolumeAsync(ctx.RarFilePath, monitorCts);
                    await producer.AwaitLaunchOrSecondVolumeAsync(monitorTask, monitorCts).ConfigureAwait(false);
                }
                else
                {
                    // Standard: run with early termination (kills RAR after first volume is complete).
                    // The helper returns rar's real exit code when the process completed on its own, or
                    // the OBSERVED cancellation exit (normally 1 — never a synthetic 0; RARCompressDirectoryAsync
                    // always awaits the producer to real completion, no grace-timeout abandonment) when
                    // early-terminated — either way early termination requires a volume to already exist,
                    // so those values never reach the not-created branch.
                    completedExitCode = await RARCompressDirectoryAsync(ctx.RarExeFilePath, ctx.InputFilesDir, ctx.RarFilePath, ctx.FinalArguments, _cts.Token, ctx.InputTail).ConfigureAwait(false);
                }

                currentProgress++;
                combinationCounted = true;
                FireBruteForceProgress(NewRow(ctx, options.ReleaseDirectoryPath, currentProgress));

                // Check if RAR file or volume files were created
                string? actualRARFilePath = MatchedRARWriter.FindCreatedRARFile(ctx.RarFilePath);
                if (actualRARFilePath == null)
                {
                    // Invariant: no classification below may run while a CompleteAllVolumes
                    // producer task is unobserved. Cancels it (if still running) and awaits it to REAL
                    // completion — no grace-timeout abandonment. A no-op when no producer was
                    // launched (standard path: already observed inside RARCompressDirectoryAsync).
                    int? observedExitCode = await ObserveProducerQuietlyAsync(producer, cancelFirst: true).ConfigureAwait(false);

                    // CompleteAllVolumes mode: read the exit code off the completed task. NOTE:
                    // RARProcess.RunAsync swallows the cancellation exception and returns exit 1, so a
                    // task ended by the cancel above (or by a user Stop) completes successfully with
                    // Result==1 — cancellation must be excluded in the classification below, not here.
                    if (observedExitCode.HasValue)
                    {
                        completedExitCode = observedExitCode.Value;
                    }

                    // A rar that RAN but exited non-zero without creating anything did no work — e.g.
                    // its loader failed on missing shared libraries (exit 127 on Linux) — and must be
                    // reported as a failed combination, not a clean "No Match" (the stderr detail is
                    // already in the Phase 2 log). Exit 0 / unknown keeps the historical no-match
                    // treatment, and a requested cancellation is never a failure (its swallowed exit 1
                    // would otherwise masquerade as one). currentProgress was already counted above;
                    // fire the flagged event only.
                    if (IsCompletedRunFailure(completedExitCode, _cts.IsCancellationRequested))
                    {
                        _logger.Warning(this, $"{ctx.VersionDirectoryName} / {ctx.DisplayArguments}: rar exited with code {completedExitCode} and no archive was created — marking this combination as failed", LogTarget.Phase2);
                        FireBruteForceProgress(NewRow(ctx, options.ReleaseDirectoryPath, currentProgress, combinationFailed: true));
                    }
                    else
                    {
                        _logger.Information(this, $"RAR file was not created: {ctx.RarFilePath}", LogTarget.Phase2);
                    }

                    continue;
                }

                // Log what file was actually created (may be different from expected if volumes were created)
                if (actualRARFilePath != ctx.RarFilePath)
                {
                    _logger.Debug(this, $"Actual file created: {actualRARFilePath} (expected: {Path.GetFileName(ctx.RarFilePath)})", LogTarget.Phase2);
                }

                string hash;
                // The quick gate's assembled result and duplicate-hash flag, reused by the win path
                // below without recomputing them.
                SRRReconstructionResult? quick = null;
                bool isDuplicateAssemblyHash = false;
                // The legacy counterpart, threaded from the gate to the win path so a duplicate
                // carrier gets the same mismatch retention the assembly path applies.
                bool isLegacyDuplicateHash = false;
                if (_useAssembly)
                {
                    (GateOutcome outcome, quick, isDuplicateAssemblyHash, string? quickHash) = await TryQuickGateAsync(
                        ctx, actualRARFilePath, fileHashes, producer, currentProgress, options).ConfigureAwait(false);
                    if (outcome == GateOutcome.NextCandidate)
                    {
                        continue;
                    }

                    hash = quickHash!;
                }
                else
                {
                    (bool legacyMatched, string legacyHash, bool legacyDuplicate) = await TryLegacyGateAsync(
                        actualRARFilePath, fileHashes, producer, options).ConfigureAwait(false);
                    if (!legacyMatched)
                    {
                        continue;
                    }

                    hash = legacyHash;
                    isLegacyDuplicateHash = legacyDuplicate;
                }

                // ---- MATCH FOUND (first volume) ----

                // If RAR is still running (CompleteAllVolumes), let it finish creating all volumes
                // before we verify the whole set — full verification must never run against an
                // in-progress volume set. UNCONDITIONAL await when non-null — not gated on
                // IsCompleted: a producer that faulted between the CAV block's own IsFaulted check
                // and here (e.g. during the hash read above) is already IsCompleted==true, and
                // gating the await on that would skip observing it entirely, letting a candidate
                // whose producer crashed mid-volume-set be finalized as a match. Awaiting an
                // already-successfully-completed task here is a no-op that just returns its exit
                // code; awaiting an already-faulted one rethrows immediately into the catch below —
                // this is the plain (unwrapped) winning-path await the invariant requires.
                await producer.JoinForWinAsync(
                    () => _logger.Information(this, "First volume matched, completing all volumes...", LogTarget.System)).ConfigureAwait(false);

                // Assembly win path: the quick gate above only verified the ASSEMBLED
                // first original volume; the legacy full-per-volume-verification/RenameMatchedOutput
                // code below operates on actualRARFilePath — the CARRIER's own produced-shape bytes,
                // never byte-identical to the original — so an assembly candidate must never fall
                // through into it. Instead: full-set assembly over the now-complete produced set (the
                // producer was just awaited to completion above), guarded per-volume verification,
                // and finalization via the transactional FinalizeAssembledSet.
                if (_useAssembly)
                {
                    CommittedMatch? assemblyMatch = await FinalizeAssemblyWinAsync(
                        ctx, quick!, isDuplicateAssemblyHash, hash, actualRARFilePath, currentProgress, options).ConfigureAwait(false);
                    if (assemblyMatch is null)
                    {
                        continue;
                    }

                    return (true, currentProgress, assemblyMatch);
                }

                // Full per-volume verification (recreate-whole-release mode with known CRCs).
                // Only engages when CompleteAllVolumes is set AND we have expected CRCs; otherwise
                // we fall through to the legacy first-volume success path (back-compat).
                CommittedMatch? legacyMatch = TryFinalizeLegacyWin(ctx, hash, isLegacyDuplicateHash, actualRARFilePath, currentProgress, options);
                if (legacyMatch is null)
                {
                    continue; // near-miss or incomplete placement: keep brute-forcing
                }

                return (true, currentProgress, legacyMatch);
            }
            catch (OperationCanceledException)
            {
                // User/stop cancellation must abort the whole run — but only after observing the
                // producer (invariant: no exit propagates while a producer task is unobserved). In
                // today's code the task that surfaces THIS exception is typically the very one
                // we'd be observing here (already resolved by the await that threw), making this a
                // fast no-op — but that is a fact about the CURRENT shape of this method, not a
                // guarantee; keeping every exit uniform means a future change to this try block
                // can't silently reintroduce an unobserved-producer exit here. A no-op when
                // no producer was launched (standard path).
                await ObserveProducerQuietlyAsync(producer, cancelFirst: true).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                // A single rar that fails to launch (e.g. a *nix binary without the execute bit, a
                // DOS-era build in an "all versions" pack that passes File.Exists but can't start on
                // 64-bit Windows, or an AV block) must not abort the entire brute-force. Log it, count
                // the combination, and fire a CombinationFailed progress event so this row is reported
                // as an error instead of a misleading clean "No Match" — then move on.
                _logger.Warning(this, $"{ctx.VersionDirectoryName} / {ctx.DisplayArguments}: RAR execution failed ({ex.Message}) — skipping this combination", LogTarget.Phase2);

                // Invariant: observe the producer to real completion before this
                // candidate's cleanup finishes and the loop moves to the next one. A no-op when
                // no producer was launched (standard path: RARCompressDirectoryAsync's own plain
                // await is how ITS fault reached this catch, and that await already observed it).
                await ObserveProducerQuietlyAsync(producer, cancelFirst: true).ConfigureAwait(false);

                // Count this combination only if the success path didn't already (a launch failure throws
                // BEFORE the increment above; a late verify/rename exception throws AFTER it). Either way,
                // surface it as an error row so it isn't reported as a clean "No Match".
                if (!combinationCounted)
                {
                    currentProgress++;
                }

                FireBruteForceProgress(NewRow(ctx, options.ReleaseDirectoryPath, currentProgress, combinationFailed: true));
                continue;
            }
            finally
            {
                producer.Dispose();
            }
        }

        return (false, currentProgress, null);
    }

    /// <summary>
    /// Assembles the first <paramref name="volumeCount"/> ORIGINAL volumes for the current
    /// candidate from the produced set. Fresh ProducedVolumesPackedSource per call
    /// (single-snapshot). volumeCount: 1 = quick gate; int.MaxValue = full set.
    /// </summary>
    private async Task<SRRReconstructionResult> AssembleCandidateAsync(
        BruteForceOptions options, string producedFirstVolume, string assemblyDir,
        string candidateSlug, int volumeCount, CancellationToken ct)
    {
        IReadOnlyList<string> names = options.RAROptions.OriginalRARFileNames;
        if (volumeCount < names.Count)
        {
            names = [.. names.Take(volumeCount)];
        }

        // The ATTEMPT PROBE the flow tests count — one line per invocation, retry included:
        _logger.Debug(this, $"Assembly attempt for {candidateSlug}: volumes={volumeCount}", LogTarget.Phase2);
        using var source = new ProducedVolumesPackedSource(producedFirstVolume);
        return await new SRRReconstructor(_logger).ReconstructAsync(
            options.RAROptions.SRRFilePath!, source, options.ReleaseDirectoryPath,
            assemblyDir, names, [], options.HashType, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds one candidate's progress row. Every field is derived from the candidate context and
    /// the run's counters, so the emission sites cannot drift apart in what they report. Callers
    /// remain responsible for WHEN they fire and for whether they have already incremented
    /// <paramref name="currentProgress"/> — that ordering differs per site and is load-bearing.
    /// </summary>
    /// <param name="ctx">The candidate this row describes.</param>
    /// <param name="releaseDirectoryPath">
    /// The run's release directory. Passed explicitly rather than read from the
    /// <see cref="BruteForceOptions"/> property: the property and the per-run parameter are the
    /// same object only by convention, and this factory must not deepen that coupling.
    /// </param>
    /// <param name="currentProgress">The progress counter as the caller wants it reported.</param>
    /// <param name="combinationFailed">Whether this row marks the combination as failed.</param>
    private static BruteForceProgressEventArgs NewRow(
        CandidateContext ctx, string releaseDirectoryPath, int currentProgress, bool combinationFailed = false)
        => new(releaseDirectoryPath, ctx.VersionDirectoryPath, ctx.DisplayArguments,
            ctx.TotalProgressSize, currentProgress, ctx.BruteForceStartDateTime)
        {
            PhaseDescription = "Phase 2: Full RAR Creation",
            InputDirectoryPath = ctx.InputFilesDir,
            OutputFilePath = ctx.RarFilePath,
            ExecutedArguments = ctx.ExecutedArguments,
            InputFileArguments = ctx.InputFileArguments,
            CombinationFailed = combinationFailed
        };

    /// <summary>The error-row shape shared with the not-created branch's "rar exited with code…"
    /// case above: one warning + one CombinationFailed progress event; then callers continue.</summary>
    private void FireAssemblyErrorRow(CandidateContext ctx, BruteForceOptions options, int currentProgress, string? diagnostic)
    {
        _logger.Warning(this, $"{Path.GetFileName(ctx.VersionDirectoryPath)} / {ctx.DisplayArguments}: assembly failed ({diagnostic}) — marking this combination as failed", LogTarget.Phase2);
        FireBruteForceProgress(NewRow(ctx, options.ReleaseDirectoryPath, currentProgress, combinationFailed: true));
    }

    /// <summary>
    /// The SRR-guided-assembly first-volume gate: assemble volume 1 from the produced set, hash it,
    /// and decide whether this candidate matches. Returns
    /// <see cref="GateOutcome.NextCandidate"/> to mean "move to the next candidate" after applying
    /// the configured retention.
    /// <para>
    /// The assembled result and the duplicate flag are returned rather than recomputed, because the
    /// win path needs both.
    /// </para>
    /// </summary>
    /// <param name="ctx">The candidate being gated.</param>
    /// <param name="actualRARFilePath">The carrier archive rar actually produced.</param>
    /// <param name="fileHashes">The run's accumulated hashes; read for duplicate detection, then added to.</param>
    /// <param name="producer">This candidate's producer; empty on the standard early-termination path.</param>
    /// <param name="currentProgress">The progress counter, for any error row this gate fires.</param>
    /// <param name="options">The run's options.</param>
    private async Task<(GateOutcome Outcome, SRRReconstructionResult? Quick, bool IsDuplicate, string? Hash)> TryQuickGateAsync(
        CandidateContext ctx, string actualRARFilePath, HashSet<string> fileHashes,
        CandidateProducer producer, int currentProgress, BruteForceOptions options)
    {
        bool skipRetentionCleanup = false;   // per-candidate; true ONLY for persistent Error (diagnosis retention)

        // Snapshot BEFORE the attempt, not after: ProducedVolumesPackedSource opens the
        // produced set as it exists AT THIS INSTANT. If the producer is still running
        // here, that snapshot may be incomplete regardless of what the producer does
        // WHILE the attempt reads it — including finishing in the background before the
        // attempt itself returns. Checking the producer's IsCompleted only AFTER the
        // attempt returns reads the producer's state as of THEN, not as of when the
        // snapshot was actually opened, and would wrongly skip the retry for exactly the
        // incomplete-snapshot case it exists to catch (a real race, not a test artifact).
        bool retryEligible = producer.RetryEligible;
        SRRReconstructionResult quick = await AssembleCandidateAsync(options, actualRARFilePath, ctx.AssemblyDir, ctx.CandidateSlug, 1, _cts.Token).ConfigureAwait(false);

        if (quick.Status != SRRReconstructionStatus.Success && retryEligible)
        {
            // Incomplete snapshot: ANY non-success while the producer runs — including
            // Error from RARStream's missing/short-header ArgumentException — awaits completion
            // and retries ONCE with a fresh source.
            // Normal wait — faults PROPAGATE (generic catch = error row); not the quiet observer.
            // The exit code is deliberately discarded: its only reader is the not-created
            // classification, which has already moved to the next candidate by the time we get here.
            if (producer.ProcessTask is not null)
            {
                await producer.ProcessTask.ConfigureAwait(false);
            }

            quick = await AssembleCandidateAsync(options, actualRARFilePath, ctx.AssemblyDir, ctx.CandidateSlug, 1, _cts.Token).ConfigureAwait(false);
        }

        string? quickHash = quick.Status == SRRReconstructionStatus.Success && quick.WrittenPaths.Count >= 1
            ? HashCalculator.Calculate(options.HashType, quick.WrittenPaths[0])
            : null;
        bool quickMatch = quickHash != null && options.Hashes.Contains(quickHash);
        // Duplicate detection BEFORE recording the hash (mirrors the legacy fileHashes pattern):
        bool isDuplicateAssemblyHash = quickHash != null && fileHashes.Contains(quickHash);
        if (quickHash != null)
        {
            fileHashes.Add(quickHash);
        }

        _logger.Information(this, $"Assembled hash for {(quick.WrittenPaths.Count >= 1 ? quick.WrittenPaths[0] : ctx.AssemblyDir)}: {quickHash ?? quick.Status.ToString()} (match: {quickMatch})", LogTarget.Phase2);

        if (quickMatch)
        {
            return (GateOutcome.Match, quick, isDuplicateAssemblyHash, quickHash);
        }

        // Post-retry classification:
        switch (quick.Status)
        {
            case SRRReconstructionStatus.Error:
                // Persistent parse/I-O failure = failed combination — the EXISTING error-row
                // shape (CombinationFailed progress event + warning). RETENTION: like the
                // exception disposition, BOTH artifact classes are LEFT IN PLACE for
                // diagnosis.
                FireAssemblyErrorRow(ctx, options, currentProgress, quick.Diagnostic);
                skipRetentionCleanup = true;
                break;
            case SRRReconstructionStatus.SourceExhausted when !options.RAROptions.CompleteAllVolumes:
                // Mirror shift in non-CAV: vol-2 bytes were never written — INCONCLUSIVE.
                if (!_inconclusiveGuidanceLogged)
                {
                    _inconclusiveGuidanceLogged = true;
                    _logger.Information(this, "Some candidates are inconclusive without full volumes — enable \"Complete all volumes\" to test them", LogTarget.System);
                }
                _logger.Debug(this, $"{ctx.CandidateSlug}: inconclusive (assembly needs produced volume 2+)", LogTarget.Phase2);
                break;
            default:
                // SourceExhausted (CAV, producer done) or a hash mismatch: real no-match.
                break;
        }

        // Diagnose a produced archive that packs its files in a different order than
        // the release: splicing the release's original headers onto data read
        // positionally from a differently-ordered solid stream produces wrong bytes
        // with no other symptom — comparing the first entry's name is enough to catch
        // it and name the likely cause. Runs regardless of quick.Status above (Error/
        // SourceExhausted/hash-mismatch can all be caused by this), gated only on
        // having something to compare. Reads BOTH files before ApplyMismatchRetention
        // below, which may delete actualRARFilePath.
        if (!_packOrderGuidanceLogged && quick.WrittenPaths.Count >= 1)
        {
            string? expectedFirstName = RARFirstEntryReader.TryGetFirstFileName(quick.WrittenPaths[0]);
            string? producedFirstName = RARFirstEntryReader.TryGetFirstFileName(actualRARFilePath);
            if (expectedFirstName != null && producedFirstName != null
                && !string.Equals(expectedFirstName, producedFirstName, StringComparison.OrdinalIgnoreCase))
            {
                _packOrderGuidanceLogged = true;
                _logger.Warning(this,
                    $"Produced archive packs files in a different order than the release ('{producedFirstName}' before '{expectedFirstName}') — an /etc/rarfiles.lst order list or a rar default switch such as -ds from .rarrc or the RAR environment variable can cause this.",
                    LogTarget.Phase2);
            }
        }

        await ObserveProducerQuietlyAsync(producer, cancelFirst: true).ConfigureAwait(false);
        if (!skipRetentionCleanup) // false for mismatch/SourceExhausted/duplicate; true for Error
        {
            ApplyMismatchRetention(ctx.AssemblyDir, actualRARFilePath, options, isDuplicateAssemblyHash);
        }

        return (GateOutcome.NextCandidate, quick, isDuplicateAssemblyHash, quickHash);
    }

    /// <summary>
    /// The SRR-guided-assembly win path, entered once volume 1 has matched: re-assemble the full
    /// set (CompleteAllVolumes only), verify it, finalize it, and log the match. Returns the
    /// committed match, or <see langword="null"/> to mean "move to the next candidate".
    /// <para>
    /// Touches no producer state: the producer was already joined unconditionally before this runs,
    /// so every path here is safe to take with the producer fully observed.
    /// </para>
    /// <para>
    /// Note the finalize-then-log order here is the reverse of <see cref="TryFinalizeLegacyWin"/>'s
    /// log-then-finalize. That asymmetry is deliberate and observable in log ordering.
    /// </para>
    /// </summary>
    /// <param name="ctx">The candidate being finalized.</param>
    /// <param name="quick">The quick gate's assembled result, reused verbatim on the non-CAV path.</param>
    /// <param name="isDuplicateAssemblyHash">Whether the quick gate saw this hash before; drives mismatch retention.</param>
    /// <param name="hash">The assembled volume 1 hash, already matched, used only for the match log.</param>
    /// <param name="actualRARFilePath">The carrier archive rar produced, which is not the reconstruction.</param>
    /// <param name="currentProgress">The progress counter, for any error row this path fires.</param>
    /// <param name="options">The run's options.</param>
    private async Task<CommittedMatch?> FinalizeAssemblyWinAsync(
        CandidateContext ctx, SRRReconstructionResult quick, bool isDuplicateAssemblyHash,
        string hash, string actualRARFilePath, int currentProgress, BruteForceOptions options)
    {
        SRRReconstructionResult assembled;
        if (options.RAROptions.CompleteAllVolumes)
        {
            // FULL assembly — fresh source over the now-complete produced set.
            // Verification and finalization use THIS result's ordered WrittenPaths,
            // never the quick gate's single-volume result.
            assembled = await AssembleCandidateAsync(options, actualRARFilePath, ctx.AssemblyDir, ctx.CandidateSlug, int.MaxValue, _cts.Token).ConfigureAwait(false);
            if (assembled.Status != SRRReconstructionStatus.Success)
            {
                // A completed-producer full assembly cannot be an incomplete snapshot —
                // there is no retry here (unlike the quick gate above).
                if (assembled.Status == SRRReconstructionStatus.Error)
                {
                    // Persistent parse/I-O failure: retains BOTH classes for diagnosis.
                    FireAssemblyErrorRow(ctx, options, currentProgress, assembled.Diagnostic);
                }
                else
                {
                    // SourceExhausted: a real no-match — mismatch retention applies to
                    // both artifact classes.
                    ApplyMismatchRetention(ctx.AssemblyDir, actualRARFilePath, options, isDuplicateAssemblyHash);
                }

                return null;
            }
        }
        else
        {
            // Non-CAV: the single quick-gate volume already IS the mode's whole outcome.
            assembled = quick;
        }

        // Per-volume verification — the same gate as the legacy path, now literally the same code
        // (see VerifyVolumeSet). The assembled set is already in memory, so its factory is a plain
        // projection.
        if (!VerifyVolumeSet(() => assembled.WrittenPaths, options,
                $"{ctx.VersionDirectoryName} / {ctx.DisplayArguments}"))
        {
            ApplyMismatchRetention(ctx.AssemblyDir, actualRARFilePath, options, isDuplicateAssemblyHash);
            return null;
        }

        // Finalization runs OUTSIDE the guard (it applies with or without a CRC map):
        (IReadOnlyList<string> assembledPlaced, bool assembledComplete) = FinalizeAssembledSet(options, assembled.WrittenPaths, ctx.CandidateSlug, ctx.RarOutputDir);
        if (!assembledComplete)
        {
            // Transactional finalization failed: retain both classes for diagnosis.
            FireAssemblyErrorRow(ctx, options, currentProgress, "finalization incomplete — destination occupied or move failed");
            return null;
        }

        // ---- FULL MATCH (SRR-guided assembly) ----
        _logger.Information(this, "*** MATCH FOUND (SRR-guided assembly)! ***", LogTarget.System);
        _logger.Information(this, $"  Version: {ctx.VersionDirectoryName}", LogTarget.System);
        _logger.Information(this, $"  Params:  {ctx.DisplayArguments}", LogTarget.System);
        _logger.Information(this, $"  Hash:    {hash}", LogTarget.System);
        _logger.Information(this, $"  RAR:     {actualRARFilePath}", LogTarget.System);

        // Success cleanup — the carrier volumes are not the reconstruction. NOTE: for
        // qualified sets (CD2/x.rar) the reconstructor created assemblyDir/CD2/... —
        // after the moves the tree still holds empty subdirectories, so removal must be
        // RECURSIVE on the file-empty tree (never assume flat).
        if (options.RAROptions.DeleteRARFiles)
        {
            DeleteRARFileAndVolumes(actualRARFilePath);
        }

        try
        {
            if (Directory.Exists(ctx.AssemblyDir)
                && !Directory.EnumerateFiles(ctx.AssemblyDir, "*", SearchOption.AllDirectories).Any())
            {
                Directory.Delete(ctx.AssemblyDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: an empty-dir cleanup failure must never convert a committed
            // match into a failure.
        }

        return new CommittedMatch(new WinningCombo(ctx.Version, ctx.CommandLineArguments), assembledPlaced);
    }

    /// <summary>
    /// The legacy (non-assembly) win path, entered once volume 1 has matched: verify the whole
    /// produced set, log the match, and transactionally place the output. Returns the committed
    /// match, or <see langword="null"/> to mean "move to the next candidate".
    /// <para>
    /// Placement is transactional: only a FULLY-placed set (the mode's whole expected volume
    /// identity) counts as a match. An incomplete placement — occupied destination, a move failure,
    /// or fewer volumes produced than the release requires — must NOT be reported as found, so the
    /// search keeps going and a later, fully-placed combo can still win without colliding with this
    /// attempt's rolled-back partial output.
    /// </para>
    /// <para>
    /// Note the log-then-finalize order here is the reverse of the assembly path's
    /// finalize-then-log. That asymmetry is deliberate and observable in log ordering.
    /// </para>
    /// </summary>
    /// <param name="ctx">The candidate being finalized.</param>
    /// <param name="hash">Volume 1's hash, already matched, used only for the match log.</param>
    /// <param name="isDuplicateHash">Whether the gate saw this hash before; drives mismatch retention.</param>
    /// <param name="actualRARFilePath">The archive rar actually created.</param>
    /// <param name="currentProgress">The progress counter, for the incomplete-placement error row.</param>
    /// <param name="options">The run's options.</param>
    private CommittedMatch? TryFinalizeLegacyWin(
        CandidateContext ctx, string hash, bool isDuplicateHash, string actualRARFilePath,
        int currentProgress, BruteForceOptions options)
    {
        // Discovery and re-patching live INSIDE the gate and must stay there: when verification is
        // not configured this path performs no enumeration and no write at all. That is why
        // VerifyVolumeSet takes a factory rather than a volume list.
        string? completed = null;
        if (!VerifyVolumeSet(
                () =>
                {
                    completed = MatchedRARWriter.FindCreatedRARFile(ctx.RarFilePath);
                    if (completed == null)
                    {
                        return []; // deliberate count mismatch
                    }

                    // Re-patch all volumes before hashing if patching is needed (CRCs are of the
                    // final bytes). PatchRARFilesHostOS is idempotent (compares before writing), so
                    // re-running over the already-patched first volume is safe.
                    if (options.RAROptions.NeedsPatching)
                    {
                        PatchRARFilesHostOS(completed, options.RAROptions);
                    }

                    return MatchedRARWriter.GetAllVolumeFiles(completed);
                },
                options,
                $"{ctx.VersionDirectoryName} / {ctx.DisplayArguments}"))
        {
            // The SAME retention rule the assembly path applies via ApplyMismatchRetention:
            // DeleteRARFiles, OR a duplicate hash when DeleteDuplicateCRCFiles is set. Before the
            // duplicate flag was threaded through from the gate, this arm honoured DeleteRARFiles
            // alone and a duplicate carrier survived a set-verification mismatch here while the
            // equivalent assembly carrier was deleted.
            bool deleteOnMismatch = options.RAROptions.DeleteRARFiles
                || (isDuplicateHash && options.RAROptions.DeleteDuplicateCRCFiles);
            if (deleteOnMismatch && completed != null)
            {
                DeleteRARFileAndVolumes(completed);
            }

            return null; // near-miss: keep brute-forcing
        }

        // ---- FULL MATCH ----

        // Log match to System tab for visibility
        LogMatchDetails(options, ctx.VersionDirectoryName, ctx.DisplayArguments, hash, actualRARFilePath);

        // Rename the matched file(s) to their final name inside the "output" subdirectory.
        (IReadOnlyList<string> placed, bool complete) = RenameMatchedOutput(options, ctx.RarFilePath, actualRARFilePath, ctx.RarOutputDir);
        if (!complete)
        {
            // A warning AND a CombinationFailed row, matching the assembly path's disposition for
            // the same failure. Previously this logged only, so an incomplete placement was
            // invisible in the version grid on the legacy path while the assembly path showed it as
            // an error row. Fired AFTER the candidate's ordinary post-run row and carrying that
            // row's already-incremented progress — the same shape FireAssemblyErrorRow produces.
            _logger.Warning(this, $"{ctx.VersionDirectoryName} / {ctx.DisplayArguments}: matched but the full volume set could not be placed — continuing", LogTarget.Phase2);
            FireBruteForceProgress(NewRow(ctx, options.ReleaseDirectoryPath, currentProgress, combinationFailed: true));
            return null;
        }

        return new CommittedMatch(new WinningCombo(ctx.Version, ctx.CommandLineArguments), placed);
    }

    /// <summary>
    /// The legacy (non-assembly) first-volume gate: patch volume 1, hash it, and decide whether this
    /// candidate matches. Returns <c>Matched: false</c> to mean "move to the next candidate" — the
    /// caller's <c>continue</c> — after applying the configured retention.
    /// <para>
    /// Only volume 1 is patched here: with CompleteAllVolumes the remaining volumes may still be
    /// in progress. Duplicate detection deliberately runs BEFORE the hash is recorded, and the
    /// producer is observed to real completion before any deletion (invariant: no deletion while a
    /// producer task is unobserved).
    /// </para>
    /// </summary>
    /// <param name="actualRARFilePath">The archive rar actually created, which may differ from the intended path.</param>
    /// <param name="fileHashes">The run's accumulated hashes; this method both reads and adds to it.</param>
    /// <param name="producer">This candidate's producer; empty on the standard early-termination path.</param>
    /// <param name="options">The run's options.</param>
    private async Task<(bool Matched, string Hash, bool IsDuplicate)> TryLegacyGateAsync(
        string actualRARFilePath, HashSet<string> fileHashes,
        CandidateProducer producer, BruteForceOptions options)
    {
        // Apply patching to first volume only (other volumes may still be in progress)
        if (options.RAROptions.NeedsPatching)
        {
            PatchRARFilesHostOS(actualRARFilePath, options.RAROptions, allVolumes: false);
        }

        string hash = HashCalculator.Calculate(options.HashType, actualRARFilePath);

        _logger.Information(this, $"Hash for {actualRARFilePath}: {hash} (match: {options.Hashes.Contains(hash)})", LogTarget.Phase2);

        // Track if we've seen this hash before (to avoid keeping duplicates)
        bool isDuplicateHash = fileHashes.Contains(hash);
        fileHashes.Add(hash);

        if (options.Hashes.Contains(hash))
        {
            return (true, hash, isDuplicateHash);
        }

        // No match - kill background RAR process if still running, and OBSERVE it to
        // real completion before touching any file below (invariant: no deletion while
        // a producer task is unobserved). A no-op when no producer was launched
        // (standard path: already observed inside RARCompressDirectoryAsync).
        await ObserveProducerQuietlyAsync(producer, cancelFirst: true).ConfigureAwait(false);

        if (options.RAROptions.DeleteRARFiles)
        {
            // Delete all non-matching files
            DeleteRARFileAndVolumes(actualRARFilePath);
        }
        else if (options.RAROptions.DeleteDuplicateCRCFiles && isDuplicateHash)
        {
            // Delete duplicates to save disk space (only keep unique CRC files)
            _logger.Debug(this, $"Deleting duplicate hash file: {actualRARFilePath} (hash: {hash})", LogTarget.Phase2);
            DeleteRARFileAndVolumes(actualRARFilePath);
        }
        // If DeleteRARFiles is false and (DeleteDuplicateCRCFiles is false or not a duplicate), keep for debugging

        return (false, hash, isDuplicateHash);
    }

    /// <summary>
    /// The per-volume CRC gate shared by the assembly and legacy win paths. Returns
    /// <see langword="true"/> when the whole produced set matches, or when verification is not
    /// configured (no CompleteAllVolumes, or no CRC map — in which case the first-volume hash was
    /// the whole gate, preserving first-hash-only parity). Returns <see langword="false"/> after
    /// logging the mismatch; the caller then applies its own retention, which differs between the
    /// two paths.
    /// <para>
    /// The volumes arrive as a FACTORY, not a list, and it is invoked only when the gate is on. The
    /// legacy caller discovers its volumes and re-patches them inside that callback; running either
    /// when verification is disabled would add filesystem enumeration, a write, and new I/O failure
    /// modes to a path that currently performs none. Before this method existed, the two blocks were
    /// kept in step by a comment reading "the gate is EXACTLY the legacy block's own below".
    /// </para>
    /// </summary>
    /// <param name="volumesFactory">Produces the ordered volume paths to hash; invoked only when the gate is on.</param>
    /// <param name="options">The run's options, carrying the expected per-volume CRCs.</param>
    /// <param name="label">"{versionDirectoryName} / {displayArguments}", used in the log line.</param>
    private bool VerifyVolumeSet(Func<IReadOnlyList<string>> volumesFactory, BruteForceOptions options, string label)
    {
        IReadOnlyList<(string Name, string Crc)> expectedInOrder = BuildExpectedInOrder(options);
        if (!options.RAROptions.CompleteAllVolumes || expectedInOrder.Count == 0)
        {
            return true;
        }

        // The SRR-embedded SFV is ALWAYS CRC32, regardless of options.HashType: it comes from
        // BuildExpectedVolumeCrcs whether the user's own verification file is a .sfv or a .sha1.
        // Hashing with options.HashType (SHA1) here would compare 40-char SHA1s against 8-char
        // CRC32s and reject every byte-correct reconstruction.
        var producedCrcs = volumesFactory()
            .Select(v => HashCalculator.Calculate(HashType.CRC32, v))
            .ToList();

        VolumeMatchResult verify = VolumeMatchEvaluator.Evaluate(producedCrcs, expectedInOrder);
        if (verify.AllMatch)
        {
            return true;
        }

        VolumeMatch? m = verify.FirstMismatch;
        string detail = verify.CountMismatch
            ? $"produced {producedCrcs.Count} volume(s), expected {expectedInOrder.Count}"
            : $"{m?.ExpectedName} CRC mismatch (expected {m?.ExpectedCrc}, got {m?.ActualCrc})";
        _logger.Information(this, $"{label}: first volume matched but {detail} — continuing", LogTarget.Phase2);
        return false;
    }

    /// <summary>Mismatch/no-match retention applied to BOTH artifact classes under the
    /// standard flags: the assembled dir and the carrier volume set.</summary>
    private void ApplyMismatchRetention(string assemblyDir, string actualRARFilePath,
        BruteForceOptions options, bool duplicate)
    {
        bool delete = options.RAROptions.DeleteRARFiles
            || (duplicate && options.RAROptions.DeleteDuplicateCRCFiles);
        if (!delete)
        {
            return;
        }

        try
        {
            if (Directory.Exists(assemblyDir))
            {
                Directory.Delete(assemblyDir, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best-effort, mirrors carrier deletion (and the identical success-cleanup catch above)
        }

        DeleteRARFileAndVolumes(actualRARFilePath);   // the existing carrier helper
    }

    // Conservative margin below Windows' real command-line limit (~32,767 chars for CreateProcess),
    // leaving headroom for the exe path, switches, and output path this guard also counts.
    private const int InputFileArgumentsLengthGuard = 25_000;

    /// <summary>
    /// Decides this candidate's rar INPUT operand — the platform mask (today's behavior) or an
    /// explicit, SRR-ordered file list — and whether <see cref="BuildFinalArguments"/> should add
    /// <c>-ds</c> to match. Explicit order only engages when SRR-guided assembly is active for this
    /// set AND the SRR supplied an archived-file order (<see cref="RAROptions.OrderedArchiveFiles"/>);
    /// every other run — legacy reconstruction, Phase 1 comment filtering, or a flat/no-SRR run —
    /// gets a <see langword="null"/> tail, so <see cref="RARProcess"/> keeps producing today's mask
    /// untouched and solid-set byte order for those runs never depends on this method at all.
    /// </summary>
    /// <param name="options">
    /// The run's options; <see cref="BruteForceOptions.OutputDirectoryPath"/> is this candidate's
    /// work root, where the command-line-length guard's fallback list file (if needed) is planned
    /// — the CALLER writes it, this method itself touches no filesystem state.
    /// </param>
    /// <param name="rarExeFilePath">
    /// This candidate's rar executable path — the first token on the real process command line,
    /// counted toward the length guard below.
    /// </param>
    /// <param name="filteredArguments">
    /// This candidate's version-filtered switches — passed through to a trial
    /// <see cref="BuildFinalArguments"/> call (with <c>useDs: true</c>) so the length guard measures
    /// the REAL final switches (<c>-cfg-</c>/<c>-ma4</c>/<c>-vn</c>/<c>-z</c>/<c>-ds</c> included),
    /// not just the pre-composition display form. The caller still calls
    /// <see cref="BuildFinalArguments"/> itself afterward with the decided <c>UseDs</c> for the
    /// actual invocation; this trial result is measurement-only and otherwise discarded.
    /// </param>
    /// <param name="version">The rar version under test — forwarded to the trial <see cref="BuildFinalArguments"/> call.</param>
    /// <param name="outputFilePath">This candidate's output rar path — counted toward the guard.</param>
    /// <returns>
    /// <c>Tail</c>: <see langword="null"/> for a mask run, or the operands to pass verbatim as
    /// <see cref="RARProcess"/>'s <c>inputPaths</c> (after the output path).
    /// <c>Display</c>: the same tail rendered for <see cref="BruteForceProgressEventArgs.InputFileArguments"/>
    /// (empty for a mask run).
    /// <c>UseDs</c>: whether <see cref="BuildFinalArguments"/> should add <c>-ds</c>.
    /// <c>ListFile</c>: non-null exactly when the tail is the <c>@listfile</c> fallback — the
    /// list file the caller must write before launching this candidate.
    /// </returns>
    private (IReadOnlyList<string>? Tail, string Display, bool UseDs, ListFilePlan? ListFile) ComposeInputFileArguments(
        BruteForceOptions options, string rarExeFilePath, List<string> filteredArguments, int version, string outputFilePath)
    {
        if (!_useAssembly || options.RAROptions.OrderedArchiveFiles.Count == 0)
        {
            return (null, "", false, null);
        }

        List<string> tailEntries = [.. options.RAROptions.OrderedArchiveFiles.Select(ToTailEntry)];
        string display = JoinExecutedArguments(tailEntries);

        // Size the guard against the real components of the final command line — with useDs: true,
        // since that's what using the tail at all implies — not just the pre-composition display
        // args: -cfg-/-ma4/-vn/-z/-ds all add up, and a file list that fits without them can still
        // push the real invocation over the guard. Not counted: the three separators between the
        // four groups (exe / switches / output / tail) and any quoting the exe/output tokens need
        // if they contain spaces — a handful of characters (the Windows limit is 32,767 UTF-16
        // characters), far inside the guard's ~7,700-character margin. This trial list is
        // measurement-only; the caller builds its own (identical, given the same inputs) copy once
        // UseDs is decided below.
        List<string> trialFinalArguments = BuildFinalArguments(filteredArguments, options, version, useDs: true);
        string joinedFinalSwitches = JoinExecutedArguments(trialFinalArguments);

        int candidateLength = rarExeFilePath.Length + joinedFinalSwitches.Length + outputFilePath.Length + display.Length;
        if (candidateLength <= InputFileArgumentsLengthGuard)
        {
            return (tailEntries, display, true, null);
        }

        // Over budget: fall back to rar's own @listfile operand (one file spec per line) instead of
        // every name on the command line. rar reads the list in an unspecified local codepage, so a
        // non-ASCII name here cannot be trusted to round-trip — rather than risk silently splicing
        // bytes for the WRONG file, give up on ordering entirely for this run and fall through to
        // rar's own (mask) ordering.
        if (!options.RAROptions.OrderedArchiveFiles.All(name => name.All(char.IsAscii)))
        {
            if (!_nonAsciiOrderFallbackLogged)
            {
                _nonAsciiOrderFallbackLogged = true;
                _logger.Warning(this,
                    "File names exceed the command-line limit and are not ASCII — using rar's own ordering for this run",
                    LogTarget.Phase2);
            }

            return (null, "", false, null);
        }

        string listPath = Path.Combine(options.OutputDirectoryPath, "rar-file-order.lst");
        var listFile = new ListFilePlan(listPath, string.Join('\n', options.RAROptions.OrderedArchiveFiles));

        List<string> fallbackTail = [$"@{listPath}"];
        return (fallbackTail, JoinExecutedArguments(fallbackTail), true, listFile);
    }

    /// <summary>
    /// The <c>@listfile</c> fallback planned by <see cref="ComposeInputFileArguments"/> — path
    /// and ASCII content — for the caller to write. Kept a plan rather than a side effect so the
    /// compose step stays pure composition.
    /// </summary>
    private readonly record struct ListFilePlan(string Path, string Content);

    /// <summary>
    /// Normalizes one <see cref="RAROptions.OrderedArchiveFiles"/> entry into a rar input operand
    /// relative to the current directory — the same "./name" convention as today's platform mask
    /// (".\*" / "./*"), so a mixed-separator name from the SRR resolves correctly regardless of the
    /// platform running rar.
    /// </summary>
    private static string ToTailEntry(string name) =>
        "." + Path.DirectorySeparatorChar + name.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

    /// <summary>
    /// Builds the final RAR argument list from the filtered arguments, auto-adding
    /// <c>-cfg-</c> (ignore user rar config), <c>-ma4</c> (RAR 5.50-6.x), <c>-vn</c> (old volume
    /// naming), the comment option (<c>-z</c>), and <c>-ds</c> where applicable.
    /// </summary>
    /// <param name="filteredArguments">This candidate's version-filtered switches.</param>
    /// <param name="options">The run's options.</param>
    /// <param name="version">The rar version under test.</param>
    /// <param name="useDs">
    /// Whether to add <c>-ds</c> (store files in the order given rather than rar's own
    /// rarfiles.lst/name-sort default) — set exactly when this candidate's rar invocation carries
    /// an explicit, SRR-ordered input file list instead of the platform mask (see
    /// <see cref="ComposeInputFileArguments"/>), never on a version gate.
    /// </param>
    private List<string> BuildFinalArguments(List<string> filteredArguments, BruteForceOptions options, int version, bool useDs)
    {
        List<string> finalArguments = [.. filteredArguments];

        // Auto-add -ma4 for RAR 5.50-6.x to force RAR4 format (unless -ma5 was explicitly requested)
        // RAR 7.x doesn't accept -ma4/-ma5 flags
        if (version >= 550 && version < RARVersionThresholds.RAR7FormatMinimum && !finalArguments.Contains("-ma4") && !finalArguments.Contains("-ma5"))
        {
            finalArguments.Insert(0, "-ma4");
        }

        // Add -vn for old volume naming if enabled (available since RAR 3.00, removed in RAR 7.x)
        if (options.RAROptions.UseOldVolumeNaming && version >= 300 && version < RARVersionThresholds.RAR7FormatMinimum && !finalArguments.Contains("-vn"))
        {
            finalArguments.Add("-vn");
        }

        if (!string.IsNullOrEmpty(_commentFilePath))
        {
            // Add comment option: -z<commentfile>
            finalArguments.Add($"-z{_commentFilePath}");
        }

        if (useDs)
        {
            finalArguments.Add("-ds");
        }

        // Force -cfg- (ignore rar.ini/rarrc) on every invocation, unconditionally and with no version
        // gate: a user's local rar config can inject switches (e.g. -ds) that silently change what
        // reconstruction produces, and -cfg- has been available in every supported rar release —
        // measured across the packs 2.03 through 7.20. Inserted last so it lands ahead of -ma4's own
        // Insert(0, ...) above rather than being pushed behind it.
        finalArguments.Insert(0, "-cfg-");

        return finalArguments;
    }

    /// <summary>
    /// Logs a found match (and any post-creation patching that was applied) to the System log.
    /// </summary>
    private void LogMatchDetails(
        BruteForceOptions options, string rarVersionDirectoryName, string displayArguments,
        string hash, string actualRARFilePath)
    {
        string patchedNote = options.RAROptions.NeedsPatching ? " (patched)" : "";
        _logger.Information(this, $"*** MATCH FOUND{patchedNote}! ***", LogTarget.System);
        _logger.Information(this, $"  Version: {rarVersionDirectoryName}", LogTarget.System);
        _logger.Information(this, $"  Params:  {displayArguments}", LogTarget.System);
        _logger.Information(this, $"  Hash:    {hash}", LogTarget.System);
        _logger.Information(this, $"  RAR:     {actualRARFilePath}", LogTarget.System);

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
    }

    /// <summary>
    /// Renames/moves the matched RAR file (or all volumes, when CompleteAllVolumes is enabled) to
    /// their final names inside the <c>output</c> subdirectory, patching remaining volumes if
    /// needed. Transactional: the full source-to-destination move map is precomputed and
    /// validated (every destination free) before any file is touched; if a move fails (a
    /// different file already occupies its destination) or throws partway through, this call's
    /// own already-completed moves are rolled back (best-effort — see
    /// <see cref="RollBackMoves"/>) and <c>Complete</c> is <see langword="false"/>, never leaving
    /// a partially-renamed set behind for a later winning combo to collide with.
    /// </summary>
    /// <returns>
    /// The destination paths actually placed, and whether the mode's full expected volume
    /// identity was placed. Completeness is judged against
    /// <see cref="RAROptions.OriginalRARFileNames"/> — the release volume names, not
    /// <see cref="BuildExpectedInOrder"/>, which omits volumes with no known CRC — by COUNT: the
    /// single first volume for non-CAV, or every volume for CAV, regardless of whether
    /// <see cref="RAROptions.RenameToOriginalNames"/> is set (a generated-name run is judged
    /// complete once the full expected count is placed). <c>Placed</c> is only meaningful when
    /// <c>Complete</c> is <see langword="true"/> — a partial result is never partially reported.
    /// </returns>
    internal (IReadOnlyList<string> Placed, bool Complete) RenameMatchedOutput(
        BruteForceOptions options, string rarFilePath, string actualRARFilePath, string rarOutputDir)
    {
        string baseName = Path.GetFileNameWithoutExtension(rarFilePath);
        string patchedBaseName = options.RAROptions.NeedsPatching ? baseName + "-patched" : baseName;
        IReadOnlyList<string> originalNames = options.RAROptions.OriginalRARFileNames;
        bool useOriginalNames = options.RAROptions.RenameToOriginalNames && originalNames.Count > 0;

        List<(string Source, string Dest)> plan;

        if (options.RAROptions.CompleteAllVolumes)
        {
            // Re-find all volumes now that RAR has completed
            string? completedRARFilePath = MatchedRARWriter.FindCreatedRARFile(rarFilePath);
            if (completedRARFilePath == null)
            {
                _logger.Warning(this, "No completed volume(s) found to place.", LogTarget.System);
                return ([], false);
            }

            // Patch remaining volumes (first volume already patched - will be no-op for it)
            if (options.RAROptions.NeedsPatching)
            {
                PatchRARFilesHostOS(completedRARFilePath, options.RAROptions);
            }

            List<string> producedVolumes = MatchedRARWriter.GetAllVolumeFiles(completedRARFilePath);

            // The expected identity is EVERY release volume name (not just the ones with a known
            // CRC) — when no names are available at all, fall back to trusting whatever was
            // produced (there is nothing to validate the count against).
            List<string> expectedNames = originalNames.Count > 0
                ? [.. originalNames.Select(LastSegment)]
                : [.. producedVolumes.Select(v => Path.GetFileName(v))];

            if (producedVolumes.Count != expectedNames.Count)
            {
                _logger.Warning(this, $"  Produced {producedVolumes.Count} volume(s) but the release expects {expectedNames.Count} — not placing a partial set.", LogTarget.System);
                return ([], false);
            }

            plan = new List<(string, string)>(producedVolumes.Count);
            for (int i = 0; i < producedVolumes.Count; i++)
            {
                string outputFileName = useOriginalNames
                    ? expectedNames[i]
                    : Path.GetFileName(producedVolumes[i]).Replace(baseName, patchedBaseName, StringComparison.Ordinal);
                plan.Add((producedVolumes[i], Path.Combine(rarOutputDir, outputFileName)));
            }
        }
        else
        {
            // Standard behavior: just the first .rar file — the mode's whole expected identity is
            // that single volume.
            string outputFileName = useOriginalNames
                ? LastSegment(originalNames[0])
                : Path.GetFileName(actualRARFilePath).Replace(baseName, patchedBaseName, StringComparison.Ordinal);
            plan = [(actualRARFilePath, Path.Combine(rarOutputDir, outputFileName))];
        }

        return ExecuteMovePlan(plan);
    }

    /// <summary>
    /// Finalizes an assembly win: moves the reconstructor's ordered <c>WrittenPaths</c> —
    /// verbatim, no volume rediscovery, no patching — transactionally into <paramref
    /// name="rarOutputDir"/> (the app's VerifiedOutputRelocator consumes committed files there).
    /// Naming: <see cref="RAROptions.RenameToOriginalNames"/> true → the assembled
    /// file's own name is kept as-is (the reconstructor already wrote it under its SRR-recorded
    /// original volume name, e.g. a qualified <c>"CD2/t.rar"</c> section flattens to
    /// <c>"t.rar"</c> via <see cref="Path.GetFileName(string)"/>); false → basename replacement
    /// preserving the COMPLETE volume suffix (<c>"foo.part01.rar"</c> → <c>"{candidateSlug}
    /// -assembled.part01.rar"</c>, <c>"foo.r00"</c> → <c>"{candidateSlug}-assembled.r00"</c>) via
    /// <see cref="RARVolumeNaming.GetBaseName"/> — never <see cref="Path.GetExtension(string)"/>,
    /// which would collapse distinct <c>.partNN.rar</c> volumes onto the same generated name.
    /// Transactional via <see cref="ExecuteMovePlan"/>: <c>Complete</c> is only ever
    /// <see langword="true"/> when every volume was placed.
    /// </summary>
    internal (IReadOnlyList<string> Placed, bool Complete) FinalizeAssembledSet(
        BruteForceOptions options, IReadOnlyList<string> assembledPaths,
        string candidateSlug, string rarOutputDir)
    {
        var plan = new List<(string Source, string Dest)>(assembledPaths.Count);
        foreach (string src in assembledPaths)
        {
            string fileName = Path.GetFileName(src);
            if (!options.RAROptions.RenameToOriginalNames)
            {
                string baseName = RARVolumeNaming.GetBaseName(fileName);
                string suffix = fileName[baseName.Length..];
                fileName = $"{candidateSlug}-assembled{suffix}";
            }

            plan.Add((src, Path.Combine(rarOutputDir, fileName)));
        }

        return ExecuteMovePlan(plan);
    }

    /// <summary>
    /// Executes a precomputed source-to-destination move plan transactionally: every destination
    /// is verified free (or is its own source — a no-op) before any file is moved; if a move then
    /// fails or throws, this call's own already-completed moves are rolled back (see
    /// <see cref="RollBackMoves"/>) and the result is <c>Complete=false</c> with an empty
    /// <c>Placed</c> — a partial result is never partially reported.
    /// </summary>
    private (IReadOnlyList<string> Placed, bool Complete) ExecuteMovePlan(List<(string Source, string Dest)> plan)
    {
        foreach ((string source, string dest) in plan)
        {
            if (!MatchedRARWriter.PathsEqual(source, dest) && File.Exists(dest))
            {
                _logger.Warning(this, $"  Volume placement aborted: destination already occupied by a different file: '{dest}'", LogTarget.System);
                return ([], false);
            }
        }

        var completedMoves = new List<(string Source, string Dest)>();
        var placed = new List<string>();

        foreach ((string source, string dest) in plan)
        {
            if (MatchedRARWriter.PathsEqual(source, dest))
            {
                // Already at its final path — nothing to move, still counts as placed.
                placed.Add(dest);
                _logger.Information(this, $"  Volume: {Path.GetFileName(dest)} (already in place)", LogTarget.System);
                continue;
            }

            bool moved;
            Exception? moveException = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                moved = MatchedRARWriter.MoveMatchedFile(source, dest);
            }
            catch (Exception ex)
            {
                moved = false;
                moveException = ex;
            }

            if (moved)
            {
                completedMoves.Add((source, dest));
                placed.Add(dest);
                _logger.Information(this, $"  Volume: {Path.GetFileName(dest)}", LogTarget.System);
                continue;
            }

            string reason = moveException != null
                ? moveException.Message
                : $"a different file already occupies '{dest}'";
            _logger.Warning(this, $"  Volume NOT written ({reason}); rolling back {completedMoves.Count} prior move(s)", LogTarget.System);

            RollBackMoves(completedMoves);
            return ([], false);
        }

        _logger.Information(this, $"  Completed {placed.Count} volume(s)", LogTarget.System);
        return (placed, true);
    }

    /// <summary>
    /// Rolls back this call's own completed moves (dest → original source), in reverse order,
    /// best-effort: a rollback move that itself fails (something now occupies the original
    /// source) or throws is logged and left as-is — the caller's overall result is
    /// <c>Complete=false</c> regardless, so a failed rollback changes only how cleanly the file
    /// system unwinds, never the reported outcome.
    /// </summary>
    internal void RollBackMoves(IReadOnlyList<(string Source, string Dest)> completedMoves)
    {
        for (int i = completedMoves.Count - 1; i >= 0; i--)
        {
            (string source, string dest) = completedMoves[i];
            try
            {
                if (!MatchedRARWriter.MoveMatchedFile(dest, source))
                {
                    _logger.Warning(this, $"  Rollback failed: could not move '{dest}' back to '{source}' (a different file now occupies it); output left inconsistent at '{dest}'.", LogTarget.System);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(this, $"  Rollback failed: could not move '{dest}' back to '{source}': {ex.Message}; output left inconsistent at '{dest}'.", LogTarget.System);
            }
        }
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
            List<string> filesToPatch = allVolumes ? MatchedRARWriter.GetAllVolumeFiles(rarFilePath) : [rarFilePath];

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
            string hostOSName = RARPatcher.GetHostOSName(opts.DetectedFileHostOS.Value);
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
