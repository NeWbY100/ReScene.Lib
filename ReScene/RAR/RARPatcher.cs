using Force.Crc32;

namespace ReScene.RAR;

/// <summary>
/// Patches RAR 4.x files to modify Host OS, attributes, and other header fields
/// while maintaining valid CRCs.
/// </summary>
internal static class RARPatcher
{
    /// <summary>
    /// Host OS name lookup.
    /// </summary>
    /// <param name="hostOS">
    /// The Host OS byte value from the RAR header.
    /// </param>
    /// <returns>
    /// A human-readable OS name.
    /// </returns>
    public static string GetHostOSName(byte hostOS) => hostOS switch
    {
        (byte)RARHostOs.MsDos => "MS-DOS",
        (byte)RARHostOs.Os2 => "OS/2",
        (byte)RARHostOs.Windows => "Windows",
        (byte)RARHostOs.Unix => "Unix",
        (byte)RARHostOs.MacOs => "Mac OS",
        (byte)RARHostOs.BeOs => "BeOS",
        _ => $"Unknown ({hostOS})"
    };

    /// <summary>
    /// Returns the full 64-bit data size for a block from its full header bytes.
    /// Combines ADD_SIZE with HIGH_PACK_SIZE when the LARGE flag is set.
    /// </summary>
    private static long GetBlockDataSize(byte[] fullHeader, ushort headerSize)
    {
        uint addSize = BitConverter.ToUInt32(fullHeader, RAR4HeaderLayout.AddSize);
        ushort flags = BitConverter.ToUInt16(fullHeader, RAR4HeaderLayout.Flags);
        byte blockType = fullHeader[RAR4HeaderLayout.Type];

        if ((blockType == (byte)RAR4BlockType.FileHeader || blockType == (byte)RAR4BlockType.Service) &&
            (flags & (ushort)RARFileFlags.Large) != 0 && headerSize >= RAR4HeaderLayout.FixedFieldsEnd + 4)
        {
            uint highPack = BitConverter.ToUInt32(fullHeader, RAR4HeaderLayout.HighPackSizeOffset);
            return addSize | ((long)highPack << 32);
        }

        return addSize;
    }

    /// <summary>
    /// Encodes a <see cref="DateTime"/> as a 32-bit DOS date+time value (high 16 bits = date,
    /// low 16 bits = time). Seconds floor to even (DOS time has 2-second resolution); the
    /// EXT_TIME +1s rounding flag is responsible for compensating odd-second values.
    /// </summary>
    private static uint EncodeDosDate(DateTime dt)
    {
        int year = Math.Clamp(dt.Year, RAR4HeaderLayout.DosEpochYear, RAR4HeaderLayout.DosMaxYear);
        uint date = (((uint)(year - RAR4HeaderLayout.DosEpochYear) & RAR4HeaderLayout.DosYearMask) << 9)
                  | (((uint)dt.Month & RAR4HeaderLayout.DosMonthMask) << 5)
                  | ((uint)dt.Day & RAR4HeaderLayout.DosDayMask);
        uint time = (((uint)dt.Hour & RAR4HeaderLayout.DosHourMask) << 11)
                  | (((uint)dt.Minute & RAR4HeaderLayout.DosMinuteMask) << 5)
                  | (((uint)dt.Second & RAR4HeaderLayout.DosSecondEvenMask) >> 1);
        return (date << 16) | time;
    }

    /// <summary>
    /// Computes the EXT_TIME +1s rounding flag and the sub-second remainder bytes for a
    /// target <see cref="DateTime"/> at the given byte-count precision (0–3, where 3 means
    /// 100ns / NTFS resolution). Mirrors the inverse of <c>RARHeaderReader.ReadExtendedTimes</c>.
    /// </summary>
    private static (bool NeedsRoundingFlag, int Remainder) EncodeMtimeFraction(DateTime dt)
    {
        int evenSecond = dt.Second / 2 * 2;
        var dosBase = new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, evenSecond, dt.Kind);
        long ticksAbove = dt.Ticks - dosBase.Ticks;
        bool needsRounding = ticksAbove >= TimeSpan.TicksPerSecond;
        long remainderTicks = needsRounding ? ticksAbove - TimeSpan.TicksPerSecond : ticksAbove;

