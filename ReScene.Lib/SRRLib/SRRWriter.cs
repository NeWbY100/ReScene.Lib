using System.Text;
using RARLib;

namespace SRRLib;

/// <summary>
/// Options for SRR file creation.
/// </summary>
public class SrrCreationOptions
{
    /// <summary>Application name to embed in the SRR header.</summary>
    public string? AppName { get; set; } = "ReScene.NET";

    /// <summary>If false, reject compressed RAR volumes (method != Store).</summary>
    public bool AllowCompressed { get; set; } = true;

    /// <summary>Whether to store directory paths in stored file names.</summary>
    public bool StorePaths { get; set; } = true;

    /// <summary>Whether to compute and store OSO hashes for archived files.</summary>
    public bool ComputeOsoHashes { get; set; }
}

/// <summary>
/// Result of SRR file creation.
/// </summary>
public class SrrCreationResult
{
    /// <summary>Whether creation succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Path to the created SRR file.</summary>
    public string? OutputPath { get; set; }

    /// <summary>Error message if creation failed.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Number of RAR volumes processed.</summary>
    public int VolumeCount { get; set; }

    /// <summary>Number of stored files embedded.</summary>
    public int StoredFileCount { get; set; }

    /// <summary>Size of the created SRR file in bytes.</summary>
    public long SrrFileSize { get; set; }

    /// <summary>Non-fatal warnings encountered during creation.</summary>
    public List<string> Warnings { get; set; } = [];
}

/// <summary>
/// Progress event args for SRR creation.
/// </summary>
public class SrrCreationProgressEventArgs : EventArgs
{
    /// <summary>Overall progress percentage (0-100).</summary>
    public int ProgressPercent { get; set; }

    /// <summary>Current volume being processed (1-based).</summary>
    public int CurrentVolume { get; set; }

    /// <summary>Total number of volumes to process.</summary>
    public int TotalVolumes { get; set; }

