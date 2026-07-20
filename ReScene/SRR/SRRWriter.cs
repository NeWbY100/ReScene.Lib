using System.Text;
using ReScene.Core;
using ReScene.RAR;

namespace ReScene.SRR;

/// <summary>
/// Creates SRR (Scene Release Reconstruction) files from RAR archives.
/// </summary>
public class SRRWriter
{
    private static ReadOnlySpan<byte> RAR4Marker => RARUtils.RAR4Marker;
    private static ReadOnlySpan<byte> RAR5Marker => RARUtils.RAR5Marker;

    /// <summary>
    /// Raised to report progress during SRR creation.
    /// </summary>
    public event EventHandler<SRRCreationProgressEventArgs>? Progress;

    /// <summary>
    /// Creates an SRR file from a list of RAR volume paths.
    /// </summary>
    /// <param name="outputPath">
    /// Path for the output SRR file.
    /// </param>
    /// <param name="rarVolumePaths">
    /// Ordered list of RAR volume file paths.
    /// </param>
    /// <param name="storedFiles">
    /// Optional ordered list of stored files. Blocks are written in this order; a stored name that
    /// repeats is written only once (first occurrence wins).
    /// </param>
    /// <param name="options">
    /// Creation options, or null for defaults.
    /// </param>
    /// <param name="ct">
    /// Cancellation token.
    /// </param>
    /// <returns>
    /// Result of the creation operation.
    /// </returns>
    public async Task<SRRCreationResult> CreateAsync(
        string outputPath,
        IReadOnlyList<string> rarVolumePaths,
        IReadOnlyList<StoredFileEntry>? storedFiles = null,
        SRRCreationOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new SRRCreationOptions();
        var result = new SRRCreationResult();

        try
        {
            if (rarVolumePaths.Count == 0)
            {
                throw new ArgumentException("At least one RAR volume path is required.", nameof(rarVolumePaths));
            }

            // Validate all files exist
            foreach (string path in rarVolumePaths)
            {
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException($"RAR volume not found: {path}", path);
                }
            }

            if (storedFiles != null)
            {
                foreach (StoredFileEntry entry in storedFiles)
                {
                    if (!File.Exists(entry.FullPath))
                    {
                        throw new FileNotFoundException($"Stored file not found: {entry.FullPath}", entry.FullPath);
                    }
                }
            }

            string? outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            using var outStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = new BinaryWriter(outStream, Encoding.UTF8, leaveOpen: true);

            // 1. Write SRR Header block
            WriteSRRHeader(writer, options.AppName);

            // 2. Write stored file blocks, in the given order. A stored name can only appear once
            //    in an SRR, so a repeat (after slash-normalization) is skipped (first wins).
            if (storedFiles != null)
            {
                var writtenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (StoredFileEntry entry in storedFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    string storedName = entry.StoredName.Replace('\\', '/');
                    if (!writtenNames.Add(storedName))
                    {
                        Log($"Skipping duplicate stored name: {storedName}");
                        continue;
                    }

                    byte[] fileData = await File.ReadAllBytesAsync(entry.FullPath, ct).ConfigureAwait(false);
                    Log($"Adding stored file: {storedName} ({fileData.Length:N0} bytes)");
                    SRRBlockWriter.WriteStoredFileBlock(writer, storedName, fileData);
                    result.StoredFileCount++;
                }
            }

            // 3. Process each RAR volume
            await WriteVolumesAsync(
                writer,
                rarVolumePaths.Select(p => (Path.GetFileName(p), p)).ToList(),
                options,
                result,
                ct).ConfigureAwait(false);

            // 4. Optionally compute and write OSO hash blocks
            if (options.ComputeOSOHashes)
            {
                Log("Computing OSO hashes...");
                List<(string FileName, ulong FileSize, byte[] Hash)> hashes = OSOHashCalculator.ComputeHashes(
                    rarVolumePaths,
                    onWarning: warning =>
                    {
                        Log(warning);
                        result.Warnings.Add(warning);
                    });
                foreach ((string? fileName, ulong fileSize, byte[]? hash) in hashes)
                {
                    Log($"Added OSO hash: {fileName}");
                    WriteOSOHashBlock(writer, fileName, fileSize, hash);
                }
            }

            // 5. Optionally generate and store languages.diz from VobSub .idx files
            if (options.GenerateLanguagesDiz)
            {
                Log("Scanning RAR archive for VobSub .idx files...");
                LanguagesDizGenerator.Result dizResult = LanguagesDizGenerator.Generate(rarVolumePaths);
                foreach (string idxFileName in dizResult.IdxFileNames)
                {
                    result.LanguagesDizIdxFiles.Add(idxFileName);
                }

                foreach (string warning in dizResult.Warnings)
                {
                    result.Warnings.Add(warning);
                }

                if (dizResult.Data is not null)
                {
                    Log($"Adding languages.diz ({dizResult.Data.Length:N0} bytes)");
                    SRRBlockWriter.WriteStoredFileBlock(writer, "languages.diz", dizResult.Data);
                    result.StoredFileCount++;
                }
                else if (dizResult.IdxFileNames.Count == 0)
                {
                    result.Warnings.Add("languages.diz requested but no VobSub .idx files were found.");
                }
                else if (dizResult.Warnings.Count == 0)
                {
                    result.Warnings.Add("languages.diz requested but no language lines could be extracted from the .idx file(s).");
                }
            }

            await outStream.FlushAsync(ct).ConfigureAwait(false);
            result.SRRFileSize = outStream.Length;
            result.OutputPath = outputPath;
            result.Success = true;

            ReportProgress(rarVolumePaths.Count, rarVolumePaths.Count, "SRR creation complete.");
        }
        catch (OperationCanceledException)
        {
            result.ErrorMessage = "Operation was cancelled.";
            StreamUtilities.TryDeleteFile(outputPath);
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            StreamUtilities.TryDeleteFile(outputPath);
        }

