using Force.Crc32;

namespace ReScene.RAR;

/// <summary>
/// Result of a patching operation on a single block.
/// </summary>
public class PatchResult
{
    public long BlockPosition { get; set; }
    public RAR4BlockType BlockType { get; set; }
    public string? FileName { get; set; }
    public byte OriginalHostOS { get; set; }
    public byte NewHostOS { get; set; }
    public uint OriginalAttributes { get; set; }
    public uint NewAttributes { get; set; }
    public ushort OriginalCrc { get; set; }
    public ushort NewCrc { get; set; }
}

/// <summary>
/// Options for patching RAR files. Supports exact values detected from SRR headers.
/// </summary>
public class PatchOptions
{
    // ===== File Header Options =====

    /// <summary>
    /// Target Host OS value for file headers (0=MS-DOS, 1=OS/2, 2=Windows, 3=Unix, 4=Mac OS, 5=BeOS).
    /// </summary>
    public byte? FileHostOS { get; set; }

    /// <summary>
    /// Target file attributes for file headers.
    /// </summary>
    public uint? FileAttributes { get; set; }

    // ===== Service Block (CMT) Options =====

    /// <summary>
    /// If true, also patch service blocks (like CMT).
    /// </summary>
    public bool PatchServiceBlocks { get; set; } = true;

    /// <summary>
    /// Target Host OS value for service blocks (CMT). If null, uses FileHostOS.
    /// </summary>
    public byte? ServiceBlockHostOS { get; set; }

    /// <summary>
    /// Target file attributes for service blocks (CMT). If null, uses FileAttributes.
    /// </summary>
    public uint? ServiceBlockAttributes { get; set; }

    /// <summary>
    /// Target file time (DOS format) for service blocks. If null, time is not patched.
    /// </summary>
    public uint? ServiceBlockFileTime { get; set; }

    // ===== LARGE Flag Options =====

    /// <summary>
    /// If true, add LARGE flag + HIGH fields. If false, remove them. If null, no change.
    /// </summary>
    public bool? SetLargeFlag { get; set; }

    /// <summary>
    /// HIGH_PACK_SIZE value to insert when adding LARGE (typically 0).
    /// </summary>
    public uint HighPackSize { get; set; }

    /// <summary>
    /// HIGH_UNP_SIZE value to insert when adding LARGE.
    /// </summary>
    public uint HighUnpSize { get; set; }

    // ===== Computed Properties =====

    /// <summary>
    /// Gets the Host OS to use for file headers. Returns null if no patching needed.
    /// </summary>
    public byte? GetFileHostOS() => FileHostOS;

    /// <summary>
    /// Gets the Host OS to use for service blocks. Falls back to FileHostOS if not set.
    /// </summary>
    public byte? GetServiceBlockHostOS() => ServiceBlockHostOS ?? FileHostOS;

    /// <summary>
    /// Gets the file attributes to use for file headers.
    /// </summary>
    public uint? GetFileAttributes() => FileAttributes;

    /// <summary>
    /// Gets the file attributes to use for service blocks. Falls back to FileAttributes if not set.
    /// </summary>
    public uint? GetServiceBlockAttributes() => ServiceBlockAttributes ?? FileAttributes;
}

/// <summary>
/// Patches RAR 4.x files to modify Host OS, attributes, and other header fields
/// while maintaining valid CRCs.
/// </summary>
public static class RARPatcher
{
    // RAR 4.x header field offsets (from block start)
    private const int OffsetCrc = 0;
    private const int OffsetType = 2;
    private const int OffsetFlags = 3;
    private const int OffsetHeaderSize = 5;
    private const int OffsetAddSize = 7;       // For file headers and service blocks
    private const int OffsetHighPackSize = 32;  // HIGH_PACK_SIZE (only when LARGE flag set)
    private const int OffsetUnpSize = 11;
    private const int OffsetHostOS = 15;
    private const int OffsetFileCrc = 16;
    private const int OffsetFileTime = 20;
    private const int OffsetUnpVer = 24;
    private const int OffsetMethod = 25;
    private const int OffsetNameSize = 26;
    private const int OffsetAttr = 28;

    /// <summary>
    /// Host OS name lookup.
    /// </summary>
    public static string GetHostOSName(byte hostOS) => hostOS switch
    {
        0 => "MS-DOS",
        1 => "OS/2",
        2 => "Windows",
        3 => "Unix",
        4 => "Mac OS",
        5 => "BeOS",
        _ => $"Unknown ({hostOS})"
    };