        // The full 24-bit 100ns remainder is always < 1s (10,000,000 ticks < 2^24), so it fits
        // in 3 bytes. Return it unmasked; the caller stores the MOST-significant `byteCount` bytes
        // to mirror the reader (RARHeaderReader.TryReadRemainder decodes MSB-first for count < 3).
        return (needsRounding, (int)remainderTicks);
    }

    /// <summary>
    /// Rewrites the DOS <c>FTIME</c> field and EXT_TIME mtime remainder for the given file
    /// header bytes if the file name matches an entry in <paramref name="targetMtimes"/>.
    /// Returns <see langword="true"/> when the header was modified.
    /// </summary>
    private static bool TryPatchFileMtime(
        byte[] fullHeader,
        string? fileName,
        ushort nameSize,
        Dictionary<string, DateTime>? targetMtimes)
    {
        if (targetMtimes is null || fileName is null
            || !targetMtimes.TryGetValue(fileName, out DateTime targetMtime))
        {
            return false;
        }

        ushort flags = BitConverter.ToUInt16(fullHeader, RAR4HeaderLayout.Flags);
        bool hasExtTime = (flags & (ushort)RARFileFlags.ExtTime) != 0;
        bool hasLarge = (flags & (ushort)RARFileFlags.Large) != 0;
        bool hasSalt = (flags & (ushort)RARFileFlags.Salt) != 0;

        bool modified = false;

        // Always patch DOS FTIME — it's at a fixed offset, regardless of EXT_TIME presence.
        uint targetDos = EncodeDosDate(targetMtime);
        if (BitConverter.ToUInt32(fullHeader, RAR4HeaderLayout.FileTime) != targetDos)
        {
            byte[] dosBytes = BitConverter.GetBytes(targetDos);
            Array.Copy(dosBytes, 0, fullHeader, RAR4HeaderLayout.FileTime, 4);
            modified = true;
        }

        if (!hasExtTime)
        {
            return modified;
        }

        // EXT_TIME starts after FILE_NAME (and optional HIGH_* / SALT).
        int extTimeOffset = RAR4HeaderLayout.FixedFieldsEnd + (hasLarge ? 8 : 0) + nameSize + (hasSalt ? 8 : 0);
        if (extTimeOffset + 2 > fullHeader.Length)
        {
            return modified;
        }

        ushort extFlags = BitConverter.ToUInt16(fullHeader, extTimeOffset);
        int mtimeNibble = (extFlags >> 12) & RAR4HeaderLayout.ExtTimeNibbleMask;
        bool mtimePresent = (mtimeNibble & RAR4HeaderLayout.ExtTimePresentBit) != 0;
        int mtimeByteCount = mtimeNibble & RAR4HeaderLayout.ExtTimePrecisionMask;

        if (!mtimePresent)
        {
            return modified;
        }

        (bool needsRounding, int remainder) = EncodeMtimeFraction(targetMtime);

        // Update the +1s rounding bit in the mtime nibble if it changed.
        int newMtimeNibble = (mtimeNibble & ~RAR4HeaderLayout.ExtTimeRoundUpBit) | (needsRounding ? RAR4HeaderLayout.ExtTimeRoundUpBit : 0);
        if (newMtimeNibble != mtimeNibble)
        {
            ushort newExtFlags = (ushort)((extFlags & RAR4HeaderLayout.MtimeNibbleMask) | (newMtimeNibble << 12));
            byte[] flagBytes = BitConverter.GetBytes(newExtFlags);
            Array.Copy(flagBytes, 0, fullHeader, extTimeOffset, 2);
            modified = true;
        }

        // Overwrite remainder bytes in-place, MSB-first, exactly mirroring the reader
        // (RARHeaderReader.TryReadRemainder decodes `bytes[j] << ((j + 3 - count) * 8)`).
        // The stored bytes hold the MOST-significant bytes of the 24-bit 100ns remainder:
        // count=3 -> byte[i] carries bits (i*8), count=2 -> byte[0] bits 8-15/byte[1] bits 16-23,
        // count=1 -> byte[0] bits 16-23. The three conventions coincide only at count=3, which is
        // why the previous LSB-first encoding corrupted -tsm2/-tsm3 (1- and 2-byte) precisions.
        int remainderOffset = extTimeOffset + 2;
        if (mtimeByteCount > 0 && remainderOffset + mtimeByteCount <= fullHeader.Length)
        {
            for (int i = 0; i < mtimeByteCount; i++)
            {
                byte newByte = (byte)((remainder >> ((i + 3 - mtimeByteCount) * 8)) & 0xFF);
                if (fullHeader[remainderOffset + i] != newByte)
                {
                    fullHeader[remainderOffset + i] = newByte;
                    modified = true;
                }
            }
        }

        return modified;
    }

    /// <summary>
    /// Patches a RAR file in-place to change Host OS and optionally attributes.
    /// </summary>
    /// <param name="filePath">
    /// Path to the RAR file to patch
    /// </param>
    /// <param name="options">
    /// Patching options
    /// </param>
    /// <returns>
    /// List of patch results for each modified block
    /// </returns>
    public static List<PatchResult> PatchFile(string filePath, PatchOptions options)
    {
        var results = new List<PatchResult>();

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        PatchStream(stream, options, results);

        return results;
    }

    /// <summary>
    /// Patches a RAR file stream to change Host OS and optionally attributes.
    /// </summary>
    /// <param name="stream">
    /// Stream with read/write access
    /// </param>
    /// <param name="options">
    /// Patching options
    /// </param>
    /// <param name="results">
    /// List to add patch results to
    /// </param>
    public static void PatchStream(Stream stream, PatchOptions options, List<PatchResult> results)
    {
        // Skip RAR signature (7 bytes for RAR 4.x)
        stream.Position = RARUtils.RAR4Marker.Length;

        // Track End of Archive block for Archive Data CRC patching
        long endArchivePosition = -1;
        ushort endArchiveFlags = 0;
        ushort endArchiveHeaderSize = 0;

        while (stream.Position + RAR4HeaderLayout.BaseHeaderSize <= stream.Length)
        {
            long blockStart = stream.Position;

            // Read base header
            byte[] baseHeader = new byte[RAR4HeaderLayout.BaseHeaderSize];
            if (stream.Read(baseHeader, 0, RAR4HeaderLayout.BaseHeaderSize) != RAR4HeaderLayout.BaseHeaderSize)
            {
                break;
            }

            byte blockType = baseHeader[RAR4HeaderLayout.Type];
            ushort headerSize = BitConverter.ToUInt16(baseHeader, RAR4HeaderLayout.HeaderSize);

            if (headerSize < RAR4HeaderLayout.BaseHeaderSize || blockStart + headerSize > stream.Length)
            {
                break;
            }

            // Check if this is a file header or service block
            bool isFileHeader = blockType == (byte)RAR4BlockType.FileHeader;
            bool isServiceBlock = blockType == (byte)RAR4BlockType.Service;

            if ((isFileHeader || (isServiceBlock && options.PatchServiceBlocks)) && headerSize >= RAR4HeaderLayout.FixedFieldsEnd)
            {
                // Read full header
                stream.Position = blockStart;
                byte[] fullHeader = new byte[headerSize];
                if (stream.Read(fullHeader, 0, headerSize) != headerSize)
                {
                    break;
                }

                // Extract current values
                ushort originalCRC = BitConverter.ToUInt16(fullHeader, RAR4HeaderLayout.Crc);
                byte originalHostOS = fullHeader[RAR4HeaderLayout.HostOs];
                uint originalAttr = BitConverter.ToUInt32(fullHeader, RAR4HeaderLayout.Attr);
                uint originalFileTime = BitConverter.ToUInt32(fullHeader, RAR4HeaderLayout.FileTime);

                // Extract filename. The name starts after the fixed fields, plus the
                // 8-byte HIGH_PACK_SIZE/HIGH_UNP_SIZE pair when the LARGE flag is set.
                // Reading from FixedFieldsEnd corrupted the name on LARGE headers
                // (files > 2 GB), which made the FileModifiedTimes lookup silently miss.
                ushort nameSize = BitConverter.ToUInt16(fullHeader, RAR4HeaderLayout.NameSize);
                ushort headerFlags = BitConverter.ToUInt16(fullHeader, RAR4HeaderLayout.Flags);
                int nameOffset = RAR4HeaderLayout.FixedFieldsEnd + (((headerFlags & (ushort)RARFileFlags.Large) != 0) ? 8 : 0);
                string? fileName = null;
                if (nameSize > 0 && nameOffset + nameSize <= headerSize)
                {
                    // Decode the name the same way the readers do (Unicode/OEM aware). Plain
                    // Encoding.ASCII produced embedded-NUL garbage for LHD_UNICODE headers, so the
                    // FileModifiedTimes lookup (keyed by RARUtils.DecodeFileName names) silently missed.
                    int nameLen = Math.Min(nameSize, headerSize - nameOffset);
                    byte[] nameBytes = new byte[nameLen];
                    Array.Copy(fullHeader, nameOffset, nameBytes, 0, nameLen);
                    fileName = RARUtils.DecodeFileName(nameBytes, (headerFlags & (ushort)RARFileFlags.Unicode) != 0);
                }

                bool modified = false;

                // Determine target values based on block type
                byte? targetHostOS = isFileHeader ? options.GetFileHostOS() : options.GetServiceBlockHostOS();
                uint? targetAttr = isFileHeader ? options.GetFileAttributes() : options.GetServiceBlockAttributes();
                uint? targetFileTime = isServiceBlock ? options.ServiceBlockFileTime : null;

                // Patch Host OS if target value is set
                byte newHostOS = originalHostOS;
                if (targetHostOS.HasValue && fullHeader[RAR4HeaderLayout.HostOs] != targetHostOS.Value)
                {
                    fullHeader[RAR4HeaderLayout.HostOs] = targetHostOS.Value;
                    newHostOS = targetHostOS.Value;
                    modified = true;
                }

                // Patch attributes if target value is set
                uint newAttr = originalAttr;
                if (targetAttr.HasValue && originalAttr != targetAttr.Value)
                {
                    newAttr = targetAttr.Value;
                    byte[] attrBytes = BitConverter.GetBytes(newAttr);
                    Array.Copy(attrBytes, 0, fullHeader, RAR4HeaderLayout.Attr, 4);
                    modified = true;
                }

                // Patch service block file time if target value is set
                if (targetFileTime.HasValue && originalFileTime != targetFileTime.Value)
                {
                    byte[] timeBytes = BitConverter.GetBytes(targetFileTime.Value);
                    Array.Copy(timeBytes, 0, fullHeader, RAR4HeaderLayout.FileTime, 4);
                    modified = true;
                }

                // Patch per-file mtime (DOS FTIME + EXT_TIME remainder) for file headers.
                if (isFileHeader && TryPatchFileMtime(fullHeader, fileName, nameSize, options.FileModifiedTimes))
                {
                    modified = true;
                }

                if (modified)
                {
                    // Recalculate CRC (CRC32 of header bytes excluding CRC field, take lower 16 bits)
                    ushort newCRC = RARUtils.CalculateHeaderCRC(fullHeader);

                    // Update CRC in header
                    byte[] crcBytes = BitConverter.GetBytes(newCRC);
                    Array.Copy(crcBytes, 0, fullHeader, RAR4HeaderLayout.Crc, 2);

                    // Write modified header back
                    stream.Position = blockStart;
                    stream.Write(fullHeader, 0, fullHeader.Length);

                    results.Add(new PatchResult
                    {
                        BlockPosition = blockStart,
                        BlockType = (RAR4BlockType)blockType,
                        FileName = fileName,
                        OriginalHostOS = originalHostOS,
                        NewHostOS = newHostOS,
                        OriginalAttributes = originalAttr,
                        NewAttributes = newAttr,
                        OriginalCRC = originalCRC,
                        NewCRC = newCRC
                    });
                }

                // Move to next block (header + data for file/service blocks)
                stream.Position = blockStart + headerSize + GetBlockDataSize(fullHeader, headerSize);
            }
            else
            {
                // Track End of Archive block position
                if (blockType == (byte)RAR4BlockType.EndArchive)
                {
                    endArchivePosition = blockStart;
                    endArchiveFlags = BitConverter.ToUInt16(baseHeader, RAR4HeaderLayout.Flags);
                    endArchiveHeaderSize = headerSize;
                }

                // Skip this block
                // For blocks with LONG_BLOCK flag or file headers, read ADD_SIZE
                ushort flags = BitConverter.ToUInt16(baseHeader, RAR4HeaderLayout.Flags);
                long dataSize = ComputeNonFileBlockDataSize(stream, blockStart, blockType, flags, headerSize);

                stream.Position = blockStart + headerSize + dataSize;
            }

            // Safety check: prevent infinite loop
            if (stream.Position <= blockStart)
            {
                break;
            }
        }

        // After all header patching, update End of Archive's Archive Data CRC if needed.
        // The Archive Data CRC covers all bytes from offset 0 to the End of Archive block,
        // so it becomes stale after patching any header bytes within that range.
        if (results.Count > 0 && endArchivePosition >= 0 &&
            (endArchiveFlags & (ushort)RAREndArchiveFlags.DataCRC) != 0 &&
            endArchiveHeaderSize >= RAR4HeaderLayout.BaseHeaderSize + RAR4HeaderLayout.EndArchiveDataCrcLength) // base + 4-byte Archive Data CRC minimum
        {
            PatchEndOfArchiveCRC(stream, endArchivePosition, endArchiveHeaderSize);
        }
    }

    /// <summary>
    /// Analyzes a RAR file and returns information about blocks that would be patched.
    /// Does not modify the file.
    /// </summary>
    /// <param name="filePath">
    /// Path to the RAR file
    /// </param>
    /// <param name="options">
    /// Patching options to simulate
    /// </param>
    /// <returns>
    /// List of blocks that would be modified
    /// </returns>
    public static List<PatchResult> AnalyzeFile(string filePath, PatchOptions options)
    {
        var results = new List<PatchResult>();

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        // Skip RAR signature (7 bytes for RAR 4.x)
        stream.Position = RARUtils.RAR4Marker.Length;

        while (stream.Position + RAR4HeaderLayout.BaseHeaderSize <= stream.Length)
        {
            long blockStart = stream.Position;

            // Read base header
            byte[] baseHeader = new byte[RAR4HeaderLayout.BaseHeaderSize];
            if (stream.Read(baseHeader, 0, RAR4HeaderLayout.BaseHeaderSize) != RAR4HeaderLayout.BaseHeaderSize)
            {
                break;
            }

            byte blockType = baseHeader[RAR4HeaderLayout.Type];
            ushort headerSize = BitConverter.ToUInt16(baseHeader, RAR4HeaderLayout.HeaderSize);

            if (headerSize < RAR4HeaderLayout.BaseHeaderSize || blockStart + headerSize > stream.Length)
            {
                break;
            }

            bool isFileHeader = blockType == (byte)RAR4BlockType.FileHeader;
            bool isServiceBlock = blockType == (byte)RAR4BlockType.Service;

            if ((isFileHeader || (isServiceBlock && options.PatchServiceBlocks)) && headerSize >= RAR4HeaderLayout.FixedFieldsEnd)
            {
                // Read full header
                stream.Position = blockStart;
                byte[] fullHeader = new byte[headerSize];
                if (stream.Read(fullHeader, 0, headerSize) != headerSize)
                {
                    break;
                }

                ushort originalCRC = BitConverter.ToUInt16(fullHeader, RAR4HeaderLayout.Crc);
                byte originalHostOS = fullHeader[RAR4HeaderLayout.HostOs];
                uint originalAttr = BitConverter.ToUInt32(fullHeader, RAR4HeaderLayout.Attr);

                ushort nameSize = BitConverter.ToUInt16(fullHeader, RAR4HeaderLayout.NameSize);
                ushort headerFlags = BitConverter.ToUInt16(fullHeader, RAR4HeaderLayout.Flags);
                int nameOffset = RAR4HeaderLayout.FixedFieldsEnd + (((headerFlags & (ushort)RARFileFlags.Large) != 0) ? 8 : 0);
                string? fileName = null;
                if (nameSize > 0 && nameOffset + nameSize <= headerSize)
                {
                    // Decode the name the same way the readers do (Unicode/OEM aware); plain ASCII
                    // corrupts LHD_UNICODE names and garbles PatchResult.FileName.
                    int nameLen = Math.Min(nameSize, headerSize - nameOffset);
                    byte[] nameBytes = new byte[nameLen];
                    Array.Copy(fullHeader, nameOffset, nameBytes, 0, nameLen);
                    fileName = RARUtils.DecodeFileName(nameBytes, (headerFlags & (ushort)RARFileFlags.Unicode) != 0);
                }

                // Determine target values based on block type
                byte? targetHostOS = isFileHeader ? options.GetFileHostOS() : options.GetServiceBlockHostOS();
                uint? targetAttr = isFileHeader ? options.GetFileAttributes() : options.GetServiceBlockAttributes();

                bool wouldModify = (targetHostOS.HasValue && originalHostOS != targetHostOS.Value) ||
                                  (targetAttr.HasValue && originalAttr != targetAttr.Value);

                if (wouldModify)
                {
                    results.Add(new PatchResult
                    {
                        BlockPosition = blockStart,
                        BlockType = (RAR4BlockType)blockType,
                        FileName = fileName,
                        OriginalHostOS = originalHostOS,
                        NewHostOS = targetHostOS ?? originalHostOS,
                        OriginalAttributes = originalAttr,
                        NewAttributes = targetAttr ?? originalAttr,
                        OriginalCRC = originalCRC,
                        NewCRC = 0 // Not calculated in analysis mode
                    });
                }

                stream.Position = blockStart + headerSize + GetBlockDataSize(fullHeader, headerSize);
            }
            else
            {
                ushort flags = BitConverter.ToUInt16(baseHeader, RAR4HeaderLayout.Flags);
                long dataSize = ComputeNonFileBlockDataSize(stream, blockStart, blockType, flags, headerSize);

                stream.Position = blockStart + headerSize + dataSize;
            }

            if (stream.Position <= blockStart)
            {
                break;
            }
        }

        return results;
    }

    /// <summary>
    /// Computes the data-area size that follows a non-file RAR4 block header while skipping it.
    /// Reads ADD_SIZE when the block carries data (LONG_BLOCK flag, file headers, or service
    /// headers) and folds in HIGH_PACK_SIZE for file/service blocks with the LARGE flag.
    /// The stream position is left indeterminate; callers re-seek to <c>blockStart + headerSize +</c>
    /// the returned size.
    /// </summary>
    private static long ComputeNonFileBlockDataSize(Stream stream, long blockStart, byte blockType, ushort flags, ushort headerSize)
    {
        bool hasAddSize = (flags & (ushort)RARFileFlags.LongBlock) != 0 ||
                          blockType == (byte)RAR4BlockType.FileHeader ||
                          blockType == (byte)RAR4BlockType.Service;

        long dataSize = 0;
        if (hasAddSize && stream.Position + 4 <= stream.Length)
        {
            byte[] addSizeBytes = new byte[4];
            stream.ReadExactly(addSizeBytes, 0, 4);
            dataSize = BitConverter.ToUInt32(addSizeBytes, 0);

            // Check for 64-bit size (HIGH_PACK_SIZE) on file/service blocks with LARGE flag
            if ((blockType == (byte)RAR4BlockType.FileHeader || blockType == (byte)RAR4BlockType.Service) &&
                (flags & (ushort)RARFileFlags.Large) != 0 && headerSize >= RAR4HeaderLayout.FixedFieldsEnd + 4 &&
                blockStart + RAR4HeaderLayout.FixedFieldsEnd + 4 <= stream.Length)
            {
                stream.Position = blockStart + RAR4HeaderLayout.HighPackSizeOffset;
                byte[] highPackBytes = new byte[4];
                stream.ReadExactly(highPackBytes, 0, 4);
                uint highPack = BitConverter.ToUInt32(highPackBytes, 0);
                dataSize |= (long)highPack << 32;
            }
        }

        return dataSize;
    }

    /// <summary>
    /// Patches LARGE flag state on file/service headers in a RAR file.
    /// This is a structural patch: it inserts or removes 8 bytes (HIGH_PACK_SIZE + HIGH_UNP_SIZE)
    /// in each file/service header, so it must run BEFORE in-place patching (PatchStream).
    /// </summary>
    /// <param name="stream">
    /// Stream with read/write access
    /// </param>
    /// <param name="options">
    /// Patching options with SetLargeFlag
    /// </param>
    /// <returns>
    /// True if any modifications were made
    /// </returns>
    public static bool PatchLargeFlags(Stream stream, PatchOptions options)
    {
        if (!options.SetLargeFlag.HasValue)
        {
            return false;
        }

        bool wantLarge = options.SetLargeFlag.Value;

        // Read entire file into memory for structural modification
        stream.Position = 0;
        byte[] original = new byte[stream.Length];
        int bytesRead = 0;
        while (bytesRead < original.Length)
        {
            int read = stream.Read(original, bytesRead, original.Length - bytesRead);
            if (read <= 0)
            {
                break;
            }

            bytesRead += read;
        }

        using var output = new MemoryStream();
        bool modified = false;

        // Copy RAR signature (7 bytes)
        if (bytesRead < RARUtils.RAR4Marker.Length)
        {
            return false;
        }

        output.Write(original, 0, RARUtils.RAR4Marker.Length);

        int pos = RARUtils.RAR4Marker.Length;

        while (pos + RAR4HeaderLayout.BaseHeaderSize <= bytesRead)
        {
            int blockStart = pos;

            // Read base header fields
            byte blockType = original[pos + RAR4HeaderLayout.Type];
            ushort flags = BitConverter.ToUInt16(original, pos + RAR4HeaderLayout.Flags);
            ushort headerSize = BitConverter.ToUInt16(original, pos + RAR4HeaderLayout.HeaderSize);

            if (headerSize < RAR4HeaderLayout.BaseHeaderSize || blockStart + headerSize > bytesRead)
            {
                break;
            }

            bool isFileHeader = blockType == (byte)RAR4BlockType.FileHeader;
            bool isServiceBlock = blockType == (byte)RAR4BlockType.Service;

            // Determine ADD_SIZE for data section
            bool hasAddSize = (flags & (ushort)RARFileFlags.LongBlock) != 0 ||
                              isFileHeader || isServiceBlock;
            uint addSize = 0;
            if (hasAddSize && blockStart + RAR4HeaderLayout.BaseHeaderSize + RAR4HeaderLayout.AddSizeFieldLength <= bytesRead)
            {
                addSize = BitConverter.ToUInt32(original, blockStart + RAR4HeaderLayout.AddSize);
            }

            if ((isFileHeader || isServiceBlock) && headerSize >= RAR4HeaderLayout.FixedFieldsEnd)
            {
                bool hasLarge = (flags & (ushort)RARFileFlags.Large) != 0;

                if (wantLarge && !hasLarge)
                {
                    // HEAD_SIZE is a ushort, so a header within 8 bytes of the maximum cannot
                    // record its own grown size: 65530 + 8 truncated to 2, desynchronizing the
                    // archive irrecoverably. Refuse rather than emit that.
                    if (headerSize > ushort.MaxValue - 8)
                    {
                        throw new InvalidDataException(
                            $"Cannot add LARGE fields to a {headerSize}-byte header: the resulting " +
                            $"size would exceed the {ushort.MaxValue}-byte HEAD_SIZE field.");
                    }

                    // ADD LARGE: insert 8 bytes at FixedFieldsEnd (after ATTR field)
                    byte[] header = new byte[headerSize + 8];
                    // Copy fixed fields (0..FixedFieldsEnd-1, up to and including ATTR)
                    Array.Copy(original, blockStart, header, 0, RAR4HeaderLayout.FixedFieldsEnd);
                    // Insert HIGH_PACK_SIZE and HIGH_UNP_SIZE at FixedFieldsEnd
                    BitConverter.GetBytes(options.HighPackSize).CopyTo(header, RAR4HeaderLayout.FixedFieldsEnd);
                    BitConverter.GetBytes(options.HighUnpSize).CopyTo(header, RAR4HeaderLayout.FixedFieldsEnd + 4);
                    // Copy remaining header bytes (from FixedFieldsEnd onward in original)
                    int remaining = headerSize - RAR4HeaderLayout.FixedFieldsEnd;
                    if (remaining > 0)
                    {
                        Array.Copy(original, blockStart + RAR4HeaderLayout.FixedFieldsEnd, header, RAR4HeaderLayout.FixedFieldsEnd + 8, remaining);
                    }

                    // Update flags: set LARGE bit
                    ushort newFlags = (ushort)(flags | (ushort)RARFileFlags.Large);
                    BitConverter.GetBytes(newFlags).CopyTo(header, RAR4HeaderLayout.Flags);

                    // Update header size (+8)
                    ushort newHeaderSize = (ushort)(headerSize + 8);
                    BitConverter.GetBytes(newHeaderSize).CopyTo(header, RAR4HeaderLayout.HeaderSize);

                    // Recalculate CRC
                    ushort newCRC = RARUtils.CalculateHeaderCRC(header);
                    BitConverter.GetBytes(newCRC).CopyTo(header, RAR4HeaderLayout.Crc);

                    // Write modified header
                    output.Write(header, 0, header.Length);
                    modified = true;
                }
                else if (!wantLarge && hasLarge)
                {
                    // REMOVE LARGE: remove 8 bytes at FixedFieldsEnd
                    if (headerSize < RAR4HeaderLayout.FixedFieldsEnd + 8)
                    {
                        // Header too small to contain HIGH fields, just copy as-is
                        output.Write(original, blockStart, headerSize);
                    }
                    else
                    {
                        byte[] header = new byte[headerSize - 8];
                        // Copy fixed fields (0..FixedFieldsEnd-1)
                        Array.Copy(original, blockStart, header, 0, RAR4HeaderLayout.FixedFieldsEnd);
                        // Skip 8 bytes (HIGH_PACK_SIZE + HIGH_UNP_SIZE), copy rest
                        int remaining = headerSize - (RAR4HeaderLayout.FixedFieldsEnd + 8);
                        if (remaining > 0)
                        {
                            Array.Copy(original, blockStart + RAR4HeaderLayout.FixedFieldsEnd + 8, header, RAR4HeaderLayout.FixedFieldsEnd, remaining);
                        }

                        // Update flags: clear LARGE bit
                        ushort newFlags = (ushort)(flags & ~(ushort)RARFileFlags.Large);
                        BitConverter.GetBytes(newFlags).CopyTo(header, RAR4HeaderLayout.Flags);

                        // Update header size (-8)
                        ushort newHeaderSize = (ushort)(headerSize - 8);
                        BitConverter.GetBytes(newHeaderSize).CopyTo(header, RAR4HeaderLayout.HeaderSize);

                        // Recalculate CRC
                        ushort newCRC = RARUtils.CalculateHeaderCRC(header);
                        BitConverter.GetBytes(newCRC).CopyTo(header, RAR4HeaderLayout.Crc);

                        // Write modified header
                        output.Write(header, 0, header.Length);
                        modified = true;
                    }
                }
                else
                {
                    // LARGE state already matches, copy header unchanged
                    output.Write(original, blockStart, headerSize);
                }

                // Copy data section unchanged
                if (addSize > 0 && addSize <= int.MaxValue && blockStart + headerSize + (int)addSize <= bytesRead)
                {
                    output.Write(original, blockStart + headerSize, (int)addSize);
                }

                pos = blockStart + headerSize + (addSize <= int.MaxValue ? (int)addSize : 0);
            }
            else
            {
                // Non-file/service block: copy unchanged (header + data)
                int blockTotalSize = headerSize + (hasAddSize && addSize <= int.MaxValue ? (int)addSize : 0);
                if (blockStart + blockTotalSize > bytesRead)
                {
                    blockTotalSize = bytesRead - blockStart;
                }

                output.Write(original, blockStart, blockTotalSize);

                pos = blockStart + blockTotalSize;
            }

            // Safety check: prevent infinite loop
            if (pos <= blockStart)
            {
                break;
            }
        }

        // Copy any trailing bytes
        if (pos < bytesRead)
        {
            output.Write(original, pos, bytesRead - pos);
        }

        if (!modified)
        {
            return false;
        }

        // Write modified content back to stream
        stream.Position = 0;
        stream.SetLength(output.Length);
        output.Position = 0;
        output.CopyTo(stream);
        stream.Flush();

        return true;
    }

    /// <summary>
    /// Recalculates the Archive Data CRC in the End of Archive block.
    /// The Archive Data CRC is a CRC32 of all bytes from offset 0 to the start of the End of Archive block.
    /// </summary>
    private static void PatchEndOfArchiveCRC(Stream stream, long endArchivePosition, ushort headerSize)
    {
        // Compute CRC32 of all bytes from offset 0 to the End of Archive block
        stream.Position = 0;
        byte[] buffer = new byte[80 * 1024];
        long remaining = endArchivePosition;
        uint archiveDataCRC = 0;

        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = stream.Read(buffer, 0, toRead);
            if (read <= 0)
            {
                break;
            }

            archiveDataCRC = Crc32Algorithm.Append(archiveDataCRC, buffer, 0, read);
            remaining -= read;
        }

        // Read the End of Archive header
        stream.Position = endArchivePosition;
        byte[] endHeader = new byte[headerSize];
        if (stream.Read(endHeader, 0, headerSize) != headerSize)
        {
            return;
        }

        // Update Archive Data CRC at offset 7 (immediately after the 7-byte base header)
        BitConverter.GetBytes(archiveDataCRC).CopyTo(endHeader, RAR4HeaderLayout.BaseHeaderSize);

        // Recalculate the End of Archive header's own CRC
        ushort newHeaderCRC = RARUtils.CalculateHeaderCRC(endHeader);
        BitConverter.GetBytes(newHeaderCRC).CopyTo(endHeader, RAR4HeaderLayout.Crc);

        // Write back
        stream.Position = endArchivePosition;
        stream.Write(endHeader, 0, endHeader.Length);
    }
}