    /// <summary>Status message describing current operation.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Creates SRR (Scene Release Reconstruction) files from RAR archives.
/// </summary>
public class SRRWriter
{
    private static readonly byte[] Rar4Marker = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];
    private static readonly byte[] Rar5Marker = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];

    /// <summary>
    /// Raised to report progress during SRR creation.
    /// </summary>
    public event EventHandler<SrrCreationProgressEventArgs>? Progress;

    /// <summary>
    /// Creates an SRR file from a list of RAR volume paths.
    /// </summary>
    /// <param name="outputPath">Path for the output SRR file.</param>
    /// <param name="rarVolumePaths">Ordered list of RAR volume file paths.</param>
    /// <param name="storedFiles">Optional dictionary of stored files (name -> path on disk).</param>
    /// <param name="options">Creation options, or null for defaults.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the creation operation.</returns>
    public async Task<SrrCreationResult> CreateAsync(
        string outputPath,
        IReadOnlyList<string> rarVolumePaths,
        IReadOnlyDictionary<string, string>? storedFiles = null,
        SrrCreationOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new SrrCreationOptions();
        var result = new SrrCreationResult();

        try
        {
            if (rarVolumePaths.Count == 0)
                throw new ArgumentException("At least one RAR volume path is required.", nameof(rarVolumePaths));

            // Validate all files exist
            foreach (string path in rarVolumePaths)
            {
                if (!File.Exists(path))
                    throw new FileNotFoundException($"RAR volume not found: {path}", path);
            }

            if (storedFiles != null)
            {
                foreach (var kvp in storedFiles)
                {
                    if (!File.Exists(kvp.Value))
                        throw new FileNotFoundException($"Stored file not found: {kvp.Value}", kvp.Value);
                }
            }

            string? outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir))
                Directory.CreateDirectory(outputDir);

            using var outStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = new BinaryWriter(outStream, Encoding.UTF8, leaveOpen: true);

            // 1. Write SRR Header block
            WriteSrrHeader(writer, options.AppName);

            // 2. Write stored file blocks
            if (storedFiles != null)
            {
                foreach (var kvp in storedFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    string storedName = options.StorePaths ? kvp.Key : Path.GetFileName(kvp.Key);
                    byte[] fileData = await File.ReadAllBytesAsync(kvp.Value, ct);
                    WriteStoredFileBlock(writer, storedName, fileData);
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

                await ProcessRarVolumeAsync(writer, volumePath, volumeName, options, result, ct);
                result.VolumeCount++;
            }

            // 4. Optionally write OSO hash blocks (placeholder - needs file data access)
            // OSO hashes require the actual file data from reconstructed files,
            // which is not available during SRR creation from headers alone.

            await outStream.FlushAsync(ct);
            result.SrrFileSize = outStream.Length;
            result.OutputPath = outputPath;
            result.Success = true;

            ReportProgress(totalVolumes, totalVolumes, "SRR creation complete.");
        }
        catch (OperationCanceledException)
        {
            result.ErrorMessage = "Operation was cancelled.";
            TryDeleteFile(outputPath);
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            TryDeleteFile(outputPath);
        }

        return result;
    }

    /// <summary>
    /// Creates an SRR file from an SFV file, automatically discovering RAR volumes.
    /// </summary>
    /// <param name="outputPath">Path for the output SRR file.</param>
    /// <param name="sfvFilePath">Path to the SFV file.</param>
    /// <param name="additionalFiles">Optional additional files to store (name -> path).</param>
    /// <param name="options">Creation options, or null for defaults.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the creation operation.</returns>
    public async Task<SrrCreationResult> CreateFromSfvAsync(
        string outputPath,
        string sfvFilePath,
        IReadOnlyList<string>? additionalFiles = null,
        SrrCreationOptions? options = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(sfvFilePath))
            return new SrrCreationResult { ErrorMessage = $"SFV file not found: {sfvFilePath}" };

        string sfvDir = Path.GetDirectoryName(sfvFilePath) ?? ".";
        string[] sfvLines = await File.ReadAllLinesAsync(sfvFilePath, ct);

        // Parse SFV to find RAR volumes
        var rarFiles = new List<string>();
        foreach (string line in sfvLines)
        {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(';'))
                continue;

            // SFV format: "filename CRC32" (CRC is last 8 chars)
            int lastSpace = trimmed.LastIndexOf(' ');
            if (lastSpace <= 0) continue;

            string fileName = trimmed[..lastSpace].Trim();
            if (IsRarVolume(fileName))
            {
                string fullPath = Path.Combine(sfvDir, fileName);
                if (File.Exists(fullPath))
                    rarFiles.Add(fullPath);
            }
        }

        if (rarFiles.Count == 0)
            return new SrrCreationResult { ErrorMessage = "No RAR volumes found in SFV file." };

        // Sort volumes in correct order
        rarFiles.Sort(CompareRarVolumeNames);

        // Build stored files dictionary - auto-include the SFV itself
        var storedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.GetFileName(sfvFilePath)] = sfvFilePath
        };

        // Add any additional stored files (NFO, etc.)
        if (additionalFiles != null)
        {
            foreach (string filePath in additionalFiles)
            {
                if (File.Exists(filePath))
                {
                    storedFiles[Path.GetFileName(filePath)] = filePath;
                }
            }
        }

        // Also auto-detect NFO files in the same directory
        foreach (string nfoFile in Directory.GetFiles(sfvDir, "*.nfo"))
        {
            string nfoName = Path.GetFileName(nfoFile);
            storedFiles.TryAdd(nfoName, nfoFile);
        }

        return await CreateAsync(outputPath, rarFiles, storedFiles, options, ct);
    }

    #region SRR Block Writers

    private static void WriteSrrHeader(BinaryWriter writer, string? appName)
    {
        ushort flags = appName != null ? (ushort)0x0001 : (ushort)0x0000;

        int headerSize = 7; // base header
        byte[]? appNameBytes = null;
        if (appName != null)
        {
            appNameBytes = Encoding.UTF8.GetBytes(appName);
            headerSize += 2 + appNameBytes.Length;
        }

        writer.Write((ushort)0x6969);          // CRC (SRR header sentinel)
        writer.Write((byte)0x69);              // SRR Header type
        writer.Write(flags);
        writer.Write((ushort)headerSize);

        if (appNameBytes != null)
        {
            writer.Write((ushort)appNameBytes.Length);
            writer.Write(appNameBytes);
        }
    }

    private static void WriteStoredFileBlock(BinaryWriter writer, string fileName, byte[] fileData)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(fileName);
        ushort headerSize = (ushort)(7 + 4 + 2 + nameBytes.Length); // base + addSize + nameLen + name
        uint addSize = (uint)fileData.Length;

        writer.Write((ushort)0x6A6A);           // CRC (SRR stored file sentinel)
        writer.Write((byte)0x6A);               // StoredFile type
        writer.Write((ushort)0x8000);           // flags: LONG_BLOCK
        writer.Write(headerSize);
        writer.Write(addSize);                  // data length
        writer.Write((ushort)nameBytes.Length);
        writer.Write(nameBytes);
        writer.Write(fileData);                 // file data
    }

    private static void WriteRarFileBlock(BinaryWriter writer, string rarFileName)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(rarFileName);
        ushort headerSize = (ushort)(7 + 2 + nameBytes.Length); // base + nameLen + name

        writer.Write((ushort)0x7171);           // CRC (SRR RAR file sentinel)
        writer.Write((byte)0x71);               // RarFile type
        writer.Write((ushort)0x0000);           // flags
        writer.Write(headerSize);
        writer.Write((ushort)nameBytes.Length);
        writer.Write(nameBytes);
    }

    private static void WriteOsoHashBlock(BinaryWriter writer, string fileName, ulong fileSize, byte[] osoHash)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(fileName);
        // pyrescene field order: fileSize, hash, nameLen, name
        ushort headerSize = (ushort)(7 + 8 + 8 + 2 + nameBytes.Length);

        writer.Write((ushort)0x6B6B);           // CRC (SRR OSO hash sentinel)
        writer.Write((byte)0x6B);               // OsoHash type
        writer.Write((ushort)0x0000);           // flags
        writer.Write(headerSize);
        writer.Write(fileSize);                 // file size (8 bytes)
        writer.Write(osoHash);                  // OSO hash (8 bytes)
        writer.Write((ushort)nameBytes.Length);  // name length
        writer.Write(nameBytes);                // file name
    }

    #endregion

    #region RAR Volume Processing

    private async Task ProcessRarVolumeAsync(
        BinaryWriter writer,
        string volumePath,
        string volumeName,
        SrrCreationOptions options,
        SrrCreationResult result,
        CancellationToken ct)
    {
        // Write the SRR RAR file reference block
        WriteRarFileBlock(writer, volumeName);

        // Open the RAR volume and extract headers
        using var fs = new FileStream(volumePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

        // Detect RAR version by checking marker
        bool isRar5 = IsRar5Volume(fs);

        if (isRar5)
        {
            await ProcessRar5VolumeAsync(writer, fs, reader, volumeName, options, result, ct);
        }
        else
        {
            await ProcessRar4VolumeAsync(writer, fs, reader, volumeName, options, result, ct);
        }
    }

    private static bool IsRar5Volume(FileStream fs)
    {
        if (fs.Length < 8)
            return false;

        long pos = fs.Position;
        byte[] marker = new byte[8];
        int read = fs.Read(marker, 0, 8);
        fs.Position = pos;

        if (read < 8) return false;

        for (int i = 0; i < 8; i++)
        {
            if (marker[i] != Rar5Marker[i])
                return false;
        }
        return true;
    }

    private Task ProcessRar4VolumeAsync(
        BinaryWriter srrWriter,
        FileStream fs,
        BinaryReader reader,
        string volumeName,
        SrrCreationOptions options,
        SrrCreationResult result,
        CancellationToken ct)
    {
        // Read and copy RAR4 marker block (7 bytes)
        if (fs.Length < 7)
        {
            result.Warnings.Add($"{volumeName}: File too small to contain RAR marker.");
            return Task.CompletedTask;
        }

        byte[] marker = reader.ReadBytes(7);
        if (!marker.AsSpan().SequenceEqual(Rar4Marker))
        {
            result.Warnings.Add($"{volumeName}: Invalid RAR4 marker.");
            return Task.CompletedTask;
        }

        // Copy marker verbatim to SRR
        srrWriter.Write(marker);

        // Process remaining blocks by reading raw bytes directly
        while (fs.Position < fs.Length)
        {
            ct.ThrowIfCancellationRequested();

            if (fs.Position + 7 > fs.Length) break;

            long blockStart = fs.Position;

            // Read base header (7 bytes) to determine block type and size
            ushort crc = reader.ReadUInt16();
            byte typeRaw = reader.ReadByte();
            ushort flags = reader.ReadUInt16();
            ushort headerSize = reader.ReadUInt16();

            if (headerSize < 7 || blockStart + headerSize > fs.Length)
                break;

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
                fs.Position = blockStart + 7;
                addSize = reader.ReadUInt32();
            }

            // Read the full raw header bytes for verbatim copy
            fs.Position = blockStart;
            byte[] headerBytes = reader.ReadBytes(headerSize);

            // Now position is at blockStart + headerSize (start of data area)
            switch (blockType)
            {
                case RAR4BlockType.ArchiveHeader:
                    srrWriter.Write(headerBytes);
                    break;

                case RAR4BlockType.FileHeader:
                    // Check compression if needed
                    if (!options.AllowCompressed && headerSize >= 26)
                    {
                        byte method = headerBytes[25]; // METHOD field at offset 25
                        if (method != 0x30) // 0x30 = Store
                        {
                            // Parse filename for the warning message
                            int nameSize = BitConverter.ToUInt16(headerBytes, 26);
                            string fName = nameSize > 0 && 32 + nameSize <= headerBytes.Length
                                ? Encoding.ASCII.GetString(headerBytes, 32, nameSize)
                                : "unknown";
                            result.Warnings.Add($"{volumeName}: Compressed file detected ({fName}).");
                        }
                    }
                    srrWriter.Write(headerBytes);
                    // Skip packed file data in source
                    fs.Seek(addSize, SeekOrigin.Current);
                    break;

                case RAR4BlockType.Service:
                    srrWriter.Write(headerBytes);
                    if (addSize > 0)
                    {
                        // Determine sub-type from header: name is at offset 32, name_size at offset 26
                        bool isCmt = false;
                        if (headerSize >= 35) // enough to read 3-byte name
                        {
                            int nameSize = BitConverter.ToUInt16(headerBytes, 26);
                            if (nameSize == 3 && 32 + 3 <= headerBytes.Length)
                            {
                                string subType = Encoding.ASCII.GetString(headerBytes, 32, 3);
                                isCmt = string.Equals(subType, "CMT", StringComparison.OrdinalIgnoreCase);
                            }
                        }

                        if (isCmt)
                        {
                            // Copy CMT data verbatim
                            CopyData(fs, srrWriter.BaseStream, addSize);
                        }
                        else
                        {
                            // Skip data for other service blocks (RR, AV, etc.)
                            fs.Seek(addSize, SeekOrigin.Current);
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

        return Task.CompletedTask;
    }

    private Task ProcessRar5VolumeAsync(
        BinaryWriter srrWriter,
        FileStream fs,
        BinaryReader reader,
        string volumeName,
        SrrCreationOptions options,
        SrrCreationResult result,
        CancellationToken ct)
    {
        // Read and copy RAR5 marker (8 bytes)
        if (fs.Length < 8)
        {
            result.Warnings.Add($"{volumeName}: File too small to contain RAR5 marker.");
            return Task.CompletedTask;
        }

        byte[] marker = reader.ReadBytes(8);
        if (!marker.AsSpan().SequenceEqual(Rar5Marker))
        {
            result.Warnings.Add($"{volumeName}: Invalid RAR5 marker.");
            return Task.CompletedTask;
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

            var block = rarReader.ReadBlock();
            if (block == null) break;

            // Calculate actual header bytes on disk:
            // CRC32 (4 bytes) + header size vint + header content
            long headerEndPos = block.BlockPosition + (long)block.HeaderSize;

            // Read the full raw header bytes (CRC + vint + header content)
            long rawHeaderSize = headerEndPos - blockStart;
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
                        SkipData(fs, block.DataSize);
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
                            CopyData(fs, srrWriter.BaseStream, block.DataSize);
                        }
                    }
                    else
                    {
                        // Skip data for other service blocks
                        if (block.DataSize > 0)
                        {
                            SkipData(fs, block.DataSize);
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
                        SkipData(fs, block.DataSize);
                    }
                    break;
            }
        }

        return Task.CompletedTask;
    }

    #endregion

    #region SFV Parsing Helpers

    private static bool IsRarVolume(string fileName)
    {
        string ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(ext)) return false;

        // .rar (including .partN.rar)
        if (ext.Equals(".rar", StringComparison.OrdinalIgnoreCase))
            return true;

        // Old-style extensions: .r00, .r01, ..., .r99, .s00, etc.
        if (ext.Length == 4 && ext[0] == '.' &&
            char.IsLetter(ext[1]) && char.IsDigit(ext[2]) && char.IsDigit(ext[3]))
            return true;

        // Extensions like .001, .002 (numbered volumes)
        if (ext.Length == 4 && ext[0] == '.' &&
            char.IsDigit(ext[1]) && char.IsDigit(ext[2]) && char.IsDigit(ext[3]))
            return true;

        return false;
    }

    public static int CompareRarVolumeNames(string a, string b)
    {
        string nameA = Path.GetFileName(a);
        string nameB = Path.GetFileName(b);

        // Handle new-style naming: name.part01.rar, name.part02.rar
        int partNumA = ExtractPartNumber(nameA);
        int partNumB = ExtractPartNumber(nameB);

        if (partNumA >= 0 && partNumB >= 0)
            return partNumA.CompareTo(partNumB);

        // Handle old-style naming: name.rar, name.r00, name.r01, etc.
        string extA = Path.GetExtension(nameA).ToLowerInvariant();
        string extB = Path.GetExtension(nameB).ToLowerInvariant();

        int orderA = GetOldStyleOrder(extA);
        int orderB = GetOldStyleOrder(extB);

        return orderA.CompareTo(orderB);
    }

    private static int ExtractPartNumber(string fileName)
    {
        // Look for .partNN.rar pattern
        string lower = fileName.ToLowerInvariant();
        int partIdx = lower.LastIndexOf(".part", StringComparison.Ordinal);
        if (partIdx < 0) return -1;

        int dotRar = lower.IndexOf(".rar", partIdx + 5, StringComparison.Ordinal);
        if (dotRar < 0) return -1;

        string numStr = lower[(partIdx + 5)..dotRar];
        return int.TryParse(numStr, out int num) ? num : -1;
    }

    private static int GetOldStyleOrder(string ext)
    {
        // .rar is always first
        if (ext == ".rar") return -1;

        // .r00, .r01, ..., .s00, .s01, etc.
        if (ext.Length == 4 && ext[0] == '.' && char.IsLetter(ext[1]))
        {
            int letterOffset = (ext[1] - 'r') * 100;
            if (int.TryParse(ext[2..], out int num))
                return letterOffset + num;
        }

        // .001, .002, etc.
        if (ext.Length == 4 && ext[0] == '.' && int.TryParse(ext[1..], out int numExt))
            return numExt;

        return int.MaxValue;
    }

    #endregion

    #region Helpers

    private static void SkipData(Stream stream, uint bytes)
    {
        stream.Seek(bytes, SeekOrigin.Current);
    }

    private static void SkipData(Stream stream, ulong bytes)
    {
        stream.Seek((long)bytes, SeekOrigin.Current);
    }

    private static void CopyData(Stream source, Stream destination, uint bytes)
    {
        CopyData(source, destination, (long)bytes);
    }

    private static void CopyData(Stream source, Stream destination, ulong bytes)
    {
        CopyData(source, destination, (long)bytes);
    }

    private static void CopyData(Stream source, Stream destination, long bytes)
    {
        byte[] buffer = new byte[81920];
        long remaining = bytes;

        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = source.Read(buffer, 0, toRead);
            if (read <= 0) break;
            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private void ReportProgress(int current, int total, string message)
    {
        int percent = total > 0 ? (int)(current * 100.0 / total) : 0;
        Progress?.Invoke(this, new SrrCreationProgressEventArgs
        {
            ProgressPercent = percent,
            CurrentVolume = current,
            TotalVolumes = total,
            Message = message
        });
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { }
    }

    #endregion
}
