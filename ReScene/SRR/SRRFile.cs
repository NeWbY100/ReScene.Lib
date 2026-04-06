using ReScene.RAR;

namespace ReScene.SRR;

/// <summary>
/// Data model for SRR (Scene Release Reconstruction) files.
/// </summary>
public class SRRFile
{
    /// <summary>
    /// Gets the SRR file header block.
    /// </summary>
    public SrrHeaderBlock? HeaderBlock
    {
        get; internal set;
    }

    /// <summary>
    /// Gets the OSO hash blocks from the SRR file.
    /// </summary>
    public List<SrrOsoHashBlock> OsoHashBlocks { get; internal set; } = [];

    /// <summary>
    /// Gets the RAR padding blocks from the SRR file.
    /// </summary>
    public List<SrrRarPaddingBlock> RarPaddingBlocks { get; internal set; } = [];

    /// <summary>
    /// Gets the RAR file reference blocks from the SRR file.
    /// </summary>
    public List<SrrRarFileBlock> RarFiles { get; internal set; } = [];

    /// <summary>
    /// Gets the stored file blocks (SFV, NFO, etc.) from the SRR file.
    /// </summary>
    public List<SrrStoredFileBlock> StoredFiles { get; internal set; } = [];

    /// <summary>
    /// Gets the set of archived file paths (normalized, case-insensitive).
    /// </summary>
    public HashSet<string> ArchivedFiles { get; internal set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the set of archived directory paths (normalized, case-insensitive).
    /// </summary>
    public HashSet<string> ArchivedDirectories { get; internal set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets directory modification times keyed by normalized path.
    /// </summary>
    public Dictionary<string, DateTime> ArchivedDirectoryTimestamps { get; internal set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets directory creation times keyed by normalized path.
    /// </summary>
    public Dictionary<string, DateTime> ArchivedDirectoryCreationTimes { get; internal set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets directory access times keyed by normalized path.
    /// </summary>
    public Dictionary<string, DateTime> ArchivedDirectoryAccessTimes { get; internal set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets file modification times keyed by normalized path.
    /// </summary>
    public Dictionary<string, DateTime> ArchivedFileTimestamps { get; internal set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets file creation times keyed by normalized path.
    /// </summary>
    public Dictionary<string, DateTime> ArchivedFileCreationTimes { get; internal set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets file access times keyed by normalized path.
    /// </summary>
    public Dictionary<string, DateTime> ArchivedFileAccessTimes { get; internal set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets file CRC32 values (as hex strings) keyed by normalized path.
    /// </summary>
    public Dictionary<string, string> ArchivedFileCrcs { get; internal set; } = new(StringComparer.OrdinalIgnoreCase);

    internal Dictionary<string, RARFileFlags> ArchivedFileCrcFlags { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the calculated RAR volume sizes in bytes for each volume.
    /// </summary>
    public List<long> RarVolumeSizes { get; internal set; } = [];

    /// <summary>
    /// Gets the most common volume size in bytes (for multi-volume archives).
    /// </summary>
    public long? VolumeSizeBytes
    {
        get; internal set;
    }

    /// <summary>
    /// Gets the compression method from the first file header (0x30=Store, 0x31-0x35=Fastest..Best).
    /// </summary>
    public int? CompressionMethod
    {
        get; internal set;
    }

    /// <summary>
    /// Gets the dictionary size in kilobytes from the first file header.
    /// </summary>
    public int? DictionarySize
    {
        get; internal set;
    }

    /// <summary>
    /// Gets whether the archive uses solid compression.
    /// </summary>
    public bool? IsSolidArchive
    {
        get; internal set;
    }

    /// <summary>
    /// Gets whether the archive is a multi-volume archive.
    /// </summary>
    public bool? IsVolumeArchive
    {
        get; internal set;
    }

    /// <summary>
    /// Gets whether the archive contains a recovery record.
    /// </summary>
    public bool? HasRecoveryRecord
    {
        get; internal set;
    }

    /// <summary>
    /// Gets the RAR format version (e.g. 29 for RAR 2.9/3.x, 50 for RAR 5.0).
    /// </summary>
    public int? RARVersion
    {
        get; internal set;
    }

    /// <summary>
    /// Gets whether the archive uses new-style volume naming (name.part01.rar).
    /// </summary>
    public bool? HasNewVolumeNaming
    {
        get; internal set;
    }

    /// <summary>
    /// Gets whether the first volume flag is set in the archive header.
    /// </summary>
    public bool? HasFirstVolumeFlag
    {
        get; internal set;
    }

    /// <summary>
    /// Gets whether the archive has encrypted headers.
    /// </summary>
    public bool? HasEncryptedHeaders
    {
        get; internal set;
    }

    /// <summary>
    /// Gets whether the archive contains files larger than 2 GB (LARGE flag).
    /// </summary>
    public bool? HasLargeFiles
    {
        get; internal set;
    }

    /// <summary>
    /// Gets whether the archive contains files with Unicode filenames.
    /// </summary>
    public bool? HasUnicodeNames
    {
        get; internal set;
    }

    /// <summary>
    /// Gets whether the archive file headers contain extended time fields.
    /// </summary>
    public bool? HasExtendedTime
    {
        get; internal set;
    }

    /// <summary>
    /// Upper 32 bits of packed size from first LARGE file header.
    /// </summary>
    public uint? DetectedHighPackSize
    {
        get; internal set;
    }

    /// <summary>
    /// Upper 32 bits of unpacked size from first LARGE file header.
    /// </summary>
    public uint? DetectedHighUnpSize
    {
        get; internal set;
    }

    /// <summary>
    /// Gets the number of RAR header CRC mismatches detected during parsing.
    /// </summary>
    public int HeaderCrcMismatches
    {
        get; internal set;
    }

    // Custom RAR packer detection

    /// <summary>
    /// True if any file header has sentinel unpacked_size values indicating a custom RAR packer (not WinRAR).
    /// Known groups: RELOADED, HI2U, 0x0007, 0x0815, QCF.
    /// </summary>
    public bool HasCustomPackerHeaders
    {
        get; internal set;
    }

    /// <summary>
    /// The type of custom packer anomaly detected, if any.
    /// </summary>
    public CustomPackerType CustomPackerDetected
    {
        get; internal set;
    }

    /// <summary>
    /// Gets the decoded archive comment text extracted from the CMT sub-block.
    /// </summary>
    public string? ArchiveComment
    {
        get; internal set;
    }

    /// <summary>
    /// Raw archive comment bytes (for exact reconstruction).
    /// </summary>
    public byte[]? ArchiveCommentBytes
    {
        get; internal set;
    }

    /// <summary>
    /// Raw CMT block compressed data (for Phase 1 brute-force comparison).
    /// </summary>
    public byte[]? CmtCompressedData
    {
        get; internal set;
    }

    /// <summary>
    /// CMT block compression method (0x30=Store, 0x31-0x35=Compressed).
    /// </summary>
    public byte? CmtCompressionMethod
    {
        get; internal set;
    }

    // Host OS and timestamp settings detected from headers

    /// <summary>
    /// Host OS from file headers (0=MS-DOS, 1=OS/2, 2=Windows, 3=Unix).
    /// </summary>
    public byte? DetectedHostOS
    {
        get; internal set;
    }

    /// <summary>
    /// Host OS name for display.
    /// </summary>
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

    /// <summary>
    /// File attributes from first file header (for patching).
    /// </summary>
    public uint? DetectedFileAttributes
    {
        get; internal set;
    }

    /// <summary>
    /// Host OS from CMT service block (may differ from file headers).
    /// </summary>
    public byte? CmtHostOS
    {
        get; internal set;
    }

    /// <summary>
    /// CMT Host OS name for display.
    /// </summary>
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

    /// <summary>
    /// Raw DOS file time from CMT block (0 = zeroed/no timestamp).
    /// </summary>
    public uint? CmtFileTimeDOS
    {
        get; internal set;
    }

    /// <summary>
    /// True if CMT block has zeroed file time (suggests -ts- or similar option).
    /// </summary>
    public bool CmtHasZeroedFileTime => CmtFileTimeDOS == 0;

    /// <summary>
    /// File attributes from CMT block.
    /// </summary>
    public uint? CmtFileAttributes
    {
        get; internal set;
    }

    /// <summary>
    /// Whether CMT timestamp appears to be current time vs zeroed.
    /// </summary>
    public string CmtTimestampMode => CmtFileTimeDOS switch
    {
        null => "Unknown",
        0 => "Zeroed (no timestamp)",
        _ => "Preserved (has timestamp)"
    };

    // ===== Timestamp Precision (from file headers) =====

    /// <summary>
    /// Modification time precision from file headers (maps to -tsm0 through -tsm4).
    /// </summary>
    public TimestampPrecision? FileMtimePrecision
    {
        get; internal set;
    }

    /// <summary>
    /// Creation time precision from file headers (maps to -tsc0 through -tsc4).
    /// </summary>
    public TimestampPrecision? FileCtimePrecision
    {
        get; internal set;
    }

    /// <summary>
    /// Access time precision from file headers (maps to -tsa0 through -tsa4).
    /// </summary>
    public TimestampPrecision? FileAtimePrecision
    {
        get; internal set;
    }

    // ===== Timestamp Precision (from CMT service block) =====

    /// <summary>
    /// Modification time precision from CMT block (maps to -tsm0 through -tsm4).
    /// </summary>
    public TimestampPrecision? CmtMtimePrecision
    {
        get; internal set;
    }

    /// <summary>
    /// Creation time precision from CMT block (maps to -tsc0 through -tsc4).
    /// </summary>
    public TimestampPrecision? CmtCtimePrecision
    {
        get; internal set;
    }

    /// <summary>
    /// Access time precision from CMT block (maps to -tsa0 through -tsa4).
    /// </summary>
    public TimestampPrecision? CmtAtimePrecision
    {
        get; internal set;
    }

    /// <summary>
    /// Loads and parses an SRR file from the specified path.
    /// </summary>
    /// <param name="filePath">
    /// The path to the SRR file.
    /// </param>
    /// <returns>
    /// A parsed <see cref="SRRFile"/> instance containing all extracted metadata.
    /// </returns>
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
            if (fs.Position + 7 > fs.Length)
            {
                break;
            }

            long startPos = fs.Position;
            ushort crc = reader.ReadUInt16();
            byte typeRaw = reader.ReadByte();
            ushort flags = reader.ReadUInt16();
            ushort headerSize = reader.ReadUInt16();

            // Check if this is an SRR block type
            if (!SRRFileParser.IsSrrBlockType(typeRaw))
            {
                // Unknown block, skip it
                fs.Seek(startPos, SeekOrigin.Begin);
                break;
            }

            SRRBlockType type = (SRRBlockType)typeRaw;

            uint addSize = 0;
            if ((flags & (ushort)SRRBlockFlags.LongBlock) != 0 || type == SRRBlockType.StoredFile)
            {
                if (fs.Position + 4 > fs.Length)
                {
                    break;
                }

                addSize = reader.ReadUInt32();
            }

            if (headerSize < 7)
            {
                break;
            }

            long blockEndPos = startPos + headerSize + addSize;
            if (blockEndPos <= startPos || blockEndPos > fs.Length)
            {
                break;
            }

            switch (type)
            {
                case SRRBlockType.Header:
                    srr.HeaderBlock = SRRFileParser.ParseHeaderBlock(reader, fs, startPos, crc, type, flags, headerSize);
                    fs.Seek(blockEndPos, SeekOrigin.Begin);
                    break;

                case SRRBlockType.StoredFile:
                    SrrStoredFileBlock? storedBlock = SRRFileParser.ParseStoredFileBlock(reader, fs, startPos, crc, type, flags, headerSize, addSize);
                    if (storedBlock == null)
                    {
                        goto exitLoop;
                    }

                    srr.StoredFiles.Add(storedBlock);
                    fs.Seek(blockEndPos, SeekOrigin.Begin);
                    break;

                case SRRBlockType.OsoHash:
                    SrrOsoHashBlock? osoBlock = SRRFileParser.ParseOsoHashBlock(reader, fs, startPos, crc, type, flags, headerSize);
                    if (osoBlock != null)
                    {
                        srr.OsoHashBlocks.Add(osoBlock);
                    }

                    fs.Seek(blockEndPos, SeekOrigin.Begin);
                    break;

                case SRRBlockType.RarPadding:
                    SrrRarPaddingBlock? paddingBlock = SRRFileParser.ParseRarPaddingBlock(reader, fs, startPos, crc, type, flags, headerSize, addSize);
                    if (paddingBlock != null)
                    {
                        srr.RarPaddingBlocks.Add(paddingBlock);
                    }

                    fs.Seek(blockEndPos, SeekOrigin.Begin);
                    break;

                case SRRBlockType.RarFile:
                    SrrRarFileBlock? rarBlock = SRRFileParser.ParseRarFileBlock(reader, fs, startPos, crc, type, flags, headerSize, addSize);
                    if (rarBlock == null)
                    {
                        goto exitLoop;
                    }

                    srr.RarFiles.Add(rarBlock);

                    // Parse embedded RAR headers that follow
                    long volumeTotalSize = SRRFileParser.ParseEmbeddedRarHeaders(reader, fs, srr);
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

        SRRFileParser.CalculateVolumeSizeBytes(srr);
        return srr;
    }

    /// <summary>
    /// Extracts a stored file from the SRR archive to the specified output directory.
    /// </summary>
    /// <param name="srrFilePath">
    /// The path to the SRR file containing the stored data.
    /// </param>
    /// <param name="outputDirectory">
    /// The directory to extract the file to.
    /// </param>
    /// <param name="match">
    /// A predicate function to match the desired file by name.
    /// </param>
    /// <returns>
    /// The path to the extracted file, or <c>null</c> if no matching file was found.
    /// </returns>
    /// <exception cref="ArgumentException">Thrown when required parameters are null or empty.</exception>
    /// <exception cref="InvalidDataException">Thrown when stored file data is corrupted or out of bounds.</exception>
    public string? ExtractStoredFile(string srrFilePath, string outputDirectory, Func<string, bool> match)
    {
        if (string.IsNullOrWhiteSpace(srrFilePath))
        {
            throw new ArgumentException("SRR file path is required.", nameof(srrFilePath));
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("Output directory is required.", nameof(outputDirectory));
        }

        ArgumentNullException.ThrowIfNull(match);

        SrrStoredFileBlock? storedFile = null;
        foreach (SrrStoredFileBlock stored in StoredFiles)
        {
            if (match(stored.FileName))
            {
                storedFile = stored;
                break;
            }
        }

        if (storedFile == null)
        {
            return null;
        }

        string safeName = Path.GetFileName(storedFile.FileName);
        if (string.IsNullOrEmpty(safeName))
        {
            return null;
        }

        Directory.CreateDirectory(outputDirectory);
        string outputPath = Path.Combine(outputDirectory, safeName);

        using FileStream fs = new(srrFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long dataOffset = storedFile.DataOffset;
        long dataLength = storedFile.FileLength;

        if (dataOffset < 0 || dataOffset > fs.Length)
        {
            throw new InvalidDataException("Stored file data offset is outside the SRR file bounds.");
        }

        long dataEnd = dataOffset + dataLength;
        if (dataEnd < dataOffset || dataEnd > fs.Length)
        {
            throw new InvalidDataException("Stored file length exceeds SRR file bounds.");
        }

        fs.Seek(dataOffset, SeekOrigin.Begin);
        using FileStream output = new(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        StreamUtilities.CopyBytesStrict(fs, output, dataLength,
            "Unexpected end of SRR file while reading stored file data.");

        return outputPath;
    }
}
