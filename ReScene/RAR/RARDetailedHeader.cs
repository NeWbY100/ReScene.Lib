using System.Text;

namespace ReScene.RAR;

/// <summary>
/// Represents a single field within a RAR header, with its offset and raw/formatted values.
/// </summary>
public class RARHeaderField
{
    /// <summary>
    /// Field name (e.g., "Header CRC", "Flags", "Packed Size").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Byte offset from the start of the file.
    /// </summary>
    public long Offset
    {
        get; set;
    }

    /// <summary>
    /// Length in bytes.
    /// </summary>
    public int Length
    {
        get; set;
    }

    /// <summary>
    /// Raw bytes of this field.
    /// </summary>
    public ReadOnlyMemory<byte> RawBytes { get; set; }

    /// <summary>
    /// Formatted display value.
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Additional description or decoded meaning.
    /// </summary>
    public string? Description
    {
        get; set;
    }

    /// <summary>
    /// Child fields (for nested structures like flags).
    /// </summary>
    public IList<RARHeaderField> Children { get; } = [];

    public override string ToString() => $"{Name}: {Value}";
}

/// <summary>
/// Represents a complete RAR header block with all its fields parsed in detail.
/// </summary>
public class RARDetailedBlock
{
    /// <summary>
    /// Block type name.
    /// </summary>
    public string BlockType { get; set; } = string.Empty;

    /// <summary>
    /// Block type value.
    /// </summary>
    public byte BlockTypeValue
    {
        get; set;
    }

    /// <summary>
    /// Start offset of this block.
    /// </summary>
    public long StartOffset
    {
        get; set;
    }

    /// <summary>
    /// Total block size (header + data).
    /// </summary>
    public long TotalSize
    {
        get; set;
    }

    /// <summary>
    /// Header size only.
    /// </summary>
    public int HeaderSize
    {
        get; set;
    }

    /// <summary>
    /// All fields in this block header.
    /// </summary>
    public IList<RARHeaderField> Fields { get; } = [];

    /// <summary>
    /// True if this block has associated data after the header.
    /// </summary>
    public bool HasData
    {
        get; set;
    }

    /// <summary>
    /// Size of data after header.
    /// </summary>
    public long DataSize
    {
        get; set;
    }

    /// <summary>
    /// For file/service blocks: the item name.
    /// </summary>
    public string? ItemName
    {
        get; set;
    }
}

/// <summary>
/// Parses RAR files and extracts detailed header information with byte offsets.
/// </summary>
public static class RARDetailedParser
{
    /// <summary>
    /// Parses a RAR file and returns all header blocks with detailed field information.
    /// </summary>
    /// <param name="filePath">
    /// The path to the RAR file.
    /// </param>
    /// <param name="enableSfx">
    /// Whether to scan for RAR data inside SFX executables.
    /// </param>
    /// <returns>
    /// A list of parsed detailed blocks.
    /// </returns>
    public static IReadOnlyList<RARDetailedBlock> Parse(string filePath, bool enableSfx = false)
    {
        using FileStream fs = File.OpenRead(filePath);
        return Parse(fs, enableSfx);
    }

    /// <summary>
    /// Parses a RAR stream and returns all header blocks with detailed field information.
    /// </summary>
    /// <param name="stream">
    /// The stream containing RAR data.
    /// </param>
    /// <param name="enableSfx">
    /// Whether to scan for RAR data inside SFX executables.
    /// </param>
    /// <returns>
    /// A list of parsed detailed blocks.
    /// </returns>
    public static IReadOnlyList<RARDetailedBlock> Parse(Stream stream, bool enableSfx = false)
    {
        var blocks = new List<RARDetailedBlock>();

        // Check RAR version at position 0
        bool isRAR5 = RAR5HeaderReader.IsRAR5(stream);
        stream.Position = 0;

        if (!HasValidRARSignature(stream))
        {
            if (!enableSfx)
            {
                return blocks;
            }

            // SFX mode: scan for the RAR marker within the executable
            long offset = RARUtils.FindRarMarkerOffset(stream);
            if (offset < 0)
            {
                return blocks;
            }

            stream.Position = offset;
            isRAR5 = stream.Length - offset >= 8 && RAR5HeaderReader.IsRAR5(stream, offset);
            stream.Position = offset;
        }

        if (isRAR5)
        {
            ParseRAR5(stream, blocks);
        }
        else
        {
            ParseRAR4(stream, blocks);
        }

        return blocks;
    }

    /// <summary>
    /// Parses RAR blocks starting from the current stream position.
    /// Used for parsing embedded RAR data within SRR files.
    /// </summary>
    /// <param name="stream">
    /// The stream positioned at the start of embedded RAR data.
    /// </param>
    /// <returns>
    /// A list of parsed detailed blocks.
    /// </returns>
    public static IReadOnlyList<RARDetailedBlock> ParseFromPosition(Stream stream)
    {
        var blocks = new List<RARDetailedBlock>();

        if (!HasValidRARSignature(stream))
        {
            return blocks;
        }

        bool isRAR5 = RAR5HeaderReader.IsRAR5(stream);

        if (isRAR5)
        {
            ParseRAR5(stream, blocks);
        }
        else
        {
            ParseRAR4(stream, blocks);
        }

        return blocks;
    }

    private static ReadOnlySpan<byte> Rar4Signature => RARUtils.Rar4Marker;

    /// <summary>
    /// Formats a value as a zero-padded hex string based on the field's byte length.
    /// 1 byte = 0x00, 2 bytes = 0x0000, 4 bytes = 0x00000000, etc.
    /// </summary>
    private static string FormatHex(ulong value, int byteLength)
    {
        int hexChars = byteLength * 2;
        return $"0x{value.ToString($"X{hexChars}")}";
    }

    /// <summary>
    /// Checks whether a valid RAR4 or RAR5 signature exists at the current stream position.
    /// Restores the stream position after checking.
    /// </summary>
    private static bool HasValidRARSignature(Stream stream)
    {
        long pos = stream.Position;
        if (stream.Length - pos < 7)
        {
            return false;
        }

        byte[] buf = new byte[8];
        int read = stream.Read(buf, 0, 8);
        stream.Position = pos;

        if (read < 7)
        {
            return false;
        }

        // Check RAR4 signature (7 bytes)
        bool isRar4 = true;
        for (int i = 0; i < 7; i++)
        {
            if (buf[i] != Rar4Signature[i])
            {
                isRar4 = false;
                break;
            }
        }

        if (isRar4)
        {
            return true;
        }

        // Check RAR5 signature (8 bytes)
        if (read < 8)
        {
            return false;
        }

        for (int i = 0; i < 8; i++)
        {
            if (buf[i] != RAR5HeaderReader.RAR5Marker[i])
            {
                return false;
            }
        }

        return true;
    }

    #region RAR 4.x Parsing

    private static void ParseRAR4(Stream stream, List<RARDetailedBlock> blocks)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        // Parse signature
        long sigStart = stream.Position;
        if (stream.Length - sigStart >= 7)
        {
            var sigBlock = new RARDetailedBlock
            {
                BlockType = "Signature",
                BlockTypeValue = 0,
                StartOffset = sigStart,
                TotalSize = 7,
                HeaderSize = 7
            };

            byte[] sig = reader.ReadBytes(7);
            sigBlock.Fields.Add(new RARHeaderField
            {
                Name = "Signature",
                Offset = sigStart,
                Length = 7,
                RawBytes = sig,
                Value = BitConverter.ToString(sig).Replace("-", " ", StringComparison.Ordinal),
                Description = IsValidRAR4Signature(sig) ? "Valid RAR 4.x signature" : "Invalid signature"
            });

            blocks.Add(sigBlock);
        }

