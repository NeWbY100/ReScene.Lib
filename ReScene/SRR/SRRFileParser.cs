using System.Text;
using ReScene.RAR;
using ReScene.RAR.Decompression;

namespace ReScene.SRR;

/// <summary>
/// Contains all parsing and processing logic for SRR files.
/// </summary>
internal static class SRRFileParser
{
    internal static bool IsSRRBlockType(byte type) => type is 0x69 or 0x6A or 0x6B or 0x6C or 0x71;

    internal static SRRHeaderBlock ParseHeaderBlock(BinaryReader reader, FileStream fs,
        long startPos, ushort crc, SRRBlockType type, ushort flags, ushort headerSize)
    {
        var block = new SRRHeaderBlock
        {
            CRC = crc,
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

    internal static SRROsoHashBlock? ParseOSOHashBlock(BinaryReader reader, FileStream fs,
        long startPos, ushort crc, SRRBlockType type, ushort flags, ushort headerSize)
    {
        long headerEnd = startPos + headerSize;

        // OSO hash block format (pyrescene order): 8 bytes file size + 8 bytes hash + 2 bytes name length + name
        if (fs.Position + 18 > headerEnd)
        {
            return null;
        }

        ulong fileSize = reader.ReadUInt64();
        byte[] osoHash = reader.ReadBytes(8);

        if (fs.Position + 2 > headerEnd)
        {
            return null;
        }

        ushort nameLen = reader.ReadUInt16();
        if (fs.Position + nameLen > headerEnd || nameLen == 0)
        {
            return null;
        }

        byte[] nameBytes = reader.ReadBytes(nameLen);
        string fileName = Encoding.UTF8.GetString(nameBytes);

        return new SRROsoHashBlock
        {
            CRC = crc,
            BlockType = type,
            Flags = flags,
            HeaderSize = headerSize,
            BlockPosition = startPos,
            FileName = fileName,
            FileSize = fileSize,
            OSOHash = osoHash
        };
    }

    internal static SRRRarPaddingBlock? ParseRarPaddingBlock(BinaryReader reader, FileStream fs,
        long startPos, ushort crc, SRRBlockType type, ushort flags, ushort headerSize, uint addSize)
    {
        long headerEnd = startPos + headerSize;

        // RAR padding block format: 2 bytes name length + RAR filename
        if (fs.Position + 2 > headerEnd)
        {
            return null;
        }

        ushort nameLen = reader.ReadUInt16();
        if (fs.Position + nameLen > headerEnd)
        {
            return null;
        }

        string rarFileName = string.Empty;
        if (nameLen > 0)
        {
            byte[] nameBytes = reader.ReadBytes(nameLen);
            rarFileName = Encoding.UTF8.GetString(nameBytes);
        }

        return new SRRRarPaddingBlock
        {
            CRC = crc,
            BlockType = type,
            Flags = flags,
            HeaderSize = headerSize,
            BlockPosition = startPos,
            AddSize = addSize,
            RARFileName = rarFileName,
            PaddingSize = addSize
        };
    }

    internal static SRRStoredFileBlock? ParseStoredFileBlock(BinaryReader reader, FileStream fs,
        long startPos, ushort crc, SRRBlockType type, ushort flags, ushort headerSize, uint addSize)
    {
        const int minStoredHeaderSize = 7 + 4 + 2;
        if (headerSize < minStoredHeaderSize)
        {
            return null;
        }

        long headerEnd = startPos + headerSize;
        if (headerEnd <= startPos || headerEnd > fs.Length)
        {
            return null;
        }

        if (fs.Position + 2 > headerEnd)
        {
            return null;
        }

        ushort nameLen = reader.ReadUInt16();
        if (fs.Position + nameLen > headerEnd || fs.Position + nameLen > fs.Length)
        {
            return null;
        }

        byte[] nameBytes = reader.ReadBytes(nameLen);
        string fileName = Encoding.UTF8.GetString(nameBytes);

        long dataOffset = startPos + headerSize;
        if (dataOffset < startPos || dataOffset > fs.Length)
        {
            return null;
        }

        return new SRRStoredFileBlock
        {
            CRC = crc,
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

    internal static SRRRarFileBlock? ParseRarFileBlock(BinaryReader reader, FileStream fs,
        long startPos, ushort crc, SRRBlockType type, ushort flags, ushort headerSize, uint addSize)
    {
        // Guard the name-length read: a RARFile block with headerSize 7 and no
        // data at EOF passes Load's guards but leaves nothing to read here, so
        // reading without this check threw EndOfStreamException on truncated SRRs.
        if (fs.Position + 2 > fs.Length)
        {
            return null;
        }

        ushort nameLen = reader.ReadUInt16();
        if (fs.Position + nameLen > fs.Length)
        {
            return null;
        }

        byte[] nameBytes = reader.ReadBytes(nameLen);
        string fileName = Encoding.UTF8.GetString(nameBytes);

        return new SRRRarFileBlock
        {
            CRC = crc,
            BlockType = type,
            Flags = flags,
            HeaderSize = headerSize,
            BlockPosition = startPos,
            AddSize = addSize,
            FileName = fileName
        };
    }

    internal static long ParseEmbeddedRarHeaders(BinaryReader reader, FileStream fs, SRRFile srr)
    {
        // Check if this is RAR5 format by looking for the marker
        if (RARUtils.IsRar5Marker(fs))
        {
            return ParseEmbeddedRar5Headers(fs, srr);
        }

        return ParseEmbeddedRar4Headers(reader, fs, srr);
    }

    internal static long ParseEmbeddedRar4Headers(BinaryReader reader, FileStream fs, SRRFile srr)
    {
        long volumeTotalSize = 0;
        var rarReader = new RARHeaderReader(reader);

        while (fs.Position < fs.Length)
        {
            // Check if we've hit another SRR block
            byte? peekType = rarReader.PeekBlockType();
            if (peekType == null)
            {
                break;
            }

            if (IsSRRBlockType(peekType.Value))
            {
                break;
            }

            // Read the RAR block
            RARBlockReadResult? block = rarReader.ReadBlock(parseContents: true);
            if (block == null)
            {
                fs.Seek(fs.Length, SeekOrigin.Begin);
                break;
            }

            // Track CRC mismatches
            if (!block.CRCValid)
            {
                srr.HeaderCRCMismatches++;
            }

            // Calculate actual RAR volume size (header + packed data for all blocks)
            // For file headers, AddSize = PackedSize (compressed data size)
            volumeTotalSize += block.HeaderSize + block.AddSize;

            // Process archive header
            if (block.ArchiveHeader != null)
            {
                ProcessArchiveHeader(srr, block.ArchiveHeader);
            }

            // Process file header
            if (block.FileHeader != null)
            {
                ProcessFileHeader(srr, block.FileHeader);
            }

            // Process service block (CMT comment, RR recovery, etc.)
            if (block.ServiceBlockInfo != null)
            {
                ProcessServiceBlock(srr, block, rarReader);
            }

            // Skip to next block (headers only for file blocks in SRR)
            rarReader.SkipBlock(block, includeData: block.BlockType != RAR4BlockType.FileHeader);
        }

        return volumeTotalSize;
    }

    internal static long ParseEmbeddedRar5Headers(FileStream fs, SRRFile srr)
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
            if (peekType == null)
            {
                break;
            }
            // SRR blocks have types in 0x69-0x71 range, RAR5 blocks are 0-5
            if (peekType.Value is >= 0x69 and <= 0x71)
            {
                break;
            }

            // Read the RAR5 block
            RAR5BlockReadResult? block = rarReader.ReadBlock();
            if (block == null)
            {
                fs.Seek(fs.Length, SeekOrigin.Begin);
                break;
            }

            // Track CRC mismatches
            if (!block.CRCValid)
            {
                srr.HeaderCRCMismatches++;
            }

            // Calculate RAR5 volume size (CRC + header size vint + header + data)
            // Approximate: 4 (CRC) + 1 (header size vint) + header + data
            volumeTotalSize += 4 + 1 + (long)block.HeaderSize + (long)block.DataSize;

            // Process archive header
            if (block.ArchiveInfo != null)
            {
                ProcessRar5ArchiveHeader(srr, block.ArchiveInfo);
            }

            // Process file header
            if (block.FileInfo != null)
            {
                ProcessRar5FileHeader(srr, block.FileInfo);
            }

            // Process service block (CMT comment, RR recovery, etc.)
            if (block.ServiceBlockInfo != null)
            {
                ProcessRar5ServiceBlock(srr, block, rarReader);
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
                {
                    fs.Position = target;
                }
            }
        }

        return volumeTotalSize;
    }

    internal static void ProcessArchiveHeader(SRRFile srr, RARArchiveHeader header)
    {
        srr.IsVolumeArchive ??= header.IsVolume;
        srr.IsSolidArchive ??= header.IsSolid;
        srr.HasRecoveryRecord ??= header.HasRecoveryRecord;
        srr.HasNewVolumeNaming ??= header.HasNewVolumeNaming;
        srr.HasFirstVolumeFlag ??= header.IsFirstVolume;
        srr.HasEncryptedHeaders ??= header.HasEncryptedHeaders;

        SrrArchiveSet? set = srr.CurrentArchiveSet;
        if (set != null)
        {
            set.IsSolid ??= header.IsSolid;
            set.HasRecoveryRecord ??= header.HasRecoveryRecord;
        }
    }

    internal static void ProcessFileHeader(SRRFile srr, RARFileHeader header)
    {
        // Detect custom RAR packer sentinel values in unpacked_size
        if (!srr.HasCustomPackerHeaders && !header.IsDirectory)
        {
            if (header.UnpackedSize == 0xFFFFFFFFFFFFFFFF)
            {
                // Both low and high 32-bit fields are all ones (LARGE flag set).
                // Known groups: RELOADED, HI2U, 0x0007, 0x0815
                srr.HasCustomPackerHeaders = true;
                srr.CustomPackerDetected = CustomPackerType.AllOnesWithLargeFlag;
            }
            else if (header.UnpackedSize == 0xFFFFFFFF && !header.HasLargeSize)
            {
                // Raw 32-bit UNP_SIZE maxed out without LARGE flag.
                // Known group: QCF
                srr.HasCustomPackerHeaders = true;
                srr.CustomPackerDetected = CustomPackerType.MaxUint32WithoutLargeFlag;
            }
        }

        // Store first file's compression settings
        if (srr.CompressionMethod == null)
        {
            srr.CompressionMethod = header.CompressionMethod;
            srr.DictionarySize = header.DictionarySizeKB;
            srr.RARVersion = header.UnpackVersion;
            srr.HasLargeFiles = header.HasLargeSize;
            srr.HasUnicodeNames = header.HasUnicodeName;
            srr.HasExtendedTime = header.HasExtendedTime;

            // Capture HIGH values from first LARGE file header
            if (header.HasLargeSize)
            {
                srr.DetectedHighPackSize = header.HighPackSize;
                srr.DetectedHighUnpSize = header.HighUnpSize;
            }
        }

        // Capture Host OS and file attributes from first file header
        srr.DetectedHostOS ??= header.HostOS;
        srr.DetectedFileAttributes ??= header.FileAttributes;

        // Capture timestamp precision from first file header
        srr.FileMtimePrecision ??= header.MtimePrecision;
        srr.FileCtimePrecision ??= header.CtimePrecision;
        srr.FileAtimePrecision ??= header.AtimePrecision;

        SrrArchiveSet? set = srr.CurrentArchiveSet;
        if (set != null)
        {
            set.CompressionMethod ??= header.CompressionMethod;
            set.DictionarySize ??= header.DictionarySizeKB;
            set.RARVersion ??= header.UnpackVersion;
            set.HasLargeFiles ??= header.HasLargeSize;
            set.DetectedHostOS ??= header.HostOS;
            set.DetectedFileAttributes ??= header.FileAttributes;
            if (header.HasLargeSize)
            {
                set.DetectedHighPackSize ??= header.HighPackSize;
                set.DetectedHighUnpSize ??= header.HighUnpSize;
            }
        }

        // Add archive entry
        if (!string.IsNullOrEmpty(header.FileName))
        {
            AddArchiveEntry(
                srr,
                header.FileName,
                header.IsDirectory,
                header.FileCRC,
                header.Flags,
                header.ModifiedTime,
                header.CreationTime,
                header.AccessTime);
        }
    }

    internal static void ProcessServiceBlock(SRRFile srr, RARBlockReadResult block, RARHeaderReader rarReader)
    {
        RARServiceBlockInfo? serviceInfo = block.ServiceBlockInfo;
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
        srr.CmtHostOS = serviceInfo.HostOS;
        srr.CmtFileTimeDOS = serviceInfo.FileTimeDOS;
        srr.CmtFileAttributes = serviceInfo.FileAttributes;

        // Capture CMT timestamp precision
        srr.CmtMtimePrecision = serviceInfo.MtimePrecision;
        srr.CmtCtimePrecision = serviceInfo.CtimePrecision;
        srr.CmtAtimePrecision = serviceInfo.AtimePrecision;

        // Read the comment data (compressed or stored)
        byte[]? commentData = rarReader.ReadServiceBlockData(block);
        if (commentData == null || commentData.Length == 0)
        {
            return;
        }

        serviceInfo.RawData = commentData;

        // Store compressed CMT data for Phase 1 brute-force comparison
        srr.CmtCompressedData = commentData;
        srr.CmtCompressionMethod = serviceInfo.CompressionMethod;

        // Try to extract comment text
        if (serviceInfo.IsStored)
        {
            // Stored (uncompressed) comment - decode directly
            srr.ArchiveCommentBytes = commentData;
            srr.ArchiveComment = DecodeText(commentData, trimNulls: false);
        }
        else
        {
            // Compressed comment - use native decompression
            (srr.ArchiveComment, srr.ArchiveCommentBytes) = TryNativeDecompressComment(serviceInfo, commentData);
        }
    }

    internal static (string? Comment, ReadOnlyMemory<byte>? Bytes) TryNativeDecompressComment(RARServiceBlockInfo serviceInfo, byte[] compressedData)
        // RAR4 service blocks store an already RAR4-encoded method byte.
        => TryDecompressComment((int)serviceInfo.UnpackedSize, compressedData, serviceInfo.CompressionMethod, isRar5: false);

    /// <summary>
    /// Shared compressed-comment decode tail for RAR4 and RAR5. The caller supplies a
    /// pre-normalized RAR4-encoded <paramref name="method"/> byte and the
    /// <paramref name="isRar5"/> flag used to select the native decompressor variant.
    /// </summary>
    private static (string? Comment, ReadOnlyMemory<byte>? Bytes) TryDecompressComment(
        int uncompressedSize, byte[] compressedData, byte method, bool isRar5)
    {
        try
        {
            if (uncompressedSize is <= 0 or > (1024 * 1024)) // Sanity check: max 1MB
            {
                return (null, null);
            }

            // Use native decompressor to get raw bytes
            byte[]? rawBytes = RARDecompressor.DecompressCommentBytes(
                compressedData,
                uncompressedSize,
                method,
                isRAR5: isRar5);

            if (rawBytes == null)
            {
                return (null, null);
            }

            // Convert bytes to string for display (with TrimEnd for readability)
            string? comment = DecodeText(rawBytes, trimNulls: true);

            return (comment, rawBytes);
        }
        catch
        {
            // Native decompression failed
            return (null, null);
        }
    }

    internal static void ProcessRar5ArchiveHeader(SRRFile srr, RAR5ArchiveInfo info)
    {
        srr.IsVolumeArchive ??= info.IsVolume;
        srr.IsSolidArchive ??= info.IsSolid;
        srr.HasRecoveryRecord ??= info.HasRecoveryRecord;
        srr.HasNewVolumeNaming ??= info.HasVolumeNumber; // RAR5 uses volume number field
    }

    internal static void ProcessRar5FileHeader(SRRFile srr, RAR5FileInfo info)
    {
        // Store first file's compression settings
        if (srr.CompressionMethod == null)
        {
            // RAR5 compression method: 0=store, 1-5=compression levels
            srr.CompressionMethod = info.CompressionMethod == 0 ? 0x30 : 0x30 + info.CompressionMethod;
            srr.DictionarySize = info.DictionarySizeKB;
            srr.RARVersion = 50;
        }

        SrrArchiveSet? set = srr.CurrentArchiveSet;
        if (set != null)
        {
            set.CompressionMethod ??= info.CompressionMethod == 0 ? 0x30 : 0x30 + info.CompressionMethod;
            set.DictionarySize ??= info.DictionarySizeKB;
            set.RARVersion ??= 50;
            // Note: RAR5 file headers do not expose HostOS, FileAttributes, or HasLargeFiles
            // in the same per-file fields as RAR4, so DetectedHostOS, DetectedFileAttributes,
            // HasLargeFiles, DetectedHighPackSize, and DetectedHighUnpSize are not set per-set
            // for RAR5 (matching the flat-side behavior above where they are also not populated).
        }

        // Add archive entry
        if (!string.IsNullOrEmpty(info.FileName))
        {
            // RAR5 uses split before/after flags in header flags, not file flags
            RARFileFlags flags = RARFileFlags.None;
            if (info.IsSplitBefore)
            {
                flags |= RARFileFlags.SplitBefore;
            }

            if (info.IsSplitAfter)
            {
                flags |= RARFileFlags.SplitAfter;
            }

            // Convert Unix timestamp to DateTime if present
            DateTime? modifiedTime = null;
            if (info.ModificationTime.HasValue)
            {
                modifiedTime = DateTimeOffset.FromUnixTimeSeconds(info.ModificationTime.Value).LocalDateTime;
            }

            AddArchiveEntry(
                srr,
                info.FileName,
                info.IsDirectory,
                info.FileCRC,
                flags,
                modifiedTime,
                null, // RAR5 doesn't store creation time in basic header
                null); // RAR5 doesn't store access time in basic header
        }
    }

    internal static void ProcessRar5ServiceBlock(SRRFile srr, RAR5BlockReadResult block, RAR5HeaderReader rarReader)
    {
        RAR5ServiceBlockInfo? serviceInfo = block.ServiceBlockInfo;
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
            srr.ArchiveCommentBytes = commentData;
            srr.ArchiveComment = DecodeText(commentData, trimNulls: true);
        }
        else
        {
            // Compressed comment - use native RAR5 decompression
            (srr.ArchiveComment, srr.ArchiveCommentBytes) = TryNativeDecompressRar5Comment(serviceInfo, commentData);
        }
    }

    internal static (string? Comment, ReadOnlyMemory<byte>? Bytes) TryNativeDecompressRar5Comment(RAR5ServiceBlockInfo serviceInfo, byte[] compressedData)
    {
        // Map RAR5 method to the RAR4-encoded method byte (RAR5 method 0=store, 1-5=compression).
        byte method = (byte)(serviceInfo.CompressionMethod == 0 ? 0x30 : 0x30 + serviceInfo.CompressionMethod);
        return TryDecompressComment((int)serviceInfo.UnpackedSize, compressedData, method, isRar5: true);
    }

    /// <summary>
    /// Decodes archive-comment bytes as UTF-8 for display. <see cref="Encoding.UTF8"/> replaces
    /// undecodable bytes rather than throwing, so the previous Encoding.Default fallback was
    /// unreachable and has been removed. When <paramref name="trimNulls"/> is set, trailing NUL
    /// padding is stripped.
    /// </summary>
    private static string? DecodeText(ReadOnlySpan<byte> data, bool trimNulls)
    {
        string text = Encoding.UTF8.GetString(data);
        return trimNulls ? text.TrimEnd('\0') : text;
    }

    internal static string? NormalizeArchivePath(string path)
    {
        string normalized = path.Trim();
        if (normalized.Length == 0)
        {
            return null;
        }

        normalized = normalized.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        while (normalized.StartsWith("." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        normalized = normalized.TrimStart(Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
        if (normalized.Length == 0)
        {
            return null;
        }

        string[] parts = normalized.Split(Path.DirectorySeparatorChar);
        foreach (string part in parts)
        {
            if (part is "." or "..")
            {
                return null;
            }
        }

        return normalized;
    }

    internal static void AddArchiveEntry(SRRFile srr, string rawName, bool isDirectory, uint? fileCRC, RARFileFlags flags,
        DateTime? modifiedTime, DateTime? creationTime, DateTime? accessTime)
    {
        string? normalized = NormalizeArchivePath(rawName);
        if (string.IsNullOrEmpty(normalized))
        {
            return;
        }

        SrrArchiveSet? set = srr.CurrentArchiveSet;

        if (isDirectory)
        {
            srr.ArchivedDirectories.Add(normalized);
            SetDirectoryTimes(srr, normalized, modifiedTime, creationTime, accessTime);
            return;
        }

        srr.ArchivedFiles.Add(normalized);

        bool overwriteTimes = false;
        if (fileCRC.HasValue)
        {
            string crcString = fileCRC.Value.ToString("x8");
            bool newHasSplitAfter = (flags & RARFileFlags.SplitAfter) != 0;
            if (!srr.ArchivedFileCrcs.TryGetValue(normalized, out _))
            {
                srr.ArchivedFileCrcs[normalized] = crcString;
                srr.ArchivedFileCRCFlags[normalized] = flags;
                overwriteTimes = true;
            }
            else
            {
                RARFileFlags existingFlags = srr.ArchivedFileCRCFlags.TryGetValue(normalized, out RARFileFlags storedFlags) ? storedFlags : RARFileFlags.None;
                bool existingHasSplitAfter = (existingFlags & RARFileFlags.SplitAfter) != 0;

                if (existingHasSplitAfter && !newHasSplitAfter)
                {
                    srr.ArchivedFileCrcs[normalized] = crcString;
                    srr.ArchivedFileCRCFlags[normalized] = flags;
                    overwriteTimes = true;
                }
            }
        }

        SetFileTimes(srr, normalized, modifiedTime, creationTime, accessTime, overwriteTimes);

        // Track which set this file belongs to; CRC/timestamps are finalized after all headers
        // are parsed via SRRFile.FinalizeArchiveSets() to ensure split-after CRC overrides
        // (applied to the flat dict above) are reflected in the per-set view.
        set?.ArchivedFiles.Add(normalized);
    }

    internal static void SetDirectoryTimes(SRRFile srr, string path, DateTime? modifiedTime, DateTime? creationTime, DateTime? accessTime)
    {
        if (modifiedTime.HasValue)
        {
            srr.ArchivedDirectoryTimestamps[path] = modifiedTime.Value;
        }

        if (creationTime.HasValue)
        {
            srr.ArchivedDirectoryCreationTimes[path] = creationTime.Value;
        }

        if (accessTime.HasValue)
        {
            srr.ArchivedDirectoryAccessTimes[path] = accessTime.Value;
        }
    }

    internal static void SetFileTimes(SRRFile srr, string path, DateTime? modifiedTime, DateTime? creationTime, DateTime? accessTime, bool overwrite)
    {
        if (modifiedTime.HasValue && (overwrite || !srr.ArchivedFileTimestamps.ContainsKey(path)))
        {
            srr.ArchivedFileTimestamps[path] = modifiedTime.Value;
        }

        if (creationTime.HasValue && (overwrite || !srr.ArchivedFileCreationTimes.ContainsKey(path)))
        {
            srr.ArchivedFileCreationTimes[path] = creationTime.Value;
        }

        if (accessTime.HasValue && (overwrite || !srr.ArchivedFileAccessTimes.ContainsKey(path)))
        {
            srr.ArchivedFileAccessTimes[path] = accessTime.Value;
        }
    }

    internal static void CalculateVolumeSizeBytes(SRRFile srr)
    {
        if (srr.RARVolumeSizes.Count == 0)
        {
            return;
        }

        Dictionary<long, int> counts = [];
        foreach (long size in srr.RARVolumeSizes)
        {
            counts.TryGetValue(size, out int count);
            counts[size] = count + 1;
        }

        long bestSize = 0;
        int bestCount = 0;
        foreach (KeyValuePair<long, int> entry in counts)
        {
            if (entry.Value > bestCount || (entry.Value == bestCount && entry.Key > bestSize))
            {
                bestSize = entry.Key;
                bestCount = entry.Value;
            }
        }

        if (bestSize > 0)
        {
            srr.VolumeSizeBytes = bestSize;
        }
    }
}
