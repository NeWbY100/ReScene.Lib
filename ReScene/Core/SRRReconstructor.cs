using System.Text;
using ReScene.Core.Cryptography;
using ReScene.Core.IO;
using ReScene.RAR;
using ReScene.SRR;

namespace ReScene.Core;

/// <summary>
/// Reconstructs RAR archive files directly from an SRR binary stream and source files.
/// Used when the SRR indicates a custom packer (not WinRAR) created the original RARs,
/// making brute-force reconstruction impossible. Also exposes <see cref="PreflightSet"/> to check
/// an SRR's assemblability up front, without writing anything.
/// </summary>
internal class SRRReconstructor(IReSceneLogger? logger = null)
{
    /// <summary>
    /// Occurs when reconstruction progress updates.
    /// </summary>
    public event EventHandler<BruteForceProgressEventArgs>? Progress;

    private readonly IReSceneLogger _logger = logger ?? NullReSceneLogger.Instance;

    /// <summary>
    /// Reconstructs RAR files from an SRR file by replaying original headers and splicing in packed
    /// file data supplied by <paramref name="packedSource"/>.
    /// </summary>
    /// <returns>
    /// A typed result: <see cref="SRRReconstructionStatus.Success"/> when every expected release
    /// volume (by count and normalized name — see <see cref="VolumeIdentityMatcher"/>) was written
    /// and hash-verified; otherwise a failure status with a diagnostic. <see
    /// cref="SRRReconstructionResult.WrittenPaths"/> holds the absolute paths actually written
    /// regardless of overall success, so a partial/failed run's output can still be inspected or
    /// cleaned up by the caller.
    /// </returns>
    /// <remarks>
    /// This walk and <see cref="PreflightSet"/>'s must stay seek-rule-identical; change one,
    /// change both (SRRPreflightTests pins the pairs).
    /// </remarks>
    public async Task<SRRReconstructionResult> ReconstructAsync(
        string srrFilePath,
        IPackedSource packedSource,
        string releaseDirectoryForProgress,
        string outputDirectory,
        IReadOnlyList<string> originalRARFileNames,
        HashSet<string> hashes,
        HashType hashType,
        CancellationToken cancellationToken)
    {
        SRRReconstructionResult preflight = PreflightSet(srrFilePath, originalRARFileNames);
        if (preflight.Status != SRRReconstructionStatus.Success)
        {
            return preflight;
        }

        _logger.Information(this, $"=== Direct SRR Reconstruction ===", LogTarget.System);
        _logger.Information(this, $"SRR: {srrFilePath}", LogTarget.System);
        _logger.Information(this, $"Input: {releaseDirectoryForProgress}", LogTarget.System);
        _logger.Information(this, $"Output: {outputDirectory}", LogTarget.System);
        _logger.Information(this, $"Expected volumes: {originalRARFileNames.Count}", LogTarget.System);

        Directory.CreateDirectory(outputDirectory);

        DateTime startTime = DateTime.Now;
        int totalVolumes = originalRARFileNames.Count;
        int completedVolumes = 0;
        bool allMatched = true;
        List<string> writtenPaths = [];
        List<string> writtenRARFileNames = [];

        // Track open source file streams for multi-volume spanning
        Stream? currentSourceStream = null;
        string? currentSourceFileName = null;

        FileStream? outputStream = null;
        string? currentOutputPath = null;
        string? currentRARFileName = null;

        try
        {
            using FileStream srrStream = new(srrFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using BinaryReader reader = new(srrStream);

            while (srrStream.Position < srrStream.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (srrStream.Position + 7 > srrStream.Length)
                {
                    break;
                }

                long blockStartPos = srrStream.Position;
                ushort crc = reader.ReadUInt16();
                byte blockType = reader.ReadByte();
                ushort flags = reader.ReadUInt16();
                ushort headerSize = reader.ReadUInt16();

                if (headerSize < 7)
                {
                    break;
                }

                // Determine ADD_SIZE for blocks with LONG_BLOCK flag or stored file blocks.
                // LONG_BLOCK is the shared 0x8000 base-header bit (SRRBlockFlags.LongBlock mirrors
                // RARFileFlags.LongBlock); this flag word is read before the SRR-vs-RAR block
                // discrimination below, so the same value gates both the SRR and embedded-RAR paths.
                uint addSize = 0;
                bool hasLongBlock = (flags & (ushort)SRRBlockFlags.LongBlock) != 0;

                if (IsSRRBlockType(blockType))
                {
                    // SRR blocks
                    if (hasLongBlock || blockType == (byte)SRRBlockType.StoredFile)
                    {
                        if (srrStream.Position + 4 > srrStream.Length)
                        {
                            break;
                        }

                        addSize = reader.ReadUInt32();
                    }

                    if (blockType == (byte)SRRBlockType.RARFile)
                    {
                        // Close previous volume and verify
                        if (outputStream != null && currentOutputPath != null && currentRARFileName != null)
                        {
                            outputStream.Dispose();
                            outputStream = null;

                            await VerifyAndReportVolumeAsync(currentOutputPath, currentRARFileName, hashes, hashType, ref allMatched).ConfigureAwait(false);
                            completedVolumes++;
                            writtenPaths.Add(currentOutputPath);
                            writtenRARFileNames.Add(currentRARFileName);
                            FireProgress(releaseDirectoryForProgress, currentRARFileName, totalVolumes, completedVolumes, startTime);
                        }

                        // Read the RAR filename from the SRRRARFile block
                        if (srrStream.Position + 2 > srrStream.Length)
                        {
                            break;
                        }

                        ushort nameLen = reader.ReadUInt16();
                        if (srrStream.Position + nameLen > srrStream.Length)
                        {
                            break;
                        }

                        if (7 + 2 + nameLen > headerSize)
                        {
                            throw new InvalidDataException(
                                $"SRR RAR-file block at offset {blockStartPos} has a name that overflows its declared header size.");
                        }

                        byte[] nameBytes = reader.ReadBytes(nameLen);
                        currentRARFileName = Encoding.UTF8.GetString(nameBytes);

                        // Guard against path traversal (Zip-Slip): a malicious SRR could name a
                        // volume "..\..\x" or an absolute path, which Path.Combine would resolve
                        // outside outputDirectory (arbitrary file write). Resolve the name through
                        // the containment check and reject anything that escapes the output dir.
                        if (!FileOperations.TryResolveRelativePath(outputDirectory, currentRARFileName,
                                out string safeRARRelativePath))
                        {
                            throw new InvalidDataException(
                                $"SRR contains an unsafe RAR file path that escapes the output directory: '{currentRARFileName}'");
                        }

                        // Open new output file
                        currentOutputPath = Path.Combine(outputDirectory, safeRARRelativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(currentOutputPath)!);
                        outputStream = new FileStream(currentOutputPath, FileMode.Create, FileAccess.Write, FileShare.None);

                        _logger.Information(this, $"Reconstructing: {currentRARFileName}", LogTarget.System);

                        // Absolute seek — the same formula every other SRR block type uses —
                        // rather than trusting that reading the name landed the stream exactly at
                        // the header's declared end. See PreflightSet's identical RARFile handling.
                        if (blockStartPos + headerSize + addSize > srrStream.Length)
                        {
                            throw new InvalidDataException(
                                $"SRR RAR-file block at offset {blockStartPos} extends past the end of the file.");
                        }

                        srrStream.Seek(blockStartPos + headerSize + addSize, SeekOrigin.Begin);
                    }
                    else if (blockType == (byte)SRRBlockType.RARPadding)
                    {
                        // Read the padding block's RAR filename
                        long paddingHeaderEnd = blockStartPos + headerSize;
                        if (srrStream.Position + 2 <= paddingHeaderEnd)
                        {
                            ushort paddingNameLen = reader.ReadUInt16();
                            if (srrStream.Position + paddingNameLen <= paddingHeaderEnd)
                            {
                                srrStream.Seek(paddingNameLen, SeekOrigin.Current); // Skip filename
                            }
                        }

                        // Write padding bytes to output
                        if (outputStream != null && addSize > 0)
                        {
                            byte[] padding = new byte[addSize];
                            outputStream.Write(padding, 0, padding.Length);
                            _logger.Debug(this, $"Wrote {addSize} bytes of padding");
                        }

                        // Skip any remaining add data in SRR
                        srrStream.Seek(blockStartPos + headerSize + addSize, SeekOrigin.Begin);
                    }
                    else
                    {
                        // Skip other SRR blocks (header, stored file, oso hash)
                        srrStream.Seek(blockStartPos + headerSize + addSize, SeekOrigin.Begin);
                    }
                }
                else if (outputStream != null)
                {
                    // RAR block — write to output
                    srrStream.Seek(blockStartPos, SeekOrigin.Begin);

                    byte[] fullHeader = reader.ReadBytes(headerSize);

                    // Calculate ADD_SIZE from the header bytes (if LONG_BLOCK flag set)
                    uint rarAddSize = 0;
                    if (headerSize >= 11 && (flags & (ushort)RARFileFlags.LongBlock) != 0)
                    {
                        rarAddSize = BitConverter.ToUInt32(fullHeader, 7);
                    }

                    switch (blockType)
                    {
                        case (byte)RAR4BlockType.Marker:
                            outputStream.Write(fullHeader, 0, fullHeader.Length);
                            break;

                        case (byte)RAR4BlockType.ArchiveHeader:
                            outputStream.Write(fullHeader, 0, fullHeader.Length);
                            break;

                        case (byte)RAR4BlockType.FileHeader:
                            outputStream.Write(fullHeader, 0, fullHeader.Length);

                            long packedSize = rarAddSize;

                            // Check for LARGE flag for 64-bit sizes
                            if (((RARFileFlags)flags).HasFlag(RARFileFlags.Large) && headerSize >= RAR4HeaderLayout.HighPackSizeOffset + RAR4HeaderLayout.AddSizeFieldLength)
                            {
                                uint highPackSize = BitConverter.ToUInt32(fullHeader, RAR4HeaderLayout.HighPackSizeOffset);
                                packedSize |= (long)highPackSize << 32;
                            }

                            // Extract filename from file header
                            string? archivedFileName = DecodeEmbeddedName(fullHeader, flags, headerSize);
                            archivedFileName = archivedFileName?.Replace('\\', Path.DirectorySeparatorChar);

                            bool isSplitBefore = (flags & (ushort)RARFileFlags.SplitBefore) != 0;
                            bool isSplitAfter = (flags & (ushort)RARFileFlags.SplitAfter) != 0;

                            // RAR4 directory entries set all LHD_WINDOWMASK bits (0x00E0) and carry no
                            // packed data. FindSourceFile would throw FileNotFoundException for the
                            // directory name (File.Exists is false for a directory), aborting the whole
                            // reconstruction — so never open a source stream for them (the header bytes
                            // are still written above; packedSize is 0, so there is nothing to copy).
                            bool isDirectory = ((RARFileFlags)flags).HasFlag(RARFileFlags.Directory);

                            // A data-bearing, non-directory, non-continuation entry with no decodable
                            // name has nowhere to source its packed bytes from. Letting archivedFileName
                            // == null alone skip the open+copy below (as it must for directories/
                            // continuations) would silently produce a header-only/truncated volume that
                            // still reports Success when no hashes are supplied — surface it as a typed
                            // failure instead of a silent skip.
                            if (!isSplitBefore && !isDirectory && archivedFileName == null && packedSize > 0)
                            {
                                throw new InvalidDataException(
                                    $"RAR file header at SRR offset {blockStartPos} has packed data ({packedSize} bytes) but no decodable archived file name.");
                            }

                            try
                            {
                                if (!isSplitBefore && archivedFileName != null && !isDirectory)
                                {
                                    if (currentSourceStream != null && currentSourceFileName != archivedFileName)
                                    {
                                        currentSourceStream.Dispose();
                                        currentSourceStream = null;
                                    }

                                    if (currentSourceStream == null)
                                    {
                                        currentSourceStream = packedSource.OpenPackedStream(archivedFileName);
                                        currentSourceFileName = archivedFileName;
                                        _logger.Debug(this, $"Opened source file: {archivedFileName}");
                                    }
                                }

                                if (currentSourceStream != null && packedSize > 0)
                                {
                                    await CopyBytesAsync(currentSourceStream, outputStream, packedSize, cancellationToken).ConfigureAwait(false);
                                }
                            }
                            catch (ArgumentException ex)
                            {
                                // Scoped narrowly to the packed-source interaction: RARStream (a future
                                // producer-backed IPackedSource) throws ArgumentException when the
                                // snapshot it opens has no visible target header or does not start at
                                // volume 1 (spec §4). Catching ArgumentException at the method level
                                // instead would also swallow HashCalculator's ArgumentOutOfRangeException
                                // for an invalid HashType below — a programmer error that must keep
                                // propagating, not become an ordinary Error.
                                return SRRReconstructionResult.Fail(SRRReconstructionStatus.Error, ex.Message, writtenPaths);
                            }

                            if (!isSplitAfter && currentSourceStream != null)
                            {
                                currentSourceStream.Dispose();
                                currentSourceStream = null;
                                currentSourceFileName = null;
                            }

                            break;

                        case (byte)RAR4BlockType.Service:
                            outputStream.Write(fullHeader, 0, fullHeader.Length);
                            if (rarAddSize > 0)
                            {
                                // PreflightSet declines any non-CMT service block with a declared
                                // payload before reconstruction starts, so CMT (whose payload IS
                                // stored) is the only name this branch should ever see with
                                // rarAddSize > 0. Reaching here with any other name means the two
                                // SRR walks have diverged — log loudly rather than silently
                                // emitting whatever bytes follow.
                                string? serviceName = DecodeEmbeddedName(fullHeader, flags, headerSize);
                                if (!string.Equals(serviceName, "CMT", StringComparison.Ordinal))
                                {
                                    _logger.Error(this,
                                        $"Reconstruction reached service block '{serviceName}' with a declared payload — PreflightSet should have declined this SRR (the two walks have diverged).",
                                        LogTarget.System);
                                }

                                byte[] serviceData = reader.ReadBytes((int)rarAddSize);
                                outputStream.Write(serviceData, 0, serviceData.Length);
                            }

                            break;

                        case (byte)RAR4BlockType.EndArchive:
                            outputStream.Write(fullHeader, 0, fullHeader.Length);
                            if (rarAddSize > 0)
                            {
                                // EndArchive has no ADD_SIZE field (see RAR4HeaderLayout) —
                                // PreflightSet declines this malformed shape before reconstruction
                                // starts, so this should be unreachable. Error rather than
                                // silently consuming whatever bytes follow as "end data".
                                throw new InvalidDataException(
                                    $"RAR EndArchive block at SRR offset {blockStartPos} declares an ADD_SIZE ({rarAddSize} bytes), but EndArchive has no ADD_SIZE field.");
                            }

                            break;

                        default:
                            outputStream.Write(fullHeader, 0, fullHeader.Length);
                            if (rarAddSize > 0)
                            {
                                // PreflightSet declines any other old-style data-bearing block
                                // before reconstruction starts, so this branch should be
                                // unreachable. Log loudly rather than silently emitting whatever
                                // bytes follow (the two SRR walks have diverged).
                                _logger.Error(this,
                                    $"Reconstruction reached block type 0x{blockType:X2} with a declared payload — PreflightSet should have declined this SRR (the two walks have diverged).",
                                    LogTarget.System);
                                byte[] unknownData = reader.ReadBytes((int)rarAddSize);
                                outputStream.Write(unknownData, 0, unknownData.Length);
                            }

                            break;
                    }
                }
                else
                {
                    // No output stream open yet and not an SRR block — skip
                    long skipTo = blockStartPos + headerSize;
                    if (hasLongBlock && headerSize >= 11)
                    {
                        srrStream.Seek(blockStartPos + 7, SeekOrigin.Begin);
                        uint skipAddSize = reader.ReadUInt32();
                        skipTo = blockStartPos + headerSize + skipAddSize;
                    }

                    srrStream.Seek(skipTo, SeekOrigin.Begin);
                }
            }

            // Close and verify the last volume
            if (outputStream != null && currentOutputPath != null && currentRARFileName != null)
            {
                outputStream.Dispose();
                outputStream = null;

                await VerifyAndReportVolumeAsync(currentOutputPath, currentRARFileName, hashes, hashType, ref allMatched).ConfigureAwait(false);
                completedVolumes++;
                writtenPaths.Add(currentOutputPath);
                writtenRARFileNames.Add(currentRARFileName);
                FireProgress(releaseDirectoryForProgress, currentRARFileName, totalVolumes, completedVolumes, startTime);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (EndOfStreamException ex)
        {
            return SRRReconstructionResult.Fail(SRRReconstructionStatus.SourceExhausted, ex.Message, writtenPaths);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException
            or FileNotFoundException or UnauthorizedAccessException)
        {
            return SRRReconstructionResult.Fail(SRRReconstructionStatus.Error, ex.Message, writtenPaths);
        }
        finally
        {
            currentSourceStream?.Dispose();
            outputStream?.Dispose();
        }

        TimeSpan elapsed = DateTime.Now - startTime;

        // Completeness requires the FULL expected release volume set — by count and normalized
        // name, not merely "at least one volume was produced and hash-verified". completedVolumes
        // == 0 forces this false even when originalRARFileNames is itself empty (nothing was ever
        // expected AND nothing was produced is not a success).
        bool identityComplete = completedVolumes > 0 && VolumeIdentityMatcher.Matches(originalRARFileNames, writtenRARFileNames);
        bool success = allMatched && identityComplete;

        if (success)
        {
            _logger.Information(this, $"=== Reconstruction SUCCESS: {completedVolumes} volume(s) in {elapsed.TotalSeconds:F1}s ===", LogTarget.System);
            return SRRReconstructionResult.Ok(writtenPaths);
        }

        if (completedVolumes == 0)
        {
            string message = "=== Reconstruction FAILED: no volumes produced ===";
            _logger.Warning(this, message, LogTarget.System);
            return SRRReconstructionResult.Fail(SRRReconstructionStatus.Error, message, writtenPaths);
        }

        if (!identityComplete)
        {
            string message = $"=== Reconstruction FAILED: incomplete volume set ({completedVolumes} of {totalVolumes} expected) ===";
            _logger.Warning(this, message, LogTarget.System);
            return SRRReconstructionResult.Fail(SRRReconstructionStatus.Error, message, writtenPaths);
        }

        string mismatchMessage = $"=== Reconstruction completed with hash mismatches ({completedVolumes} volume(s), {elapsed.TotalSeconds:F1}s) ===";
        _logger.Warning(this, mismatchMessage, LogTarget.System);
        return SRRReconstructionResult.Fail(SRRReconstructionStatus.VerificationFailed, mismatchMessage, writtenPaths);
    }

    /// <summary>
    /// Preflights an SRR for guided assembly without writing anything: walks every SRR block and
    /// embedded RAR4 header looking for evidence that a required payload was stripped when the
    /// SRR was written — a recovery record (old-style 0x78, or a WinRAR "RR" service block) or any
    /// other embedded RAR block whose declared payload is not actually present. <see
    /// cref="ReconstructAsync"/> calls this first and returns its failure verbatim before creating
    /// any output. <paramref name="originalRARFileNames"/> is accepted so every embedded RAR-file
    /// section's name can be read here (keeping this walk's seek arithmetic identical to <see
    /// cref="ReconstructAsync"/>'s); a future task adds real set-membership filtering against it —
    /// every section is "selected" until then.
    /// </summary>
    /// <remarks>
    /// These two walks must stay seek-rule-identical; change one, change both (SRRPreflightTests
    /// pins the pairs). Unlike <see cref="ReconstructAsync"/>, nothing here ever opens an output
    /// stream, so there is no "is a section currently open" branch — every block class advances
    /// the stream explicitly.
    /// </remarks>
    internal SRRReconstructionResult PreflightSet(string srrFilePath, IReadOnlyList<string> originalRARFileNames)
    {
        _logger.Information(this, $"=== SRR Assembly Preflight ===", LogTarget.System);
        _logger.Information(this, $"SRR: {srrFilePath}", LogTarget.System);
        _logger.Information(this, $"Expected volumes: {originalRARFileNames.Count}", LogTarget.System);

        bool sawSrrHeader = false;

        try
        {
            using FileStream srrStream = new(srrFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using BinaryReader reader = new(srrStream);

            while (srrStream.Position < srrStream.Length)
            {
                if (srrStream.Position + 7 > srrStream.Length)
                {
                    return MalformedSrr("SRR is truncated: incomplete block header");
                }

                long blockStartPos = srrStream.Position;
                reader.ReadUInt16(); // CRC — not validated by preflight
                byte blockType = reader.ReadByte();
                ushort flags = reader.ReadUInt16();
                ushort headerSize = reader.ReadUInt16();

                if (headerSize < 7)
                {
                    return MalformedSrr($"SRR is malformed: block header size {headerSize} is smaller than the base header");
                }

                uint addSize = 0;
                bool hasLongBlock = (flags & (ushort)SRRBlockFlags.LongBlock) != 0;

                if (IsSRRBlockType(blockType))
                {
                    if (blockType == (byte)SRRBlockType.Header)
                    {
                        sawSrrHeader = true;
                    }

                    if (hasLongBlock || blockType == (byte)SRRBlockType.StoredFile)
                    {
                        if (srrStream.Position + 4 > srrStream.Length)
                        {
                            return MalformedSrr("SRR is truncated: incomplete ADD_SIZE field");
                        }

                        addSize = reader.ReadUInt32();
                    }

                    if (blockType == (byte)SRRBlockType.RARFile)
                    {
                        // Read (but do not yet filter on) the section name — a future task adds
                        // set-membership filtering against originalRARFileNames; every section is
                        // "selected" until then.
                        if (srrStream.Position + 2 > srrStream.Length)
                        {
                            return MalformedSrr("SRR is truncated: incomplete RAR-file name length");
                        }

                        ushort nameLen = reader.ReadUInt16();
                        if (srrStream.Position + nameLen > srrStream.Length)
                        {
                            return MalformedSrr("SRR is truncated: incomplete RAR-file name");
                        }

                        if (7 + 2 + nameLen > headerSize)
                        {
                            return MalformedSrr($"SRR is malformed: RAR-file name at offset {blockStartPos} overflows its declared header size");
                        }

                        reader.ReadBytes(nameLen);

                        // Absolute seek — the same formula every other SRR block type uses —
                        // rather than trusting that reading the name landed the stream exactly at
                        // the header's declared end. A header declaring extra bytes beyond the
                        // name (padding this codebase's own writer never emits, but a malformed or
                        // unusual SRR could), or LONG_BLOCK addSize on this block, would otherwise
                        // desync the walk.
                        if (blockStartPos + headerSize + addSize > srrStream.Length)
                        {
                            return MalformedSrr("SRR is truncated: RAR-file section extends past end of file");
                        }

                        srrStream.Seek(blockStartPos + headerSize + addSize, SeekOrigin.Begin);
                    }
                    else
                    {
                        if (blockStartPos + headerSize + addSize > srrStream.Length)
                        {
                            return MalformedSrr("SRR is truncated: block extends past end of file");
                        }

                        srrStream.Seek(blockStartPos + headerSize + addSize, SeekOrigin.Begin);
                    }
                }
                else
                {
                    if (blockStartPos + headerSize > srrStream.Length)
                    {
                        return MalformedSrr("SRR is truncated: embedded RAR header extends past end of file");
                    }

                    srrStream.Seek(blockStartPos, SeekOrigin.Begin);
                    byte[] fullHeader = reader.ReadBytes(headerSize);

                    uint rarAddSize = 0;
                    if (headerSize >= 11 && (flags & (ushort)RARFileFlags.LongBlock) != 0)
                    {
                        rarAddSize = BitConverter.ToUInt32(fullHeader, 7);
                    }

                    switch (blockType)
                    {
                        case (byte)RAR4BlockType.ArchiveHeader:
                            if (((RARArchiveFlags)flags).HasFlag(RARArchiveFlags.Protected))
                            {
                                return Decline("recovery record (protected archive)");
                            }

                            srrStream.Seek(blockStartPos + headerSize, SeekOrigin.Begin);
                            break;

                        case (byte)RAR4BlockType.Marker:
                        case (byte)RAR4BlockType.FileHeader:
                            // File packed data is EXTERNAL — never in the SRR; FileHeader's
                            // ADD_SIZE is not an SRR seek distance.
                            srrStream.Seek(blockStartPos + headerSize, SeekOrigin.Begin);
                            break;

                        case (byte)RAR4BlockType.EndArchive:
                            // EndArchive has no ADD_SIZE field (see RAR4HeaderLayout) — a
                            // LONG_BLOCK EndArchive declaring one is a malformed shape (not
                            // evidence of anything specific), so it is rejected rather than
                            // silently seeking past whatever it declares.
                            if (rarAddSize > 0)
                            {
                                return MalformedSrr($"SRR is malformed: EndArchive block at offset {blockStartPos} declares an ADD_SIZE, which EndArchive has no field for");
                            }

                            srrStream.Seek(blockStartPos + headerSize, SeekOrigin.Begin);
                            break;

                        case (byte)RAR4BlockType.Protect:
                            return Decline("old-style recovery block");

                        case (byte)RAR4BlockType.Service:
                            string? serviceName = DecodeEmbeddedName(fullHeader, flags, headerSize);
                            if (serviceName == null)
                            {
                                return MalformedSrr($"SRR is malformed: embedded Service block at offset {blockStartPos} has no decodable name");
                            }

                            // Rule 3 declines a Service named "RR" on name alone — unconditionally,
                            // like Protect above — unlike the generic rules 4/5 below, which only
                            // trigger when a payload is actually declared-but-absent.
                            if (string.Equals(serviceName, "RR", StringComparison.Ordinal))
                            {
                                return Decline($"\"{serviceName}\" recovery record service block");
                            }

                            if (string.Equals(serviceName, "CMT", StringComparison.Ordinal))
                            {
                                if (blockStartPos + headerSize + rarAddSize > srrStream.Length)
                                {
                                    return MalformedSrr("SRR is truncated: CMT service payload extends past end of file");
                                }

                                srrStream.Seek(blockStartPos + headerSize + rarAddSize, SeekOrigin.Begin);
                            }
                            else if (rarAddSize > 0)
                            {
                                return Decline($"stripped {serviceName} service data");
                            }
                            else
                            {
                                srrStream.Seek(blockStartPos + headerSize, SeekOrigin.Begin);
                            }

                            break;

                        default:
                            if (rarAddSize > 0)
                            {
                                return Decline($"stripped block 0x{blockType:X2} data");
                            }

                            srrStream.Seek(blockStartPos + headerSize, SeekOrigin.Begin);
                            break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException
            or FileNotFoundException or UnauthorizedAccessException or EndOfStreamException)
        {
            return SRRReconstructionResult.Fail(SRRReconstructionStatus.Error, ex.Message);
        }

        // Matches SRRVerifier's stance: a walk that never saw an SRR header block (0x69) —
        // including an empty file, where the loop above never executes at all — is not a valid
        // SRR, regardless of how clean the rest of the walk looked.
        if (!sawSrrHeader)
        {
            return MalformedSrr("Missing SRR header block (0x69).");
        }

        return SRRReconstructionResult.Ok([]);
    }

    private SRRReconstructionResult Decline(string reason)
    {
        _logger.Warning(this, $"SRR declined for guided assembly: {reason}", LogTarget.System);
        return SRRReconstructionResult.Fail(SRRReconstructionStatus.UnsupportedSrr, reason);
    }

    private SRRReconstructionResult MalformedSrr(string reason)
    {
        _logger.Warning(this, $"SRR preflight failed: {reason}", LogTarget.System);
        return SRRReconstructionResult.Fail(SRRReconstructionStatus.Error, reason);
    }

    private static bool IsSRRBlockType(byte type)
        => type is ((byte)SRRBlockType.Header)
        or ((byte)SRRBlockType.StoredFile)
        or ((byte)SRRBlockType.OSOHash)
        or ((byte)SRRBlockType.RARPadding)
        or ((byte)SRRBlockType.RARFile);

    /// <summary>
    /// Decodes the embedded NAME field of a RAR4 block that reuses the file-header layout (the
    /// FileHeader block itself, or any Service/CMT/RR/AV-style block sharing that layout):
    /// NAME_SIZE at <see cref="RAR4HeaderLayout.NameSize"/>, NAME at <see
    /// cref="RAR4HeaderLayout.FixedFieldsEnd"/> (pushed 8 bytes later when <see
    /// cref="RARFileFlags.Large"/> inserts HIGH_PACK_SIZE/HIGH_UNP_SIZE first), Unicode-decoded per
    /// <see cref="RARFileFlags.Unicode"/>. Returns null when the header is too short to carry a
    /// name field, the name would run past the header's own bytes, or the decoder rejects the
    /// bytes (e.g. a zero-length name) — callers treat null as "no decodable name" rather than
    /// guessing at a fallback.
    /// </summary>
    private static string? DecodeEmbeddedName(byte[] fullHeader, ushort flags, ushort headerSize)
    {
        if (headerSize < RAR4HeaderLayout.FixedFieldsEnd)
        {
            return null;
        }

        ushort nameSize = BitConverter.ToUInt16(fullHeader, RAR4HeaderLayout.NameSize);
        int nameOffset = RAR4HeaderLayout.FixedFieldsEnd;
        if (((RARFileFlags)flags).HasFlag(RARFileFlags.Large)
            && headerSize >= RAR4HeaderLayout.FixedFieldsEnd + 8 + nameSize)
        {
            nameOffset = RAR4HeaderLayout.FixedFieldsEnd + 8;
        }
        else if (headerSize < nameOffset + nameSize)
        {
            nameOffset = RAR4HeaderLayout.FixedFieldsEnd;
        }

        if (nameOffset + nameSize > fullHeader.Length)
        {
            return null;
        }

        byte[] nameBytes = new byte[nameSize];
        Array.Copy(fullHeader, nameOffset, nameBytes, 0, nameSize);
        return RARUtils.DecodeFileName(nameBytes, ((RARFileFlags)flags).HasFlag(RARFileFlags.Unicode));
    }

    internal static string FindSourceFile(string inputDirectory, string archivedFileName)
    {
        // Only trust the archived name for a direct/subdirectory lookup if it stays within
        // inputDirectory; a malicious "..\..\x" must not read files outside it and splice their
        // bytes into the output (path traversal). The by-filename fallbacks below use
        // Path.GetFileName, which strips directory components and is always contained.
        if (FileOperations.TryResolveRelativePath(inputDirectory, archivedFileName, out string safeRelative))
        {
            string directPath = Path.Combine(inputDirectory, safeRelative);
            if (File.Exists(directPath))
            {
                return directPath;
            }
        }

        string flatPath = Path.Combine(inputDirectory, Path.GetFileName(archivedFileName));
        if (File.Exists(flatPath))
        {
            return flatPath;
        }

        string searchDir = inputDirectory;
        string searchName = Path.GetFileName(archivedFileName);
        string? subDir = Path.GetDirectoryName(archivedFileName);

        if (!string.IsNullOrEmpty(subDir)
            && FileOperations.TryResolveRelativePath(inputDirectory, subDir, out string safeSubDir))
        {
            string subDirPath = Path.Combine(inputDirectory, safeSubDir);
            if (Directory.Exists(subDirPath))
            {
                searchDir = subDirPath;
            }
        }

        if (Directory.Exists(searchDir))
        {
            foreach (string file in Directory.GetFiles(searchDir, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFileName(file), searchName, StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }
        }

        throw new FileNotFoundException($"Source file not found for archived entry: {archivedFileName}", archivedFileName);
    }

    internal static async Task CopyBytesAsync(Stream source, Stream destination, long count, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[80 * 1024];
        long remaining = count;

        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = await source.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                throw new EndOfStreamException($"Unexpected end of source file with {remaining} bytes remaining.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
    }

    private Task VerifyAndReportVolumeAsync(string outputPath, string rarFileName, HashSet<string> hashes, HashType hashType, ref bool allMatched)
    {
        if (hashes.Count == 0)
        {
            _logger.Information(null, $"  {rarFileName}: written (no hash to verify)", LogTarget.System);
            return Task.CompletedTask;
        }

        string hash = HashCalculator.Calculate(hashType, outputPath);

        if (hashes.Contains(hash))
        {
            _logger.Information(null, $"  {rarFileName}: {hashType} {hash} MATCH", LogTarget.System);
        }
        else
        {
            _logger.Warning(null, $"  {rarFileName}: {hashType} {hash} NO MATCH", LogTarget.System);
            allMatched = false;
        }

        return Task.CompletedTask;
    }

    private void FireProgress(string inputDirectory, string rarFileName, int totalVolumes, int completedVolumes, DateTime startTime)
    {
        Progress?.Invoke(this, new BruteForceProgressEventArgs(
            inputDirectory,
            "",
            rarFileName,
            totalVolumes,
            completedVolumes,
            startTime)
        {
            PhaseDescription = "Direct SRR Reconstruction"
        });
    }
}
