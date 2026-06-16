using System.Text;

namespace ReScene.SRR;

/// <summary>
/// Provides methods to modify existing SRR files by adding or removing stored files.
/// Uses a block-level copy approach to preserve exact binary content of untouched blocks.
/// </summary>
public static class SRREditor
{
    private const int BaseHeaderSize = 7;
    private const int AddSizeFieldLength = 4;
    private const int NameLengthFieldLength = 2;

    /// <summary>
    /// Adds one or more stored files to an existing SRR file.
    /// New stored files are inserted after existing stored file blocks (before OSO/RAR blocks).
    /// </summary>
    /// <param name="srrFilePath">
    /// Path to the SRR file to modify.
    /// </param>
    /// <param name="files">
    /// List of tuples containing the stored name and file path for each file to add.
    /// </param>
    public static void AddStoredFiles(string srrFilePath, IReadOnlyList<(string StoredName, string FilePath)> files)
    {
        if (string.IsNullOrWhiteSpace(srrFilePath))
        {
            throw new ArgumentException("SRR file path is required.", nameof(srrFilePath));
        }

        if (!File.Exists(srrFilePath))
        {
            throw new FileNotFoundException("SRR file not found.", srrFilePath);
        }

        if (files is null || files.Count == 0)
        {
            return;
        }

        foreach ((string storedName, string filePath) in files)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"File not found: {filePath}", filePath);
            }
        }

        CommitViaTempFile(srrFilePath, output =>
        {
            using FileStream input = new(srrFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using BinaryReader reader = new(input);
            using BinaryWriter writer = new(output, Encoding.UTF8, leaveOpen: true);

            bool insertionDone = false;

            while (input.Position < input.Length)
            {
                if (!TryReadBlockHeader(reader, input, out SrrBlockHeader block))
                {
                    break;
                }

                // Insert new stored files at the right position:
                // After the last StoredFile block, or after Header if no stored files exist
                if (!insertionDone && block.Type != (byte)SRRBlockType.StoredFile
                    && block.Type != (byte)SRRBlockType.Header)
                {
                    // We've moved past header and any existing stored files
                    WriteNewStoredFiles(writer, files);
                    insertionDone = true;
                }

                // Copy the entire block verbatim
                input.Position = block.BlockStart;
                StreamUtilities.CopyBytesStrict(input, output, block.TotalBlockSize,
                    "Unexpected end of SRR file while copying block data.");
            }

            // If we never inserted (e.g., file only has header), do it now
            if (!insertionDone)
            {
                WriteNewStoredFiles(writer, files);
            }
        });
    }

    /// <summary>
    /// Removes stored files from an existing SRR file by name.
    /// </summary>
    /// <param name="srrFilePath">
    /// Path to the SRR file to modify.
    /// </param>
    /// <param name="storedNames">
    /// List of stored file names to remove (case-insensitive comparison).
    /// </param>
    public static void RemoveStoredFiles(string srrFilePath, IReadOnlyList<string> storedNames)
    {
        if (string.IsNullOrWhiteSpace(srrFilePath))
        {
            throw new ArgumentException("SRR file path is required.", nameof(srrFilePath));
        }

        if (!File.Exists(srrFilePath))
        {
            throw new FileNotFoundException("SRR file not found.", srrFilePath);
        }

        if (storedNames is null || storedNames.Count == 0)
        {
            return;
        }

        HashSet<string> namesToRemove = new(storedNames, StringComparer.OrdinalIgnoreCase);

        CommitViaTempFile(srrFilePath, output =>
        {
            using FileStream input = new(srrFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using BinaryReader reader = new(input);

            while (input.Position < input.Length)
            {
                if (!TryReadBlockHeader(reader, input, out SrrBlockHeader block))
                {
                    break;
                }

                long blockEnd = block.BlockStart + block.TotalBlockSize;

                // For StoredFile blocks, check if we should skip them
                if (block.Type == (byte)SRRBlockType.StoredFile)
                {
                    string? storedName = ReadStoredFileName(reader, input, block.BlockStart);

                    if (storedName is not null && namesToRemove.Contains(storedName))
                    {
                        // Skip this block entirely
                        input.Position = blockEnd;
                        continue;
                    }
                }

                // Copy the entire block verbatim
                input.Position = block.BlockStart;
                StreamUtilities.CopyBytesStrict(input, output, block.TotalBlockSize,
                    "Unexpected end of SRR file while copying block data.");
            }
        });
    }

    /// <summary>
    /// Renames an existing stored file inside the SRR by writing a fresh
    /// stored-file block in its place with the same payload but a new name.
    /// All other blocks are preserved verbatim.
    /// </summary>
    /// <param name="srrFilePath">
    /// Path to the SRR file to modify.
    /// </param>
    /// <param name="oldName">
    /// The current stored file name (case-insensitive match).
    /// </param>
    /// <param name="newName">
    /// The new name. Backslashes are normalized to forward slashes.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when any of <paramref name="srrFilePath"/>, <paramref name="oldName"/>,
    /// or <paramref name="newName"/> is empty or whitespace.
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// Thrown when the SRR file does not exist.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no stored file with <paramref name="oldName"/> is found.
    /// </exception>
    /// <remarks>
    /// If multiple stored-file blocks share the same <paramref name="oldName"/>, only the
    /// first match is renamed. Renaming to the same name is performed unconditionally and
    /// will rewrite the file.
    /// </remarks>
    public static void RenameStoredFile(string srrFilePath, string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(srrFilePath))
        {
            throw new ArgumentException("SRR file path is required.", nameof(srrFilePath));
        }

        if (!File.Exists(srrFilePath))
        {
            throw new FileNotFoundException("SRR file not found.", srrFilePath);
        }

        if (string.IsNullOrWhiteSpace(oldName))
        {
            throw new ArgumentException("Old name is required.", nameof(oldName));
        }

        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new ArgumentException("New name is required.", nameof(newName));
        }

        string normalizedNew = newName.Replace('\\', '/');
        byte[] newNameBytes = Encoding.UTF8.GetBytes(normalizedNew);

        CommitViaTempFile(srrFilePath, output =>
        {
            using FileStream input = new(srrFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using BinaryReader reader = new(input);
            using BinaryWriter writer = new(output, Encoding.UTF8, leaveOpen: true);

            bool renamed = false;

            while (input.Position < input.Length)
            {
                if (!TryReadBlockHeader(reader, input, out SrrBlockHeader block))
                {
                    break;
                }

                if (!renamed && block.Type == (byte)SRRBlockType.StoredFile)
                {
                    string? name = ReadStoredFileName(reader, input, block.BlockStart);

                    if (string.Equals(name, oldName, StringComparison.OrdinalIgnoreCase))
                    {
                        long nameLenPos = block.BlockStart + BaseHeaderSize + AddSizeFieldLength;
                        input.Position = nameLenPos;
                        ushort oldNameLen = reader.ReadUInt16();
                        input.Position += oldNameLen;
                        long payloadStart = input.Position;
                        long payloadEnd = block.BlockStart + block.TotalBlockSize;
                        long payloadLen = payloadEnd - payloadStart;
                        byte[] payload = reader.ReadBytes((int)payloadLen);

                        ushort newHeaderSize = (ushort)(BaseHeaderSize + AddSizeFieldLength + NameLengthFieldLength + newNameBytes.Length);
                        writer.Write((ushort)0x6A6A);
                        writer.Write((byte)SRRBlockType.StoredFile);
                        writer.Write(block.Flags);
                        writer.Write(newHeaderSize);
                        writer.Write(block.AddSize);
                        writer.Write((ushort)newNameBytes.Length);
                        writer.Write(newNameBytes);
                        writer.Write(payload);

                        renamed = true;
                        input.Position = payloadEnd;
                        continue;
                    }
                }

                input.Position = block.BlockStart;
                StreamUtilities.CopyBytesStrict(input, output, block.TotalBlockSize,
                    "Unexpected end of SRR file while copying block data.");
            }

            // Abort the commit (CommitViaTempFile rolls back the temp file) when the
            // target stored file was not present.
            if (!renamed)
            {
                throw new InvalidOperationException($"Stored file '{oldName}' not found.");
            }
        });
    }

    /// <summary>
    /// Moves a stored-file block within the SRR by <paramref name="offset"/> positions
    /// among other stored-file blocks. Negative moves toward the beginning of the file,
    /// positive toward the end. No-op if the resulting position would land outside the
    /// stored-file range.
    /// </summary>
    /// <param name="srrFilePath">
    /// Path to the SRR file to modify.
    /// </param>
    /// <param name="storedName">
    /// The current stored-file name (case-insensitive match).
    /// </param>
    /// <param name="offset">
    /// Number of stored-file slots to move. Use -1 for "up", +1 for "down".
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when the path or name is empty/whitespace.
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// Thrown when the SRR file does not exist.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no stored file with <paramref name="storedName"/> is found.
    /// </exception>
    public static void MoveStoredFile(string srrFilePath, string storedName, int offset)
    {
        if (string.IsNullOrWhiteSpace(srrFilePath))
        {
            throw new ArgumentException("SRR file path is required.", nameof(srrFilePath));
        }

        if (!File.Exists(srrFilePath))
        {
            throw new FileNotFoundException("SRR file not found.", srrFilePath);
        }

        if (string.IsNullOrWhiteSpace(storedName))
        {
            throw new ArgumentException("Stored name is required.", nameof(storedName));
        }

        if (offset == 0)
        {
            return;
        }

        List<BlockSnapshot> blocks = ReadAllBlocks(srrFilePath);

        List<int> storedIndices = [];
        int targetIndex = -1;

        for (int i = 0; i < blocks.Count; i++)
        {
            if (blocks[i].Type == (byte)SRRBlockType.StoredFile)
            {
                if (string.Equals(blocks[i].Name, storedName, StringComparison.OrdinalIgnoreCase))
                {
                    targetIndex = storedIndices.Count;
                }

                storedIndices.Add(i);
            }
        }

        if (targetIndex < 0)
        {
            throw new InvalidOperationException($"Stored file '{storedName}' not found.");
        }

        int newTargetIndex = targetIndex + offset;

        if (newTargetIndex < 0 || newTargetIndex >= storedIndices.Count)
        {
            return;
        }

        if (newTargetIndex == targetIndex)
        {
            return;
        }

        int oldGlobal = storedIndices[targetIndex];
        int newGlobal = storedIndices[newTargetIndex];

        BlockSnapshot moved = blocks[oldGlobal];
        blocks.RemoveAt(oldGlobal);
        blocks.Insert(newGlobal, moved);

        WriteAllBlocks(srrFilePath, blocks);
    }

    private record struct BlockSnapshot(byte[] Bytes, byte Type, string? Name);

    private static List<BlockSnapshot> ReadAllBlocks(string srrFilePath)
    {
        List<BlockSnapshot> result = [];

        using FileStream input = new(srrFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using BinaryReader reader = new(input);

        while (input.Position < input.Length)
        {
            if (!TryReadBlockHeader(reader, input, out SrrBlockHeader block))
            {
                break;
            }

            string? name = null;

            if (block.Type == (byte)SRRBlockType.StoredFile)
            {
                long mark = input.Position;
                name = ReadStoredFileName(reader, input, block.BlockStart);
                input.Position = mark;
            }

            input.Position = block.BlockStart;
            byte[] bytes = reader.ReadBytes((int)block.TotalBlockSize);
            result.Add(new BlockSnapshot(bytes, block.Type, name));
        }

        return result;
    }

    private static void WriteAllBlocks(string srrFilePath, List<BlockSnapshot> blocks)
    {
        CommitViaTempFile(srrFilePath, output =>
        {
            foreach (BlockSnapshot b in blocks)
            {
                output.Write(b.Bytes, 0, b.Bytes.Length);
            }
        });
    }

    /// <summary>
    /// Runs <paramref name="body"/> against a freshly created temporary output file, then
    /// atomically replaces <paramref name="srrFilePath"/> with it (Delete-then-Move). If
    /// <paramref name="body"/> throws, the temporary file is removed and the original SRR is
    /// left untouched. The body is given only the output stream; it opens its own input.
    /// </summary>
    private static void CommitViaTempFile(string srrFilePath, Action<FileStream> body)
    {
        string tempPath = srrFilePath + ".tmp";

        try
        {
            using (FileStream output = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                body(output);
            }

            File.Delete(srrFilePath);
            File.Move(tempPath, srrFilePath);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }
    }

    /// <summary>
    /// A parsed SRR block base header plus its computed total size.
    /// </summary>
    private readonly record struct SrrBlockHeader(
        long BlockStart, ushort Crc, byte Type, ushort Flags, ushort HeaderSize, uint AddSize, long TotalBlockSize);

    /// <summary>
    /// Reads the SRR block base header (CRC, type, flags, header size and the optional
    /// ADD_SIZE) at the current stream position and computes the total block size.
    /// Returns <see langword="false"/> when the header is truncated, the header size is
    /// invalid, or the block would extend past the end of the stream — in which case the
    /// stream position is left indeterminate and the caller should stop iterating.
    /// </summary>
    private static bool TryReadBlockHeader(BinaryReader reader, Stream input, out SrrBlockHeader header)
    {
        header = default;

        long blockStart = input.Position;
        if (blockStart + BaseHeaderSize > input.Length)
        {
            return false;
        }

        ushort crc = reader.ReadUInt16();
        byte typeRaw = reader.ReadByte();
        ushort flags = reader.ReadUInt16();
        ushort headerSize = reader.ReadUInt16();

        if (headerSize < BaseHeaderSize)
        {
            return false;
        }

        uint addSize = 0;
        bool hasAddSize = (flags & (ushort)SRRBlockFlags.LongBlock) != 0
                          || typeRaw == (byte)SRRBlockType.StoredFile;

        if (hasAddSize && input.Position + AddSizeFieldLength <= input.Length)
        {
            addSize = reader.ReadUInt32();
        }

        long totalBlockSize = headerSize + addSize;
        if (blockStart + totalBlockSize > input.Length)
        {
            return false;
        }

        header = new SrrBlockHeader(blockStart, crc, typeRaw, flags, headerSize, addSize, totalBlockSize);
        return true;
    }

    private static string? ReadStoredFileName(BinaryReader reader, FileStream input, long blockStart)
    {
        // Name length is at offset: base(7) + addSize(4)
        long nameLenPos = blockStart + BaseHeaderSize + AddSizeFieldLength;
        if (nameLenPos + NameLengthFieldLength > input.Length)
        {
            return null;
        }

        input.Position = nameLenPos;
        ushort nameLen = reader.ReadUInt16();

        if (nameLen == 0 || input.Position + nameLen > input.Length)
        {
            return null;
        }

        byte[] nameBytes = reader.ReadBytes(nameLen);
        return Encoding.UTF8.GetString(nameBytes);
    }

    private static void WriteNewStoredFiles(
        BinaryWriter writer,
        IReadOnlyList<(string StoredName, string FilePath)> files)
    {
        foreach ((string storedName, string filePath) in files)
        {
            string normalizedName = storedName.Replace('\\', '/');
            byte[] fileData = File.ReadAllBytes(filePath);
            SrrBlockWriter.WriteStoredFileBlock(writer, normalizedName, fileData);
        }
    }
}
