using System.Text;
using RARLib;
using RARLib.Decompression;

namespace SRRLib;

/// <summary>
/// Parser for SRR (Scene Release Reconstruction) files.
/// </summary>
public class SRRFile
{
    /// <summary>Gets the SRR file header block.</summary>
    public SrrHeaderBlock? HeaderBlock { get; private set; }

    /// <summary>Gets the OSO hash blocks from the SRR file.</summary>
    public List<SrrOsoHashBlock> OsoHashBlocks { get; private set; } = [];

    /// <summary>Gets the RAR padding blocks from the SRR file.</summary>
    public List<SrrRarPaddingBlock> RarPaddingBlocks { get; private set; } = [];

    /// <summary>Gets the RAR file reference blocks from the SRR file.</summary>
    public List<SrrRarFileBlock> RarFiles { get; private set; } = [];

    /// <summary>Gets the stored file blocks (SFV, NFO, etc.) from the SRR file.</summary>
    public List<SrrStoredFileBlock> StoredFiles { get; private set; } = [];

    /// <summary>Gets the set of archived file paths (normalized, case-insensitive).</summary>
    public HashSet<string> ArchivedFiles { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the set of archived directory paths (normalized, case-insensitive).</summary>
    public HashSet<string> ArchivedDirectories { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets directory modification times keyed by normalized path.</summary>
    public Dictionary<string, DateTime> ArchivedDirectoryTimestamps { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets directory creation times keyed by normalized path.</summary>
    public Dictionary<string, DateTime> ArchivedDirectoryCreationTimes { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets directory access times keyed by normalized path.</summary>
    public Dictionary<string, DateTime> ArchivedDirectoryAccessTimes { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets file modification times keyed by normalized path.</summary>
    public Dictionary<string, DateTime> ArchivedFileTimestamps { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets file creation times keyed by normalized path.</summary>
    public Dictionary<string, DateTime> ArchivedFileCreationTimes { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets file access times keyed by normalized path.</summary>
    public Dictionary<string, DateTime> ArchivedFileAccessTimes { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets file CRC32 values (as hex strings) keyed by normalized path.</summary>
    public Dictionary<string, string> ArchivedFileCrcs { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, RARFileFlags> ArchivedFileCrcFlags { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the calculated RAR volume sizes in bytes for each volume.</summary>
    public List<long> RarVolumeSizes { get; private set; } = [];

    /// <summary>Gets the most common volume size in bytes (for multi-volume archives).</summary>
    public long? VolumeSizeBytes { get; private set; }

    // Archive metadata extracted from RAR headers
    public int? CompressionMethod { get; private set; }
    public int? DictionarySize { get; private set; }
    public bool? IsSolidArchive { get; private set; }
    public bool? IsVolumeArchive { get; private set; }
    public bool? HasRecoveryRecord { get; private set; }
    public int? RARVersion { get; private set; }

    // Version indicators from RAR headers
    public bool? HasNewVolumeNaming { get; private set; }
    public bool? HasFirstVolumeFlag { get; private set; }
    public bool? HasEncryptedHeaders { get; private set; }
    public bool? HasLargeFiles { get; private set; }
    public bool? HasUnicodeNames { get; private set; }
    public bool? HasExtendedTime { get; private set; }

    /// <summary>Upper 32 bits of packed size from first LARGE file header.</summary>
    public uint? DetectedHighPackSize { get; private set; }

    /// <summary>Upper 32 bits of unpacked size from first LARGE file header.</summary>
    public uint? DetectedHighUnpSize { get; private set; }

    // CRC validation tracking
    public int HeaderCrcMismatches { get; private set; }

    // Custom RAR packer detection

    /// <summary>
    /// True if any file header has sentinel unpacked_size values indicating a custom RAR packer (not WinRAR).
    /// Known groups: RELOADED, HI2U, 0x0007, 0x0815, QCF.
    /// </summary>
    public bool HasCustomPackerHeaders { get; private set; }

    /// <summary>The type of custom packer anomaly detected, if any.</summary>
    public CustomPackerType CustomPackerDetected { get; private set; }

    // Archive comment extracted from CMT sub-block
    public string? ArchiveComment { get; private set; }

    /// <summary>Raw archive comment bytes (for exact reconstruction).</summary>
    public byte[]? ArchiveCommentBytes { get; private set; }

    /// <summary>Raw CMT block compressed data (for Phase 1 brute-force comparison).</summary>
    public byte[]? CmtCompressedData { get; private set; }

    /// <summary>CMT block compression method (0x30=Store, 0x31-0x35=Compressed).</summary>
    public byte? CmtCompressionMethod { get; private set; }

    // Host OS and timestamp settings detected from headers
    /// <summary>Host OS from file headers (0=MS-DOS, 1=OS/2, 2=Windows, 3=Unix).</summary>
    public byte? DetectedHostOS { get; private set; }

    /// <summary>Host OS name for display.</summary>
    public string DetectedHostOSName => DetectedHostOS switch
    {
        0 => "MS-DOS",
        1 => "OS/2",
        2 => "Windows",
        3 => "Unix",
        4 => "Mac OS",
        5 => "BeOS",
        null => "Unknown",
        _ => $"Unknown ({DetectedHostOS})"
    };

    /// <summary>File attributes from first file header (for patching).</summary>
    public uint? DetectedFileAttributes { get; private set; }

    /// <summary>Host OS from CMT service block (may differ from file headers).</summary>
    public byte? CmtHostOS { get; private set; }

    /// <summary>CMT Host OS name for display.</summary>
    public string CmtHostOSName => CmtHostOS switch
    {
        0 => "MS-DOS",
        1 => "OS/2",
        2 => "Windows",
        3 => "Unix",
        4 => "Mac OS",
        5 => "BeOS",
        null => "Unknown",
        _ => $"Unknown ({CmtHostOS})"
    };

    /// <summary>Raw DOS file time from CMT block (0 = zeroed/no timestamp).</summary>
    public uint? CmtFileTimeDOS { get; private set; }

    /// <summary>True if CMT block has zeroed file time (suggests -ts- or similar option).</summary>
    public bool CmtHasZeroedFileTime => CmtFileTimeDOS == 0;

    /// <summary>File attributes from CMT block.</summary>
    public uint? CmtFileAttributes { get; private set; }

    /// <summary>Whether CMT timestamp appears to be current time vs zeroed.</summary>
    public string CmtTimestampMode => CmtFileTimeDOS switch
    {
        null => "Unknown",
        0 => "Zeroed (no timestamp)",
        _ => "Preserved (has timestamp)"
    };

    // ===== Timestamp Precision (from file headers) =====

    /// <summary>Modification time precision from file headers (maps to -tsm0 through -tsm4).</summary>
    public TimestampPrecision? FileMtimePrecision { get; private set; }

    /// <summary>Creation time precision from file headers (maps to -tsc0 through -tsc4).</summary>
    public TimestampPrecision? FileCtimePrecision { get; private set; }

    /// <summary>Access time precision from file headers (maps to -tsa0 through -tsa4).</summary>
    public TimestampPrecision? FileAtimePrecision { get; private set; }

    // ===== Timestamp Precision (from CMT service block) =====

    /// <summary>Modification time precision from CMT block (maps to -tsm0 through -tsm4).</summary>
    public TimestampPrecision? CmtMtimePrecision { get; private set; }

    /// <summary>Creation time precision from CMT block (maps to -tsc0 through -tsc4).</summary>
    public TimestampPrecision? CmtCtimePrecision { get; private set; }

    /// <summary>Access time precision from CMT block (maps to -tsa0 through -tsa4).</summary>
    public TimestampPrecision? CmtAtimePrecision { get; private set; }

    /// <summary>
    /// Loads and parses an SRR file from the specified path.
    /// </summary>
    /// <param name="filePath">The path to the SRR file.</param>
    /// <returns>A parsed <see cref="SRRFile"/> instance containing all extracted metadata.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
    public static SRRFile Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException(filePath);
        }

        SRRFile srr = new();
        using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using BinaryReader reader = new(fs);

        while (fs.Position < fs.Length)
        {
            if (fs.Position + 7 > fs.Length) break;

            long startPos = fs.Position;
            ushort crc = reader.ReadUInt16();
            byte typeRaw = reader.ReadByte();
            ushort flags = reader.ReadUInt16();
            ushort headerSize = reader.ReadUInt16();

            // Check if this is an SRR block type
            if (!IsSrrBlockType(typeRaw))
            {
                // Unknown block, skip it
                fs.Seek(startPos, SeekOrigin.Begin);
                break;
            }

            SRRBlockType type = (SRRBlockType)typeRaw;

            uint addSize = 0;
            if ((flags & (ushort)SRRBlockFlags.LongBlock) != 0 || type == SRRBlockType.StoredFile)
            {
                if (fs.Position + 4 > fs.Length) break;
                addSize = reader.ReadUInt32();
            }

            if (headerSize < 7) break;

            long blockEndPos = startPos + headerSize + addSize;
            if (blockEndPos <= startPos || blockEndPos > fs.Length) break;

            switch (type)
            {
                case SRRBlockType.Header:
                    srr.HeaderBlock = ParseHeaderBlock(reader, fs, startPos, crc, type, flags, headerSize);
                    fs.Seek(blockEndPos, SeekOrigin.Begin);
                    break;

                case SRRBlockType.StoredFile:
                    var storedBlock = ParseStoredFileBlock(reader, fs, startPos, crc, type, flags, headerSize, addSize);
                    if (storedBlock == null) goto exitLoop;
                    srr.StoredFiles.Add(storedBlock);
                    fs.Seek(blockEndPos, SeekOrigin.Begin);
                    break;

                case SRRBlockType.OsoHash:
                    var osoBlock = ParseOsoHashBlock(reader, fs, startPos, crc, type, flags, headerSize);
                    if (osoBlock != null)
                        srr.OsoHashBlocks.Add(osoBlock);
                    fs.Seek(blockEndPos, SeekOrigin.Begin);
                    break;

                case SRRBlockType.RarPadding:
                    var paddingBlock = ParseRarPaddingBlock(reader, fs, startPos, crc, type, flags, headerSize, addSize);
                    if (paddingBlock != null)
                        srr.RarPaddingBlocks.Add(paddingBlock);
                    fs.Seek(blockEndPos, SeekOrigin.Begin);
                    break;

                case SRRBlockType.RarFile:
                    var rarBlock = ParseRarFileBlock(reader, fs, startPos, crc, type, flags, headerSize, addSize);
                    if (rarBlock == null) goto exitLoop;
                    srr.RarFiles.Add(rarBlock);

                    // Parse embedded RAR headers that follow
                    long volumeTotalSize = ParseEmbeddedRarHeaders(reader, fs, srr);
                    if (volumeTotalSize > 0)
                    {
                        srr.RarVolumeSizes.Add(volumeTotalSize);
                    }
                    break;

                default:
                    // Skip unknown SRR block types
                    fs.Seek(blockEndPos, SeekOrigin.Begin);
                    break;
            }
        }
    exitLoop:

        srr.CalculateVolumeSizeBytes();
        return srr;
    }

    private static bool IsSrrBlockType(byte type)
    {
        return type is 0x69 or 0x6A or 0x6B or 0x6C or 0x71;
    }

    private static SrrHeaderBlock ParseHeaderBlock(BinaryReader reader, FileStream fs,
        long startPos, ushort crc, SRRBlockType type, ushort flags, ushort headerSize)
    {
        var block = new SrrHeaderBlock
        {
            Crc = crc,
            BlockType = type,
            Flags = flags,
            HeaderSize = headerSize,
            BlockPosition = startPos
        };

        // If AppNamePresent flag is set, read the app name
        if ((flags & (ushort)SRRHeaderFlags.AppNamePresent) != 0)
        {
            long headerEnd = startPos + headerSize;
            if (fs.Position + 2 <= headerEnd)
            {
                ushort nameLen = reader.ReadUInt16();
                if (fs.Position + nameLen <= headerEnd && nameLen > 0)
                {
                    byte[] nameBytes = reader.ReadBytes(nameLen);
                    block.AppName = Encoding.UTF8.GetString(nameBytes);
                }
            }
        }

        return block;
    }

    private static SrrOsoHashBlock? ParseOsoHashBlock(BinaryReader reader, FileStream fs,
        long startPos, ushort crc, SRRBlockType type, ushort flags, ushort headerSize)
    {
        long headerEnd = startPos + headerSize;

        // OSO hash block format (pyrescene order): 8 bytes file size + 8 bytes hash + 2 bytes name length + name
        if (fs.Position + 18 > headerEnd) return null;

        ulong fileSize = reader.ReadUInt64();
        byte[] osoHash = reader.ReadBytes(8);

        if (fs.Position + 2 > headerEnd) return null;
        ushort nameLen = reader.ReadUInt16();
        if (fs.Position + nameLen > headerEnd || nameLen == 0) return null;

        byte[] nameBytes = reader.ReadBytes(nameLen);
        string fileName = Encoding.UTF8.GetString(nameBytes);

        return new SrrOsoHashBlock
        {
            Crc = crc,
            BlockType = type,
            Flags = flags,
            HeaderSize = headerSize,
            BlockPosition = startPos,
            FileName = fileName,
            FileSize = fileSize,
            OsoHash = osoHash
        };
    }

    private static SrrRarPaddingBlock? ParseRarPaddingBlock(BinaryReader reader, FileStream fs,
        long startPos, ushort crc, SRRBlockType type, ushort flags, ushort headerSize, uint addSize)
    {
        long headerEnd = startPos + headerSize;

        // RAR padding block format: 2 bytes name length + RAR filename
        if (fs.Position + 2 > headerEnd) return null;

        ushort nameLen = reader.ReadUInt16();
        if (fs.Position + nameLen > headerEnd) return null;

        string rarFileName = string.Empty;
        if (nameLen > 0)
        {
            byte[] nameBytes = reader.ReadBytes(nameLen);
            rarFileName = Encoding.UTF8.GetString(nameBytes);
        }

        return new SrrRarPaddingBlock
        {
            Crc = crc,
            BlockType = type,
            Flags = flags,
            HeaderSize = headerSize,
            BlockPosition = startPos,
            AddSize = addSize,
            RarFileName = rarFileName,
            PaddingSize = addSize
        };
    }

    private static SrrStoredFileBlock? ParseStoredFileBlock(BinaryReader reader, FileStream fs,
        long startPos, ushort crc, SRRBlockType type, ushort flags, ushort headerSize, uint addSize)
    {
        const int minStoredHeaderSize = 7 + 4 + 2;
        if (headerSize < minStoredHeaderSize) return null;

        long headerEnd = startPos + headerSize;
        if (headerEnd <= startPos || headerEnd > fs.Length) return null;

        if (fs.Position + 2 > headerEnd) return null;
        ushort nameLen = reader.ReadUInt16();
        if (fs.Position + nameLen > headerEnd || fs.Position + nameLen > fs.Length) return null;

        byte[] nameBytes = reader.ReadBytes(nameLen);
        string fileName = Encoding.UTF8.GetString(nameBytes);

        long dataOffset = startPos + headerSize;
        if (dataOffset < startPos || dataOffset > fs.Length) return null;

        return new SrrStoredFileBlock
        {
            Crc = crc,
            BlockType = type,
            Flags = flags,
            HeaderSize = headerSize,
            BlockPosition = startPos,
            AddSize = addSize,
            FileName = fileName,
            FileLength = addSize,
            DataOffset = dataOffset
        };
    }

    private static SrrRarFileBlock? ParseRarFileBlock(BinaryReader reader, FileStream fs,
        long startPos, ushort crc, SRRBlockType type, ushort flags, ushort headerSize, uint addSize)
    {
        ushort nameLen = reader.ReadUInt16();
        if (fs.Position + nameLen > fs.Length) return null;

        byte[] nameBytes = reader.ReadBytes(nameLen);
        string fileName = Encoding.UTF8.GetString(nameBytes);

        return new SrrRarFileBlock
        {
            Crc = crc,
            BlockType = type,
            Flags = flags,
            HeaderSize = headerSize,
            BlockPosition = startPos,
            AddSize = addSize,
            FileName = fileName
        };
    }

    private static long ParseEmbeddedRarHeaders(BinaryReader reader, FileStream fs, SRRFile srr)
    {
        // Check if this is RAR5 format by looking for the marker
        if (IsRar5Marker(fs))
        {
            return ParseEmbeddedRar5Headers(fs, srr);
        }

        return ParseEmbeddedRar4Headers(reader, fs, srr);
    }

    private static bool IsRar5Marker(FileStream fs)
    {
        if (fs.Position + 8 > fs.Length)
            return false;

        long pos = fs.Position;
        byte[] marker = new byte[8];
        fs.ReadExactly(marker, 0, 8);
        fs.Position = pos;

        // RAR5 marker: Rar!\x1a\x07\x01\x00
        return marker[0] == 0x52 && marker[1] == 0x61 && marker[2] == 0x72 && marker[3] == 0x21 &&
               marker[4] == 0x1A && marker[5] == 0x07 && marker[6] == 0x01 && marker[7] == 0x00;
    }

    private static long ParseEmbeddedRar4Headers(BinaryReader reader, FileStream fs, SRRFile srr)
    {
        long volumeTotalSize = 0;
        var rarReader = new RARHeaderReader(reader);

        while (fs.Position < fs.Length)
        {
            // Check if we've hit another SRR block
            byte? peekType = rarReader.PeekBlockType();
            if (peekType == null) break;
            if (IsSrrBlockType(peekType.Value)) break;

            // Read the RAR block
            var block = rarReader.ReadBlock(parseContents: true);
            if (block == null)
            {
                fs.Seek(fs.Length, SeekOrigin.Begin);
                break;
            }

            // Track CRC mismatches
            if (!block.CrcValid)
            {
                srr.HeaderCrcMismatches++;
            }

            // Calculate actual RAR volume size (header + packed data for all blocks)
            // For file headers, AddSize = PackedSize (compressed data size)
            volumeTotalSize += block.HeaderSize + block.AddSize;

            // Process archive header
            if (block.ArchiveHeader != null)
            {
                srr.ProcessArchiveHeader(block.ArchiveHeader);
            }

            // Process file header
            if (block.FileHeader != null)
            {
                srr.ProcessFileHeader(block.FileHeader);
            }

            // Process service block (CMT comment, RR recovery, etc.)
            if (block.ServiceBlockInfo != null)
            {
                srr.ProcessServiceBlock(block, rarReader);
            }

            // Skip to next block (headers only for file blocks in SRR)
            rarReader.SkipBlock(block, includeData: block.BlockType != RAR4BlockType.FileHeader);
        }

        return volumeTotalSize;
    }

    private static long ParseEmbeddedRar5Headers(FileStream fs, SRRFile srr)
    {
        long volumeTotalSize = 0;

        // Skip RAR5 marker (8 bytes)
        fs.Seek(8, SeekOrigin.Current);
        volumeTotalSize += 8;

        var rarReader = new RAR5HeaderReader(fs);
        srr.RARVersion = 50; // RAR 5.0

        while (fs.Position < fs.Length)
        {
            // Check if we've hit another SRR block (RAR5 block types are 0-5, SRR blocks are 0x69-0x71)
            byte? peekType = rarReader.PeekBlockType();
            if (peekType == null) break;
            // SRR blocks have types in 0x69-0x71 range, RAR5 blocks are 0-5
            if (peekType.Value >= 0x69 && peekType.Value <= 0x71) break;

            // Read the RAR5 block
            var block = rarReader.ReadBlock();
            if (block == null)
            {
                fs.Seek(fs.Length, SeekOrigin.Begin);
                break;
            }

            // Track CRC mismatches
            if (!block.CrcValid)
            {
                srr.HeaderCrcMismatches++;
            }

            // Calculate RAR5 volume size (CRC + header size vint + header + data)
            // Approximate: 4 (CRC) + 1 (header size vint) + header + data
            volumeTotalSize += 4 + 1 + (long)block.HeaderSize + (long)block.DataSize;

            // Process archive header
            if (block.ArchiveInfo != null)
            {
                srr.ProcessRar5ArchiveHeader(block.ArchiveInfo);
            }

            // Process file header
            if (block.FileInfo != null)
            {
                srr.ProcessRar5FileHeader(block.FileInfo);
            }

            // Process service block (CMT comment, RR recovery, etc.)
            if (block.ServiceBlockInfo != null)
            {
                srr.ProcessRar5ServiceBlock(block, rarReader);
            }

            // Skip to next block (headers only for file blocks in SRR)
            // In SRR, file data is not present, so we skip data for non-file blocks
            if (block.BlockType != RAR5BlockType.File)
            {
                rarReader.SkipBlock(block);
            }
            else
            {
                // For file blocks in SRR, only skip header (data is not present)
                long target = block.BlockPosition + (long)block.HeaderSize;
                if (target <= fs.Length)
                    fs.Position = target;
            }
        }

        return volumeTotalSize;
    }

    private void ProcessArchiveHeader(RARArchiveHeader header)
    {
        IsVolumeArchive ??= header.IsVolume;
        IsSolidArchive ??= header.IsSolid;
        HasRecoveryRecord ??= header.HasRecoveryRecord;
        HasNewVolumeNaming ??= header.HasNewVolumeNaming;
        HasFirstVolumeFlag ??= header.IsFirstVolume;
        HasEncryptedHeaders ??= header.HasEncryptedHeaders;
    }

    private void ProcessFileHeader(RARFileHeader header)
    {
        // Detect custom RAR packer sentinel values in unpacked_size
        if (!HasCustomPackerHeaders && !header.IsDirectory)
        {
            if (header.UnpackedSize == 0xFFFFFFFFFFFFFFFF)
            {
                // Both low and high 32-bit fields are all ones (LARGE flag set).
                // Known groups: RELOADED, HI2U, 0x0007, 0x0815
                HasCustomPackerHeaders = true;
                CustomPackerDetected = CustomPackerType.AllOnesWithLargeFlag;
            }
            else if (header.UnpackedSize == 0xFFFFFFFF && !header.HasLargeSize)
            {
                // Raw 32-bit UNP_SIZE maxed out without LARGE flag.
                // Known group: QCF
                HasCustomPackerHeaders = true;
                CustomPackerDetected = CustomPackerType.MaxUint32WithoutLargeFlag;
            }
        }

        // Store first file's compression settings
        if (CompressionMethod == null)
        {
            CompressionMethod = header.CompressionMethod;
            DictionarySize = header.DictionarySizeKB;
            RARVersion = header.UnpackVersion;
            HasLargeFiles = header.HasLargeSize;
            HasUnicodeNames = header.HasUnicodeName;
            HasExtendedTime = header.HasExtendedTime;

            // Capture HIGH values from first LARGE file header
            if (header.HasLargeSize)
            {
                DetectedHighPackSize = header.HighPackSize;
                DetectedHighUnpSize = header.HighUnpSize;
            }
        }

        // Capture Host OS and file attributes from first file header
        DetectedHostOS ??= header.HostOS;
        DetectedFileAttributes ??= header.FileAttributes;

        // Capture timestamp precision from first file header
        FileMtimePrecision ??= header.MtimePrecision;
        FileCtimePrecision ??= header.CtimePrecision;
        FileAtimePrecision ??= header.AtimePrecision;

        // Add archive entry
        if (!string.IsNullOrEmpty(header.FileName))
        {
            AddArchiveEntry(
                header.FileName,
                header.IsDirectory,
                header.FileCrc,
                header.Flags,
                header.ModifiedTime,
                header.CreationTime,
                header.AccessTime);
        }
    }

    private void ProcessServiceBlock(RARBlockReadResult block, RARHeaderReader rarReader)
    {
        var serviceInfo = block.ServiceBlockInfo;
        if (serviceInfo == null)
        {
            return;
        }

        // Only process CMT (comment) blocks
        if (!string.Equals(serviceInfo.SubType, "CMT", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Capture CMT-specific metadata for reconstruction
        CmtHostOS = serviceInfo.HostOS;
        CmtFileTimeDOS = serviceInfo.FileTimeDOS;
        CmtFileAttributes = serviceInfo.FileAttributes;

        // Capture CMT timestamp precision
        CmtMtimePrecision = serviceInfo.MtimePrecision;
        CmtCtimePrecision = serviceInfo.CtimePrecision;
        CmtAtimePrecision = serviceInfo.AtimePrecision;

        // Read the comment data (compressed or stored)
        byte[]? commentData = rarReader.ReadServiceBlockData(block);
        if (commentData == null || commentData.Length == 0)
        {
            return;
        }

        serviceInfo.RawData = commentData;

        // Store compressed CMT data for Phase 1 brute-force comparison
        CmtCompressedData = commentData;
        CmtCompressionMethod = serviceInfo.CompressionMethod;

        // Try to extract comment text
        if (serviceInfo.IsStored)
        {
            // Stored (uncompressed) comment - decode directly
            ArchiveCommentBytes = commentData;
            try
            {
                ArchiveComment = Encoding.UTF8.GetString(commentData);
            }
            catch
            {
                // Try with default encoding
                try
                {
                    ArchiveComment = Encoding.Default.GetString(commentData);
                }
                catch
                {
                    // Failed to decode
                }
            }
        }
        else
        {
            // Compressed comment - use native decompression
            (ArchiveComment, ArchiveCommentBytes) = TryNativeDecompressComment(serviceInfo, commentData);
        }
    }

    private static (string? Comment, byte[]? Bytes) TryNativeDecompressComment(RARServiceBlockInfo serviceInfo, byte[] compressedData)
    {
        try
        {
            // Get the uncompressed size from the service block info
            int uncompressedSize = (int)serviceInfo.UnpackedSize;
            if (uncompressedSize <= 0 || uncompressedSize > 1024 * 1024) // Sanity check: max 1MB
                return (null, null);

            // Use native decompressor to get raw bytes
            byte[]? rawBytes = RARDecompressor.DecompressCommentBytes(
                compressedData,
                uncompressedSize,
                serviceInfo.CompressionMethod,
                isRAR5: false); // SRR files typically use RAR4 format

            if (rawBytes == null)
                return (null, null);

            // Convert bytes to string for display (with TrimEnd for readability)
            string? comment = null;
            try
            {
                comment = Encoding.UTF8.GetString(rawBytes).TrimEnd('\0');
            }
            catch
            {
                try
                {
                    comment = Encoding.Default.GetString(rawBytes).TrimEnd('\0');
                }
                catch { }
            }

            return (comment, rawBytes);
        }
        catch
        {
            // Native decompression failed
            return (null, null);
        }
    }

    private void ProcessRar5ArchiveHeader(RAR5ArchiveInfo info)
    {
        IsVolumeArchive ??= info.IsVolume;
        IsSolidArchive ??= info.IsSolid;
        HasRecoveryRecord ??= info.HasRecoveryRecord;
        HasNewVolumeNaming ??= info.HasVolumeNumber; // RAR5 uses volume number field
    }

    private void ProcessRar5FileHeader(RAR5FileInfo info)
    {
        // Store first file's compression settings
        if (CompressionMethod == null)
        {
            // RAR5 compression method: 0=store, 1-5=compression levels
            CompressionMethod = info.CompressionMethod == 0 ? 0x30 : 0x30 + info.CompressionMethod;
            DictionarySize = info.DictionarySizeKB;
            RARVersion = 50;
        }

        // Add archive entry
        if (!string.IsNullOrEmpty(info.FileName))
        {
            // RAR5 uses split before/after flags in header flags, not file flags
            var flags = RARFileFlags.None;
            if (info.IsSplitBefore) flags |= RARFileFlags.SplitBefore;
            if (info.IsSplitAfter) flags |= RARFileFlags.SplitAfter;

            // Convert Unix timestamp to DateTime if present
            DateTime? modifiedTime = null;
            if (info.ModificationTime.HasValue)
            {
                modifiedTime = DateTimeOffset.FromUnixTimeSeconds(info.ModificationTime.Value).LocalDateTime;
            }

            AddArchiveEntry(
                info.FileName,
                info.IsDirectory,
                info.FileCrc,
                flags,
                modifiedTime,
                null, // RAR5 doesn't store creation time in basic header
                null); // RAR5 doesn't store access time in basic header
        }
    }

    private void ProcessRar5ServiceBlock(RAR5BlockReadResult block, RAR5HeaderReader rarReader)
    {
        var serviceInfo = block.ServiceBlockInfo;
        if (serviceInfo == null)
        {
            return;
        }

        // Only process CMT (comment) blocks
        if (!string.Equals(serviceInfo.SubType, "CMT", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Read the comment data
        byte[]? commentData = rarReader.ReadServiceBlockData(block);
        if (commentData == null || commentData.Length == 0)
        {
            return;
        }

        // Try to extract comment text
        if (serviceInfo.IsStored)
        {
            // Stored (uncompressed) comment - decode directly
            ArchiveCommentBytes = commentData;
            try
            {
                ArchiveComment = Encoding.UTF8.GetString(commentData).TrimEnd('\0');
            }
            catch
            {
                // Try with default encoding
                try
                {
                    ArchiveComment = Encoding.Default.GetString(commentData).TrimEnd('\0');
                }
                catch
                {
                    // Failed to decode
                }
            }
        }
        else
        {
            // Compressed comment - use native RAR5 decompression
            (ArchiveComment, ArchiveCommentBytes) = TryNativeDecompressRar5Comment(serviceInfo, commentData);
        }
    }

    private static (string? Comment, byte[]? Bytes) TryNativeDecompressRar5Comment(RAR5ServiceBlockInfo serviceInfo, byte[] compressedData)
    {
        try
        {
            // Get the uncompressed size from the service block info
            int uncompressedSize = (int)serviceInfo.UnpackedSize;
            if (uncompressedSize <= 0 || uncompressedSize > 1024 * 1024) // Sanity check: max 1MB
                return (null, null);

            // Map RAR5 method to RARMethod enum (RAR5 method 0=store, 1-5=compression)
            byte method = (byte)(serviceInfo.CompressionMethod == 0 ? 0x30 : 0x30 + serviceInfo.CompressionMethod);

            // Use native decompressor with RAR5 version to get raw bytes
            byte[]? rawBytes = RARDecompressor.DecompressCommentBytes(
                compressedData,
                uncompressedSize,
                method,
                isRAR5: true);

            if (rawBytes == null)
                return (null, null);

            // Convert bytes to string for display (with TrimEnd for readability)
            string? comment = null;
            try
            {
                comment = Encoding.UTF8.GetString(rawBytes).TrimEnd('\0');
            }
            catch
            {
                try
                {
                    comment = Encoding.Default.GetString(rawBytes).TrimEnd('\0');
                }
                catch { }
            }

            return (comment, rawBytes);
        }
        catch
        {
            // Native decompression failed
            return (null, null);
        }
    }

    private void AddArchiveEntry(string rawName, bool isDirectory, uint? fileCrc, RARFileFlags flags,
        DateTime? modifiedTime, DateTime? creationTime, DateTime? accessTime)
    {
        string? normalized = NormalizeArchivePath(rawName);
        if (string.IsNullOrEmpty(normalized)) return;

        if (isDirectory)
        {
            ArchivedDirectories.Add(normalized);
            SetDirectoryTimes(normalized, modifiedTime, creationTime, accessTime);
            return;
        }

        ArchivedFiles.Add(normalized);

        bool overwriteTimes = false;
        if (fileCrc.HasValue)
        {
            string crcString = fileCrc.Value.ToString("x8");
            bool newHasSplitAfter = (flags & RARFileFlags.SplitAfter) != 0;
            if (!ArchivedFileCrcs.TryGetValue(normalized, out _))
            {
                ArchivedFileCrcs[normalized] = crcString;
                ArchivedFileCrcFlags[normalized] = flags;
                overwriteTimes = true;
            }
            else
            {
                var existingFlags = ArchivedFileCrcFlags.TryGetValue(normalized, out var storedFlags) ? storedFlags : RARFileFlags.None;
                bool existingHasSplitAfter = (existingFlags & RARFileFlags.SplitAfter) != 0;

                if (existingHasSplitAfter && !newHasSplitAfter)
                {
                    ArchivedFileCrcs[normalized] = crcString;
                    ArchivedFileCrcFlags[normalized] = flags;
                    overwriteTimes = true;
                }
            }
        }

        SetFileTimes(normalized, modifiedTime, creationTime, accessTime, overwriteTimes);
    }

    private void SetDirectoryTimes(string path, DateTime? modifiedTime, DateTime? creationTime, DateTime? accessTime)
    {
        if (modifiedTime.HasValue)
            ArchivedDirectoryTimestamps[path] = modifiedTime.Value;
        if (creationTime.HasValue)
            ArchivedDirectoryCreationTimes[path] = creationTime.Value;
        if (accessTime.HasValue)
            ArchivedDirectoryAccessTimes[path] = accessTime.Value;
    }

    private void SetFileTimes(string path, DateTime? modifiedTime, DateTime? creationTime, DateTime? accessTime, bool overwrite)
    {
        if (modifiedTime.HasValue && (overwrite || !ArchivedFileTimestamps.ContainsKey(path)))
            ArchivedFileTimestamps[path] = modifiedTime.Value;
        if (creationTime.HasValue && (overwrite || !ArchivedFileCreationTimes.ContainsKey(path)))
            ArchivedFileCreationTimes[path] = creationTime.Value;
        if (accessTime.HasValue && (overwrite || !ArchivedFileAccessTimes.ContainsKey(path)))
            ArchivedFileAccessTimes[path] = accessTime.Value;
    }

    private static string? NormalizeArchivePath(string path)
    {
        string normalized = path.Trim();
        if (normalized.Length == 0) return null;

        normalized = normalized.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        while (normalized.StartsWith("." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }
        normalized = normalized.TrimStart(Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
        if (normalized.Length == 0) return null;

        string[] parts = normalized.Split(Path.DirectorySeparatorChar);
        foreach (string part in parts)
        {
            if (part == "." || part == "..") return null;
        }

        return normalized;
    }

    private void CalculateVolumeSizeBytes()
    {
        if (RarVolumeSizes.Count == 0) return;

        Dictionary<long, int> counts = [];
        foreach (long size in RarVolumeSizes)
        {
            counts.TryGetValue(size, out int count);
            counts[size] = count + 1;
        }

        long bestSize = 0;
        int bestCount = 0;
        foreach (var entry in counts)
        {
            if (entry.Value > bestCount || (entry.Value == bestCount && entry.Key > bestSize))
            {
                bestSize = entry.Key;
                bestCount = entry.Value;
            }
        }

        if (bestSize > 0)
        {
            VolumeSizeBytes = bestSize;
        }
    }

    /// <summary>
    /// Extracts a stored file from the SRR archive to the specified output directory.
    /// </summary>
    /// <param name="srrFilePath">The path to the SRR file containing the stored data.</param>
    /// <param name="outputDirectory">The directory to extract the file to.</param>
    /// <param name="match">A predicate function to match the desired file by name.</param>
    /// <returns>The path to the extracted file, or <c>null</c> if no matching file was found.</returns>
    /// <exception cref="ArgumentException">Thrown when required parameters are null or empty.</exception>
    /// <exception cref="InvalidDataException">Thrown when stored file data is corrupted or out of bounds.</exception>
    public string? ExtractStoredFile(string srrFilePath, string outputDirectory, Func<string, bool> match)
    {
        if (string.IsNullOrWhiteSpace(srrFilePath))
            throw new ArgumentException("SRR file path is required.", nameof(srrFilePath));
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("Output directory is required.", nameof(outputDirectory));
        ArgumentNullException.ThrowIfNull(match);

        SrrStoredFileBlock? storedFile = null;
        foreach (var stored in StoredFiles)
        {
            if (match(stored.FileName))
            {
                storedFile = stored;
                break;
            }
        }

        if (storedFile == null) return null;

        string safeName = Path.GetFileName(storedFile.FileName);
        if (string.IsNullOrEmpty(safeName)) return null;

        Directory.CreateDirectory(outputDirectory);
        string outputPath = Path.Combine(outputDirectory, safeName);

        using FileStream fs = new(srrFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long dataOffset = storedFile.DataOffset;
        long dataLength = storedFile.FileLength;

        if (dataOffset < 0 || dataOffset > fs.Length)
            throw new InvalidDataException("Stored file data offset is outside the SRR file bounds.");

        long dataEnd = dataOffset + dataLength;
        if (dataEnd < dataOffset || dataEnd > fs.Length)
            throw new InvalidDataException("Stored file length exceeds SRR file bounds.");

        fs.Seek(dataOffset, SeekOrigin.Begin);
        using FileStream output = new(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        CopyBytes(fs, output, dataLength);

        return outputPath;
    }

    private static void CopyBytes(Stream input, Stream output, long bytesToCopy)
    {
        byte[] buffer = new byte[81920];
        long remaining = bytesToCopy;

        while (remaining > 0)
        {
            int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0)
                throw new EndOfStreamException("Unexpected end of SRR file while reading stored file data.");
            output.Write(buffer, 0, read);
            remaining -= read;
        }
    }
}