        // Parse remaining blocks
        int maxBlocks = 100000; // Safety limit
        int blockCount = 0;
        while (stream.Position + 7 <= stream.Length && blockCount < maxBlocks)
        {
            long blockStart = stream.Position;

            try
            {
                RARDetailedBlock? block = ParseRAR4Block(reader, stream);
                if (block == null)
                {
                    break;
                }

                blocks.Add(block);
                blockCount++;

                // Calculate next block position: header start + header size + data size
                long nextPos = block.StartOffset + block.TotalSize;

                // Safety check: ensure we're making forward progress
                if (nextPos <= blockStart)
                {
                    break;
                }

                // Check if next position is within file bounds
                if (nextPos > stream.Length)
                {
                    break;
                }

                // Check for end of archive block
                if (block.BlockTypeValue == 0x7B)
                {
                    break;
                }

                stream.Position = nextPos;
            }
            catch
            {
                // Skip to try finding more blocks - move forward by minimum header size
                stream.Position = blockStart + 7;
                if (stream.Position > stream.Length - 7)
                {
                    break;
                }
            }
        }
    }

    private static bool IsValidRAR4Signature(byte[] sig)
    {
        if (sig.Length < 7)
        {
            return false;
        }

        return sig[0] == 0x52 && sig[1] == 0x61 && sig[2] == 0x72 &&
               sig[3] == 0x21 && sig[4] == 0x1A && sig[5] == 0x07 && sig[6] == 0x00;
    }

    private static RARDetailedBlock? ParseRAR4Block(BinaryReader reader, Stream stream)
    {
        long blockStart = stream.Position;

        if (blockStart + 7 > stream.Length)
        {
            return null;
        }

        var block = new RARDetailedBlock { StartOffset = blockStart };

        // Ensure we're at the right position (in case stream was repositioned)
        if (reader.BaseStream.Position != blockStart)
        {
            reader.BaseStream.Position = blockStart;
        }

        // Read base header
        var cursor = new FieldCursor(reader, blockStart);

        // HEAD_CRC (2 bytes)
        ushort headCRC = reader.ReadUInt16();
        RARHeaderField crcField = cursor.EmitFixed("Header CRC", 2, BitConverter.GetBytes(headCRC));
        crcField.Value = $"0x{headCRC:X4}";
        block.Fields.Add(crcField);

        // HEAD_TYPE (1 byte)
        byte headType = reader.ReadByte();
        block.BlockTypeValue = headType;
        block.BlockType = GetRAR4BlockTypeName(headType);
        RARHeaderField typeField = cursor.EmitFixed("Block Type", 1, new[] { headType });
        typeField.Value = $"0x{headType:X2}";
        typeField.Description = block.BlockType;
        block.Fields.Add(typeField);

        // HEAD_FLAGS (2 bytes)
        ushort headFlags = reader.ReadUInt16();
        RARHeaderField flagsField = cursor.EmitFixed("Flags", 2, BitConverter.GetBytes(headFlags));
        flagsField.Value = $"0x{headFlags:X4}";
        AddRAR4FlagDescriptions(flagsField, headType, headFlags);
        block.Fields.Add(flagsField);

        // HEAD_SIZE (2 bytes)
        ushort headSize = reader.ReadUInt16();
        block.HeaderSize = headSize;
        RARHeaderField headSizeField = cursor.EmitFixed("Header Size", 2, BitConverter.GetBytes(headSize));
        headSizeField.Value = $"{headSize} bytes";
        block.Fields.Add(headSizeField);

        if (headSize < 7)
        {
            block.TotalSize = 7;
            return block;
        }

        block.TotalSize = headSize;

        // File headers (0x74), service blocks (0x7A), and any block with LONG_BLOCK flag have ADD_SIZE
        bool hasAddSize = (headFlags & 0x8000) != 0 ||
                          headType == 0x74 || headType == 0x7A;

        // ADD_SIZE (4 bytes) for file headers and blocks with LONG_BLOCK flag
        // Note: For file/service blocks, ADD_SIZE always exists and comes after the base 7-byte header
        uint addSize = 0;
        if (hasAddSize && stream.Position + 4 <= stream.Length)
        {
            // Read ADD_SIZE - it's packed data size that follows the header
            addSize = reader.ReadUInt32();
            RARHeaderField addSizeField = cursor.EmitFixed("Data Size (ADD_SIZE)", 4, BitConverter.GetBytes(addSize));
            addSizeField.Value = $"{addSize} bytes";
            block.Fields.Add(addSizeField);
            block.DataSize = addSize;
            block.HasData = addSize > 0;
            block.TotalSize = headSize + addSize;
        }

        long pos = cursor.Pos;

        // Parse type-specific fields
        switch (headType)
        {
            case 0x73: // Archive header
                ParseRAR4ArchiveHeader(reader, block, pos, blockStart + headSize);
                break;
            case 0x74: // File header
                ParseRAR4FileHeader(reader, block, pos, blockStart + headSize, headFlags, addSize);
                break;
            case 0x7A: // Service block (CMT, RR, etc.)
                ParseRAR4ServiceBlock(reader, block, pos, blockStart + headSize, headFlags, addSize);
                break;
            case 0x7B: // End of archive
                ParseRAR4EndBlock(reader, block, pos, blockStart + headSize, headFlags);
                break;
        }

        // Show data area for service blocks with data
        if (block.HasData && block.DataSize > 0 && headType == 0x7A)
        {
            long dataStart = blockStart + headSize;
            ParseRAR4DataArea(reader, stream, block, dataStart);
        }

        return block;
    }

    // RAR4 CMT data is stored when the Compression Method field reads "0x30".
    private static void ParseRAR4DataArea(BinaryReader reader, Stream stream, RARDetailedBlock block, long dataStart)
        => EmitCmtOrGenericDataArea(reader, stream, block, dataStart, b =>
            b.Fields.Any(f => f.Name == "Compression Method" && f.Value == "0x30"));

    /// <summary>
    /// Emits the "Data Area" fields for a parsed block. CMT service blocks up to 1 MB get their
    /// comment text decoded (when <paramref name="isStored"/> reports the data is uncompressed);
    /// everything else gets a generic size/offset placeholder. Shared by the RAR4 and RAR5 paths,
    /// which differ only in how they detect the stored flag.
    /// </summary>
    private static void EmitCmtOrGenericDataArea(
        BinaryReader reader, Stream stream, RARDetailedBlock block, long dataStart, Func<RARDetailedBlock, bool> isStored)
    {
        if (dataStart + block.DataSize > stream.Length)
        {
            return;
        }

        block.Fields.Add(new RARHeaderField { Name = "--- Data Area ---", Value = "" });

        // For CMT service blocks with stored data, decode the comment text.
        if (block.ItemName == "CMT" && block.DataSize <= 1_000_000)
        {
            if (block.DataSize > int.MaxValue)
            {
                return;
            }

            stream.Position = dataStart;
            byte[] data = reader.ReadBytes((int)block.DataSize);

            if (isStored(block))
            {
                string comment = Encoding.UTF8.GetString(data);
                block.Fields.Add(new RARHeaderField
                {
                    Name = "Comment Data",
                    Offset = dataStart,
                    Length = (int)block.DataSize,
                    RawBytes = data,
                    Value = comment,
                    Description = "Stored (uncompressed)"
                });
            }
            else
            {
                block.Fields.Add(new RARHeaderField
                {
                    Name = "Comment Data",
                    Offset = dataStart,
                    Length = (int)block.DataSize,
                    RawBytes = data,
                    Value = $"{block.DataSize:N0} bytes (compressed)",
                    Description = "Requires decompression to read"
                });
            }
        }
        else
        {
            block.Fields.Add(new RARHeaderField
            {
                Name = "Data",
                Offset = dataStart,
                Length = (int)Math.Min(block.DataSize, int.MaxValue),
                Value = $"{block.DataSize:N0} bytes at offset 0x{dataStart:X8}"
            });
        }
    }

    private static string GetRAR4BlockTypeName(byte type) => type switch
    {
        0x72 => "Marker Block",
        0x73 => "Archive Header",
        0x74 => "File Header",
        0x75 => "Comment (old)",
        0x76 => "Extra Info (old)",
        0x77 => "Subblock (old)",
        0x78 => "Recovery Record (old)",
        0x79 => "Auth Info (old)",
        0x7A => "Service Block",
        0x7B => "End of Archive",
        _ => $"Unknown (0x{type:X2})"
    };

    // C-style flag names and descriptions VERBATIM (Enum.GetName cannot reproduce the
    // descriptions, and the names must match upstream RAR exactly). Order matters: the
    // children are emitted in table order so existing snapshots stay byte-identical.
    private static readonly (ushort Mask, string Name, string Description)[] _rar4ArchiveFlags =
    [
        (0x0001, "VOLUME", "Multi-volume archive"),
        (0x0002, "COMMENT", "Archive comment present"),
        (0x0004, "LOCK", "Archive is locked"),
        (0x0008, "SOLID", "Solid archive"),
        (0x0010, "NEW_NUMBERING", "New volume naming scheme"),
        (0x0020, "AV", "Authenticity verification present"),
        (0x0040, "PROTECT", "Recovery record present"),
        (0x0080, "PASSWORD", "Headers are encrypted"),
        (0x0100, "FIRST_VOLUME", "First volume"),
    ];

    // File-header/service-block flags below DICT_SIZE (which is not a simple bit and is
    // emitted between these and the high flags, preserving the original ordering).
    private static readonly (ushort Mask, string Name, string Description)[] _rar4FileFlagsLow =
    [
        (0x0001, "SPLIT_BEFORE", "File continued from previous volume"),
        (0x0002, "SPLIT_AFTER", "File continues in next volume"),
        (0x0004, "PASSWORD", "File is encrypted"),
        (0x0008, "COMMENT", "File comment present"),
        (0x0010, "SOLID", "Info from previous files used"),
    ];

    private static readonly (ushort Mask, string Name, string Description)[] _rar4FileFlagsHigh =
    [
        (0x0100, "LARGE", "64-bit sizes"),
        (0x0200, "UNICODE", "Unicode filename"),
        (0x0400, "SALT", "Salt present"),
        (0x0800, "VERSION", "File version present"),
        (0x1000, "EXTTIME", "Extended time present"),
    ];

    private static readonly (ushort Mask, string Name, string Description)[] _rar4EndFlags =
    [
        (0x0001, "NEXT_VOLUME", "Archive continues in next volume"),
        (0x0002, "DATA_CRC", "Data CRC present"),
        (0x0004, "REV_SPACE", "Reserved space present"),
        (0x0008, "VOL_NUMBER", "Volume number present"),
    ];

    private static readonly (ushort Mask, string Name, string Description)[] _rar5HeaderFlags =
    [
        (0x0001, "HFL_EXTRA", "Extra area present"),
        (0x0002, "HFL_DATA", "Data area present"),
        (0x0004, "HFL_SKIPIFUNKNOWN", "Skip if unknown"),
        (0x0008, "HFL_SPLITBEFORE", "Split before"),
        (0x0010, "HFL_SPLITAFTER", "Split after"),
        (0x0020, "HFL_CHILD", "Child block"),
        (0x0040, "HFL_INHERITED", "Inherited"),
    ];

    private static void EmitFlags(
        RARHeaderField flagsField, ushort flags,
        (ushort Mask, string Name, string Description)[] table)
    {
        foreach ((ushort mask, string name, string description) in table)
        {
            if ((flags & mask) != 0)
            {
                flagsField.Children.Add(new RARHeaderField { Name = name, Value = description });
            }
        }
    }

    private static void AddRAR4FlagDescriptions(RARHeaderField flagsField, byte blockType, ushort flags)
    {
        // Common flags
        if ((flags & 0x8000) != 0)
        {
            flagsField.Children.Add(new RARHeaderField { Name = "LONG_BLOCK", Value = "Has ADD_SIZE field" });
        }

        if (blockType == 0x73) // Archive header
        {
            EmitFlags(flagsField, flags, _rar4ArchiveFlags);
        }
        else if (blockType is 0x74 or 0x7A) // File header or service block
        {
            EmitFlags(flagsField, flags, _rar4FileFlagsLow);

            // DICT_SIZE is not a simple bit: it is the 3-bit dictionary-size field
            // (bits 5-7) and is emitted between the low and high flags.
            int dictBits = (flags >> 5) & 0x7;
            string dictSize = dictBits switch
            {
                0 => "64 KB",
                1 => "128 KB",
                2 => "256 KB",
                3 => "512 KB",
                4 => "1024 KB",
                5 => "2048 KB",
                6 => "4096 KB",
                7 => "Directory",
                _ => "Unknown"
            };
            flagsField.Children.Add(new RARHeaderField { Name = "DICT_SIZE", Value = dictSize });

            EmitFlags(flagsField, flags, _rar4FileFlagsHigh);
        }
        else if (blockType == 0x7B) // End of archive
        {
            EmitFlags(flagsField, flags, _rar4EndFlags);
        }
    }

    private static void ParseRAR4ArchiveHeader(BinaryReader reader, RARDetailedBlock block, long pos, long headerEnd)
    {
        var cursor = new FieldCursor(reader, pos);

        // HighPosAV (2 bytes) - upper 16 bits of AV position
        if (cursor.Pos + 2 <= headerEnd)
        {
            ushort highPosAV = reader.ReadUInt16();
            RARHeaderField highPosField = cursor.EmitFixed("HighPosAV", 2, BitConverter.GetBytes(highPosAV));
            highPosField.Value = $"0x{highPosAV:X4}";
            block.Fields.Add(highPosField);
        }

        // PosAV (4 bytes) - AV position
        if (cursor.Pos + 4 <= headerEnd)
        {
            uint posAV = reader.ReadUInt32();
            RARHeaderField posField = cursor.EmitFixed("PosAV", 4, BitConverter.GetBytes(posAV));
            posField.Value = $"0x{posAV:X8}";
            block.Fields.Add(posField);
        }
    }

    private static void ParseRAR4FileHeader(BinaryReader reader, RARDetailedBlock block, long pos, long headerEnd, ushort flags, uint packSize)
    {
        var cursor = new FieldCursor(reader, pos);

        // UNP_SIZE (4 bytes)
        uint unpSize = 0;
        if (cursor.Pos + 4 <= headerEnd)
        {
            unpSize = reader.ReadUInt32();
            string? unpDesc = null;
            if (unpSize == 0xFFFFFFFF && (flags & 0x0100) == 0) // max without LARGE flag
            {
                unpDesc = "Custom packer sentinel (e.g. QCF) — size unreliable";
            }

            RARHeaderField unpField = cursor.EmitFixed("Unpacked Size", 4, BitConverter.GetBytes(unpSize));
            unpField.Value = $"{unpSize:N0} bytes";
            unpField.Description = unpDesc;
            block.Fields.Add(unpField);
        }

        // HOST_OS (1 byte)
        if (cursor.Pos + 1 <= headerEnd)
        {
            byte hostOs = reader.ReadByte();
            RARHeaderField hostField = cursor.EmitFixed("Host OS", 1, new[] { hostOs });
            hostField.Value = $"0x{hostOs:X2}";
            hostField.Description = RARPatcher.GetHostOSName(hostOs);
            block.Fields.Add(hostField);
        }

        // FILE_CRC (4 bytes)
        if (cursor.Pos + 4 <= headerEnd)
        {
            uint fileCRC = reader.ReadUInt32();
            RARHeaderField crcField = cursor.EmitFixed("File CRC32", 4, BitConverter.GetBytes(fileCRC));
            crcField.Value = $"0x{fileCRC:X8}";
            block.Fields.Add(crcField);
        }

        // FTIME (4 bytes) - DOS format
        if (cursor.Pos + 4 <= headerEnd)
        {
            uint ftime = reader.ReadUInt32();
            DateTime? dt = RARUtils.DosDateToDateTime(ftime);
            RARHeaderField ftimeField = cursor.EmitFixed("File Time (DOS)", 4, BitConverter.GetBytes(ftime));
            ftimeField.Value = $"0x{ftime:X8}";
            ftimeField.Description = dt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "Invalid";
            block.Fields.Add(ftimeField);
        }

        // UNP_VER (1 byte)
        if (cursor.Pos + 1 <= headerEnd)
        {
            byte unpVer = reader.ReadByte();
            RARHeaderField unpVerField = cursor.EmitFixed("Unpack Version", 1, new[] { unpVer });
            unpVerField.Value = $"{unpVer}";
            unpVerField.Description = $"RAR {unpVer / 10}.{unpVer % 10}";
            block.Fields.Add(unpVerField);
        }

        // METHOD (1 byte)
        if (cursor.Pos + 1 <= headerEnd)
        {
            byte method = reader.ReadByte();
            RARHeaderField methodField = cursor.EmitFixed("Compression Method", 1, new[] { method });
            methodField.Value = $"0x{method:X2}";
            methodField.Description = Core.Comparison.FileComparer.GetCompressionMethodName(method);
            block.Fields.Add(methodField);
        }

        // NAME_SIZE (2 bytes)
        ushort nameSize = 0;
        if (cursor.Pos + 2 <= headerEnd)
        {
            nameSize = reader.ReadUInt16();
            RARHeaderField nameSizeField = cursor.EmitFixed("Name Size", 2, BitConverter.GetBytes(nameSize));
            nameSizeField.Value = $"{nameSize} bytes";
            block.Fields.Add(nameSizeField);
        }

        // ATTR (4 bytes)
        if (cursor.Pos + 4 <= headerEnd)
        {
            uint attr = reader.ReadUInt32();
            RARHeaderField attrField = cursor.EmitFixed("File Attributes", 4, BitConverter.GetBytes(attr));
            attrField.Value = $"0x{attr:X8}";
            block.Fields.Add(attrField);
        }

        // HIGH_PACK_SIZE (4 bytes) - if LARGE flag set
        if ((flags & 0x0100) != 0 && cursor.Pos + 4 <= headerEnd)
        {
            uint highPack = reader.ReadUInt32();
            string? highPackDesc = null;
            if (highPack == 0xFFFFFFFF && unpSize == 0xFFFFFFFF)
            {
                highPackDesc = "Custom packer sentinel (e.g. RELOADED, HI2U) — size unreliable";
            }

            RARHeaderField highPackField = cursor.EmitFixed("High Pack Size", 4, BitConverter.GetBytes(highPack));
            highPackField.Value = $"0x{highPack:X8}";
            highPackField.Description = highPackDesc;
            block.Fields.Add(highPackField);

            // Update block sizes with the full 64-bit packed size
            long fullPackSize = packSize | ((long)highPack << 32);
            block.DataSize = fullPackSize;
            block.HasData = fullPackSize > 0;
            block.TotalSize = block.HeaderSize + fullPackSize;
        }

        // HIGH_UNP_SIZE (4 bytes) - if LARGE flag set
        if ((flags & 0x0100) != 0 && cursor.Pos + 4 <= headerEnd)
        {
            uint highUnp = reader.ReadUInt32();
            string? highUnpDesc = null;
            if (highUnp == 0xFFFFFFFF && unpSize == 0xFFFFFFFF)
            {
                highUnpDesc = "Custom packer sentinel (e.g. RELOADED, HI2U) — size unreliable";
            }

            RARHeaderField highUnpField = cursor.EmitFixed("High Unpack Size", 4, BitConverter.GetBytes(highUnp));
            highUnpField.Value = $"0x{highUnp:X8}";
            highUnpField.Description = highUnpDesc;
            block.Fields.Add(highUnpField);
        }

        // FILE_NAME (variable)
        if (nameSize > 0 && cursor.Pos + nameSize <= headerEnd)
        {
            byte[] nameBytes = reader.ReadBytes(nameSize);
            string fileName = RARUtils.DecodeFileName(nameBytes, (flags & 0x0200) != 0) ?? "";
            block.ItemName = fileName;
            RARHeaderField nameField = cursor.EmitFixed("File Name", nameSize, nameBytes);
            nameField.Value = fileName;
            nameField.Description = (flags & 0x0200) != 0 ? "Unicode encoded" : "OEM encoded";
            block.Fields.Add(nameField);
        }

        // SALT (8 bytes) - if SALT flag set
        if ((flags & 0x0400) != 0 && cursor.Pos + 8 <= headerEnd)
        {
            byte[] salt = reader.ReadBytes(8);
            RARHeaderField saltField = cursor.EmitFixed("Salt", 8, salt);
            saltField.Value = BitConverter.ToString(salt).Replace("-", " ", StringComparison.Ordinal);
            block.Fields.Add(saltField);
        }

        // EXT_TIME (variable) - if EXTTIME flag set
        if ((flags & 0x1000) != 0 && cursor.Pos + 2 <= headerEnd)
        {
            ushort extFlags = reader.ReadUInt16();
            RARHeaderField extFlagsField = cursor.EmitFixed("Extended Time Flags", 2, BitConverter.GetBytes(extFlags));
            extFlagsField.Value = $"0x{extFlags:X4}";

            // Decode flag bits for each timestamp
            string[] timeLabels = { "mtime", "ctime", "atime", "arctime" };
            string[] precisionLabels = { "DOS (1s)", "+1 byte (~6.5ms)", "+2 bytes (~25.6\u00B5s)", "+3 bytes (100ns)" };
            for (int t = 0; t < 4; t++)
            {
                int rmode = (extFlags >> ((3 - t) * 4)) & 0xF;
                bool present = (rmode & 0x8) != 0;
                bool roundUp = (rmode & 0x4) != 0;
                int extraBytes = rmode & 0x3;

                string desc = present
                    ? $"Present, {precisionLabels[extraBytes]}{(roundUp ? ", +1s rounding" : "")}"
                    : "Not present";

                extFlagsField.Children.Add(new RARHeaderField
                {
                    Name = $"{timeLabels[t]} [bits {(3 - t) * 4 + 3}-{(3 - t) * 4}]",
                    Value = $"0x{rmode:X} ({desc})"
                });
            }

            block.Fields.Add(extFlagsField);

            // Parse each time field (mtime, ctime, atime, arctime)
            for (int i = 0; i < 4 && cursor.Pos < headerEnd; i++)
            {
                int rmode = (extFlags >> ((3 - i) * 4)) & 0xF;
                if ((rmode & 0x8) == 0)
                {
                    continue;
                }

                // Time present
                if (i != 0 && cursor.Pos + 4 <= headerEnd)
                {
                    uint dosTime = reader.ReadUInt32();
                    string dosDesc = $"0x{dosTime:X8}";
                    DateTime? dt = RARUtils.DosDateToDateTime(dosTime);
                    if (dt.HasValue)
                    {
                        dosDesc += $" ({dt.Value:yyyy-MM-dd HH:mm:ss})";
                    }

                    RARHeaderField dosField = cursor.EmitFixed($"Ext {timeLabels[i]} DOS", 4, BitConverter.GetBytes(dosTime));
                    dosField.Value = dosDesc;
                    block.Fields.Add(dosField);
                }

                int count = rmode & 0x3;
                if (count > 0 && cursor.Pos + count <= headerEnd)
                {
                    byte[] remainder = reader.ReadBytes(count);

                    // Convert subsec bytes to 100ns ticks (same as RARHeaderReader.TryReadRemainder)
                    int ticks = 0;
                    for (int j = 0; j < count; j++)
                    {
                        ticks |= remainder[j] << ((j + 3 - count) * 8);
                    }

                    // Convert ticks (100ns units) to fractional seconds
                    double seconds = ticks / 10_000_000.0;
                    string hexStr = BitConverter.ToString(remainder).Replace("-", " ", StringComparison.Ordinal);

                    RARHeaderField subsecField = cursor.EmitFixed($"Ext {timeLabels[i]} subsec", count, remainder);
                    subsecField.Value = $"{hexStr} ({seconds:F7}s, {ticks} ticks)";
                    block.Fields.Add(subsecField);
                }
            }
        }
    }

    // Service blocks have same structure as file headers
    private static void ParseRAR4ServiceBlock(BinaryReader reader, RARDetailedBlock block, long pos, long headerEnd, ushort flags, uint packSize) =>
        ParseRAR4FileHeader(reader, block, pos, headerEnd, flags, packSize);

    private static void ParseRAR4EndBlock(BinaryReader reader, RARDetailedBlock block, long pos, long headerEnd, ushort flags)
    {
        var cursor = new FieldCursor(reader, pos);

        // Archive end flags
        if ((flags & 0x0002) != 0 && cursor.Pos + 4 <= headerEnd)
        {
            uint dataCRC = reader.ReadUInt32();
            RARHeaderField crcField = cursor.EmitFixed("Archive Data CRC", 4, BitConverter.GetBytes(dataCRC));
            crcField.Value = $"0x{dataCRC:X8}";
            block.Fields.Add(crcField);
        }

        // EARC_VOLNUMBER = 0x0008
        if ((flags & 0x0008) != 0 && cursor.Pos + 2 <= headerEnd)
        {
            ushort volNumber = reader.ReadUInt16();
            RARHeaderField volField = cursor.EmitFixed("Volume Number", 2, BitConverter.GetBytes(volNumber));
            volField.Value = volNumber.ToString();
            block.Fields.Add(volField);
        }
    }

    #endregion

    #region RAR 5.x Parsing

    private static void ParseRAR5(Stream stream, List<RARDetailedBlock> blocks)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        // Parse signature (8 bytes)
        long sigStart = stream.Position;
        if (stream.Length - sigStart >= 8)
        {
            var sigBlock = new RARDetailedBlock
            {
                BlockType = "Signature",
                BlockTypeValue = 0,
                StartOffset = sigStart,
                TotalSize = 8,
                HeaderSize = 8
            };

            byte[] sig = reader.ReadBytes(8);
            sigBlock.Fields.Add(new RARHeaderField
            {
                Name = "Signature",
                Offset = sigStart,
                Length = 8,
                RawBytes = sig,
                Value = BitConverter.ToString(sig).Replace("-", " ", StringComparison.Ordinal),
                Description = "RAR 5.x signature"
            });

            blocks.Add(sigBlock);
        }

        // Parse remaining blocks
        while (stream.Position < stream.Length)
        {
            RARDetailedBlock? block = ParseRAR5Block(reader, stream);
            if (block == null)
            {
                break;
            }

            blocks.Add(block);

            // Skip to next block
            long nextPos = block.StartOffset + block.TotalSize;
            if (nextPos <= block.StartOffset || nextPos > stream.Length)
            {
                break;
            }

            stream.Position = nextPos;
        }
    }

    private static RARDetailedBlock? ParseRAR5Block(BinaryReader reader, Stream stream)
    {
        long blockStart = stream.Position;

        if (stream.Position + 4 > stream.Length)
        {
            return null;
        }

        var block = new RARDetailedBlock { StartOffset = blockStart };
        var cursor = new FieldCursor(reader, blockStart);

        // HEAD_CRC (4 bytes)
        uint headCRC = reader.ReadUInt32();
        RARHeaderField crcField = cursor.EmitFixed("Header CRC32", 4, BitConverter.GetBytes(headCRC));
        crcField.Value = $"0x{headCRC:X8}";
        block.Fields.Add(crcField);

        // HEAD_SIZE (vint)
        ulong headSize = cursor.EmitVInt("Header Size", out RARHeaderField headSizeField);
        block.HeaderSize = (int)headSize + 4 + headSizeField.Length; // CRC + vint + header data
        headSizeField.Value = $"{headSize} bytes (vint)";
        block.Fields.Add(headSizeField);

        if (headSize == 0)
        {
            block.TotalSize = cursor.Pos - blockStart;
            return block;
        }

        // HEAD_TYPE (vint)
        ulong headType = cursor.EmitVInt("Header Type", out RARHeaderField headTypeField);
        block.BlockTypeValue = (byte)headType;
        block.BlockType = GetRAR5BlockTypeName((int)headType);
        headTypeField.Value = $"{headType}";
        headTypeField.Description = block.BlockType;
        block.Fields.Add(headTypeField);

        // HEAD_FLAGS (vint)
        ulong headFlags = cursor.EmitVInt("Header Flags", out RARHeaderField flagsField);
        flagsField.Value = FormatHex(headFlags, flagsField.Length);
        AddRAR5FlagDescriptions(flagsField, headFlags);
        block.Fields.Add(flagsField);

        long headerEnd = blockStart + block.HeaderSize;
        block.TotalSize = block.HeaderSize;

        // Extra area size (vint) - if HFL_EXTRA flag
        ulong extraAreaSize = 0;
        if ((headFlags & 0x0001) != 0)
        {
            extraAreaSize = cursor.EmitVInt("Extra Area Size", out RARHeaderField extraField);
            extraField.Value = $"{extraAreaSize} bytes";
            block.Fields.Add(extraField);
        }

        // Data size (vint) - if HFL_DATA flag
        if ((headFlags & 0x0002) != 0)
        {
            ulong dataSize = cursor.EmitVInt("Data Size", out RARHeaderField dataSizeField);
            dataSizeField.Value = $"{dataSize} bytes";
            block.Fields.Add(dataSizeField);
            block.DataSize = (long)dataSize;
            block.HasData = dataSize > 0;
            block.TotalSize += (long)dataSize;
        }

        long pos = cursor.Pos;

        // Parse type-specific fields
        switch ((int)headType)
        {
            case 1: // Main archive header
                ParseRAR5MainHeader(reader, block, pos, headerEnd);
                break;
            case 2: // File header
            case 3: // Service header
                ParseRAR5FileHeader(reader, block, pos, headerEnd);
                break;
            case 4: // Encryption header
                ParseRAR5EncryptionHeader(reader, block, pos, headerEnd);
                break;
            case 5: // End of archive
                ParseRAR5EndHeader(reader, block, pos, headerEnd);
                break;
        }

        // Parse extra area if present
        if (extraAreaSize > 0)
        {
            long extraStart = headerEnd - (long)extraAreaSize;
            if (extraStart >= blockStart && extraStart < headerEnd)
            {
                stream.Position = extraStart;
                ParseRAR5ExtraArea(reader, stream, block, headerEnd, (int)headType);
            }
        }

        // Show data area if present
        if (block.HasData && block.DataSize > 0)
        {
            long dataStart = headerEnd;
            ParseRAR5DataArea(reader, stream, block, dataStart);
        }

        return block;
    }

    private static string GetRAR5BlockTypeName(int type) => type switch
    {
        1 => "Main Archive Header",
        2 => "File Header",
        3 => "Service Header",
        4 => "Encryption Header",
        5 => "End of Archive",
        _ => $"Unknown ({type})"
    };

    private static void AddRAR5FlagDescriptions(RARHeaderField flagsField, ulong flags)
        => EmitFlags(flagsField, (ushort)flags, _rar5HeaderFlags);

    private static void ParseRAR5MainHeader(BinaryReader reader, RARDetailedBlock block, long pos, long headerEnd)
    {
        var cursor = new FieldCursor(reader, pos);

        // Archive flags (vint)
        if (cursor.Pos < headerEnd)
        {
            ulong archFlags = cursor.EmitVInt("Archive Flags", out RARHeaderField archFlagsField);
            archFlagsField.Value = FormatHex(archFlags, archFlagsField.Length);

            if ((archFlags & 0x0001) != 0)
            {
                archFlagsField.Children.Add(new RARHeaderField { Name = "VOLUME", Value = "Multi-volume" });
            }

            if ((archFlags & 0x0002) != 0)
            {
                archFlagsField.Children.Add(new RARHeaderField { Name = "VOLNUMBER", Value = "Volume number present" });
            }

            if ((archFlags & 0x0004) != 0)
            {
                archFlagsField.Children.Add(new RARHeaderField { Name = "SOLID", Value = "Solid archive" });
            }

            if ((archFlags & 0x0008) != 0)
            {
                archFlagsField.Children.Add(new RARHeaderField { Name = "PROTECT", Value = "Recovery record present" });
            }

            if ((archFlags & 0x0010) != 0)
            {
                archFlagsField.Children.Add(new RARHeaderField { Name = "LOCK", Value = "Locked archive" });
            }

            block.Fields.Add(archFlagsField);

            // Volume number (vint) - if VOLNUMBER flag
            if ((archFlags & 0x0002) != 0 && cursor.Pos < headerEnd)
            {
                ulong volNum = cursor.EmitVInt("Volume Number", out RARHeaderField volNumField);
                volNumField.Value = volNum.ToString();
                block.Fields.Add(volNumField);
            }
        }
    }

    private static void ParseRAR5FileHeader(BinaryReader reader, RARDetailedBlock block, long pos, long headerEnd)
    {
        var cursor = new FieldCursor(reader, pos);

        // File flags (vint)
        if (cursor.Pos < headerEnd)
        {
            ulong fileFlags = cursor.EmitVInt("File Flags", out RARHeaderField fileFlagsField);
            fileFlagsField.Value = FormatHex(fileFlags, fileFlagsField.Length);

            if ((fileFlags & 0x0001) != 0)
            {
                fileFlagsField.Children.Add(new RARHeaderField { Name = "DIRECTORY", Value = "Directory entry" });
            }

            if ((fileFlags & 0x0002) != 0)
            {
                fileFlagsField.Children.Add(new RARHeaderField { Name = "UTIME", Value = "Unix time present" });
            }

            if ((fileFlags & 0x0004) != 0)
            {
                fileFlagsField.Children.Add(new RARHeaderField { Name = "CRC32", Value = "CRC32 present" });
            }

            if ((fileFlags & 0x0008) != 0)
            {
                fileFlagsField.Children.Add(new RARHeaderField { Name = "UNPSIZE", Value = "Unpacked size unknown" });
            }

            block.Fields.Add(fileFlagsField);

            // Unpacked size (vint)
            if (cursor.Pos < headerEnd)
            {
                ulong unpSize = cursor.EmitVInt("Unpacked Size", out RARHeaderField unpSizeField);
                unpSizeField.Value = $"{unpSize:N0} bytes";
                block.Fields.Add(unpSizeField);
            }

            // Attributes (vint)
            if (cursor.Pos < headerEnd)
            {
                ulong attr = cursor.EmitVInt("Attributes", out RARHeaderField attrField);
                attrField.Value = FormatHex(attr, attrField.Length);
                block.Fields.Add(attrField);
            }

            // mtime (4 bytes) - if UTIME flag
            if ((fileFlags & 0x0002) != 0 && cursor.Pos + 4 <= headerEnd)
            {
                uint mtime = reader.ReadUInt32();
                DateTime dt = DateTimeOffset.FromUnixTimeSeconds(mtime).LocalDateTime;
                RARHeaderField mtimeField = cursor.EmitFixed("Modification Time", 4, BitConverter.GetBytes(mtime));
                mtimeField.Value = $"{mtime}";
                mtimeField.Description = dt.ToString("yyyy-MM-dd HH:mm:ss");
                block.Fields.Add(mtimeField);
            }

            // CRC32 (4 bytes) - if CRC32 flag
            if ((fileFlags & 0x0004) != 0 && cursor.Pos + 4 <= headerEnd)
            {
                uint crc = reader.ReadUInt32();
                RARHeaderField crcField = cursor.EmitFixed("Data CRC32", 4, BitConverter.GetBytes(crc));
                crcField.Value = $"0x{crc:X8}";
                block.Fields.Add(crcField);
            }

            // Compression info (vint)
            if (cursor.Pos < headerEnd)
            {
                ulong compInfo = cursor.EmitVInt("Compression Info", out RARHeaderField compInfoField);

                int version = (int)(compInfo & 0x3F);
                bool solid = (compInfo & 0x40) != 0;
                int method = (int)((compInfo >> 7) & 0x7);
                int dictSizeLog = (int)((compInfo >> 10) & 0xF);

                compInfoField.Value = FormatHex(compInfo, compInfoField.Length);

                string versionName = version switch
                {
                    0 => "RAR 5.0",
                    1 => "RAR 7.0",
                    _ => $"Unknown ({version})"
                };
                compInfoField.Children.Add(new RARHeaderField { Name = "VERSION", Value = versionName });
                compInfoField.Children.Add(new RARHeaderField { Name = "SOLID", Value = solid ? "Yes" : "No" });
                string methodName = method switch
                {
                    0 => "Store",
                    1 => "Fastest",
                    2 => "Fast",
                    3 => "Normal",
                    4 => "Good",
                    5 => "Best",
                    _ => $"Unknown ({method})"
                };
                compInfoField.Children.Add(new RARHeaderField { Name = "METHOD", Value = $"{method} ({methodName})" });
                compInfoField.Children.Add(new RARHeaderField { Name = "DICT_SIZE", Value = FormatDictSize(128L << dictSizeLog) });

                block.Fields.Add(compInfoField);
            }

            // Host OS (vint)
            if (cursor.Pos < headerEnd)
            {
                ulong hostOs = cursor.EmitVInt("Host OS", out RARHeaderField hostOsField);
                hostOsField.Value = hostOs.ToString();
                hostOsField.Description = hostOs == 0 ? "Windows" : "Unix";
                block.Fields.Add(hostOsField);
            }

            // Name length (vint)
            if (cursor.Pos < headerEnd)
            {
                ulong nameLen = cursor.EmitVInt("Name Length", out RARHeaderField nameLenField);
                nameLenField.Value = $"{nameLen} bytes";
                block.Fields.Add(nameLenField);

                // Name (UTF-8)
                if (nameLen > 0 && cursor.Pos + (long)nameLen <= headerEnd)
                {
                    byte[] nameBytes = reader.ReadBytes((int)nameLen);
                    string name = Encoding.UTF8.GetString(nameBytes);
                    block.ItemName = name;
                    RARHeaderField nameField = cursor.EmitFixed("File Name", (int)nameLen, nameBytes);
                    nameField.Value = name;
                    nameField.Description = "UTF-8 encoded";
                    block.Fields.Add(nameField);
                }
            }
        }
    }

    private static void ParseRAR5EncryptionHeader(BinaryReader reader, RARDetailedBlock block, long pos, long headerEnd)
    {
        var cursor = new FieldCursor(reader, pos);

        // Encryption version (vint)
        if (cursor.Pos < headerEnd)
        {
            ulong encVer = cursor.EmitVInt("Encryption Version", out RARHeaderField encVerField);
            encVerField.Value = encVer.ToString();
            block.Fields.Add(encVerField);
        }

        // Encryption flags (vint)
        if (cursor.Pos < headerEnd)
        {
            ulong encFlags = cursor.EmitVInt("Encryption Flags", out RARHeaderField encFlagsField);
            encFlagsField.Value = FormatHex(encFlags, encFlagsField.Length);
            block.Fields.Add(encFlagsField);
        }

        // KDF count (1 byte)
        if (cursor.Pos + 1 <= headerEnd)
        {
            byte kdfCount = reader.ReadByte();
            RARHeaderField kdfField = cursor.EmitFixed("KDF Count", 1, new[] { kdfCount });
            kdfField.Value = kdfCount.ToString();
            kdfField.Description = $"Iterations = 2^{kdfCount}";
            block.Fields.Add(kdfField);
        }

        // Salt (16 bytes)
        if (cursor.Pos + 16 <= headerEnd)
        {
            byte[] salt = reader.ReadBytes(16);
            RARHeaderField saltField = cursor.EmitFixed("Salt", 16, salt);
            saltField.Value = BitConverter.ToString(salt).Replace("-", " ", StringComparison.Ordinal);
            block.Fields.Add(saltField);
        }
    }

    private static void ParseRAR5EndHeader(BinaryReader reader, RARDetailedBlock block, long pos, long headerEnd)
    {
        var cursor = new FieldCursor(reader, pos);

        // End flags (vint)
        if (cursor.Pos < headerEnd)
        {
            ulong endFlags = cursor.EmitVInt("End Flags", out RARHeaderField endFlagsField);
            endFlagsField.Value = FormatHex(endFlags, endFlagsField.Length);

            if ((endFlags & 0x0001) != 0)
            {
                endFlagsField.Children.Add(new RARHeaderField { Name = "NEXTVOLUME", Value = "Archive continues" });
            }

            block.Fields.Add(endFlagsField);
        }
    }

    // RAR5 CMT data is stored when the METHOD child of Compression Info reads "0 ...".
    private static void ParseRAR5DataArea(BinaryReader reader, Stream stream, RARDetailedBlock block, long dataStart)
        => EmitCmtOrGenericDataArea(reader, stream, block, dataStart, b =>
            b.Fields.Any(f => f.Name == "Compression Info" && f.Children.Any(c =>
                c.Name == "METHOD" && c.Value.StartsWith("0 ", StringComparison.Ordinal))));

    private static void ParseRAR5ExtraArea(BinaryReader reader, Stream stream, RARDetailedBlock block, long headerEnd, int headType)
    {
        block.Fields.Add(new RARHeaderField { Name = "--- Extra Area ---", Value = "" });

        while (stream.Position + 2 <= headerEnd)
        {
            long recordStart = stream.Position;

            // Record size (vint) - size of type + data (not including this size vint itself)
            ulong fieldSize = ReadVInt(reader, stream);

            if (fieldSize == 0 || stream.Position + (long)fieldSize > headerEnd)
            {
                break;
            }

            long nextRecord = stream.Position + (long)fieldSize;

            // Record type (vint)
            ulong fieldType = ReadVInt(reader, stream);

            string recordName = GetExtraRecordName(headType, fieldType);

            var recordField = new RARHeaderField
            {
                Name = recordName,
                Offset = recordStart,
                Length = (int)(nextRecord - recordStart),
                Value = $"{fieldSize} bytes"
            };
            block.Fields.Add(recordField);

            // Parse type-specific sub-fields
            if (headType == 1) // Main Archive
            {
                ParseMainExtraRecord(reader, stream, block, fieldType, nextRecord);
            }
            else if (headType is 2 or 3) // File/Service
            {
                ParseFileExtraRecord(reader, stream, block, fieldType, nextRecord);
            }

            stream.Position = nextRecord;
        }
    }

    private static string GetExtraRecordName(int headType, ulong fieldType)
    {
        if (headType == 1) // Main Archive
        {
            return fieldType switch
            {
                1 => "Locator",
                2 => "Metadata",
                _ => $"Unknown Extra ({fieldType})"
            };
        }

        // File/Service
        return fieldType switch
        {
            1 => "Encryption",
            2 => "File Hash",
            3 => "File Time",
            4 => "File Version",
            5 => "Redirection",
            6 => "Unix Owner",
            7 => "Service Data",
            _ => $"Unknown Extra ({fieldType})"
        };
    }

    private static void ParseMainExtraRecord(BinaryReader reader, Stream stream, RARDetailedBlock block, ulong fieldType, long recordEnd)
    {
        switch (fieldType)
        {
            case 1: // Locator
                {
                    long vintStart = stream.Position;
                    ulong flags = ReadVInt(reader, stream);
                    int vintLen = (int)(stream.Position - vintStart);
                    var flagsField = new RARHeaderField
                    {
                        Name = "  Locator Flags",
                        Offset = vintStart,
                        Length = vintLen,
                        Value = FormatHex(flags, vintLen)
                    };
                    if ((flags & 0x01) != 0)
                    {
                        flagsField.Children.Add(new RARHeaderField { Name = "QLIST", Value = "Quick open offset present" });
                    }

                    if ((flags & 0x02) != 0)
                    {
                        flagsField.Children.Add(new RARHeaderField { Name = "RR", Value = "Recovery record offset present" });
                    }

                    block.Fields.Add(flagsField);

                    if ((flags & 0x01) != 0 && stream.Position < recordEnd)
                    {
                        vintStart = stream.Position;
                        ulong qOpenOffset = ReadVInt(reader, stream);
                        vintLen = (int)(stream.Position - vintStart);
                        block.Fields.Add(new RARHeaderField
                        {
                            Name = "  Quick Open Offset",
                            Offset = vintStart,
                            Length = vintLen,
                            Value = qOpenOffset == 0 ? "0 (not available)" : $"{qOpenOffset}",
                            Description = qOpenOffset != 0 ? $"Absolute: 0x{qOpenOffset + (ulong)block.StartOffset:X8}" : null
                        });
                    }

                    if ((flags & 0x02) != 0 && stream.Position < recordEnd)
                    {
                        vintStart = stream.Position;
                        ulong rrOffset = ReadVInt(reader, stream);
                        vintLen = (int)(stream.Position - vintStart);
                        block.Fields.Add(new RARHeaderField
                        {
                            Name = "  Recovery Record Offset",
                            Offset = vintStart,
                            Length = vintLen,
                            Value = rrOffset == 0 ? "0 (not available)" : $"{rrOffset}",
                            Description = rrOffset != 0 ? $"Absolute: 0x{rrOffset + (ulong)block.StartOffset:X8}" : null
                        });
                    }

                    break;
                }

            case 2: // Metadata
                {
                    long vintStart = stream.Position;
                    ulong flags = ReadVInt(reader, stream);
                    int vintLen = (int)(stream.Position - vintStart);
                    var flagsField = new RARHeaderField
                    {
                        Name = "  Metadata Flags",
                        Offset = vintStart,
                        Length = vintLen,
                        Value = FormatHex(flags, vintLen)
                    };
                    if ((flags & 0x01) != 0)
                    {
                        flagsField.Children.Add(new RARHeaderField { Name = "NAME", Value = "Archive name present" });
                    }

                    if ((flags & 0x02) != 0)
                    {
                        flagsField.Children.Add(new RARHeaderField { Name = "CTIME", Value = "Creation time present" });
                    }

                    if ((flags & 0x04) != 0)
                    {
                        flagsField.Children.Add(new RARHeaderField { Name = "UNIXTIME", Value = "Unix time format" });
                    }

                    if ((flags & 0x08) != 0)
                    {
                        flagsField.Children.Add(new RARHeaderField { Name = "UNIX_NS", Value = "Nanosecond precision" });
                    }

                    block.Fields.Add(flagsField);

                    if ((flags & 0x01) != 0 && stream.Position < recordEnd)
                    {
                        vintStart = stream.Position;
                        ulong nameSize = ReadVInt(reader, stream);
                        vintLen = (int)(stream.Position - vintStart);
                        if (nameSize > 0 && stream.Position + (long)nameSize <= recordEnd)
                        {
                            byte[] nameBytes = reader.ReadBytes((int)nameSize);
                            string name = Encoding.UTF8.GetString(nameBytes);
                            block.Fields.Add(new RARHeaderField
                            {
                                Name = "  Archive Name",
                                Offset = vintStart,
                                Length = vintLen + (int)nameSize,
                                Value = name
                            });
                        }
                    }

                    if ((flags & 0x02) != 0 && stream.Position < recordEnd)
                    {
                        bool unixTime = (flags & 0x04) != 0;
                        bool unixNs = (flags & 0x08) != 0;
                        long timePos = stream.Position;

                        if (unixTime && unixNs && stream.Position + 8 <= recordEnd)
                        {
                            long ns = reader.ReadInt64();
                            DateTime dt = DateTimeOffset.FromUnixTimeSeconds(ns / 1_000_000_000).AddTicks(ns % 1_000_000_000 / 100).LocalDateTime;
                            block.Fields.Add(new RARHeaderField
                            {
                                Name = "  Creation Time",
                                Offset = timePos,
                                Length = 8,
                                Value = dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff")
                            });
                        }
                        else if (unixTime && stream.Position + 4 <= recordEnd)
                        {
                            uint ts = reader.ReadUInt32();
                            DateTime dt = DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime;
                            block.Fields.Add(new RARHeaderField
                            {
                                Name = "  Creation Time",
                                Offset = timePos,
                                Length = 4,
                                Value = dt.ToString("yyyy-MM-dd HH:mm:ss")
                            });
                        }
                        else if (!unixTime && stream.Position + 8 <= recordEnd)
                        {
                            long ft = reader.ReadInt64();
                            var dt = DateTime.FromFileTime(ft);
                            block.Fields.Add(new RARHeaderField
                            {
                                Name = "  Creation Time",
                                Offset = timePos,
                                Length = 8,
                                Value = dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff")
                            });
                        }
                    }

                    break;
                }
        }
    }

    private static void ParseFileExtraRecord(BinaryReader reader, Stream stream, RARDetailedBlock block, ulong fieldType, long recordEnd)
    {
        switch (fieldType)
        {
            case 2: // File Hash
                {
                    long vintStart = stream.Position;
                    ulong hashType = ReadVInt(reader, stream);
                    int vintLen = (int)(stream.Position - vintStart);
                    string hashTypeName = hashType == 0 ? "BLAKE2sp" : $"Unknown ({hashType})";
                    block.Fields.Add(new RARHeaderField
                    {
                        Name = "  Hash Type",
                        Offset = vintStart,
                        Length = vintLen,
                        Value = hashTypeName
                    });

                    if (hashType == 0 && stream.Position + 32 <= recordEnd)
                    {
                        long hashPos = stream.Position;
                        byte[] hash = reader.ReadBytes(32);
                        block.Fields.Add(new RARHeaderField
                        {
                            Name = "  BLAKE2sp Hash",
                            Offset = hashPos,
                            Length = 32,
                            RawBytes = hash,
                            Value = Convert.ToHexString(hash)
                        });
                    }

                    break;
                }

            case 3: // File Time (HTIME)
                {
                    long vintStart = stream.Position;
                    ulong flags = ReadVInt(reader, stream);
                    int vintLen = (int)(stream.Position - vintStart);
                    bool unixTime = (flags & 0x01) != 0;
                    bool unixNs = (flags & 0x10) != 0;

                    var flagsField = new RARHeaderField
                    {
                        Name = "  Time Flags",
                        Offset = vintStart,
                        Length = vintLen,
                        Value = FormatHex(flags, vintLen)
                    };
                    if (unixTime)
                    {
                        flagsField.Children.Add(new RARHeaderField { Name = "UNIXTIME", Value = "Unix time format" });
                    }

                    if ((flags & 0x02) != 0)
                    {
                        flagsField.Children.Add(new RARHeaderField { Name = "MTIME", Value = "Modification time present" });
                    }

                    if ((flags & 0x04) != 0)
                    {
                        flagsField.Children.Add(new RARHeaderField { Name = "CTIME", Value = "Creation time present" });
                    }

                    if ((flags & 0x08) != 0)
                    {
                        flagsField.Children.Add(new RARHeaderField { Name = "ATIME", Value = "Access time present" });
                    }

                    if (unixNs)
                    {
                        flagsField.Children.Add(new RARHeaderField { Name = "UNIX_NS", Value = "Nanosecond precision" });
                    }

                    block.Fields.Add(flagsField);

                    int timeSize = unixTime ? 4 : 8;

                    if ((flags & 0x02) != 0 && stream.Position + timeSize <= recordEnd)
                    {
                        AddTimeField(reader, block, "  Modification Time", stream.Position, unixTime);
                    }

                    if ((flags & 0x04) != 0 && stream.Position + timeSize <= recordEnd)
                    {
                        AddTimeField(reader, block, "  Creation Time", stream.Position, unixTime);
                    }

                    if ((flags & 0x08) != 0 && stream.Position + timeSize <= recordEnd)
                    {
                        AddTimeField(reader, block, "  Access Time", stream.Position, unixTime);
                    }

                    // Nanosecond fields
                    if (unixTime && unixNs)
                    {
                        if ((flags & 0x02) != 0 && stream.Position + 4 <= recordEnd)
                        {
                            long nsPos = stream.Position;
                            uint ns = reader.ReadUInt32() & 0x3FFFFFFF;
                            block.Fields.Add(new RARHeaderField
                            {
                                Name = "  MTime Nanoseconds",
                                Offset = nsPos,
                                Length = 4,
                                Value = $"{ns} ns"
                            });
                        }

                        if ((flags & 0x04) != 0 && stream.Position + 4 <= recordEnd)
                        {
                            long nsPos = stream.Position;
                            uint ns = reader.ReadUInt32() & 0x3FFFFFFF;
                            block.Fields.Add(new RARHeaderField
                            {
                                Name = "  CTime Nanoseconds",
                                Offset = nsPos,
                                Length = 4,
                                Value = $"{ns} ns"
                            });
                        }

                        if ((flags & 0x08) != 0 && stream.Position + 4 <= recordEnd)
                        {
                            long nsPos = stream.Position;
                            uint ns = reader.ReadUInt32() & 0x3FFFFFFF;
                            block.Fields.Add(new RARHeaderField
                            {
                                Name = "  ATime Nanoseconds",
                                Offset = nsPos,
                                Length = 4,
                                Value = $"{ns} ns"
                            });
                        }
                    }

                    break;
                }

            case 1: // Encryption
                {
                    long vintStart = stream.Position;
                    ulong encVersion = ReadVInt(reader, stream);
                    int vintLen = (int)(stream.Position - vintStart);
                    block.Fields.Add(new RARHeaderField
                    {
                        Name = "  Encryption Version",
                        Offset = vintStart,
                        Length = vintLen,
                        Value = encVersion.ToString()
                    });

                    if (stream.Position < recordEnd)
                    {
                        vintStart = stream.Position;
                        ulong encFlags = ReadVInt(reader, stream);
                        vintLen = (int)(stream.Position - vintStart);
                        var encFlagsField = new RARHeaderField
                        {
                            Name = "  Encryption Flags",
                            Offset = vintStart,
                            Length = vintLen,
                            Value = FormatHex(encFlags, vintLen)
                        };
                        if ((encFlags & 0x01) != 0)
                        {
                            encFlagsField.Children.Add(new RARHeaderField { Name = "PSWCHECK", Value = "Password check present" });
                        }

                        if ((encFlags & 0x02) != 0)
                        {
                            encFlagsField.Children.Add(new RARHeaderField { Name = "HASHMAC", Value = "Hash MAC present" });
                        }

                        block.Fields.Add(encFlagsField);

                        if (stream.Position + 1 <= recordEnd)
                        {
                            long kdfPos = stream.Position;
                            byte kdfCount = reader.ReadByte();
                            block.Fields.Add(new RARHeaderField
                            {
                                Name = "  KDF Count",
                                Offset = kdfPos,
                                Length = 1,
                                Value = kdfCount.ToString(),
                                Description = $"Iterations = 2^{kdfCount}"
                            });
                        }

                        if (stream.Position + 16 <= recordEnd)
                        {
                            long saltPos = stream.Position;
                            byte[] salt = reader.ReadBytes(16);
                            block.Fields.Add(new RARHeaderField
                            {
                                Name = "  Salt",
                                Offset = saltPos,
                                Length = 16,
                                RawBytes = salt,
                                Value = BitConverter.ToString(salt).Replace("-", " ", StringComparison.Ordinal)
                            });
                        }

                        if (stream.Position + 16 <= recordEnd)
                        {
                            long ivPos = stream.Position;
                            byte[] iv = reader.ReadBytes(16);
                            block.Fields.Add(new RARHeaderField
                            {
                                Name = "  IV",
                                Offset = ivPos,
                                Length = 16,
                                RawBytes = iv,
                                Value = BitConverter.ToString(iv).Replace("-", " ", StringComparison.Ordinal)
                            });
                        }
                    }

                    break;
                }

            case 4: // File Version
                {
                    long vintStart = stream.Position;
                    ulong flags = ReadVInt(reader, stream);
                    int vintLen = (int)(stream.Position - vintStart);
                    block.Fields.Add(new RARHeaderField
                    {
                        Name = "  Version Flags",
                        Offset = vintStart,
                        Length = vintLen,
                        Value = FormatHex(flags, vintLen)
                    });

                    if (stream.Position < recordEnd)
                    {
                        vintStart = stream.Position;
                        ulong version = ReadVInt(reader, stream);
                        vintLen = (int)(stream.Position - vintStart);
                        block.Fields.Add(new RARHeaderField
                        {
                            Name = "  Version Number",
                            Offset = vintStart,
                            Length = vintLen,
                            Value = version.ToString()
                        });
                    }

                    break;
                }

            case 5: // Redirection
                {
                    long vintStart = stream.Position;
                    ulong redirType = ReadVInt(reader, stream);
                    int vintLen = (int)(stream.Position - vintStart);
                    string redirTypeName = redirType switch
                    {
                        1 => "Unix symlink",
                        2 => "Windows symlink",
                        3 => "Windows junction",
                        4 => "Hard link",
                        5 => "File copy",
                        _ => $"Unknown ({redirType})"
                    };
                    block.Fields.Add(new RARHeaderField
                    {
                        Name = "  Redirect Type",
                        Offset = vintStart,
                        Length = vintLen,
                        Value = redirTypeName
                    });

                    if (stream.Position < recordEnd)
                    {
                        vintStart = stream.Position;
                        ulong flags = ReadVInt(reader, stream);
                        vintLen = (int)(stream.Position - vintStart);
                        var flagsField = new RARHeaderField
                        {
                            Name = "  Redirect Flags",
                            Offset = vintStart,
                            Length = vintLen,
                            Value = FormatHex(flags, vintLen)
                        };
                        if ((flags & 0x01) != 0)
                        {
                            flagsField.Children.Add(new RARHeaderField { Name = "DIRECTORY", Value = "Directory redirect" });
                        }

                        block.Fields.Add(flagsField);
                    }

                    if (stream.Position < recordEnd)
                    {
                        vintStart = stream.Position;
                        ulong nameLen = ReadVInt(reader, stream);
                        vintLen = (int)(stream.Position - vintStart);
                        if (nameLen > 0 && stream.Position + (long)nameLen <= recordEnd)
                        {
                            byte[] nameBytes = reader.ReadBytes((int)nameLen);
                            string targetName = Encoding.UTF8.GetString(nameBytes);
                            block.Fields.Add(new RARHeaderField
                            {
                                Name = "  Target Name",
                                Offset = vintStart,
                                Length = vintLen + (int)nameLen,
                                Value = targetName
                            });
                        }
                    }

                    break;
                }

            case 6: // Unix Owner
                {
                    long vintStart = stream.Position;
                    ulong flags = ReadVInt(reader, stream);
                    int vintLen = (int)(stream.Position - vintStart);
                    var flagsField = new RARHeaderField
                    {
                        Name = "  Owner Flags",
                        Offset = vintStart,
                        Length = vintLen,
                        Value = FormatHex(flags, vintLen)
                    };
                    if ((flags & 0x01) != 0)
                    {
                        flagsField.Children.Add(new RARHeaderField { Name = "UNAME", Value = "User name present" });
                    }

                    if ((flags & 0x02) != 0)
                    {
                        flagsField.Children.Add(new RARHeaderField { Name = "GNAME", Value = "Group name present" });
                    }

                    if ((flags & 0x04) != 0)
                    {
                        flagsField.Children.Add(new RARHeaderField { Name = "NUMUID", Value = "Numeric UID present" });
                    }

                    if ((flags & 0x08) != 0)
                    {
                        flagsField.Children.Add(new RARHeaderField { Name = "NUMGID", Value = "Numeric GID present" });
                    }

                    block.Fields.Add(flagsField);

                    if ((flags & 0x01) != 0 && stream.Position < recordEnd)
                    {
                        vintStart = stream.Position;
                        ulong nameLen = ReadVInt(reader, stream);
                        vintLen = (int)(stream.Position - vintStart);
                        if (nameLen > 0 && stream.Position + (long)nameLen <= recordEnd)
                        {
                            byte[] nameBytes = reader.ReadBytes((int)nameLen);
                            block.Fields.Add(new RARHeaderField
                            {
                                Name = "  User Name",
                                Offset = vintStart,
                                Length = vintLen + (int)nameLen,
                                Value = Encoding.UTF8.GetString(nameBytes)
                            });
                        }
                    }

                    if ((flags & 0x02) != 0 && stream.Position < recordEnd)
                    {
                        vintStart = stream.Position;
                        ulong nameLen = ReadVInt(reader, stream);
                        vintLen = (int)(stream.Position - vintStart);
                        if (nameLen > 0 && stream.Position + (long)nameLen <= recordEnd)
                        {
                            byte[] nameBytes = reader.ReadBytes((int)nameLen);
                            block.Fields.Add(new RARHeaderField
                            {
                                Name = "  Group Name",
                                Offset = vintStart,
                                Length = vintLen + (int)nameLen,
                                Value = Encoding.UTF8.GetString(nameBytes)
                            });
                        }
                    }

                    if ((flags & 0x04) != 0 && stream.Position < recordEnd)
                    {
                        vintStart = stream.Position;
                        ulong uid = ReadVInt(reader, stream);
                        vintLen = (int)(stream.Position - vintStart);
                        block.Fields.Add(new RARHeaderField
                        {
                            Name = "  UID",
                            Offset = vintStart,
                            Length = vintLen,
                            Value = uid.ToString()
                        });
                    }

                    if ((flags & 0x08) != 0 && stream.Position < recordEnd)
                    {
                        vintStart = stream.Position;
                        ulong gid = ReadVInt(reader, stream);
                        vintLen = (int)(stream.Position - vintStart);
                        block.Fields.Add(new RARHeaderField
                        {
                            Name = "  GID",
                            Offset = vintStart,
                            Length = vintLen,
                            Value = gid.ToString()
                        });
                    }

                    break;
                }
        }
    }

    private static void AddTimeField(BinaryReader reader, RARDetailedBlock block, string name, long pos, bool unixTime)
    {
        if (unixTime)
        {
            uint ts = reader.ReadUInt32();
            DateTime dt = DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime;
            block.Fields.Add(new RARHeaderField
            {
                Name = name,
                Offset = pos,
                Length = 4,
                Value = dt.ToString("yyyy-MM-dd HH:mm:ss")
            });
        }
        else
        {
            long ft = reader.ReadInt64();
            var dt = DateTime.FromFileTime(ft);
            block.Fields.Add(new RARHeaderField
            {
                Name = name,
                Offset = pos,
                Length = 8,
                Value = dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff")
            });
        }
    }

    /// <summary>
    /// A small cursor over the RAR5 detailed parser's <c>pos</c>/<see cref="Stream.Position"/>
    /// pair. It factors out the repetitive "create a <see cref="RARHeaderField"/> with the
    /// right Offset/Length/RawBytes and advance the position" bookkeeping while leaving value
    /// formatting and Children/Description decoration to the call site.
    /// </summary>
    /// <remarks>
    /// Preserves the original vint-vs-fixed asymmetry: vint fields leave <see
    /// cref="RARHeaderField.RawBytes"/> empty and derive their length from how far the stream
    /// advanced, while fixed fields carry their raw bytes and advance the cursor by a fixed width.
    /// The cursor keeps <see cref="Pos"/> coupled to the underlying <see cref="Stream.Position"/>
    /// exactly as the hand-rolled code did (vint sets <c>Pos = stream.Position</c>; fixed advances
    /// <c>Pos</c> by the field width after the caller's read moved the stream).
    /// </remarks>
    private sealed class FieldCursor(BinaryReader reader, long pos)
    {
        private readonly Stream _stream = reader.BaseStream;

        /// <summary>Current byte offset from the start of the file.</summary>
        public long Pos { get; set; } = pos;

        /// <summary>
        /// Reads a vint, returning its raw value and a field with Offset/Length set (RawBytes
        /// intentionally left empty). Advances the cursor to the post-read stream position.
        /// The caller sets <see cref="RARHeaderField.Value"/> and any decoration.
        /// </summary>
        public ulong EmitVInt(string name, out RARHeaderField field)
        {
            long start = Pos;
            ulong value = ReadVInt(reader, _stream);
            int length = (int)(_stream.Position - start);
            field = new RARHeaderField { Name = name, Offset = start, Length = length };
            Pos = _stream.Position;
            return value;
        }

        /// <summary>
        /// Builds a fixed-width field with Offset/Length/RawBytes set and advances the cursor by
        /// <paramref name="length"/>. The caller is responsible for the actual stream read (which
        /// has already moved <see cref="Stream.Position"/>) and for setting the formatted value.
        /// </summary>
        public RARHeaderField EmitFixed(string name, int length, ReadOnlyMemory<byte> rawBytes)
        {
            var field = new RARHeaderField
            {
                Name = name,
                Offset = Pos,
                Length = length,
                RawBytes = rawBytes
            };
            Pos += length;
            return field;
        }
    }

    private static ulong ReadVInt(BinaryReader reader, Stream stream)
    {
        ulong result = 0;
        int shift = 0;

        while (stream.Position < stream.Length)
        {
            byte b = reader.ReadByte();
            result |= ((ulong)(b & 0x7F)) << shift;
            if ((b & 0x80) == 0)
            {
                break;
            }

            shift += 7;
            if (shift > 63)
            {
                break;
            }
        }

        return result;
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Formats a dictionary size in KB to a human-friendly string (e.g., "128 KB", "4 MB", "1 GB").
    /// </summary>
    private static string FormatDictSize(long sizeKB)
    {
        if (sizeKB >= 1024 * 1024)
        {
            return $"{sizeKB / (1024 * 1024)} GB";
        }

        if (sizeKB >= 1024)
        {
            return $"{sizeKB / 1024} MB";
        }

        return $"{sizeKB} KB";
    }

    #endregion
}
