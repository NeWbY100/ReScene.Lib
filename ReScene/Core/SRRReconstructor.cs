using System.Text;
using ReScene.Core.Cryptography;

namespace ReScene.Core;

/// <summary>
/// Reconstructs RAR archive files directly from an SRR binary stream and source files.
/// Used when the SRR indicates a custom packer (not WinRAR) created the original RARs,
/// making brute-force reconstruction impossible.
/// </summary>
public class SRRReconstructor(IReSceneLogger? logger = null)
{
    /// <summary>
    /// Occurs when reconstruction progress updates.
    /// </summary>
    public event EventHandler<BruteForceProgressEventArgs>? Progress;

    private readonly IReSceneLogger _logger = logger ?? NullReSceneLogger.Instance;

    // RAR block type constants
    private const byte RarMarkerType = 0x72;
    private const byte ArchiveHeaderType = 0x73;
    private const byte FileHeaderType = 0x74;
    private const byte ServiceBlockType = 0x7A;
    private const byte EndArchiveType = 0x7B;

    // RAR flag constants
    private const ushort FlagLongBlock = 0x8000;
    private const ushort FlagSplitBefore = 0x0001;
    private const ushort FlagSplitAfter = 0x0002;

    // SRR block type constants
    private const byte SrrHeaderType = 0x69;
    private const byte SrrStoredFileType = 0x6A;
    private const byte SrrOsoHashType = 0x6B;
    private const byte SrrRarPaddingType = 0x6C;
    private const byte SrrRarFileType = 0x71;