        return result;
    }

    /// <summary>
    /// Creates an SRR file from an SFV file, automatically discovering RAR volumes.
    /// </summary>
    /// <param name="outputPath">
    /// Path for the output SRR file.
    /// </param>
    /// <param name="sfvFilePath">
    /// Path to the SFV file.
    /// </param>
    /// <param name="additionalFiles">
    /// Optional ordered list of additional files to store (written before the RAR-derived blocks,
    /// in this order). Entries whose source file is missing are skipped.
    /// </param>
    /// <param name="options">
    /// Creation options, or null for defaults.
    /// </param>
    /// <param name="ct">
    /// Cancellation token.
    /// </param>
    /// <returns>
    /// Result of the creation operation.
    /// </returns>
    public async Task<SRRCreationResult> CreateFromSFVAsync(
        string outputPath,
        string sfvFilePath,
        IReadOnlyList<StoredFileEntry>? additionalFiles = null,
        SRRCreationOptions? options = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(sfvFilePath))
        {
            return new SRRCreationResult { ErrorMessage = $"SFV file not found: {sfvFilePath}" };
        }

        string sfvDir = Path.GetDirectoryName(sfvFilePath) ?? ".";
        string[] sfvLines = await File.ReadAllLinesAsync(sfvFilePath, ct).ConfigureAwait(false);

        // Parse SFV to find RAR volumes
        var rarFiles = new List<string>();
        foreach (string fileName in SfvVolumeResolver.ParseSfvEntryNames(sfvLines))
        {
            if (RARVolumeIdentifier.IsRARVolume(fileName))
            {
                string fullPath = Path.Combine(sfvDir, fileName);
                if (File.Exists(fullPath))
                {
                    rarFiles.Add(fullPath);
                }
            }
        }

        if (rarFiles.Count == 0)
        {
            return new SRRCreationResult { ErrorMessage = "No RAR volumes found in SFV file." };
        }

        // Sort volumes in correct order
        rarFiles.Sort(RARVolumeNameComparer.Instance);

        // Keep the caller's order; skip entries whose source is missing. CreateAsync writes them
        // before the RAR-derived blocks and drops any repeated stored name.
        var storedFiles = additionalFiles
            ?.Where(e => File.Exists(e.FullPath))
            .ToList();

        return await CreateAsync(outputPath, rarFiles, storedFiles, options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a single SRR file covering N input sets (each a <c>.sfv</c> or a first-volume
    /// <c>.rar</c>), plus explicit stored files. See spec §1/§1a for the full contract.
    /// Zero inputs is valid: with stored files it produces a storage-only SRR, with none of
    /// either it produces a header-only SRR — emptiness is never an error for this overload.
    /// Writes to a temp file in <paramref name="outputPath"/>'s own directory and atomically
    /// moves it into place on success; any failure (or a pre-cancelled <paramref name="ct"/>,
    /// which PROPAGATES rather than becoming an error result) deletes the temp file and leaves
    /// a pre-existing destination untouched.
    /// </summary>
    public async Task<SRRCreationResult> CreateFromInputsAsync(
        string outputPath,
        IReadOnlyList<string> inputFiles,
        string? rootFolder,
        bool storeRelativePaths,
        IReadOnlyList<StoredFileEntry>? additionalFiles = null,
        SRRCreationOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new SRRCreationOptions();
        var result = new SRRCreationResult();
        string tmpPath = string.Empty;
        bool tmpCreated = false;

        try
        {
            ct.ThrowIfCancellationRequested();

            if (storeRelativePaths && rootFolder is null)
            {
                throw new ArgumentException(
                    "rootFolder is required when storeRelativePaths is true.", nameof(rootFolder));
            }

            string? rootFinal = storeRelativePaths ? SrrNameCanonicalizer.GetFinalPath(rootFolder!) : null;

            // Ensure the output directory exists BEFORE the self-collision check below, since
            // GetFinalPath (used to compute the output's comparison key) requires its target to
            // exist — a fresh nested output path must not be misread as "cannot resolve". A later
            // validation/write failure can leave this freshly-created directory behind, empty;
            // that's an accepted trade-off — no cleanup here, since removing it could race another
            // writer or delete a directory that pre-existed for an unrelated reason.
            string? outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            string outputKey = ComputeOutputKey(outputPath);

            // Reject an outputPath that resolves to any explicit input or stored source before
            // touching disk — fails fast for the common explicit-input case.
            RejectIfOutputMatches(outputKey, inputFiles, "an input");
            if (additionalFiles != null)
            {
                RejectIfOutputMatches(outputKey, additionalFiles.Select(e => e.FullPath), "a stored source");
            }

            List<(string Name, string Path)> rawVolumes =
                await ResolveVolumesAsync(inputFiles, rootFinal, storeRelativePaths, ct).ConfigureAwait(false);
            (List<StoredFileEntry> storedFiles, HashSet<string> sourcesSeen, Dictionary<string, string> namesSeen) =
                ResolveStoredFiles(inputFiles, additionalFiles, rootFinal, storeRelativePaths);
            List<(string Name, string Path)> volumes =
                ReconcileVolumesAgainstStoredFiles(rawVolumes, sourcesSeen, namesSeen);

            // Re-validate against the FULLY RESOLVED emission set (codex/peer C1): an outputPath
            // equal to a volume DISCOVERED via an SFV (never itself an `inputFiles` entry) or to a
            // stored source resolved from one wasn't caught by the pre-resolution check above, yet
            // File.Move below would still destroy it.
            RejectIfOutputMatches(outputKey, volumes.Select(v => v.Path), "a resolved volume");
            RejectIfOutputMatches(outputKey, storedFiles.Select(f => f.FullPath), "a resolved stored source");

            (FileStream outStream, tmpPath) = CreateExclusiveTempFile(outputPath);
            tmpCreated = true;
            using (outStream)
            using (var writer = new BinaryWriter(outStream, Encoding.UTF8, leaveOpen: true))
            {
                WriteSRRHeader(writer, options.AppName);

                foreach (StoredFileEntry entry in storedFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    byte[] fileData = await File.ReadAllBytesAsync(entry.FullPath, ct).ConfigureAwait(false);
                    Log($"Adding stored file: {entry.StoredName} ({fileData.Length:N0} bytes)");
                    SRRBlockWriter.WriteStoredFileBlock(writer, entry.StoredName, fileData);
                    result.StoredFileCount++;
                }

                await WriteVolumesAsync(writer, volumes, options, result, ct).ConfigureAwait(false);

                List<string> volumePaths = volumes.Select(v => v.Path).ToList();
                if (options.ComputeOSOHashes)
                {
                    Log("Computing OSO hashes...");
                    List<(string FileName, ulong FileSize, byte[] Hash)> hashes = OSOHashCalculator.ComputeHashes(
                        volumePaths,
                        onWarning: warning =>
                        {
                            Log(warning);
                            result.Warnings.Add(warning);
                        });
                    foreach ((string? fileName, ulong fileSize, byte[]? hash) in hashes)
                    {
                        Log($"Added OSO hash: {fileName}");
                        WriteOSOHashBlock(writer, fileName, fileSize, hash);
                    }
                }

                if (options.GenerateLanguagesDiz)
                {
                    Log("Scanning RAR archive for VobSub .idx files...");
                    LanguagesDizGenerator.Result dizResult = LanguagesDizGenerator.Generate(volumePaths);
                    foreach (string idxFileName in dizResult.IdxFileNames)
                    {
                        result.LanguagesDizIdxFiles.Add(idxFileName);
                    }

                    foreach (string warning in dizResult.Warnings)
                    {
                        result.Warnings.Add(warning);
                    }

                    if (dizResult.Data is not null)
                    {
                        Log($"Adding languages.diz ({dizResult.Data.Length:N0} bytes)");
                        SRRBlockWriter.WriteStoredFileBlock(writer, "languages.diz", dizResult.Data);
                        result.StoredFileCount++;
                    }
                    else if (dizResult.IdxFileNames.Count == 0)
                    {
                        result.Warnings.Add("languages.diz requested but no VobSub .idx files were found.");
                    }
                    else if (dizResult.Warnings.Count == 0)
                    {
                        result.Warnings.Add("languages.diz requested but no language lines could be extracted from the .idx file(s).");
                    }
                }

                await outStream.FlushAsync(ct).ConfigureAwait(false);
                result.SRRFileSize = outStream.Length;
            }

            File.Move(tmpPath, outputPath, overwrite: true);
            result.OutputPath = outputPath;
            result.Success = true;

            // The commit above must be the LAST fallible action affecting the result (codex/peer
            // C5): a throwing Progress subscriber here — including one that throws
            // OperationCanceledException — must not flip an already-committed success into an
            // error result, nor propagate as a "cancelled" outcome after the destination has
            // already been replaced. Swallow: the caller already has everything it needs from
            // `result`, and the commit genuinely succeeded regardless of what a listener does.
            try
            {
                ReportProgress(volumes.Count, volumes.Count, "SRR creation complete.");
            }
            catch
            {
                // Intentionally ignored — see comment above.
            }
        }
        catch (OperationCanceledException)
        {
            if (tmpCreated)
            {
                StreamUtilities.TryDeleteFile(tmpPath);
            }

            throw;
        }
        catch (Exception ex)
        {
            if (tmpCreated)
            {
                StreamUtilities.TryDeleteFile(tmpPath);
            }

            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Computes the comparison key for an <paramref name="outputPath"/> self-collision check
    /// (clause 5): the OS final path when it already exists, else its DIRECTORY's final path
    /// with the file name reattached (since <see cref="SrrNameCanonicalizer.GetFinalPath"/>
    /// requires its target to exist, and the normal case is an outputPath that doesn't exist
    /// yet).
    /// </summary>
    private static string ComputeOutputKey(string outputPath) =>
        File.Exists(outputPath)
            ? SrrNameCanonicalizer.GetFinalPath(outputPath)
            : Path.Combine(
                SrrNameCanonicalizer.GetFinalPath(Path.GetDirectoryName(Path.GetFullPath(outputPath))!),
                Path.GetFileName(outputPath));

    /// <summary>
    /// Rejects <paramref name="outputKey"/> (from <see cref="ComputeOutputKey"/>) if it matches
    /// the OS final path of any of <paramref name="candidatePaths"/> — reused for BOTH the
    /// pre-resolution check (explicit inputs/stored sources) and the post-resolution check
    /// (discovered volumes/stored sources, codex/peer C1) so a symlink/junction can't disguise a
    /// self-collision either way.
    /// </summary>
    private static void RejectIfOutputMatches(string outputKey, IEnumerable<string> candidatePaths, string sourceKind)
    {
        foreach (string candidate in candidatePaths)
        {
            if (string.Equals(outputKey, SrrNameCanonicalizer.GetFinalPath(candidate), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Output path is the same as {sourceKind}: {candidate}");
            }
        }
    }

    /// <summary>
    /// Creates the transaction's temp file exclusively (<see cref="FileMode.CreateNew"/>) so an
    /// astronomically unlikely 8-hex suffix collision with a pre-existing file can never be
    /// silently truncated (codex/peer C7, unlike <see cref="FileMode.Create"/>); on that
    /// collision, regenerates the suffix and retries a bounded number of times.
    /// </summary>
    private static (FileStream Stream, string Path) CreateExclusiveTempFile(string outputPath)
    {
        const int maxAttempts = 5;
        for (int attempt = 1; ; attempt++)
        {
            string candidate = outputPath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            try
            {
                return (new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None), candidate);
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                // Regenerate and retry — see summary above.
            }
        }
    }

    /// <summary>
    /// Resolves every input into an ordered, chain-grouped volume list (clause 2): each
    /// <c>.sfv</c> input contributes its RAR-volume entries (via <see cref="SrrNameCanonicalizer.ResolveSfvEntry"/>),
    /// each other input is walked as a first-volume RAR via the existing chain-discovery logic
    /// (<see cref="FileOperations.GetAllVolumeFiles"/>). Volumes are grouped by archive-set key
    /// (directory + base name) in first-seen order, sorted only WITHIN their own chain via
    /// <see cref="RARVolumeNameComparer"/> — never across chains, so two interleaved or same-
    /// basename-different-directory chains stay distinct and internally ordered. Each volume's
    /// SRR block name is then computed per clause 3.
    /// </summary>
    private static async Task<List<(string Name, string Path)>> ResolveVolumesAsync(
        IReadOnlyList<string> inputFiles, string? rootFinal, bool storeRelativePaths, CancellationToken ct)
    {
        var chainOrder = new List<string>();
        var chains = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (string input in inputFiles)
        {
            if (IsSfvPath(input))
            {
                string sfvDir = Path.GetDirectoryName(input) ?? ".";
                string[] lines = await File.ReadAllLinesAsync(input, ct).ConfigureAwait(false);

                // SFV->ordered-chains resolution now lives in the shared SfvVolumeResolver (single
                // source of truth with the folder-mode subtitle path — codex Task 9 fix-3 G3/G4).
                // Fold each resolved chain's volumes into this cross-input accumulator: re-grouping
                // already-grouped volumes through AddVolumeToChain is idempotent (same first-seen
                // key order; the final per-chain sort below re-sorts), so ResolveVolumesAsync's
                // exact byte output is unchanged — proven by FullPipelineGoldenTests.
                foreach (IReadOnlyList<string> chain in SfvVolumeResolver.ResolveOrderedChains(sfvDir, lines))
                {
                    foreach (string volumePath in chain)
                    {
                        AddVolumeToChain(chains, chainOrder, volumePath);
                    }
                }

                continue;
            }

            // Clause 2: a first RAR volume is always named .rar — plain old-style .rar, or new-
            // style .partN.rar (which itself ends in ".rar"). A lone .rNN/.NNN continuation can
            // never be a first volume, even when no sibling .rar exists on disk to disprove it
            // via the chain-walk below (pyrescene get_start_rar_files parity).
            if (!string.Equals(Path.GetExtension(input), ".rar", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"'{Path.GetFileName(input)}' is not a first RAR volume.");
            }

            List<string> chainFiles = FileOperations.GetAllVolumeFiles(input);
            if (chainFiles.Count == 0)
            {
                throw new FileNotFoundException($"RAR volume not found: {input}", input);
            }

            chainFiles.Sort(RARVolumeNameComparer.Instance);
            if (!string.Equals(Path.GetFullPath(chainFiles[0]), Path.GetFullPath(input), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"'{Path.GetFileName(input)}' is not a first RAR volume.");
            }

            foreach (string volumePath in chainFiles)
            {
                AddVolumeToChain(chains, chainOrder, volumePath);
            }
        }

        var result = new List<(string Name, string Path)>();
        foreach (string key in chainOrder)
        {
            List<string> chainVolumes = chains[key];
            chainVolumes.Sort(RARVolumeNameComparer.Instance);
            foreach (string volumePath in chainVolumes)
            {
                string name = storeRelativePaths
                    ? SrrNameCanonicalizer.CanonicalizeRelative(rootFinal!, volumePath)
                    : SrrNameCanonicalizer.CanonicalizeLogicalName(Path.GetFileName(volumePath));
                result.Add((name, volumePath));
            }
        }

        return result;
    }

    private static void AddVolumeToChain(Dictionary<string, List<string>> chains, List<string> chainOrder, string volumePath)
    {
        string key = RARVolumeIdentifier.GetArchiveSetKey(volumePath);
        if (!chains.TryGetValue(key, out List<string>? chainVolumes))
        {
            chainVolumes = [];
            chains[key] = chainVolumes;
            chainOrder.Add(key);
        }

        chainVolumes.Add(volumePath);
    }

    private static bool IsSfvPath(string path) =>
        string.Equals(Path.GetExtension(path), ".sfv", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the deduplicated, collision-checked stored-file list (clause 4):
    /// <paramref name="additionalFiles"/> in caller order, then each <c>.sfv</c> input not
    /// already present as a source. A source already seen (by <see cref="SrrNameCanonicalizer.GetFinalPath"/>,
    /// ordinal-ignore-case) is silently skipped; a logical name claimed by two DISTINCT sources
    /// is an error naming both (strict — unlike <see cref="CreateAsync"/>'s legacy first-wins skip).
    /// Also returns the source/name registries so <see cref="ReconcileVolumesAgainstStoredFiles"/>
    /// can extend the SAME collision/dedup policy over volumes (spec §1a is writer-wide, not
    /// stored-files-only — codex/peer C3).
    /// </summary>
    private static (List<StoredFileEntry> Files, HashSet<string> SourcesSeen, Dictionary<string, string> NamesSeen) ResolveStoredFiles(
        IReadOnlyList<string> inputFiles,
        IReadOnlyList<StoredFileEntry>? additionalFiles,
        string? rootFinal,
        bool storeRelativePaths)
    {
        var sourcesSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var namesSeen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<StoredFileEntry>();

        void AddCandidate(string logicalName, string sourcePath)
        {
            string sourceFinal = SrrNameCanonicalizer.GetFinalPath(sourcePath);
            if (!sourcesSeen.Add(sourceFinal))
            {
                return; // Duplicate identical source -> deduplicated silently (spec §1a).
            }

            if (namesSeen.TryGetValue(logicalName, out string? existingSource))
            {
                throw new InvalidOperationException(
                    $"Logical name '{logicalName}' has distinct sources: '{existingSource}' and '{sourcePath}'.");
            }

            namesSeen[logicalName] = sourcePath;
            result.Add(new StoredFileEntry(logicalName, sourcePath));
        }

        if (additionalFiles != null)
        {
            foreach (StoredFileEntry entry in additionalFiles)
            {
                AddCandidate(SrrNameCanonicalizer.CanonicalizeLogicalName(entry.StoredName), entry.FullPath);
            }
        }

        foreach (string input in inputFiles)
        {
            if (!IsSfvPath(input))
            {
                continue;
            }

            // Flat names still route through CanonicalizeLogicalName (codex/peer C2): a POSIX
            // file name may legally contain '\', which would otherwise survive raw and become a
            // traversal-form name when the SRR is later parsed/extracted on Windows.
            string logicalName = storeRelativePaths
                ? SrrNameCanonicalizer.CanonicalizeRelative(rootFinal!, input)
                : SrrNameCanonicalizer.CanonicalizeLogicalName(Path.GetFileName(input));
            AddCandidate(logicalName, input);
        }

        return (result, sourcesSeen, namesSeen);
    }

    /// <summary>
    /// Extends <see cref="ResolveStoredFiles"/>'s collision/dedup registry over the resolved
    /// volume list, in emission order (stored files are written first, so they seed the
    /// registry; volumes are walked next) — closing the writer-wide policy gap (§1a is not
    /// stored-files-only): the SAME volume source resolved twice (e.g. two SFVs referencing it)
    /// is silently deduplicated, a volume name colliding with an already-claimed name (whether
    /// from a stored file or an earlier volume) by a DISTINCT source is a strict error.
    /// </summary>
    private static List<(string Name, string Path)> ReconcileVolumesAgainstStoredFiles(
        IEnumerable<(string Name, string Path)> volumes, HashSet<string> sourcesSeen, Dictionary<string, string> namesSeen)
    {
        var result = new List<(string Name, string Path)>();
        foreach ((string Name, string Path) volume in volumes)
        {
            string sourceFinal = SrrNameCanonicalizer.GetFinalPath(volume.Path);
            if (!sourcesSeen.Add(sourceFinal))
            {
                continue; // Duplicate identical source -> deduplicated silently (spec §1a).
            }

            if (namesSeen.TryGetValue(volume.Name, out string? existingSource))
            {
                throw new InvalidOperationException(
                    $"Logical name '{volume.Name}' has distinct sources: '{existingSource}' and '{volume.Path}'.");
            }

            namesSeen[volume.Name] = volume.Path;
            result.Add(volume);
        }

        return result;
    }

    #region SRR Block Writers

    private static void WriteSRRHeader(BinaryWriter writer, string? appName)
    {
        ushort flags = appName != null ? (ushort)SRRHeaderFlags.AppNamePresent : (ushort)SRRHeaderFlags.None;

        int headerSize = SRRBlockLayout.BaseHeaderSize; // base header
        byte[]? appNameBytes = null;
        if (appName != null)
        {
            appNameBytes = Encoding.UTF8.GetBytes(appName);
            headerSize += 2 + appNameBytes.Length;
        }

        writer.Write(SRRBlockLayout.HeaderSentinel);       // CRC (SRR header sentinel)
        writer.Write((byte)SRRBlockType.Header);           // SRR Header type
        writer.Write(flags);
        writer.Write((ushort)headerSize);

        if (appNameBytes != null)
        {
            writer.Write((ushort)appNameBytes.Length);
            writer.Write(appNameBytes);
        }
    }

    private static void WriteRARFileBlock(BinaryWriter writer, string rarFileName)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(rarFileName);
        ushort headerSize = (ushort)(SRRBlockLayout.BaseHeaderSize + SRRBlockLayout.NameLengthFieldLength + nameBytes.Length); // base + nameLen + name

        writer.Write(SRRBlockLayout.RARFileSentinel);      // CRC (SRR RAR file sentinel)
        writer.Write((byte)SRRBlockType.RARFile);          // RARFile type
        // pyReScene parity (see SRRBlockFlags.RecoveryBlocksRemoved doc): set unconditionally.
        writer.Write((ushort)SRRBlockFlags.RecoveryBlocksRemoved); // flags
        writer.Write(headerSize);
        writer.Write((ushort)nameBytes.Length);
        writer.Write(nameBytes);
    }

    private static void WriteOSOHashBlock(BinaryWriter writer, string fileName, ulong fileSize, byte[] osoHash)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(fileName);
        // pyrescene field order: fileSize, hash, nameLen, name
        ushort headerSize = (ushort)(SRRBlockLayout.BaseHeaderSize + SRRBlockLayout.OSOFileSizeLength + SRRBlockLayout.OSOHashLength + SRRBlockLayout.NameLengthFieldLength + nameBytes.Length);

        writer.Write(SRRBlockLayout.OSOSentinel);          // CRC (SRR OSO hash sentinel)
        writer.Write((byte)SRRBlockType.OSOHash);          // OSOHash type
        writer.Write((ushort)SRRBlockFlags.None);          // flags
        writer.Write(headerSize);
        writer.Write(fileSize);                 // file size (8 bytes)
        writer.Write(osoHash);                  // OSO hash (8 bytes)
        writer.Write((ushort)nameBytes.Length);  // name length
        writer.Write(nameBytes);                // file name
    }

    #endregion

    #region RAR Volume Processing

    /// <summary>
    /// Writes the RARFile reference block + copied headers for each volume, in order. Extracted
    /// from <see cref="CreateAsync"/>'s volume loop so <see cref="CreateFromInputsAsync"/> can
    /// reuse it over a chain-grouped, possibly-renamed volume list. No step here is actually
    /// asynchronous (<see cref="ProcessRARVolume"/> is synchronous I/O) — returning
    /// <see cref="Task.CompletedTask"/> rather than using the <c>async</c> keyword avoids a
    /// "lacks await operators" warning while keeping the awaitable signature both callers share.
    /// </summary>
    private Task WriteVolumesAsync(
        BinaryWriter writer,
        IReadOnlyList<(string Name, string Path)> volumes,
        SRRCreationOptions options,
        SRRCreationResult result,
        CancellationToken ct)
    {
        int totalVolumes = volumes.Count;
        for (int i = 0; i < totalVolumes; i++)
        {
            ct.ThrowIfCancellationRequested();

            (string volumeName, string volumePath) = volumes[i];
            ReportProgress(i + 1, totalVolumes, $"Processing {volumeName}...");

            ProcessRARVolume(writer, volumePath, volumeName, options, result, ct);
            result.VolumeCount++;
        }

        return Task.CompletedTask;
    }

    private static void ProcessRARVolume(
        BinaryWriter writer,
        string volumePath,
        string volumeName,
        SRRCreationOptions options,
        SRRCreationResult result,
        CancellationToken ct)
    {
        // Write the SRR RAR file reference block
        WriteRARFileBlock(writer, volumeName);

        // Open the RAR volume and extract headers
        using var fs = new FileStream(volumePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

        // Detect RAR version by checking marker
        bool isRAR5 = RARUtils.IsRAR5Marker(fs);

        if (isRAR5)
        {
            ProcessRAR5Volume(writer, fs, reader, volumeName, result, ct);
        }
        else
        {
            ProcessRAR4Volume(writer, fs, reader, volumeName, options, result, ct);
        }
    }


    private static void ProcessRAR4Volume(
        BinaryWriter srrWriter,
        FileStream fs,
        BinaryReader reader,
        string volumeName,
        SRRCreationOptions options,
        SRRCreationResult result,
        CancellationToken ct)
    {
        // Read and copy RAR4 marker block (7 bytes)
        if (fs.Length < RARUtils.RAR4Marker.Length)
        {
            result.Warnings.Add($"{volumeName}: File too small to contain RAR marker.");
            return;
        }

        byte[] marker = reader.ReadBytes(RARUtils.RAR4Marker.Length);
        if (!marker.AsSpan().SequenceEqual(RAR4Marker))
        {
            result.Warnings.Add($"{volumeName}: Invalid RAR4 marker.");
            return;
        }

        // Copy marker verbatim to SRR
        srrWriter.Write(marker);

        // Process remaining blocks by reading raw bytes directly
        while (fs.Position < fs.Length)
        {
            ct.ThrowIfCancellationRequested();

            if (fs.Position + RAR4HeaderLayout.BaseHeaderSize > fs.Length)
            {
                break;
            }

            long blockStart = fs.Position;

            // Read base header (7 bytes) to determine block type and size
            _ = reader.ReadUInt16(); // CRC (not needed, consumed to advance past header)
            byte typeRaw = reader.ReadByte();
            ushort flags = reader.ReadUInt16();
            ushort headerSize = reader.ReadUInt16();

            if (headerSize < RAR4HeaderLayout.BaseHeaderSize || blockStart + headerSize > fs.Length)
            {
                break;
            }

            var blockType = (RAR4BlockType)typeRaw;

            // Determine if this block has ADD_SIZE (packed data size)
            bool hasAddSize = (flags & (ushort)RARFileFlags.LongBlock) != 0 ||
                              blockType == RAR4BlockType.FileHeader ||
                              blockType == RAR4BlockType.Service;

            uint addSize = 0;
            if (hasAddSize)
            {
                // ADD_SIZE is at offset 7 in the header, already part of headerSize bytes
                // But we need to read it to know how much data to skip
                // Seek to offset 7 in the header to read ADD_SIZE
                fs.Position = blockStart + RAR4HeaderLayout.AddSize;
                addSize = reader.ReadUInt32();
            }

            // Read the full raw header bytes for verbatim copy
            fs.Position = blockStart;
            byte[] headerBytes = reader.ReadBytes(headerSize);

            // For file/service blocks with the LARGE flag, the true packed data size is 64-bit:
            // (HIGH_PACK_SIZE << 32) | ADD_SIZE. HIGH_PACK_SIZE sits at header offset 32 (after
            // ATTR), matching RARHeaderReader. Reading only the 32-bit ADD_SIZE under-skips a
            // >= 4 GiB packed entry, dropping every subsequent header (silently) or copying garbage.
            long fileDataSize = addSize;
            if ((blockType == RAR4BlockType.FileHeader || blockType == RAR4BlockType.Service) &&
                (flags & (ushort)RARFileFlags.Large) != 0 && headerSize >= RAR4HeaderLayout.HighPackSizeOffset + RAR4HeaderLayout.AddSizeFieldLength)
            {
                uint highPackSize = BitConverter.ToUInt32(headerBytes, RAR4HeaderLayout.HighPackSizeOffset);
                fileDataSize = addSize | ((long)highPackSize << 32);
            }

            // Now position is at blockStart + headerSize (start of data area)
            switch (blockType)
            {
                case RAR4BlockType.ArchiveHeader:
                    srrWriter.Write(headerBytes);
                    break;

                case RAR4BlockType.FileHeader:
                    // Check compression if needed
                    if (!options.AllowCompressed)
                    {
                        WarnIfRAR4Compressed(headerBytes, headerSize, volumeName, result);
                    }

                    srrWriter.Write(headerBytes);
                    // Skip packed file data in source (full 64-bit size for LARGE entries)
                    fs.Seek(fileDataSize, SeekOrigin.Current);
                    break;

                case RAR4BlockType.Service:
                    srrWriter.Write(headerBytes);
                    if (fileDataSize > 0)
                    {
                        if (IsRAR4CmtServiceBlock(headerBytes, headerSize))
                        {
                            // Copy CMT data verbatim (comments are never LARGE, so addSize suffices)
                            StreamUtilities.CopyBytes(fs, srrWriter.BaseStream, addSize);
                        }
                        else
                        {
                            // Skip data for other service blocks (RR, AV, etc.) — full 64-bit size
                            fs.Seek(fileDataSize, SeekOrigin.Current);
                        }
                    }

                    break;

                case RAR4BlockType.EndArchive:
                    srrWriter.Write(headerBytes);
                    break;

                case RAR4BlockType.Marker:
                    srrWriter.Write(headerBytes);
                    break;

                default:
                    // Old blocks (0x75-0x79): copy header only, skip any data
                    srrWriter.Write(headerBytes);
                    if (hasAddSize && addSize > 0)
                    {
                        fs.Seek(addSize, SeekOrigin.Current);
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Adds a warning when a RAR4 file-header block uses a compression method other than Store.
    /// </summary>
    private static void WarnIfRAR4Compressed(
        byte[] headerBytes, ushort headerSize, string volumeName, SRRCreationResult result)
    {
        if (headerSize < RAR4HeaderLayout.Method + 1)
        {
            return;
        }

        byte method = headerBytes[RAR4HeaderLayout.Method]; // METHOD field at offset 25
        if (method == RAR4HeaderLayout.AsciiDigitZero) // 0x30 = Store
        {
            return;
        }

        // Parse filename for the warning message
        int nameSize = BitConverter.ToUInt16(headerBytes, RAR4HeaderLayout.NameSize);
        string fName = nameSize > 0 && RAR4HeaderLayout.FixedFieldsEnd + nameSize <= headerBytes.Length
            ? Encoding.ASCII.GetString(headerBytes, RAR4HeaderLayout.FixedFieldsEnd, nameSize)
            : "unknown";
        result.Warnings.Add($"{volumeName}: Compressed file detected ({fName}).");
    }

    /// <summary>
    /// Determines whether a RAR4 service-block header is a CMT (comment) block, whose data
    /// must be preserved verbatim in the SRR.
    /// </summary>
    private static bool IsRAR4CmtServiceBlock(byte[] headerBytes, ushort headerSize)
    {
        const int CmtSubTypeLength = 3;

        // Determine sub-type from header: name is at offset 32, name_size at offset 26
        if (headerSize < RAR4HeaderLayout.FixedFieldsEnd + CmtSubTypeLength) // not enough to read 3-byte name
        {
            return false;
        }

        int nameSize = BitConverter.ToUInt16(headerBytes, RAR4HeaderLayout.NameSize);
        if (nameSize != CmtSubTypeLength || RAR4HeaderLayout.FixedFieldsEnd + CmtSubTypeLength > headerBytes.Length)
        {
            return false;
        }

        string subType = Encoding.ASCII.GetString(headerBytes, RAR4HeaderLayout.FixedFieldsEnd, CmtSubTypeLength);
        return string.Equals(subType, "CMT", StringComparison.OrdinalIgnoreCase);
    }

    private static void ProcessRAR5Volume(
        BinaryWriter srrWriter,
        FileStream fs,
        BinaryReader reader,
        string volumeName,
        SRRCreationResult result,
        CancellationToken ct)
    {
        // Read and copy RAR5 marker (8 bytes)
        if (fs.Length < RARUtils.RAR5Marker.Length)
        {
            result.Warnings.Add($"{volumeName}: File too small to contain RAR5 marker.");
            return;
        }

        byte[] marker = reader.ReadBytes(RARUtils.RAR5Marker.Length);
        if (!marker.AsSpan().SequenceEqual(RAR5Marker))
        {
            result.Warnings.Add($"{volumeName}: Invalid RAR5 marker.");
            return;
        }

        // Copy marker verbatim
        srrWriter.Write(marker);

        // Process RAR5 blocks
        var rarReader = new RAR5HeaderReader(fs);
        while (fs.Position < fs.Length)
        {
            ct.ThrowIfCancellationRequested();

            // Read the block start position
            long blockStart = fs.Position;

            RAR5BlockReadResult? block = rarReader.ReadBlock();
            if (block == null)
            {
                break;
            }

            // Calculate actual header bytes on disk:
            // CRC32 (4 bytes) + header size vint + header content
            long headerEndPos = block.BlockPosition + (long)block.HeaderSize;

            // Read the full raw header bytes (CRC + vint + header content)
            long rawHeaderSize = headerEndPos - blockStart;
            if (rawHeaderSize is <= 0 or > int.MaxValue)
            {
                break;
            }

            fs.Position = blockStart;
            byte[] rawHeaderBytes = reader.ReadBytes((int)rawHeaderSize);

            switch (block.BlockType)
            {
                case RAR5BlockType.Main:
                    // Copy archive header verbatim
                    srrWriter.Write(rawHeaderBytes);
                    break;

                case RAR5BlockType.File:
                    // Copy header only, skip packed data
                    srrWriter.Write(rawHeaderBytes);
                    if (block.DataSize > 0)
                    {
                        StreamUtilities.SkipBytes(fs, block.DataSize);
                    }

                    break;

                case RAR5BlockType.Service:
                    srrWriter.Write(rawHeaderBytes);
                    if (block.ServiceBlockInfo != null &&
                        string.Equals(block.ServiceBlockInfo.SubType, "CMT", StringComparison.OrdinalIgnoreCase))
                    {
                        // Copy CMT data verbatim
                        if (block.DataSize > 0)
                        {
                            StreamUtilities.CopyBytes(fs, srrWriter.BaseStream, block.DataSize);
                        }
                    }
                    else
                    {
                        // Skip data for other service blocks
                        if (block.DataSize > 0)
                        {
                            StreamUtilities.SkipBytes(fs, block.DataSize);
                        }
                    }

                    break;

                case RAR5BlockType.EndArchive:
                    // Copy end archive verbatim
                    srrWriter.Write(rawHeaderBytes);
                    break;

                default:
                    // Copy header, skip data
                    srrWriter.Write(rawHeaderBytes);
                    if (block.DataSize > 0)
                    {
                        StreamUtilities.SkipBytes(fs, block.DataSize);
                    }

                    break;
            }
        }
    }

    #endregion


    #region Helpers

    private int _lastProgressPercent;
    private int _lastCurrent;
    private int _lastTotal;

    private void ReportProgress(int current, int total, string message)
    {
        _lastCurrent = current;
        _lastTotal = total;
        _lastProgressPercent = total > 0 ? (int)(current * 100.0 / total) : 0;
        Progress?.Invoke(this, new SRRCreationProgressEventArgs
        {
            ProgressPercent = _lastProgressPercent,
            CurrentVolume = current,
            TotalVolumes = total,
            Message = message
        });
    }

    /// <summary>
    /// Emits a progress event carrying only a log message; the percentage and volume
    /// counters reuse the last-reported values so the progress bar doesn't flicker.
    /// </summary>
    private void Log(string message)
    {
        Progress?.Invoke(this, new SRRCreationProgressEventArgs
        {
            ProgressPercent = _lastProgressPercent,
            CurrentVolume = _lastCurrent,
            TotalVolumes = _lastTotal,
            Message = message
        });
    }

    #endregion
}
