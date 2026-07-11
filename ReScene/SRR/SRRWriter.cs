using System.Text;
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
            int totalVolumes = rarVolumePaths.Count;
            for (int i = 0; i < totalVolumes; i++)
            {
                ct.ThrowIfCancellationRequested();

                string volumePath = rarVolumePaths[i];
                string volumeName = Path.GetFileName(volumePath);

                ReportProgress(i + 1, totalVolumes, $"Processing {volumeName}...");

                ProcessRARVolume(writer, volumePath, volumeName, options, result, ct);
                result.VolumeCount++;
            }

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

            ReportProgress(totalVolumes, totalVolumes, "SRR creation complete.");
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
        foreach (string line in sfvLines)
        {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(';'))
            {
                continue;
            }

            // SFV format: "filename CRC32" (CRC is last 8 chars)
            int lastSpace = trimmed.LastIndexOf(' ');
            if (lastSpace <= 0)
            {
                continue;
            }

            string fileName = trimmed[..lastSpace].Trim();
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
        writer.Write((ushort)SRRBlockFlags.None);          // flags
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
