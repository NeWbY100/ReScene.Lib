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
    public long Offset { get; set; }

    /// <summary>
    /// Length in bytes.
    /// </summary>
    public int Length { get; set; }

    /// <summary>
    /// Raw bytes of this field.
    /// </summary>
    public byte[] RawBytes { get; set; } = [];

    /// <summary>
    /// Formatted display value.
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Additional description or decoded meaning.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Child fields (for nested structures like flags).
    /// </summary>
    public List<RARHeaderField> Children { get; set; } = [];

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
    public byte BlockTypeValue { get; set; }

    /// <summary>
    /// Start offset of this block.
    /// </summary>
    public long StartOffset { get; set; }

    /// <summary>
    /// Total block size (header + data).
    /// </summary>
    public long TotalSize { get; set; }

    /// <summary>
    /// Header size only.
    /// </summary>
    public int HeaderSize { get; set; }

    /// <summary>
    /// All fields in this block header.
    /// </summary>
    public List<RARHeaderField> Fields { get; set; } = [];

    /// <summary>
    /// True if this block has associated data after the header.
    /// </summary>
    public bool HasData { get; set; }

    /// <summary>
    /// Size of data after header.
    /// </summary>
    public long DataSize { get; set; }

    /// <summary>
    /// For file/service blocks: the item name.
    /// </summary>
    public string? ItemName { get; set; }
}

/// <summary>
/// Parses RAR files and extracts detailed header information with byte offsets.
/// </summary>
public class RARDetailedParser
{
    /// <summary>
    /// Parses a RAR file and returns all header blocks with detailed field information.
    /// </summary>
    public static List<RARDetailedBlock> Parse(string filePath, bool enableSfx = false)
    {
        using var fs = File.OpenRead(filePath);
        return Parse(fs, enableSfx);
    }

    /// <summary>
    /// Parses a RAR stream and returns all header blocks with detailed field information.
    /// </summary>
    public static List<RARDetailedBlock> Parse(Stream stream, bool enableSfx = false)
    {
        var blocks = new List<RARDetailedBlock>();

        // Check RAR version at position 0
        bool isRAR5 = RAR5HeaderReader.IsRAR5(stream);
        stream.Position = 0;

        if (!HasValidRARSignature(stream))
        {
            if (!enableSfx)
                return blocks;

            // SFX mode: scan for the RAR marker within the executable
            long offset = RARUtils.FindRarMarkerOffset(stream);
            if (offset < 0)
                return blocks;

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
    public static List<RARDetailedBlock> ParseFromPosition(Stream stream)
    {
        var blocks = new List<RARDetailedBlock>();

        if (!HasValidRARSignature(stream))
            return blocks;

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

    private static readonly byte[] RAR4Signature = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];

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
            return false;

        byte[] buf = new byte[8];
        int read = stream.Read(buf, 0, 8);
        stream.Position = pos;

        if (read < 7)
            return false;

        // Check RAR4 signature (7 bytes)
        bool isRar4 = true;
        for (int i = 0; i < 7; i++)
        {
            if (buf[i] != RAR4Signature[i])
            {
                isRar4 = false;
                break;
            }
        }
        if (isRar4)
            return true;

        // Check RAR5 signature (8 bytes)
        if (read < 8)
            return false;

        for (int i = 0; i < 8; i++)
        {
            if (buf[i] != RAR5HeaderReader.RAR5Marker[i])
                return false;
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
                Value = BitConverter.ToString(sig).Replace("-", " "),
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
                var block = ParseRAR4Block(reader, stream);
                if (block == null) break;
                blocks.Add(block);
                blockCount++;

                // Calculate next block position: header start + header size + data size
                long nextPos = block.StartOffset + block.TotalSize;

                // Safety check: ensure we're making forward progress
                if (nextPos <= blockStart)
                    break;

                // Check if next position is within file bounds
                if (nextPos > stream.Length)
                    break;

                // Check for end of archive block
                if (block.BlockTypeValue == 0x7B)
                    break;

                stream.Position = nextPos;
            }
            catch
            {
                // Skip to try finding more blocks - move forward by minimum header size
                stream.Position = blockStart + 7;
                if (stream.Position > stream.Length - 7)
                    break;
            }
        }
    }

    private static bool IsValidRAR4Signature(byte[] sig)
    {
        if (sig.Length < 7) return false;
        return sig[0] == 0x52 && sig[1] == 0x61 && sig[2] == 0x72 &&
               sig[3] == 0x21 && sig[4] == 0x1A && sig[5] == 0x07 && sig[6] == 0x00;
    }

    private static RARDetailedBlock? ParseRAR4Block(BinaryReader reader, Stream stream)
    {
        long blockStart = stream.Position;

        if (blockStart + 7 > stream.Length)
            return null;

        var block = new RARDetailedBlock { StartOffset = blockStart };

        // Ensure we're at the right position (in case stream was repositioned)
        if (reader.BaseStream.Position != blockStart)
        {
            reader.BaseStream.Position = blockStart;
        }

        // Read base header
        long pos = blockStart;

        // HEAD_CRC (2 bytes)
        ushort headCrc = reader.ReadUInt16();
        block.Fields.Add(new RARHeaderField
        {
            Name = "Header CRC",
            Offset = pos,
            Length = 2,
            RawBytes = BitConverter.GetBytes(headCrc),
            Value = $"0x{headCrc:X4}"
        });
        pos += 2;

        // HEAD_TYPE (1 byte)
        byte headType = reader.ReadByte();
        block.BlockTypeValue = headType;
        block.BlockType = GetRAR4BlockTypeName(headType);
        block.Fields.Add(new RARHeaderField
        {
            Name = "Block Type",
            Offset = pos,
            Length = 1,
            RawBytes = new[] { headType },
            Value = $"0x{headType:X2}",
            Description = block.BlockType
        });
        pos += 1;

        // HEAD_FLAGS (2 bytes)
        ushort headFlags = reader.ReadUInt16();
        var flagsField = new RARHeaderField
        {
            Name = "Flags",
            Offset = pos,
            Length = 2,
            RawBytes = BitConverter.GetBytes(headFlags),
            Value = $"0x{headFlags:X4}"
        };
        AddRAR4FlagDescriptions(flagsField, headType, headFlags);
        block.Fields.Add(flagsField);
        pos += 2;

        // HEAD_SIZE (2 bytes)
        ushort headSize = reader.ReadUInt16();
        block.HeaderSize = headSize;
        block.Fields.Add(new RARHeaderField
        {
            Name = "Header Size",
            Offset = pos,
            Length = 2,
            RawBytes = BitConverter.GetBytes(headSize),
            Value = $"{headSize} bytes"
        });
        pos += 2;

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
            block.Fields.Add(new RARHeaderField
            {
                Name = "Data Size (ADD_SIZE)",
                Offset = pos,
                Length = 4,
                RawBytes = BitConverter.GetBytes(addSize),
                Value = $"{addSize} bytes"
            });
            pos += 4;
            block.DataSize = addSize;
            block.HasData = addSize > 0;
            block.TotalSize = headSize + addSize;
        }