    /// <summary>
    /// Returns the full 64-bit data size for a block from its full header bytes.
    /// Combines ADD_SIZE with HIGH_PACK_SIZE when the LARGE flag is set.
    /// </summary>
    private static long GetBlockDataSize(byte[] fullHeader, ushort headerSize)
    {
        uint addSize = BitConverter.ToUInt32(fullHeader, OffsetAddSize);
        ushort flags = BitConverter.ToUInt16(fullHeader, OffsetFlags);
        byte blockType = fullHeader[OffsetType];

        if ((blockType == (byte)RAR4BlockType.FileHeader || blockType == (byte)RAR4BlockType.Service) &&
            (flags & (ushort)RARFileFlags.Large) != 0 && headerSize >= 36)
        {
            uint highPack = BitConverter.ToUInt32(fullHeader, OffsetHighPackSize);
            return addSize | ((long)highPack << 32);
        }

        return addSize;
    }

    /// <summary>
    /// Patches a RAR file in-place to change Host OS and optionally attributes.
    /// </summary>
    /// <param name="filePath">Path to the RAR file to patch</param>
    /// <param name="options">Patching options</param>
    /// <returns>List of patch results for each modified block</returns>
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
    /// <param name="stream">Stream with read/write access</param>
    /// <param name="options">Patching options</param>
    /// <param name="results">List to add patch results to</param>
    public static void PatchStream(Stream stream, PatchOptions options, List<PatchResult> results)
    {
        // Skip RAR signature (7 bytes for RAR 4.x)
        stream.Position = 7;

        // Track End of Archive block for Archive Data CRC patching
        long endArchivePosition = -1;
        ushort endArchiveFlags = 0;
        ushort endArchiveHeaderSize = 0;

        while (stream.Position + 7 <= stream.Length)
        {
            long blockStart = stream.Position;

            // Read base header
            byte[] baseHeader = new byte[7];
            if (stream.Read(baseHeader, 0, 7) != 7)
                break;

            byte blockType = baseHeader[OffsetType];
            ushort headerSize = BitConverter.ToUInt16(baseHeader, OffsetHeaderSize);

            if (headerSize < 7 || blockStart + headerSize > stream.Length)
                break;

            // Check if this is a file header or service block
            bool isFileHeader = blockType == (byte)RAR4BlockType.FileHeader;
            bool isServiceBlock = blockType == (byte)RAR4BlockType.Service;

            if ((isFileHeader || (isServiceBlock && options.PatchServiceBlocks)) && headerSize >= 32)
            {
                // Read full header
                stream.Position = blockStart;
                byte[] fullHeader = new byte[headerSize];
                if (stream.Read(fullHeader, 0, headerSize) != headerSize)
                    break;

                // Extract current values
                ushort originalCrc = BitConverter.ToUInt16(fullHeader, OffsetCrc);
                byte originalHostOS = fullHeader[OffsetHostOS];
                uint originalAttr = BitConverter.ToUInt32(fullHeader, OffsetAttr);
                uint originalFileTime = BitConverter.ToUInt32(fullHeader, OffsetFileTime);

                // Extract filename for logging
                ushort nameSize = BitConverter.ToUInt16(fullHeader, OffsetNameSize);
                string? fileName = null;
                if (nameSize > 0 && 32 + nameSize <= headerSize)
                {
                    fileName = System.Text.Encoding.ASCII.GetString(fullHeader, 32, Math.Min(nameSize, headerSize - 32));
                }

                bool modified = false;

                // Determine target values based on block type
                byte? targetHostOS = isFileHeader ? options.GetFileHostOS() : options.GetServiceBlockHostOS();
                uint? targetAttr = isFileHeader ? options.GetFileAttributes() : options.GetServiceBlockAttributes();
                uint? targetFileTime = isServiceBlock ? options.ServiceBlockFileTime : null;

                // Patch Host OS if target value is set
                byte newHostOS = originalHostOS;
                if (targetHostOS.HasValue && fullHeader[OffsetHostOS] != targetHostOS.Value)
                {
                    fullHeader[OffsetHostOS] = targetHostOS.Value;
                    newHostOS = targetHostOS.Value;
                    modified = true;
                }

                // Patch attributes if target value is set
                uint newAttr = originalAttr;
                if (targetAttr.HasValue && originalAttr != targetAttr.Value)
                {
                    newAttr = targetAttr.Value;
                    byte[] attrBytes = BitConverter.GetBytes(newAttr);
                    Array.Copy(attrBytes, 0, fullHeader, OffsetAttr, 4);
                    modified = true;
                }

                // Patch service block file time if target value is set
                uint newFileTime = originalFileTime;
                if (targetFileTime.HasValue && originalFileTime != targetFileTime.Value)
                {
                    newFileTime = targetFileTime.Value;
                    byte[] timeBytes = BitConverter.GetBytes(newFileTime);
                    Array.Copy(timeBytes, 0, fullHeader, OffsetFileTime, 4);
                    modified = true;
                }

                if (modified)
                {
                    // Recalculate CRC (CRC32 of header bytes excluding CRC field, take lower 16 bits)
                    uint crc32 = Crc32Algorithm.Compute(fullHeader, 2, fullHeader.Length - 2);
                    ushort newCrc = (ushort)(crc32 & 0xFFFF);

                    // Update CRC in header
                    byte[] crcBytes = BitConverter.GetBytes(newCrc);
                    Array.Copy(crcBytes, 0, fullHeader, OffsetCrc, 2);

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
                        OriginalCrc = originalCrc,
                        NewCrc = newCrc
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
                    endArchiveFlags = BitConverter.ToUInt16(baseHeader, OffsetFlags);
                    endArchiveHeaderSize = headerSize;
                }

                // Skip this block
                // For blocks with LONG_BLOCK flag or file headers, read ADD_SIZE
                ushort flags = BitConverter.ToUInt16(baseHeader, OffsetFlags);
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
                        (flags & (ushort)RARFileFlags.Large) != 0 && headerSize >= 36 &&
                        blockStart + 36 <= stream.Length)
                    {
                        stream.Position = blockStart + OffsetHighPackSize;
                        byte[] highPackBytes = new byte[4];
                        stream.ReadExactly(highPackBytes, 0, 4);
                        uint highPack = BitConverter.ToUInt32(highPackBytes, 0);
                        dataSize |= (long)highPack << 32;
                    }
                }

                stream.Position = blockStart + headerSize + dataSize;
            }

