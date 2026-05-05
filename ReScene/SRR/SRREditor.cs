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
    private const int CopyBufferSize = 64 * 1024;

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

        string tempPath = srrFilePath + ".tmp";
        try
        {
            using (FileStream input = new(srrFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (BinaryReader reader = new(input))
            using (FileStream output = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (BinaryWriter writer = new(output))
            {
                bool insertionDone = false;

                while (input.Position < input.Length)
                {
                    if (input.Position + BaseHeaderSize > input.Length)
                    {
                        break;
                    }

                    long blockStart = input.Position;
                    ushort crc = reader.ReadUInt16();
                    byte typeRaw = reader.ReadByte();
                    ushort flags = reader.ReadUInt16();
                    ushort headerSize = reader.ReadUInt16();

                    if (headerSize < BaseHeaderSize)
                    {
                        break;
                    }

                    uint addSize = 0;
                    bool hasAddSize = (flags & (ushort)SRRBlockFlags.LongBlock) != 0
                                     || typeRaw == (byte)SRRBlockType.StoredFile;

                    if (hasAddSize && input.Position + AddSizeFieldLength <= input.Length)
                    {
                        addSize = reader.ReadUInt32();
                    }

                    long totalBlockSize = headerSize + addSize;
                    long blockEnd = blockStart + totalBlockSize;

                    if (blockEnd > input.Length)
                    {
                        break;
                    }

                    // Insert new stored files at the right position:
                    // After the last StoredFile block, or after Header if no stored files exist
                    if (!insertionDone && typeRaw != (byte)SRRBlockType.StoredFile
                        && typeRaw != (byte)SRRBlockType.Header)
                    {
                        // We've moved past header and any existing stored files
                        WriteNewStoredFiles(writer, files);
                        insertionDone = true;
                    }

                    // Copy the entire block verbatim
                    input.Position = blockStart;
                    CopyBytes(input, output, totalBlockSize);
                }

                // If we never inserted (e.g., file only has header), do it now
                if (!insertionDone)
                {
                    WriteNewStoredFiles(writer, files);
                }
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

        string tempPath = srrFilePath + ".tmp";
        try
        {
            using (FileStream input = new(srrFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (BinaryReader reader = new(input))
            using (FileStream output = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                while (input.Position < input.Length)
                {
                    if (input.Position + BaseHeaderSize > input.Length)
                    {
                        break;
                    }

                    long blockStart = input.Position;
                    ushort crc = reader.ReadUInt16();
                    byte typeRaw = reader.ReadByte();
                    ushort flags = reader.ReadUInt16();
                    ushort headerSize = reader.ReadUInt16();

                    if (headerSize < BaseHeaderSize)
                    {
                        break;
                    }

                    uint addSize = 0;
                    bool hasAddSize = (flags & (ushort)SRRBlockFlags.LongBlock) != 0
                                     || typeRaw == (byte)SRRBlockType.StoredFile;

                    if (hasAddSize && input.Position + AddSizeFieldLength <= input.Length)
                    {
                        addSize = reader.ReadUInt32();
                    }

                    long totalBlockSize = headerSize + addSize;
                    long blockEnd = blockStart + totalBlockSize;

                    if (blockEnd > input.Length)
                    {
                        break;
                    }

                    // For StoredFile blocks, check if we should skip them
                    if (typeRaw == (byte)SRRBlockType.StoredFile)
                    {
                        string? storedName = ReadStoredFileName(reader, input, blockStart);

                        if (storedName is not null && namesToRemove.Contains(storedName))
                        {
                            // Skip this block entirely
                            input.Position = blockEnd;
                            continue;
                        }
                    }

                    // Copy the entire block verbatim
                    input.Position = blockStart;
                    CopyBytes(input, output, totalBlockSize);
                }
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

        string tempPath = srrFilePath + ".tmp";
        bool renamed = false;

        try
        {
            using (FileStream input = new(srrFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (BinaryReader reader = new(input))
            using (FileStream output = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (BinaryWriter writer = new(output))
            {
                while (input.Position < input.Length)
                {
                    long blockStart = input.Position;

                    if (blockStart + BaseHeaderSize > input.Length)
                    {
                        break;
                    }

                    ushort crc = reader.ReadUInt16();
                    byte typeRaw = reader.ReadByte();
                    ushort flags = reader.ReadUInt16();
                    ushort headerSize = reader.ReadUInt16();

                    if (headerSize < BaseHeaderSize)
                    {
                        break;
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
                        break;
                    }

                    if (!renamed && typeRaw == (byte)SRRBlockType.StoredFile)
                    {
                        string? name = ReadStoredFileName(reader, input, blockStart);

                        if (string.Equals(name, oldName, StringComparison.OrdinalIgnoreCase))
                        {
                            long nameLenPos = blockStart + BaseHeaderSize + AddSizeFieldLength;
                            input.Position = nameLenPos;
                            ushort oldNameLen = reader.ReadUInt16();
                            input.Position += oldNameLen;
                            long payloadStart = input.Position;
                            long payloadEnd = blockStart + totalBlockSize;
                            long payloadLen = payloadEnd - payloadStart;
                            byte[] payload = reader.ReadBytes((int)payloadLen);

                            ushort newHeaderSize = (ushort)(BaseHeaderSize + AddSizeFieldLength + NameLengthFieldLength + newNameBytes.Length);
                            writer.Write((ushort)0x6A6A);
                            writer.Write((byte)SRRBlockType.StoredFile);
                            writer.Write(flags);
                            writer.Write(newHeaderSize);
                            writer.Write(addSize);
                            writer.Write((ushort)newNameBytes.Length);
                            writer.Write(newNameBytes);
                            writer.Write(payload);

                            renamed = true;
                            input.Position = payloadEnd;
                            continue;
                        }
                    }

                    input.Position = blockStart;
                    CopyBytes(input, output, totalBlockSize);
                }
            }

            if (!renamed)
            {
                File.Delete(tempPath);
                throw new InvalidOperationException($"Stored file '{oldName}' not found.");
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
            long blockStart = input.Position;

            if (blockStart + BaseHeaderSize > input.Length)
            {
                break;
            }

            ushort crc = reader.ReadUInt16();
            byte typeRaw = reader.ReadByte();
            ushort flags = reader.ReadUInt16();
            ushort headerSize = reader.ReadUInt16();

            if (headerSize < BaseHeaderSize)
            {
                break;
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
                break;
            }

            string? name = null;

            if (typeRaw == (byte)SRRBlockType.StoredFile)
            {
                long mark = input.Position;
                name = ReadStoredFileName(reader, input, blockStart);
                input.Position = mark;
            }

            input.Position = blockStart;
            byte[] bytes = reader.ReadBytes((int)totalBlockSize);
            result.Add(new BlockSnapshot(bytes, typeRaw, name));
        }

        return result;
    }

    private static void WriteAllBlocks(string srrFilePath, List<BlockSnapshot> blocks)
    {
        string tempPath = srrFilePath + ".tmp";

        try
        {
            using (FileStream output = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                foreach (BlockSnapshot b in blocks)
                {
                    output.Write(b.Bytes, 0, b.Bytes.Length);
                }
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
            WriteStoredFileBlock(writer, normalizedName, fileData);
        }
    }

    private static void WriteStoredFileBlock(BinaryWriter writer, string fileName, byte[] fileData)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(fileName);
        ushort headerSize = (ushort)(BaseHeaderSize + AddSizeFieldLength + NameLengthFieldLength + nameBytes.Length);
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

    private static void CopyBytes(Stream source, Stream destination, long count)
    {
        byte[] buffer = new byte[CopyBufferSize];
        long remaining = count;

        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = source.Read(buffer, 0, toRead);

            if (read == 0)
            {
                throw new InvalidDataException("Unexpected end of SRR file while copying block data.");
            }

            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }
}