        // Parse type-specific fields
        switch (headType)
        {
            case 0x73: // Archive header
                ParseRAR4ArchiveHeader(reader, stream, block, pos, blockStart + headSize, headFlags);
                break;
            case 0x74: // File header
                ParseRAR4FileHeader(reader, stream, block, pos, blockStart + headSize, headFlags, addSize);
                break;
            case 0x7A: // Service block (CMT, RR, etc.)
                ParseRAR4ServiceBlock(reader, stream, block, pos, blockStart + headSize, headFlags, addSize);
                break;
            case 0x7B: // End of archive
                ParseRAR4EndBlock(reader, stream, block, pos, blockStart + headSize, headFlags);
                break;
        }

        // Show data area for service blocks with data
        if (block.HasData && block.DataSize > 0 && headType == 0x7A)
        {
            long dataStart = blockStart + headSize;
            ParseRAR4DataArea(reader, stream, block, dataStart, headFlags);
        }

        return block;
    }

    private static void ParseRAR4DataArea(BinaryReader reader, Stream stream, RARDetailedBlock block, long dataStart, ushort flags)
    {
        if (dataStart + block.DataSize > stream.Length)
            return;

        block.Fields.Add(new RARHeaderField { Name = "--- Data Area ---", Value = "" });

        // Check if this is a CMT block and if the data is stored (method byte 0x30 = Store)
        if (block.ItemName == "CMT" && block.DataSize <= 1_000_000)
        {
            bool isStored = block.Fields.Exists(f =>
                f.Name == "Compression Method" && f.Value == "0x30");

            if (block.DataSize > int.MaxValue)
                return;
            stream.Position = dataStart;
            byte[] data = reader.ReadBytes((int)block.DataSize);

            if (isStored)
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

    private static void AddRAR4FlagDescriptions(RARHeaderField flagsField, byte blockType, ushort flags)
    {
        // Common flags
        if ((flags & 0x8000) != 0)
            flagsField.Children.Add(new RARHeaderField { Name = "LONG_BLOCK", Value = "Has ADD_SIZE field" });

        if (blockType == 0x73) // Archive header
        {
            if ((flags & 0x0001) != 0) flagsField.Children.Add(new RARHeaderField { Name = "VOLUME", Value = "Multi-volume archive" });
            if ((flags & 0x0002) != 0) flagsField.Children.Add(new RARHeaderField { Name = "COMMENT", Value = "Archive comment present" });
            if ((flags & 0x0004) != 0) flagsField.Children.Add(new RARHeaderField { Name = "LOCK", Value = "Archive is locked" });
            if ((flags & 0x0008) != 0) flagsField.Children.Add(new RARHeaderField { Name = "SOLID", Value = "Solid archive" });
            if ((flags & 0x0010) != 0) flagsField.Children.Add(new RARHeaderField { Name = "NEW_NUMBERING", Value = "New volume naming scheme" });
            if ((flags & 0x0020) != 0) flagsField.Children.Add(new RARHeaderField { Name = "AV", Value = "Authenticity verification present" });
            if ((flags & 0x0040) != 0) flagsField.Children.Add(new RARHeaderField { Name = "PROTECT", Value = "Recovery record present" });
            if ((flags & 0x0080) != 0) flagsField.Children.Add(new RARHeaderField { Name = "PASSWORD", Value = "Headers are encrypted" });
            if ((flags & 0x0100) != 0) flagsField.Children.Add(new RARHeaderField { Name = "FIRST_VOLUME", Value = "First volume" });
        }
        else if (blockType == 0x74 || blockType == 0x7A) // File header or service block
        {
            if ((flags & 0x0001) != 0) flagsField.Children.Add(new RARHeaderField { Name = "SPLIT_BEFORE", Value = "File continued from previous volume" });
            if ((flags & 0x0002) != 0) flagsField.Children.Add(new RARHeaderField { Name = "SPLIT_AFTER", Value = "File continues in next volume" });
            if ((flags & 0x0004) != 0) flagsField.Children.Add(new RARHeaderField { Name = "PASSWORD", Value = "File is encrypted" });
            if ((flags & 0x0008) != 0) flagsField.Children.Add(new RARHeaderField { Name = "COMMENT", Value = "File comment present" });
            if ((flags & 0x0010) != 0) flagsField.Children.Add(new RARHeaderField { Name = "SOLID", Value = "Info from previous files used" });

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

            if ((flags & 0x0100) != 0) flagsField.Children.Add(new RARHeaderField { Name = "LARGE", Value = "64-bit sizes" });
            if ((flags & 0x0200) != 0) flagsField.Children.Add(new RARHeaderField { Name = "UNICODE", Value = "Unicode filename" });
            if ((flags & 0x0400) != 0) flagsField.Children.Add(new RARHeaderField { Name = "SALT", Value = "Salt present" });
            if ((flags & 0x0800) != 0) flagsField.Children.Add(new RARHeaderField { Name = "VERSION", Value = "File version present" });
            if ((flags & 0x1000) != 0) flagsField.Children.Add(new RARHeaderField { Name = "EXTTIME", Value = "Extended time present" });
        }
        else if (blockType == 0x7B) // End of archive
        {
            if ((flags & 0x0001) != 0) flagsField.Children.Add(new RARHeaderField { Name = "NEXT_VOLUME", Value = "Archive continues in next volume" });
            if ((flags & 0x0002) != 0) flagsField.Children.Add(new RARHeaderField { Name = "DATA_CRC", Value = "Data CRC present" });
            if ((flags & 0x0004) != 0) flagsField.Children.Add(new RARHeaderField { Name = "REV_SPACE", Value = "Reserved space present" });
            if ((flags & 0x0008) != 0) flagsField.Children.Add(new RARHeaderField { Name = "VOL_NUMBER", Value = "Volume number present" });
        }
    }

    private static void ParseRAR4ArchiveHeader(BinaryReader reader, Stream stream, RARDetailedBlock block, long pos, long headerEnd, ushort flags)
    {
        // HighPosAV (2 bytes) - upper 16 bits of AV position
        if (pos + 2 <= headerEnd)
        {
            ushort highPosAV = reader.ReadUInt16();
            block.Fields.Add(new RARHeaderField
            {
                Name = "HighPosAV",
                Offset = pos,
                Length = 2,
                RawBytes = BitConverter.GetBytes(highPosAV),
                Value = $"0x{highPosAV:X4}"
            });
            pos += 2;
        }

        // PosAV (4 bytes) - AV position
        if (pos + 4 <= headerEnd)
        {
            uint posAV = reader.ReadUInt32();
            block.Fields.Add(new RARHeaderField
            {
                Name = "PosAV",
                Offset = pos,
                Length = 4,
                RawBytes = BitConverter.GetBytes(posAV),
                Value = $"0x{posAV:X8}"
            });
            pos += 4;
        }
    }

    private static void ParseRAR4FileHeader(BinaryReader reader, Stream stream, RARDetailedBlock block, long pos, long headerEnd, ushort flags, uint packSize)
    {
        // UNP_SIZE (4 bytes)
        uint unpSize = 0;
        if (pos + 4 <= headerEnd)
        {
            unpSize = reader.ReadUInt32();
            string? unpDesc = null;
            if (unpSize == 0xFFFFFFFF && (flags & 0x0100) == 0) // max without LARGE flag
                unpDesc = "Custom packer sentinel (e.g. QCF) — size unreliable";
            block.Fields.Add(new RARHeaderField
            {
                Name = "Unpacked Size",
                Offset = pos,
                Length = 4,
                RawBytes = BitConverter.GetBytes(unpSize),
                Value = $"{unpSize:N0} bytes",
                Description = unpDesc
            });
            pos += 4;
        }

        // HOST_OS (1 byte)
        if (pos + 1 <= headerEnd)
        {
            byte hostOs = reader.ReadByte();
            block.Fields.Add(new RARHeaderField
            {
                Name = "Host OS",
                Offset = pos,
                Length = 1,
                RawBytes = new[] { hostOs },
                Value = $"0x{hostOs:X2}",
                Description = GetHostOSName(hostOs)
            });
            pos += 1;
        }

        // FILE_CRC (4 bytes)
        if (pos + 4 <= headerEnd)
        {
            uint fileCrc = reader.ReadUInt32();
            block.Fields.Add(new RARHeaderField
            {
                Name = "File CRC32",
                Offset = pos,
                Length = 4,
                RawBytes = BitConverter.GetBytes(fileCrc),
                Value = $"0x{fileCrc:X8}"
            });
            pos += 4;
        }

        // FTIME (4 bytes) - DOS format
        if (pos + 4 <= headerEnd)
        {
            uint ftime = reader.ReadUInt32();
            var dt = RARUtils.DosDateToDateTime(ftime);
            block.Fields.Add(new RARHeaderField
            {
                Name = "File Time (DOS)",
                Offset = pos,
                Length = 4,
                RawBytes = BitConverter.GetBytes(ftime),
                Value = $"0x{ftime:X8}",
                Description = dt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "Invalid"
            });
            pos += 4;
        }

        // UNP_VER (1 byte)
        if (pos + 1 <= headerEnd)
        {
            byte unpVer = reader.ReadByte();
            block.Fields.Add(new RARHeaderField
            {
                Name = "Unpack Version",
                Offset = pos,
                Length = 1,
                RawBytes = new[] { unpVer },
                Value = $"{unpVer}",
                Description = $"RAR {unpVer / 10}.{unpVer % 10}"
            });
            pos += 1;
        }

        // METHOD (1 byte)
        if (pos + 1 <= headerEnd)
        {
            byte method = reader.ReadByte();
            block.Fields.Add(new RARHeaderField
            {
                Name = "Compression Method",
                Offset = pos,
                Length = 1,
                RawBytes = new[] { method },
                Value = $"0x{method:X2}",
                Description = GetCompressionMethodName(method)
            });
            pos += 1;
        }

        // NAME_SIZE (2 bytes)
        ushort nameSize = 0;
        if (pos + 2 <= headerEnd)
        {
            nameSize = reader.ReadUInt16();
            block.Fields.Add(new RARHeaderField
            {
                Name = "Name Size",
                Offset = pos,
                Length = 2,
                RawBytes = BitConverter.GetBytes(nameSize),
                Value = $"{nameSize} bytes"
            });
            pos += 2;
        }

        // ATTR (4 bytes)
        if (pos + 4 <= headerEnd)
        {
            uint attr = reader.ReadUInt32();
            block.Fields.Add(new RARHeaderField
            {
                Name = "File Attributes",
                Offset = pos,
                Length = 4,
                RawBytes = BitConverter.GetBytes(attr),
                Value = $"0x{attr:X8}"
            });
            pos += 4;
        }

        // HIGH_PACK_SIZE (4 bytes) - if LARGE flag set
        uint highPack = 0;
        if ((flags & 0x0100) != 0 && pos + 4 <= headerEnd)
        {
            highPack = reader.ReadUInt32();
            string? highPackDesc = null;
            if (highPack == 0xFFFFFFFF && unpSize == 0xFFFFFFFF)
                highPackDesc = "Custom packer sentinel (e.g. RELOADED, HI2U) — size unreliable";
            block.Fields.Add(new RARHeaderField
            {
                Name = "High Pack Size",
                Offset = pos,
                Length = 4,
                RawBytes = BitConverter.GetBytes(highPack),
                Value = $"0x{highPack:X8}",
                Description = highPackDesc
            });
            pos += 4;

            // Update block sizes with the full 64-bit packed size
            long fullPackSize = packSize | ((long)highPack << 32);
            block.DataSize = fullPackSize;
            block.HasData = fullPackSize > 0;
            block.TotalSize = block.HeaderSize + fullPackSize;
        }

        // HIGH_UNP_SIZE (4 bytes) - if LARGE flag set
        if ((flags & 0x0100) != 0 && pos + 4 <= headerEnd)
        {
            uint highUnp = reader.ReadUInt32();
            string? highUnpDesc = null;
            if (highUnp == 0xFFFFFFFF && unpSize == 0xFFFFFFFF)
                highUnpDesc = "Custom packer sentinel (e.g. RELOADED, HI2U) — size unreliable";
            block.Fields.Add(new RARHeaderField
            {
                Name = "High Unpack Size",
                Offset = pos,
                Length = 4,
                RawBytes = BitConverter.GetBytes(highUnp),
                Value = $"0x{highUnp:X8}",
                Description = highUnpDesc
            });
            pos += 4;
        }

        // FILE_NAME (variable)
        if (nameSize > 0 && pos + nameSize <= headerEnd)
        {
            byte[] nameBytes = reader.ReadBytes(nameSize);
            string fileName = RARUtils.DecodeFileName(nameBytes, (flags & 0x0200) != 0) ?? "";
            block.ItemName = fileName;
            block.Fields.Add(new RARHeaderField
            {
                Name = "File Name",
                Offset = pos,
                Length = nameSize,
                RawBytes = nameBytes,
                Value = fileName,
                Description = (flags & 0x0200) != 0 ? "Unicode encoded" : "OEM encoded"
            });
            pos += nameSize;
        }

        // SALT (8 bytes) - if SALT flag set
        if ((flags & 0x0400) != 0 && pos + 8 <= headerEnd)
        {
            byte[] salt = reader.ReadBytes(8);
            block.Fields.Add(new RARHeaderField
            {
                Name = "Salt",
                Offset = pos,
                Length = 8,
                RawBytes = salt,
                Value = BitConverter.ToString(salt).Replace("-", " ")
            });
            pos += 8;
        }

        // EXT_TIME (variable) - if EXTTIME flag set
        if ((flags & 0x1000) != 0 && pos + 2 <= headerEnd)
        {
            long extTimeStart = pos;
            ushort extFlags = reader.ReadUInt16();
            var extFlagsField = new RARHeaderField
            {
                Name = "Extended Time Flags",
                Offset = pos,
                Length = 2,
                RawBytes = BitConverter.GetBytes(extFlags),
                Value = $"0x{extFlags:X4}"
            };

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
            pos += 2;

            // Parse each time field (mtime, ctime, atime, arctime)
            for (int i = 0; i < 4 && pos < headerEnd; i++)
            {
                int rmode = (extFlags >> ((3 - i) * 4)) & 0xF;
                if ((rmode & 0x8) == 0) continue;

                // Time present
                if (i != 0 && pos + 4 <= headerEnd)
                {
                    uint dosTime = reader.ReadUInt32();
                    string dosDesc = $"0x{dosTime:X8}";
                    var dt = RARUtils.DosDateToDateTime(dosTime);
                    if (dt.HasValue)
                        dosDesc += $" ({dt.Value:yyyy-MM-dd HH:mm:ss})";

                    block.Fields.Add(new RARHeaderField
                    {
                        Name = $"Ext {timeLabels[i]} DOS",
                        Offset = pos,
                        Length = 4,
                        RawBytes = BitConverter.GetBytes(dosTime),
                        Value = dosDesc
                    });
                    pos += 4;
                }

                int count = rmode & 0x3;
                if (count > 0 && pos + count <= headerEnd)
                {
                    byte[] remainder = reader.ReadBytes(count);

                    // Convert subsec bytes to 100ns ticks (same as RARHeaderReader.TryReadRemainder)
                    int ticks = 0;
                    for (int j = 0; j < count; j++)
                        ticks |= remainder[j] << ((j + 3 - count) * 8);

                    // Convert ticks (100ns units) to fractional seconds
                    double seconds = ticks / 10_000_000.0;
                    string hexStr = BitConverter.ToString(remainder).Replace("-", " ");

                    block.Fields.Add(new RARHeaderField
                    {
                        Name = $"Ext {timeLabels[i]} subsec",
                        Offset = pos,
                        Length = count,
                        RawBytes = remainder,
                        Value = $"{hexStr} ({seconds:F7}s, {ticks} ticks)"
                    });
                    pos += count;
                }
            }
        }
    }

    private static void ParseRAR4ServiceBlock(BinaryReader reader, Stream stream, RARDetailedBlock block, long pos, long headerEnd, ushort flags, uint packSize)
    {
        // Service blocks have same structure as file headers
        ParseRAR4FileHeader(reader, stream, block, pos, headerEnd, flags, packSize);
    }

    private static void ParseRAR4EndBlock(BinaryReader reader, Stream stream, RARDetailedBlock block, long pos, long headerEnd, ushort flags)
    {
        // Archive end flags
        if ((flags & 0x0002) != 0 && pos + 4 <= headerEnd)
        {
            uint dataCrc = reader.ReadUInt32();
            block.Fields.Add(new RARHeaderField
            {
                Name = "Archive Data CRC",
                Offset = pos,
                Length = 4,
                RawBytes = BitConverter.GetBytes(dataCrc),
                Value = $"0x{dataCrc:X8}"
            });
            pos += 4;
        }

        // EARC_VOLNUMBER = 0x0008
        if ((flags & 0x0008) != 0 && pos + 2 <= headerEnd)
        {
            ushort volNumber = reader.ReadUInt16();
            block.Fields.Add(new RARHeaderField
            {
                Name = "Volume Number",
                Offset = pos,
                Length = 2,
                RawBytes = BitConverter.GetBytes(volNumber),
                Value = volNumber.ToString()
            });
            pos += 2;
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
                Value = BitConverter.ToString(sig).Replace("-", " "),
                Description = "RAR 5.x signature"
            });

            blocks.Add(sigBlock);
        }

        // Parse remaining blocks
        while (stream.Position < stream.Length)
        {
            var block = ParseRAR5Block(reader, stream);
            if (block == null) break;
            blocks.Add(block);

            // Skip to next block
            long nextPos = block.StartOffset + block.TotalSize;
            if (nextPos <= block.StartOffset || nextPos > stream.Length)
                break;
            stream.Position = nextPos;
        }
    }

    private static RARDetailedBlock? ParseRAR5Block(BinaryReader reader, Stream stream)
    {
        long blockStart = stream.Position;

        if (stream.Position + 4 > stream.Length)
            return null;

        var block = new RARDetailedBlock { StartOffset = blockStart };
        long pos = blockStart;

        // HEAD_CRC (4 bytes)
        uint headCrc = reader.ReadUInt32();
        block.Fields.Add(new RARHeaderField
        {
            Name = "Header CRC32",
            Offset = pos,
            Length = 4,
            RawBytes = BitConverter.GetBytes(headCrc),
            Value = $"0x{headCrc:X8}"
        });
        pos += 4;

        // HEAD_SIZE (vint)
        long vintStart = pos;
        ulong headSize = ReadVInt(reader, stream);
        int vintLen = (int)(stream.Position - vintStart);
        block.HeaderSize = (int)headSize + 4 + vintLen; // CRC + vint + header data
        block.Fields.Add(new RARHeaderField
        {
            Name = "Header Size",
            Offset = vintStart,
            Length = vintLen,
            Value = $"{headSize} bytes (vint)"
        });
        pos = stream.Position;

        if (headSize == 0)
        {
            block.TotalSize = pos - blockStart;
            return block;
        }

        // HEAD_TYPE (vint)
        vintStart = pos;
        ulong headType = ReadVInt(reader, stream);
        vintLen = (int)(stream.Position - vintStart);
        block.BlockTypeValue = (byte)headType;
        block.BlockType = GetRAR5BlockTypeName((int)headType);
        block.Fields.Add(new RARHeaderField
        {
            Name = "Header Type",
            Offset = vintStart,
            Length = vintLen,
            Value = $"{headType}",
            Description = block.BlockType
        });
        pos = stream.Position;

        // HEAD_FLAGS (vint)
        vintStart = pos;
        ulong headFlags = ReadVInt(reader, stream);
        vintLen = (int)(stream.Position - vintStart);
        var flagsField = new RARHeaderField
        {
            Name = "Header Flags",
            Offset = vintStart,
            Length = vintLen,
            Value = FormatHex(headFlags, vintLen)
        };
        AddRAR5FlagDescriptions(flagsField, (int)headType, headFlags);
        block.Fields.Add(flagsField);
        pos = stream.Position;

        long headerEnd = blockStart + block.HeaderSize;
        block.TotalSize = block.HeaderSize;

        // Extra area size (vint) - if HFL_EXTRA flag
        ulong extraAreaSize = 0;
        if ((headFlags & 0x0001) != 0)
        {
            vintStart = pos;
            extraAreaSize = ReadVInt(reader, stream);
            vintLen = (int)(stream.Position - vintStart);
            block.Fields.Add(new RARHeaderField
            {
                Name = "Extra Area Size",
                Offset = vintStart,
                Length = vintLen,
                Value = $"{extraAreaSize} bytes"
            });
            pos = stream.Position;
        }

        // Data size (vint) - if HFL_DATA flag
        if ((headFlags & 0x0002) != 0)
        {
            vintStart = pos;
            ulong dataSize = ReadVInt(reader, stream);
            vintLen = (int)(stream.Position - vintStart);
            block.Fields.Add(new RARHeaderField
            {
                Name = "Data Size",
                Offset = vintStart,
                Length = vintLen,
                Value = $"{dataSize} bytes"
            });
            block.DataSize = (long)dataSize;
            block.HasData = dataSize > 0;
            block.TotalSize += (long)dataSize;
            pos = stream.Position;
        }

        // Parse type-specific fields
        switch ((int)headType)
        {
            case 1: // Main archive header
                ParseRAR5MainHeader(reader, stream, block, pos, headerEnd, headFlags);
                break;
            case 2: // File header
            case 3: // Service header
                ParseRAR5FileHeader(reader, stream, block, pos, headerEnd, headFlags);
                break;
            case 4: // Encryption header
                ParseRAR5EncryptionHeader(reader, stream, block, pos, headerEnd);
                break;
            case 5: // End of archive
                ParseRAR5EndHeader(reader, stream, block, pos, headerEnd, headFlags);
                break;
        }

        // Parse extra area if present
        if (extraAreaSize > 0)
        {
            long extraStart = headerEnd - (long)extraAreaSize;
            if (extraStart >= blockStart && extraStart < headerEnd)
            {
                stream.Position = extraStart;
                ParseRAR5ExtraArea(reader, stream, block, extraStart, headerEnd, (int)headType);
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

    private static void AddRAR5FlagDescriptions(RARHeaderField flagsField, int blockType, ulong flags)
    {
        if ((flags & 0x0001) != 0) flagsField.Children.Add(new RARHeaderField { Name = "HFL_EXTRA", Value = "Extra area present" });
        if ((flags & 0x0002) != 0) flagsField.Children.Add(new RARHeaderField { Name = "HFL_DATA", Value = "Data area present" });
        if ((flags & 0x0004) != 0) flagsField.Children.Add(new RARHeaderField { Name = "HFL_SKIPIFUNKNOWN", Value = "Skip if unknown" });
        if ((flags & 0x0008) != 0) flagsField.Children.Add(new RARHeaderField { Name = "HFL_SPLITBEFORE", Value = "Split before" });
        if ((flags & 0x0010) != 0) flagsField.Children.Add(new RARHeaderField { Name = "HFL_SPLITAFTER", Value = "Split after" });
        if ((flags & 0x0020) != 0) flagsField.Children.Add(new RARHeaderField { Name = "HFL_CHILD", Value = "Child block" });
        if ((flags & 0x0040) != 0) flagsField.Children.Add(new RARHeaderField { Name = "HFL_INHERITED", Value = "Inherited" });
    }

    private static void ParseRAR5MainHeader(BinaryReader reader, Stream stream, RARDetailedBlock block, long pos, long headerEnd, ulong headFlags)
    {
        // Archive flags (vint)
        if (pos < headerEnd)
        {
            long vintStart = pos;
            ulong archFlags = ReadVInt(reader, stream);
            int vintLen = (int)(stream.Position - vintStart);

            var archFlagsField = new RARHeaderField
            {
                Name = "Archive Flags",
                Offset = vintStart,
                Length = vintLen,
                Value = FormatHex(archFlags, vintLen)
            };

            if ((archFlags & 0x0001) != 0) archFlagsField.Children.Add(new RARHeaderField { Name = "VOLUME", Value = "Multi-volume" });
            if ((archFlags & 0x0002) != 0) archFlagsField.Children.Add(new RARHeaderField { Name = "VOLNUMBER", Value = "Volume number present" });
            if ((archFlags & 0x0004) != 0) archFlagsField.Children.Add(new RARHeaderField { Name = "SOLID", Value = "Solid archive" });
            if ((archFlags & 0x0008) != 0) archFlagsField.Children.Add(new RARHeaderField { Name = "PROTECT", Value = "Recovery record present" });
            if ((archFlags & 0x0010) != 0) archFlagsField.Children.Add(new RARHeaderField { Name = "LOCK", Value = "Locked archive" });

            block.Fields.Add(archFlagsField);
            pos = stream.Position;

            // Volume number (vint) - if VOLNUMBER flag
            if ((archFlags & 0x0002) != 0 && pos < headerEnd)
            {
                vintStart = pos;
                ulong volNum = ReadVInt(reader, stream);
                vintLen = (int)(stream.Position - vintStart);
                block.Fields.Add(new RARHeaderField
                {
                    Name = "Volume Number",
                    Offset = vintStart,
                    Length = vintLen,
                    Value = volNum.ToString()
                });
            }
        }
    }

    private static void ParseRAR5FileHeader(BinaryReader reader, Stream stream, RARDetailedBlock block, long pos, long headerEnd, ulong headFlags)
    {
        // File flags (vint)
        if (pos < headerEnd)
        {
            long vintStart = pos;
            ulong fileFlags = ReadVInt(reader, stream);
            int vintLen = (int)(stream.Position - vintStart);

            var fileFlagsField = new RARHeaderField
            {
                Name = "File Flags",
                Offset = vintStart,
                Length = vintLen,
                Value = FormatHex(fileFlags, vintLen)
            };

            if ((fileFlags & 0x0001) != 0) fileFlagsField.Children.Add(new RARHeaderField { Name = "DIRECTORY", Value = "Directory entry" });
            if ((fileFlags & 0x0002) != 0) fileFlagsField.Children.Add(new RARHeaderField { Name = "UTIME", Value = "Unix time present" });
            if ((fileFlags & 0x0004) != 0) fileFlagsField.Children.Add(new RARHeaderField { Name = "CRC32", Value = "CRC32 present" });
            if ((fileFlags & 0x0008) != 0) fileFlagsField.Children.Add(new RARHeaderField { Name = "UNPSIZE", Value = "Unpacked size unknown" });

            block.Fields.Add(fileFlagsField);
            pos = stream.Position;

            // Unpacked size (vint)
            if (pos < headerEnd)
            {
                vintStart = pos;
                ulong unpSize = ReadVInt(reader, stream);
                vintLen = (int)(stream.Position - vintStart);
                block.Fields.Add(new RARHeaderField
                {
                    Name = "Unpacked Size",
                    Offset = vintStart,
                    Length = vintLen,
                    Value = $"{unpSize:N0} bytes"
                });
                pos = stream.Position;
            }

            // Attributes (vint)
            if (pos < headerEnd)
            {
                vintStart = pos;
                ulong attr = ReadVInt(reader, stream);
                vintLen = (int)(stream.Position - vintStart);
                block.Fields.Add(new RARHeaderField
                {
                    Name = "Attributes",
                    Offset = vintStart,
                    Length = vintLen,
                    Value = FormatHex(attr, vintLen)
                });
                pos = stream.Position;
            }

            // mtime (4 bytes) - if UTIME flag
            if ((fileFlags & 0x0002) != 0 && pos + 4 <= headerEnd)
            {
                uint mtime = reader.ReadUInt32();
                var dt = DateTimeOffset.FromUnixTimeSeconds(mtime).LocalDateTime;
                block.Fields.Add(new RARHeaderField
                {
                    Name = "Modification Time",
                    Offset = pos,
                    Length = 4,
                    RawBytes = BitConverter.GetBytes(mtime),
                    Value = $"{mtime}",
                    Description = dt.ToString("yyyy-MM-dd HH:mm:ss")
                });
                pos += 4;
            }

            // CRC32 (4 bytes) - if CRC32 flag
            if ((fileFlags & 0x0004) != 0 && pos + 4 <= headerEnd)
            {
                uint crc = reader.ReadUInt32();
                block.Fields.Add(new RARHeaderField
                {
                    Name = "Data CRC32",
                    Offset = pos,
                    Length = 4,
                    RawBytes = BitConverter.GetBytes(crc),
                    Value = $"0x{crc:X8}"
                });
                pos += 4;
            }

            // Compression info (vint)
            if (pos < headerEnd)
            {
                vintStart = pos;
                ulong compInfo = ReadVInt(reader, stream);
                vintLen = (int)(stream.Position - vintStart);

                int version = (int)(compInfo & 0x3F);
                bool solid = (compInfo & 0x40) != 0;
                int method = (int)((compInfo >> 7) & 0x7);
                int dictSizeLog = (int)((compInfo >> 10) & 0xF);

                var compInfoField = new RARHeaderField
                {
                    Name = "Compression Info",
                    Offset = vintStart,
                    Length = vintLen,
                    Value = FormatHex(compInfo, vintLen)
                };

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
                pos = stream.Position;
            }

            // Host OS (vint)
            if (pos < headerEnd)
            {
                vintStart = pos;
                ulong hostOs = ReadVInt(reader, stream);
                vintLen = (int)(stream.Position - vintStart);
                block.Fields.Add(new RARHeaderField
                {
                    Name = "Host OS",
                    Offset = vintStart,
                    Length = vintLen,
                    Value = hostOs.ToString(),
                    Description = hostOs == 0 ? "Windows" : "Unix"
                });
                pos = stream.Position;
            }

            // Name length (vint)
            if (pos < headerEnd)
            {
                vintStart = pos;
                ulong nameLen = ReadVInt(reader, stream);
                vintLen = (int)(stream.Position - vintStart);
                block.Fields.Add(new RARHeaderField
                {
                    Name = "Name Length",
                    Offset = vintStart,
                    Length = vintLen,
                    Value = $"{nameLen} bytes"
                });
                pos = stream.Position;

                // Name (UTF-8)
                if (nameLen > 0 && pos + (long)nameLen <= headerEnd)
                {
                    byte[] nameBytes = reader.ReadBytes((int)nameLen);
                    string name = Encoding.UTF8.GetString(nameBytes);
                    block.ItemName = name;
                    block.Fields.Add(new RARHeaderField
                    {
                        Name = "File Name",
                        Offset = pos,
                        Length = (int)nameLen,
                        RawBytes = nameBytes,
                        Value = name,
                        Description = "UTF-8 encoded"
                    });
                }
            }
        }
    }

    private static void ParseRAR5EncryptionHeader(BinaryReader reader, Stream stream, RARDetailedBlock block, long pos, long headerEnd)
    {
        // Encryption version (vint)
        if (pos < headerEnd)
        {
            long vintStart = pos;
            ulong encVer = ReadVInt(reader, stream);
            int vintLen = (int)(stream.Position - vintStart);
            block.Fields.Add(new RARHeaderField
            {
                Name = "Encryption Version",
                Offset = vintStart,
                Length = vintLen,
                Value = encVer.ToString()
            });
            pos = stream.Position;
        }

        // Encryption flags (vint)
        if (pos < headerEnd)
        {
            long vintStart = pos;
            ulong encFlags = ReadVInt(reader, stream);
            int vintLen = (int)(stream.Position - vintStart);
            block.Fields.Add(new RARHeaderField
            {
                Name = "Encryption Flags",
                Offset = vintStart,
                Length = vintLen,
                Value = FormatHex(encFlags, vintLen)
            });
            pos = stream.Position;
        }

        // KDF count (1 byte)
        if (pos + 1 <= headerEnd)
        {
            byte kdfCount = reader.ReadByte();
            block.Fields.Add(new RARHeaderField
            {
                Name = "KDF Count",
                Offset = pos,
                Length = 1,
                RawBytes = new[] { kdfCount },
                Value = kdfCount.ToString(),
                Description = $"Iterations = 2^{kdfCount}"
            });
            pos += 1;
        }

        // Salt (16 bytes)
        if (pos + 16 <= headerEnd)
        {
            byte[] salt = reader.ReadBytes(16);
            block.Fields.Add(new RARHeaderField
            {
                Name = "Salt",
                Offset = pos,
                Length = 16,
                RawBytes = salt,
                Value = BitConverter.ToString(salt).Replace("-", " ")
            });
        }
    }

    private static void ParseRAR5EndHeader(BinaryReader reader, Stream stream, RARDetailedBlock block, long pos, long headerEnd, ulong headFlags)
    {
        // End flags (vint)
        if (pos < headerEnd)
        {
            long vintStart = pos;
            ulong endFlags = ReadVInt(reader, stream);
            int vintLen = (int)(stream.Position - vintStart);

            var endFlagsField = new RARHeaderField
            {
                Name = "End Flags",
                Offset = vintStart,
                Length = vintLen,
                Value = FormatHex(endFlags, vintLen)
            };

            if ((endFlags & 0x0001) != 0) endFlagsField.Children.Add(new RARHeaderField { Name = "NEXTVOLUME", Value = "Archive continues" });

            block.Fields.Add(endFlagsField);
        }
    }

    private static void ParseRAR5DataArea(BinaryReader reader, Stream stream, RARDetailedBlock block, long dataStart)
    {
        if (dataStart + block.DataSize > stream.Length)
            return;

        block.Fields.Add(new RARHeaderField { Name = "--- Data Area ---", Value = "" });

        // For CMT service blocks with stored data, decode the comment text
        if (block.ItemName == "CMT" && block.DataSize <= 1_000_000)
        {
            // Check if stored (method=0) by finding the METHOD child of Compression Info
            bool isStored = block.Fields.Exists(f =>
                f.Name == "Compression Info" && f.Children.Exists(c =>
                    c.Name == "METHOD" && c.Value.StartsWith("0 ")));

            if (block.DataSize > int.MaxValue)
                return;
            stream.Position = dataStart;
            byte[] data = reader.ReadBytes((int)block.DataSize);

            if (isStored)
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

    private static void ParseRAR5ExtraArea(BinaryReader reader, Stream stream, RARDetailedBlock block, long extraStart, long headerEnd, int headType)
    {
        block.Fields.Add(new RARHeaderField { Name = "--- Extra Area ---", Value = "" });

        while (stream.Position + 2 <= headerEnd)
        {
            long recordStart = stream.Position;

            // Record size (vint) - size of type + data (not including this size vint itself)
            long vintStart = stream.Position;
            ulong fieldSize = ReadVInt(reader, stream);
            int sizeVintLen = (int)(stream.Position - vintStart);

            if (fieldSize == 0 || stream.Position + (long)fieldSize > headerEnd)
                break;

            long nextRecord = stream.Position + (long)fieldSize;

            // Record type (vint)
            vintStart = stream.Position;
            ulong fieldType = ReadVInt(reader, stream);

            long dataPos = stream.Position;
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
                ParseMainExtraRecord(reader, stream, block, fieldType, dataPos, nextRecord);
            else if (headType is 2 or 3) // File/Service
                ParseFileExtraRecord(reader, stream, block, fieldType, dataPos, nextRecord);

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

    private static void ParseMainExtraRecord(BinaryReader reader, Stream stream, RARDetailedBlock block, ulong fieldType, long dataPos, long recordEnd)
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
                if ((flags & 0x01) != 0) flagsField.Children.Add(new RARHeaderField { Name = "QLIST", Value = "Quick open offset present" });
                if ((flags & 0x02) != 0) flagsField.Children.Add(new RARHeaderField { Name = "RR", Value = "Recovery record offset present" });
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
                if ((flags & 0x01) != 0) flagsField.Children.Add(new RARHeaderField { Name = "NAME", Value = "Archive name present" });
                if ((flags & 0x02) != 0) flagsField.Children.Add(new RARHeaderField { Name = "CTIME", Value = "Creation time present" });
                if ((flags & 0x04) != 0) flagsField.Children.Add(new RARHeaderField { Name = "UNIXTIME", Value = "Unix time format" });
                if ((flags & 0x08) != 0) flagsField.Children.Add(new RARHeaderField { Name = "UNIX_NS", Value = "Nanosecond precision" });
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
                        var dt = DateTimeOffset.FromUnixTimeSeconds(ns / 1_000_000_000).AddTicks(ns % 1_000_000_000 / 100).LocalDateTime;
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
                        var dt = DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime;
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

    private static void ParseFileExtraRecord(BinaryReader reader, Stream stream, RARDetailedBlock block, ulong fieldType, long dataPos, long recordEnd)
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
                        Value = BitConverter.ToString(hash).Replace("-", "")
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
                if (unixTime) flagsField.Children.Add(new RARHeaderField { Name = "UNIXTIME", Value = "Unix time format" });
                if ((flags & 0x02) != 0) flagsField.Children.Add(new RARHeaderField { Name = "MTIME", Value = "Modification time present" });
                if ((flags & 0x04) != 0) flagsField.Children.Add(new RARHeaderField { Name = "CTIME", Value = "Creation time present" });
                if ((flags & 0x08) != 0) flagsField.Children.Add(new RARHeaderField { Name = "ATIME", Value = "Access time present" });
                if (unixNs) flagsField.Children.Add(new RARHeaderField { Name = "UNIX_NS", Value = "Nanosecond precision" });
                block.Fields.Add(flagsField);

                int timeSize = unixTime ? 4 : 8;

                if ((flags & 0x02) != 0 && stream.Position + timeSize <= recordEnd)
                    AddTimeField(reader, block, "  Modification Time", stream.Position, unixTime, timeSize);

                if ((flags & 0x04) != 0 && stream.Position + timeSize <= recordEnd)
                    AddTimeField(reader, block, "  Creation Time", stream.Position, unixTime, timeSize);

                if ((flags & 0x08) != 0 && stream.Position + timeSize <= recordEnd)
                    AddTimeField(reader, block, "  Access Time", stream.Position, unixTime, timeSize);

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
                    if ((encFlags & 0x01) != 0) encFlagsField.Children.Add(new RARHeaderField { Name = "PSWCHECK", Value = "Password check present" });
                    if ((encFlags & 0x02) != 0) encFlagsField.Children.Add(new RARHeaderField { Name = "HASHMAC", Value = "Hash MAC present" });
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
                            Value = BitConverter.ToString(salt).Replace("-", " ")
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
                            Value = BitConverter.ToString(iv).Replace("-", " ")
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
                    if ((flags & 0x01) != 0) flagsField.Children.Add(new RARHeaderField { Name = "DIRECTORY", Value = "Directory redirect" });
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
                if ((flags & 0x01) != 0) flagsField.Children.Add(new RARHeaderField { Name = "UNAME", Value = "User name present" });
                if ((flags & 0x02) != 0) flagsField.Children.Add(new RARHeaderField { Name = "GNAME", Value = "Group name present" });
                if ((flags & 0x04) != 0) flagsField.Children.Add(new RARHeaderField { Name = "NUMUID", Value = "Numeric UID present" });
                if ((flags & 0x08) != 0) flagsField.Children.Add(new RARHeaderField { Name = "NUMGID", Value = "Numeric GID present" });
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

    private static void AddTimeField(BinaryReader reader, RARDetailedBlock block, string name, long pos, bool unixTime, int size)
    {
        if (unixTime)
        {
            uint ts = reader.ReadUInt32();
            var dt = DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime;
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

    private static ulong ReadVInt(BinaryReader reader, Stream stream)
    {
        ulong result = 0;
        int shift = 0;

        while (stream.Position < stream.Length)
        {
            byte b = reader.ReadByte();
            result |= ((ulong)(b & 0x7F)) << shift;
            if ((b & 0x80) == 0)
                break;
            shift += 7;
            if (shift > 63)
                break;
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
            return $"{sizeKB / (1024 * 1024)} GB";
        if (sizeKB >= 1024)
            return $"{sizeKB / 1024} MB";
        return $"{sizeKB} KB";
    }

    private static string GetHostOSName(byte os) => os switch
    {
        0 => "MS-DOS",
        1 => "OS/2",
        2 => "Windows",
        3 => "Unix",
        4 => "Mac OS",
        5 => "BeOS",
        _ => $"Unknown ({os})"
    };

    private static string GetCompressionMethodName(byte method) => method switch
    {
        0x30 => "Store",
        0x31 => "Fastest",
        0x32 => "Fast",
        0x33 => "Normal",
        0x34 => "Good",
        0x35 => "Best",
        _ when method >= 0 && method <= 5 => new[] { "Store", "Fastest", "Fast", "Normal", "Good", "Best" }[method],
        _ => $"Unknown (0x{method:X2})"
    };

    #endregion
}