            // Safety check: prevent infinite loop
            if (stream.Position <= blockStart)
                break;
        }

        // After all header patching, update End of Archive's Archive Data CRC if needed.
        // The Archive Data CRC covers all bytes from offset 0 to the End of Archive block,
        // so it becomes stale after patching any header bytes within that range.
        if (results.Count > 0 && endArchivePosition >= 0 &&
            (endArchiveFlags & (ushort)RAREndArchiveFlags.DataCrc) != 0 &&
            endArchiveHeaderSize >= 11) // 7 base + 4 Archive Data CRC minimum
        {
            PatchEndOfArchiveCrc(stream, endArchivePosition, endArchiveHeaderSize);
        }
    }

    /// <summary>
    /// Analyzes a RAR file and returns information about blocks that would be patched.
    /// Does not modify the file.
    /// </summary>
    /// <param name="filePath">Path to the RAR file</param>
    /// <param name="options">Patching options to simulate</param>
    /// <returns>List of blocks that would be modified</returns>
    public static List<PatchResult> AnalyzeFile(string filePath, PatchOptions options)
    {
        var results = new List<PatchResult>();

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        // Skip RAR signature (7 bytes for RAR 4.x)
        stream.Position = 7;

        while (stream.Position + 7 <= stream.Length)
        {
            long blockStart = stream.Position;

            // Read base header
            byte[] baseHeader = new byte[7];
            if (stream.Read(baseHeader, 0, 7) != 7)
                break;

            byte blockType = baseHeader[OffsetType];
            ushort headerSize = BitConverter.ToUInt16(baseHeader, OffsetHeaderSize);

            if (headerSize < 7 || blockStart + headerSize > stream.Length)
                break;

            bool isFileHeader = blockType == (byte)RAR4BlockType.FileHeader;
            bool isServiceBlock = blockType == (byte)RAR4BlockType.Service;

            if ((isFileHeader || (isServiceBlock && options.PatchServiceBlocks)) && headerSize >= 32)
            {
                // Read full header
                stream.Position = blockStart;
                byte[] fullHeader = new byte[headerSize];
                if (stream.Read(fullHeader, 0, headerSize) != headerSize)
                    break;

                ushort originalCrc = BitConverter.ToUInt16(fullHeader, OffsetCrc);
                byte originalHostOS = fullHeader[OffsetHostOS];
                uint originalAttr = BitConverter.ToUInt32(fullHeader, OffsetAttr);

                ushort nameSize = BitConverter.ToUInt16(fullHeader, OffsetNameSize);
                string? fileName = null;
                if (nameSize > 0 && 32 + nameSize <= headerSize)
                {
                    fileName = System.Text.Encoding.ASCII.GetString(fullHeader, 32, Math.Min(nameSize, headerSize - 32));
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
                        OriginalCrc = originalCrc,
                        NewCrc = 0 // Not calculated in analysis mode
                    });
                }

                stream.Position = blockStart + headerSize + GetBlockDataSize(fullHeader, headerSize);
            }
            else
            {
                ushort flags = BitConverter.ToUInt16(baseHeader, OffsetFlags);
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
                        (flags & (ushort)RARFileFlags.Large) != 0 && headerSize >= 36 &&
                        blockStart + 36 <= stream.Length)
                    {
                        stream.Position = blockStart + OffsetHighPackSize;
                        byte[] highPackBytes = new byte[4];
                        stream.ReadExactly(highPackBytes, 0, 4);
                        uint highPack = BitConverter.ToUInt32(highPackBytes, 0);
                        dataSize |= (long)highPack << 32;
                    }
                }

                stream.Position = blockStart + headerSize + dataSize;
            }

            if (stream.Position <= blockStart)
                break;
        }

        return results;
    }

    /// <summary>
    /// Patches LARGE flag state on file/service headers in a RAR file.
    /// This is a structural patch: it inserts or removes 8 bytes (HIGH_PACK_SIZE + HIGH_UNP_SIZE)
    /// in each file/service header, so it must run BEFORE in-place patching (PatchStream).
    /// </summary>
    /// <param name="stream">Stream with read/write access</param>
    /// <param name="options">Patching options with SetLargeFlag</param>
    /// <returns>True if any modifications were made</returns>
    public static bool PatchLargeFlags(Stream stream, PatchOptions options)
    {
        if (!options.SetLargeFlag.HasValue)
            return false;

        bool wantLarge = options.SetLargeFlag.Value;

        // Read entire file into memory for structural modification
        stream.Position = 0;
        byte[] original = new byte[stream.Length];
        int bytesRead = 0;
        while (bytesRead < original.Length)
        {
            int read = stream.Read(original, bytesRead, original.Length - bytesRead);
            if (read <= 0) break;
            bytesRead += read;
        }

        using var output = new MemoryStream();
        bool modified = false;

        // Copy RAR signature (7 bytes)
        if (bytesRead < 7) return false;
        output.Write(original, 0, 7);

        int pos = 7;

        while (pos + 7 <= bytesRead)
        {
            int blockStart = pos;

            // Read base header fields
            byte blockType = original[pos + OffsetType];
            ushort flags = BitConverter.ToUInt16(original, pos + OffsetFlags);
            ushort headerSize = BitConverter.ToUInt16(original, pos + OffsetHeaderSize);

            if (headerSize < 7 || blockStart + headerSize > bytesRead)
                break;

            bool isFileHeader = blockType == (byte)RAR4BlockType.FileHeader;
            bool isServiceBlock = blockType == (byte)RAR4BlockType.Service;

            // Determine ADD_SIZE for data section
            bool hasAddSize = (flags & (ushort)RARFileFlags.LongBlock) != 0 ||
                              isFileHeader || isServiceBlock;
            uint addSize = 0;
            if (hasAddSize && blockStart + 11 <= bytesRead)
            {
                addSize = BitConverter.ToUInt32(original, blockStart + OffsetAddSize);
            }

            if ((isFileHeader || isServiceBlock) && headerSize >= 32)
            {
                bool hasLarge = (flags & (ushort)RARFileFlags.Large) != 0;

                if (wantLarge && !hasLarge)
                {
                    // ADD LARGE: insert 8 bytes at offset 32 (after ATTR field)
                    byte[] header = new byte[headerSize + 8];
                    // Copy bytes 0-31 (up to and including ATTR)
                    Array.Copy(original, blockStart, header, 0, 32);
                    // Insert HIGH_PACK_SIZE and HIGH_UNP_SIZE at offset 32
                    BitConverter.GetBytes(options.HighPackSize).CopyTo(header, 32);
                    BitConverter.GetBytes(options.HighUnpSize).CopyTo(header, 36);
                    // Copy remaining header bytes (from offset 32 onward in original)
                    int remaining = headerSize - 32;
                    if (remaining > 0)
                    {
                        Array.Copy(original, blockStart + 32, header, 40, remaining);
                    }

                    // Update flags: set LARGE bit
                    ushort newFlags = (ushort)(flags | (ushort)RARFileFlags.Large);
                    BitConverter.GetBytes(newFlags).CopyTo(header, OffsetFlags);

                    // Update header size (+8)
                    ushort newHeaderSize = (ushort)(headerSize + 8);
                    BitConverter.GetBytes(newHeaderSize).CopyTo(header, OffsetHeaderSize);

                    // Recalculate CRC
                    uint crc32 = Crc32Algorithm.Compute(header, 2, header.Length - 2);
                    ushort newCrc = (ushort)(crc32 & 0xFFFF);
                    BitConverter.GetBytes(newCrc).CopyTo(header, OffsetCrc);

                    // Write modified header
                    output.Write(header, 0, header.Length);
                    modified = true;
                }
                else if (!wantLarge && hasLarge)
                {
                    // REMOVE LARGE: remove 8 bytes at offset 32
                    if (headerSize < 40)
                    {
                        // Header too small to contain HIGH fields, just copy as-is
                        output.Write(original, blockStart, headerSize);
                    }
                    else
                    {
                        byte[] header = new byte[headerSize - 8];
                        // Copy bytes 0-31
                        Array.Copy(original, blockStart, header, 0, 32);
                        // Skip 8 bytes (HIGH_PACK_SIZE + HIGH_UNP_SIZE), copy rest
                        int remaining = headerSize - 40;
                        if (remaining > 0)
                        {
                            Array.Copy(original, blockStart + 40, header, 32, remaining);
                        }

                        // Update flags: clear LARGE bit
                        ushort newFlags = (ushort)(flags & ~(ushort)RARFileFlags.Large);
                        BitConverter.GetBytes(newFlags).CopyTo(header, OffsetFlags);

                        // Update header size (-8)
                        ushort newHeaderSize = (ushort)(headerSize - 8);
                        BitConverter.GetBytes(newHeaderSize).CopyTo(header, OffsetHeaderSize);

                        // Recalculate CRC
                        uint crc32 = Crc32Algorithm.Compute(header, 2, header.Length - 2);
                        ushort newCrc = (ushort)(crc32 & 0xFFFF);
                        BitConverter.GetBytes(newCrc).CopyTo(header, OffsetCrc);

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
                if (addSize > 0 && blockStart + headerSize + addSize <= bytesRead)
                {
                    output.Write(original, blockStart + headerSize, (int)addSize);
                }

                pos = blockStart + headerSize + (int)addSize;
            }
            else
            {
                // Non-file/service block: copy unchanged (header + data)
                int blockTotalSize = headerSize + (hasAddSize ? (int)addSize : 0);
                if (blockStart + blockTotalSize > bytesRead)
                    blockTotalSize = bytesRead - blockStart;
                output.Write(original, blockStart, blockTotalSize);

                pos = blockStart + blockTotalSize;
            }

            // Safety check: prevent infinite loop
            if (pos <= blockStart)
                break;
        }

        // Copy any trailing bytes
        if (pos < bytesRead)
        {
            output.Write(original, pos, bytesRead - pos);
        }

        if (!modified)
            return false;

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
    private static void PatchEndOfArchiveCrc(Stream stream, long endArchivePosition, ushort headerSize)
    {
        // Compute CRC32 of all bytes from offset 0 to the End of Archive block
        stream.Position = 0;
        byte[] buffer = new byte[81920];
        long remaining = endArchivePosition;
        uint archiveDataCrc = 0;

        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = stream.Read(buffer, 0, toRead);
            if (read <= 0) break;
            archiveDataCrc = Crc32Algorithm.Append(archiveDataCrc, buffer, 0, read);
            remaining -= read;
        }

        // Read the End of Archive header
        stream.Position = endArchivePosition;
        byte[] endHeader = new byte[headerSize];
        if (stream.Read(endHeader, 0, headerSize) != headerSize)
            return;

        // Update Archive Data CRC at offset 7 (immediately after the 7-byte base header)
        BitConverter.GetBytes(archiveDataCrc).CopyTo(endHeader, 7);

        // Recalculate the End of Archive header's own CRC
        uint crc32 = Crc32Algorithm.Compute(endHeader, 2, endHeader.Length - 2);
        ushort newHeaderCrc = (ushort)(crc32 & 0xFFFF);
        BitConverter.GetBytes(newHeaderCrc).CopyTo(endHeader, OffsetCrc);

        // Write back
        stream.Position = endArchivePosition;
        stream.Write(endHeader, 0, endHeader.Length);
    }
}