    /// <summary>
    /// Reconstructs RAR files from an SRR file by replaying original headers and splicing in source file data.
    /// </summary>
    public async Task<bool> ReconstructAsync(
        string srrFilePath,
        string inputDirectory,
        string outputDirectory,
        List<string> originalRarFileNames,
        HashSet<string> hashes,
        HashType hashType,
        CancellationToken cancellationToken)
    {
        _logger.Information(this, $"=== Direct SRR Reconstruction ===", LogTarget.System);
        _logger.Information(this, $"SRR: {srrFilePath}", LogTarget.System);
        _logger.Information(this, $"Input: {inputDirectory}", LogTarget.System);
        _logger.Information(this, $"Output: {outputDirectory}", LogTarget.System);
        _logger.Information(this, $"Expected volumes: {originalRarFileNames.Count}", LogTarget.System);

        Directory.CreateDirectory(outputDirectory);

        DateTime startTime = DateTime.Now;
        int totalVolumes = originalRarFileNames.Count;
        int completedVolumes = 0;
        bool allMatched = true;

        // Track open source file streams for multi-volume spanning
        FileStream? currentSourceStream = null;
        string? currentSourceFileName = null;

        FileStream? outputStream = null;
        string? currentOutputPath = null;
        string? currentRarFileName = null;

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

                // Determine ADD_SIZE for blocks with LONG_BLOCK flag or stored file blocks
                uint addSize = 0;
                bool hasLongBlock = (flags & FlagLongBlock) != 0;

                if (IsSrrBlockType(blockType))
                {
                    // SRR blocks
                    if (hasLongBlock || blockType == SrrStoredFileType)
                    {
                        if (srrStream.Position + 4 > srrStream.Length)
                        {
                            break;
                        }

                        addSize = reader.ReadUInt32();
                    }

                    if (blockType == SrrRarFileType)
                    {
                        // Close previous volume and verify
                        if (outputStream != null && currentOutputPath != null && currentRarFileName != null)
                        {
                            outputStream.Dispose();
                            outputStream = null;

                            await VerifyAndReportVolumeAsync(currentOutputPath, currentRarFileName, hashes, hashType, ref allMatched);
                            completedVolumes++;
                            FireProgress(inputDirectory, currentRarFileName, totalVolumes, completedVolumes, startTime);
                        }

                        // Read the RAR filename from the SrrRarFile block
                        if (srrStream.Position + 2 > srrStream.Length)
                        {
                            break;
                        }

                        ushort nameLen = reader.ReadUInt16();
                        if (srrStream.Position + nameLen > srrStream.Length)
                        {
                            break;
                        }

                        byte[] nameBytes = reader.ReadBytes(nameLen);
                        currentRarFileName = Encoding.UTF8.GetString(nameBytes);

                        // Open new output file
                        currentOutputPath = Path.Combine(outputDirectory, currentRarFileName);
                        Directory.CreateDirectory(Path.GetDirectoryName(currentOutputPath)!);
                        outputStream = new FileStream(currentOutputPath, FileMode.Create, FileAccess.Write, FileShare.None);

                        _logger.Information(this, $"Reconstructing: {currentRarFileName}", LogTarget.System);
                    }
                    else if (blockType == SrrRarPaddingType)
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
                    if (headerSize >= 11 && (flags & FlagLongBlock) != 0)
                    {
                        rarAddSize = BitConverter.ToUInt32(fullHeader, 7);
                    }

                    switch (blockType)
                    {
                        case RarMarkerType:
                            outputStream.Write(fullHeader, 0, fullHeader.Length);
                            break;

                        case ArchiveHeaderType:
                            outputStream.Write(fullHeader, 0, fullHeader.Length);
                            break;

                        case FileHeaderType:
                            outputStream.Write(fullHeader, 0, fullHeader.Length);

                            long packedSize = rarAddSize;

                            // Check for LARGE flag (LHD_LARGE = 0x0100) for 64-bit sizes
                            if ((flags & 0x0100) != 0 && headerSize >= 36)
                            {
                                uint highPackSize = BitConverter.ToUInt32(fullHeader, 32);
                                packedSize |= (long)highPackSize << 32;
                            }

                            // Extract filename from file header
                            string? archivedFileName = null;
                            if (headerSize >= 32)
                            {
                                ushort nameSize = BitConverter.ToUInt16(fullHeader, 26);
                                int nameOffset = 32;
                                if ((flags & 0x0100) != 0 && headerSize >= 40 + nameSize)
                                {
                                    nameOffset = 40;
                                }
                                else if (headerSize < nameOffset + nameSize)
                                {
                                    nameOffset = 32;
                                }

                                if (nameOffset + nameSize <= fullHeader.Length)
                                {
                                    archivedFileName = Encoding.ASCII.GetString(fullHeader, nameOffset, nameSize);
                                    int nullIdx = archivedFileName.IndexOf('\0');
                                    if (nullIdx >= 0)
                                    {
                                        archivedFileName = archivedFileName[..nullIdx];
                                    }

                                    archivedFileName = archivedFileName.Replace('\\', Path.DirectorySeparatorChar);
                                }
                            }

                            bool isSplitBefore = (flags & FlagSplitBefore) != 0;
                            bool isSplitAfter = (flags & FlagSplitAfter) != 0;

                            if (!isSplitBefore && archivedFileName != null)
                            {
                                if (currentSourceStream != null && currentSourceFileName != archivedFileName)
                                {
                                    currentSourceStream.Dispose();
                                    currentSourceStream = null;
                                }

                                if (currentSourceStream == null)
                                {
                                    string sourcePath = FindSourceFile(inputDirectory, archivedFileName);
                                    currentSourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                                    currentSourceFileName = archivedFileName;
                                    _logger.Debug(this, $"Opened source file: {archivedFileName}");
                                }
                            }

                            if (currentSourceStream != null && packedSize > 0)
                            {
                                await CopyBytesAsync(currentSourceStream, outputStream, packedSize, cancellationToken);
                            }

                            if (!isSplitAfter && currentSourceStream != null)
                            {
                                currentSourceStream.Dispose();
                                currentSourceStream = null;
                                currentSourceFileName = null;
                            }

                            break;

                        case ServiceBlockType:
                            outputStream.Write(fullHeader, 0, fullHeader.Length);
                            if (rarAddSize > 0)
                            {
                                byte[] serviceData = reader.ReadBytes((int)rarAddSize);
                                outputStream.Write(serviceData, 0, serviceData.Length);
                            }

                            break;

                        case EndArchiveType:
                            outputStream.Write(fullHeader, 0, fullHeader.Length);
                            if (rarAddSize > 0)
                            {
                                byte[] endData = reader.ReadBytes((int)rarAddSize);
                                outputStream.Write(endData, 0, endData.Length);
                            }

                            break;

                        default:
                            outputStream.Write(fullHeader, 0, fullHeader.Length);
                            if (rarAddSize > 0)
                            {
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
            if (outputStream != null && currentOutputPath != null && currentRarFileName != null)
            {
                outputStream.Dispose();
                outputStream = null;

                await VerifyAndReportVolumeAsync(currentOutputPath, currentRarFileName, hashes, hashType, ref allMatched);
                completedVolumes++;
                FireProgress(inputDirectory, currentRarFileName, totalVolumes, completedVolumes, startTime);
            }
        }
        finally
        {
            currentSourceStream?.Dispose();
            outputStream?.Dispose();
        }

        var elapsed = DateTime.Now - startTime;
        if (allMatched && completedVolumes > 0)
        {
            _logger.Information(this, $"=== Reconstruction SUCCESS: {completedVolumes} volume(s) in {elapsed.TotalSeconds:F1}s ===", LogTarget.System);
        }
        else if (completedVolumes == 0)
        {
            _logger.Warning(this, $"=== Reconstruction FAILED: no volumes produced ===", LogTarget.System);
            allMatched = false;
        }
        else
        {
            _logger.Warning(this, $"=== Reconstruction completed with hash mismatches ({completedVolumes} volume(s), {elapsed.TotalSeconds:F1}s) ===", LogTarget.System);
        }

        return allMatched;
    }

    private static bool IsSrrBlockType(byte type)
    {
        return type is SrrHeaderType or SrrStoredFileType or SrrOsoHashType or SrrRarPaddingType or SrrRarFileType;
    }

    private static string FindSourceFile(string inputDirectory, string archivedFileName)
    {
        string directPath = Path.Combine(inputDirectory, archivedFileName);
        if (File.Exists(directPath))
        {
            return directPath;
        }

        string flatPath = Path.Combine(inputDirectory, Path.GetFileName(archivedFileName));
        if (File.Exists(flatPath))
        {
            return flatPath;
        }

        string searchDir = inputDirectory;
        string searchName = Path.GetFileName(archivedFileName);
        string? subDir = Path.GetDirectoryName(archivedFileName);

        if (!string.IsNullOrEmpty(subDir))
        {
            string subDirPath = Path.Combine(inputDirectory, subDir);
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

    private static async Task CopyBytesAsync(Stream source, Stream destination, long count, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[80 * 1024];
        long remaining = count;

        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = await source.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
            if (read <= 0)
            {
                throw new EndOfStreamException($"Unexpected end of source file with {remaining} bytes remaining.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
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

        string hash = hashType switch
        {
            HashType.CRC32 => CRC32.Calculate(outputPath),
            HashType.SHA1 => SHA1.Calculate(outputPath),
            _ => throw new ArgumentOutOfRangeException(nameof(hashType))
        };

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
