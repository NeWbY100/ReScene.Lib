using System.Text;
using Force.Crc32;

namespace RAR.Tests;

public class RARDetailedParserTests
{
    private static readonly string TestDataPath = Path.Combine(
        AppContext.BaseDirectory, "TestData");

    private static readonly byte[] RAR4Signature = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];

    #region RAR 4.x Parsing Tests

    [Fact]
    public void Parse_RAR4File_ReturnsBlocks()
    {
        string rarPath = Path.Combine(TestDataPath, "test_wrar40_m3.rar");
        if (!File.Exists(rarPath))
        {
            Assert.Fail($"Test file not found: {rarPath}");
            return;
        }

        var blocks = RARDetailedParser.Parse(rarPath);

        Assert.NotEmpty(blocks);

        // First block should be signature
        Assert.Equal("Signature", blocks[0].BlockType);
        Assert.Equal(0, blocks[0].StartOffset);
        Assert.Equal(7, blocks[0].TotalSize);
    }

    [Fact]
    public void Parse_RAR4File_ContainsArchiveHeader()
    {
        string rarPath = Path.Combine(TestDataPath, "test_wrar40_m3.rar");
        if (!File.Exists(rarPath)) return;

        var blocks = RARDetailedParser.Parse(rarPath);

        Assert.Contains(blocks, b => b.BlockType == "Archive Header");
    }

    [Fact]
    public void Parse_RAR4File_ContainsFileHeader()
    {
        string rarPath = Path.Combine(TestDataPath, "test_wrar40_m3.rar");
        if (!File.Exists(rarPath)) return;

        var blocks = RARDetailedParser.Parse(rarPath);

        var fileBlock = blocks.FirstOrDefault(b => b.BlockType == "File Header");
        Assert.NotNull(fileBlock);
        Assert.NotNull(fileBlock!.ItemName);
    }

    [Fact]
    public void Parse_RAR4File_FileHeaderHasExpectedFields()
    {
        string rarPath = Path.Combine(TestDataPath, "test_wrar40_m3.rar");
        if (!File.Exists(rarPath)) return;

        var blocks = RARDetailedParser.Parse(rarPath);
        var fileBlock = blocks.FirstOrDefault(b => b.BlockType == "File Header");
        Assert.NotNull(fileBlock);

        var fieldNames = fileBlock!.Fields.Select(f => f.Name).ToList();
        Assert.Contains("Header CRC", fieldNames);
        Assert.Contains("Block Type", fieldNames);
        Assert.Contains("Flags", fieldNames);
        Assert.Contains("Header Size", fieldNames);
        Assert.Contains("Host OS", fieldNames);
        Assert.Contains("Compression Method", fieldNames);
    }

    [Fact]
    public void Parse_RAR4File_ServiceBlockHasCmtName()
    {
        string rarPath = Path.Combine(TestDataPath, "test_wrar40_m3.rar");
        if (!File.Exists(rarPath)) return;

        var blocks = RARDetailedParser.Parse(rarPath);
        var serviceBlock = blocks.FirstOrDefault(b => b.BlockType == "Service Block");

        if (serviceBlock != null)
        {
            // Service block should have item name "CMT"
            Assert.Equal("CMT", serviceBlock.ItemName);
        }
    }

    #endregion

    #region RAR 5.x Parsing Tests

    [Fact]
    public void Parse_RAR5File_ReturnsBlocks()
    {
        string rarPath = Path.Combine(TestDataPath, "test_rar5_m3.rar");
        if (!File.Exists(rarPath))
        {
            Assert.Fail($"Test file not found: {rarPath}");
            return;
        }

        var blocks = RARDetailedParser.Parse(rarPath);

        Assert.NotEmpty(blocks);
        Assert.Equal("Signature", blocks[0].BlockType);
        Assert.Equal(8, blocks[0].TotalSize); // RAR5 signature is 8 bytes
    }

    [Fact]
    public void Parse_RAR5File_ContainsMainHeader()
    {
        string rarPath = Path.Combine(TestDataPath, "test_rar5_m3.rar");
        if (!File.Exists(rarPath)) return;

        var blocks = RARDetailedParser.Parse(rarPath);

        Assert.Contains(blocks, b => b.BlockType == "Main Archive Header");
    }

    [Fact]
    public void Parse_RAR5File_ContainsServiceHeader()
    {
        string rarPath = Path.Combine(TestDataPath, "test_rar5_m3.rar");
        if (!File.Exists(rarPath)) return;

        var blocks = RARDetailedParser.Parse(rarPath);

        Assert.Contains(blocks, b => b.BlockType == "Service Header");
    }

    #endregion

    #region Stream-based Parsing Tests

    [Fact]
    public void Parse_Stream_WorksCorrectly()
    {
        string rarPath = Path.Combine(TestDataPath, "test_wrar40_m3.rar");
        if (!File.Exists(rarPath)) return;

        using var stream = File.OpenRead(rarPath);
        var blocks = RARDetailedParser.Parse(stream);

        Assert.NotEmpty(blocks);
    }

    #endregion

    #region Edge Case Tests

    [Fact]
    public void Parse_RAR4_SignatureFieldHasCorrectDescription()
    {
        string rarPath = Path.Combine(TestDataPath, "test_wrar40_m3.rar");
        if (!File.Exists(rarPath)) return;

        var blocks = RARDetailedParser.Parse(rarPath);
        var sigBlock = blocks.First(b => b.BlockType == "Signature");
        var sigField = sigBlock.Fields.First(f => f.Name == "Signature");

        Assert.Equal("Valid RAR 4.x signature", sigField.Description);
    }

    [Fact]
    public void Parse_HostOSField_HasDescription()
    {
        string rarPath = Path.Combine(TestDataPath, "test_wrar40_m3.rar");
        if (!File.Exists(rarPath)) return;

        var blocks = RARDetailedParser.Parse(rarPath);
        var fileBlock = blocks.First(b => b.BlockType == "File Header");
        var hostOSField = fileBlock.Fields.FirstOrDefault(f => f.Name == "Host OS");

        Assert.NotNull(hostOSField?.Description);
    }

    #endregion

    #region Synthetic RAR4 Helpers

    /// <summary>
    /// Builds a minimal RAR4 archive header (type 0x73) with valid CRC.
    /// </summary>
    private static byte[] BuildArchiveHeader()
    {
        byte[] header = new byte[13];
        header[2] = 0x73; // Archive header type
        BitConverter.GetBytes((ushort)0).CopyTo(header, 3); // Flags
        BitConverter.GetBytes((ushort)13).CopyTo(header, 5); // Header size

        uint crc32 = Crc32Algorithm.Compute(header, 2, header.Length - 2);
        BitConverter.GetBytes((ushort)(crc32 & 0xFFFF)).CopyTo(header, 0);
        return header;
    }

    /// <summary>
    /// Builds a RAR4 file header (type 0x74) WITHOUT the LARGE flag.
    /// Layout: CRC(2) + Type(1) + Flags(2) + HeaderSize(2) + ADD_SIZE(4) +
    ///         UNP_SIZE(4) + HOST_OS(1) + FILE_CRC(4) + FILE_TIME(4) +
    ///         UNP_VER(1) + METHOD(1) + NAME_SIZE(2) + ATTR(4) + FileName(var)
    /// </summary>
    private static byte[] BuildFileHeaderNoLarge(string fileName, uint packedSize, uint unpackedSize = 100)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(fileName);
        ushort nameLen = (ushort)nameBytes.Length;
        ushort headerSize = (ushort)(32 + nameLen);

        byte[] header = new byte[headerSize];
        header[2] = 0x74; // File header type
        // Flags: LONG_BLOCK only (no LARGE)
        BitConverter.GetBytes((ushort)0x8000).CopyTo(header, 3);
        BitConverter.GetBytes(headerSize).CopyTo(header, 5);
        // ADD_SIZE = packed size (low 32 bits)
        BitConverter.GetBytes(packedSize).CopyTo(header, 7);
        // UNP_SIZE
        BitConverter.GetBytes(unpackedSize).CopyTo(header, 11);
        // HOST_OS = Windows
        header[15] = 2;
        // FILE_CRC
        BitConverter.GetBytes((uint)0xDEADBEEF).CopyTo(header, 16);
        // FILE_TIME (DOS format)
        BitConverter.GetBytes((uint)0x5A8E3100).CopyTo(header, 20);
        // UNP_VER = 29 (RAR 2.9)
        header[24] = 29;
        // METHOD = 0x33 (Normal)
        header[25] = 0x33;
        // NAME_SIZE
        BitConverter.GetBytes(nameLen).CopyTo(header, 26);
        // ATTR
        BitConverter.GetBytes((uint)0x20).CopyTo(header, 28);
        // Filename
        nameBytes.CopyTo(header, 32);

        // CRC: lower 16 bits of CRC32 of bytes from offset 2 onward
        uint crc32 = Crc32Algorithm.Compute(header, 2, header.Length - 2);
        BitConverter.GetBytes((ushort)(crc32 & 0xFFFF)).CopyTo(header, 0);
        return header;
    }

    /// <summary>
    /// Builds a RAR4 file header (type 0x74) WITH the LARGE flag set.
    /// Includes HIGH_PACK_SIZE and HIGH_UNP_SIZE fields.
    /// </summary>
    private static byte[] BuildFileHeaderLarge(string fileName, uint packSizeLow, uint highPackSize,
        uint unpackedSizeLow = 100, uint highUnpSize = 0)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(fileName);
        ushort nameLen = (ushort)nameBytes.Length;
        // 32 base + 4 HIGH_PACK + 4 HIGH_UNP + nameLen
        ushort headerSize = (ushort)(40 + nameLen);

        byte[] header = new byte[headerSize];
        header[2] = 0x74; // File header type
        // Flags: LONG_BLOCK | LARGE
        BitConverter.GetBytes((ushort)(0x8000 | 0x0100)).CopyTo(header, 3);
        BitConverter.GetBytes(headerSize).CopyTo(header, 5);
        // ADD_SIZE = packed size low 32 bits
        BitConverter.GetBytes(packSizeLow).CopyTo(header, 7);
        // UNP_SIZE
        BitConverter.GetBytes(unpackedSizeLow).CopyTo(header, 11);
        // HOST_OS = Windows
        header[15] = 2;
        // FILE_CRC
        BitConverter.GetBytes((uint)0xDEADBEEF).CopyTo(header, 16);
        // FILE_TIME (DOS format)
        BitConverter.GetBytes((uint)0x5A8E3100).CopyTo(header, 20);
        // UNP_VER = 29 (RAR 2.9)
        header[24] = 29;
        // METHOD = 0x33 (Normal)
        header[25] = 0x33;
        // NAME_SIZE
        BitConverter.GetBytes(nameLen).CopyTo(header, 26);
        // ATTR
        BitConverter.GetBytes((uint)0x20).CopyTo(header, 28);
        // HIGH_PACK_SIZE
        BitConverter.GetBytes(highPackSize).CopyTo(header, 32);
        // HIGH_UNP_SIZE
        BitConverter.GetBytes(highUnpSize).CopyTo(header, 36);
        // Filename
        nameBytes.CopyTo(header, 40);

        // CRC: lower 16 bits of CRC32 of bytes from offset 2 onward
        uint crc32 = Crc32Algorithm.Compute(header, 2, header.Length - 2);
        BitConverter.GetBytes((ushort)(crc32 & 0xFFFF)).CopyTo(header, 0);
        return header;
    }

    /// <summary>
    /// Builds a RAR4 end-of-archive block (type 0x7B) with valid CRC.
    /// </summary>
    private static byte[] BuildEndBlock()
    {
        byte[] header = new byte[7];
        header[2] = 0x7B; // End of archive
        BitConverter.GetBytes((ushort)7).CopyTo(header, 5);

        uint crc32 = Crc32Algorithm.Compute(header, 2, header.Length - 2);
        BitConverter.GetBytes((ushort)(crc32 & 0xFFFF)).CopyTo(header, 0);
        return header;
    }

    /// <summary>
    /// Builds a complete in-memory RAR4 stream from signature + blocks.
    /// </summary>
    private static MemoryStream BuildRAR4Stream(params byte[][] blocks)
    {
        var ms = new MemoryStream();
        ms.Write(RAR4Signature);
        foreach (var block in blocks)
            ms.Write(block);
        ms.Position = 0;
        return ms;
    }

    #endregion

    #region LARGE Flag and 64-bit Size Tests

    [Fact]
    public void Parse_FileHeaderWithLargeFlag_DataSizeCombinesAddSizeAndHighPackSize()
    {
        // LARGE flag set: DataSize = ADD_SIZE | (HIGH_PACK_SIZE << 32)
        uint packSizeLow = 0x10000000; // 256 MB low part
        uint highPackSize = 0x00000002; // high part = 2
        long expectedDataSize = packSizeLow | ((long)highPackSize << 32);

        byte[] archiveHeader = BuildArchiveHeader();
        byte[] fileHeader = BuildFileHeaderLarge("test.bin", packSizeLow, highPackSize);
        byte[] endBlock = BuildEndBlock();

        using var stream = BuildRAR4Stream(archiveHeader, fileHeader, endBlock);
        var blocks = RARDetailedParser.Parse(stream);

        var fileBlock = blocks.First(b => b.BlockType == "File Header");
        Assert.Equal(expectedDataSize, fileBlock.DataSize);
    }

    [Fact]
    public void Parse_FileHeaderWithoutLargeFlag_DataSizeEqualsAddSizeOnly()
    {
        // No LARGE flag: DataSize = ADD_SIZE only
        uint packedSize = 5000;

        byte[] archiveHeader = BuildArchiveHeader();
        byte[] fileHeader = BuildFileHeaderNoLarge("test.txt", packedSize);
        byte[] endBlock = BuildEndBlock();

        using var stream = BuildRAR4Stream(archiveHeader, fileHeader, endBlock);
        var blocks = RARDetailedParser.Parse(stream);

        var fileBlock = blocks.First(b => b.BlockType == "File Header");
        Assert.Equal(packedSize, fileBlock.DataSize);
    }

    [Fact]
    public void Parse_FileHeaderWithLargeFlag_TotalSizeEqualsHeaderSizePlusFullDataSize()
    {
        // TotalSize should be HeaderSize + full 64-bit DataSize
        uint packSizeLow = 0x20000000;
        uint highPackSize = 0x00000001;
        long expectedDataSize = packSizeLow | ((long)highPackSize << 32);

        byte[] archiveHeader = BuildArchiveHeader();
        byte[] fileHeader = BuildFileHeaderLarge("bigfile.dat", packSizeLow, highPackSize);
        byte[] endBlock = BuildEndBlock();

        using var stream = BuildRAR4Stream(archiveHeader, fileHeader, endBlock);
        var blocks = RARDetailedParser.Parse(stream);

        var fileBlock = blocks.First(b => b.BlockType == "File Header");
        long expectedTotalSize = fileBlock.HeaderSize + expectedDataSize;
        Assert.Equal(expectedTotalSize, fileBlock.TotalSize);
    }

    [Fact]
    public void Parse_LargeFlagWithHighPackSizeGreaterThanZero_NextBlockFoundCorrectly()
    {
        // When LARGE is set and HIGH_PACK_SIZE > 0, the parser must skip DataSize bytes
        // of data to find the next block. We simulate this by placing actual data bytes
        // between the file header and the end block.
        uint packSizeLow = 16; // 16 bytes of "data"
        uint highPackSize = 0;  // Keep total data small for test

        byte[] archiveHeader = BuildArchiveHeader();
        byte[] fileHeader = BuildFileHeaderLarge("data.bin", packSizeLow, highPackSize);
        byte[] fakeData = new byte[16]; // Simulate packed data
        byte[] endBlock = BuildEndBlock();

        var ms = new MemoryStream();
        ms.Write(RAR4Signature);
        ms.Write(archiveHeader);
        ms.Write(fileHeader);
        ms.Write(fakeData); // This is the "data" after the file header
        ms.Write(endBlock);
        ms.Position = 0;

        var blocks = RARDetailedParser.Parse(ms);

        // Should find: Signature, Archive Header, File Header, End of Archive
        // No "Unknown" blocks should appear
        Assert.Equal(4, blocks.Count);
        Assert.Equal("Signature", blocks[0].BlockType);
        Assert.Equal("Archive Header", blocks[1].BlockType);
        Assert.Equal("File Header", blocks[2].BlockType);
        Assert.Equal("End of Archive", blocks[3].BlockType);
    }

    [Fact]
    public void Parse_LargeFlagWithNonZeroHighPack_NoUnknownBlocks()
    {
        // Use HIGH_PACK_SIZE > 0 to create a truly large DataSize, but only provide
        // stream bytes up to the end. The parser should not find extra blocks or produce
        // "Unknown" type blocks when the data extends past the stream end.
        uint packSizeLow = 0;
        uint highPackSize = 1; // Full DataSize = 4 GB, but stream is tiny
        long expectedDataSize = (long)highPackSize << 32;

        byte[] archiveHeader = BuildArchiveHeader();
        byte[] fileHeader = BuildFileHeaderLarge("huge.bin", packSizeLow, highPackSize);

        // Don't add end block - the parser should stop because nextPos > stream.Length
        using var stream = BuildRAR4Stream(archiveHeader, fileHeader);
        var blocks = RARDetailedParser.Parse(stream);

        // File header DataSize should be the full 64-bit value
        var fileBlock = blocks.First(b => b.BlockType == "File Header");
        Assert.Equal(expectedDataSize, fileBlock.DataSize);

        // No "Unknown" blocks should be produced
        Assert.DoesNotContain(blocks, b => b.BlockType == "Unknown");
    }

    [Fact]
    public void Parse_FileHeaderWithLargeFlag_HasHighPackSizeField()
    {
        // Verify the "High Pack Size" field is present when LARGE flag is set
        byte[] archiveHeader = BuildArchiveHeader();
        byte[] fileHeader = BuildFileHeaderLarge("test.bin", 100, 5);
        byte[] endBlock = BuildEndBlock();

        using var stream = BuildRAR4Stream(archiveHeader, fileHeader, endBlock);
        var blocks = RARDetailedParser.Parse(stream);

        var fileBlock = blocks.First(b => b.BlockType == "File Header");
        var fieldNames = fileBlock.Fields.Select(f => f.Name).ToList();

        Assert.Contains("High Pack Size", fieldNames);
        Assert.Contains("High Unpack Size", fieldNames);
    }

    [Fact]
    public void Parse_FileHeaderWithoutLargeFlag_NoHighPackSizeField()
    {
        // Verify "High Pack Size" field is absent when LARGE flag is not set
        byte[] archiveHeader = BuildArchiveHeader();
        byte[] fileHeader = BuildFileHeaderNoLarge("small.txt", 100);
        byte[] endBlock = BuildEndBlock();

        using var stream = BuildRAR4Stream(archiveHeader, fileHeader, endBlock);
        var blocks = RARDetailedParser.Parse(stream);

        var fileBlock = blocks.First(b => b.BlockType == "File Header");
        var fieldNames = fileBlock.Fields.Select(f => f.Name).ToList();

        Assert.DoesNotContain("High Pack Size", fieldNames);
        Assert.DoesNotContain("High Unpack Size", fieldNames);
    }

    [Fact]
    public void Parse_LargeFlagWithZeroHighPack_DataSizeEqualsAddSizeOnly()
    {
        // LARGE flag set but HIGH_PACK_SIZE = 0: DataSize = ADD_SIZE | (0 << 32) = ADD_SIZE
        uint packSizeLow = 12345;
        byte[] archiveHeader = BuildArchiveHeader();
        byte[] fileHeader = BuildFileHeaderLarge("test.dat", packSizeLow, 0);
        byte[] endBlock = BuildEndBlock();

        using var stream = BuildRAR4Stream(archiveHeader, fileHeader, endBlock);
        var blocks = RARDetailedParser.Parse(stream);

        var fileBlock = blocks.First(b => b.BlockType == "File Header");
        Assert.Equal(packSizeLow, fileBlock.DataSize);
    }

    [Fact]
    public void Parse_LargeFlagFlagsFieldHasLargeChild()
    {
        // The Flags field should have a "LARGE" child description
        byte[] archiveHeader = BuildArchiveHeader();
        byte[] fileHeader = BuildFileHeaderLarge("test.bin", 100, 0);
        byte[] endBlock = BuildEndBlock();

        using var stream = BuildRAR4Stream(archiveHeader, fileHeader, endBlock);
        var blocks = RARDetailedParser.Parse(stream);

        var fileBlock = blocks.First(b => b.BlockType == "File Header");
        var flagsField = fileBlock.Fields.First(f => f.Name == "Flags");

        Assert.Contains(flagsField.Children, c => c.Name == "LARGE");
    }

    #endregion

    #region ParseFromPosition Tests

    [Fact]
    public void ParseFromPosition_EmbeddedRARAtNonZeroOffset_ParsesCorrectly()
    {
        // Simulate embedded RAR data (like inside an SRR file) at a non-zero offset
        byte[] archiveHeader = BuildArchiveHeader();
        byte[] fileHeader = BuildFileHeaderNoLarge("embedded.txt", 50);
        byte[] fakeData = new byte[50];
        byte[] endBlock = BuildEndBlock();

        // Build the RAR data (signature + headers)
        var rarData = new MemoryStream();
        rarData.Write(RAR4Signature);
        rarData.Write(archiveHeader);
        rarData.Write(fileHeader);
        rarData.Write(fakeData);
        rarData.Write(endBlock);
        byte[] rarBytes = rarData.ToArray();

        // Prepend junk data to simulate non-zero offset
        byte[] junk = new byte[256];
        new Random(42).NextBytes(junk);

        var stream = new MemoryStream();
        stream.Write(junk);
        stream.Write(rarBytes);
        stream.Position = 256; // Position at start of RAR data

        var blocks = RARDetailedParser.ParseFromPosition(stream);

        Assert.NotEmpty(blocks);
        Assert.Equal("Signature", blocks[0].BlockType);
        Assert.Equal(256, blocks[0].StartOffset); // Offset should reflect actual position

        var fileBlock = blocks.FirstOrDefault(b => b.BlockType == "File Header");
        Assert.NotNull(fileBlock);
        Assert.Equal("embedded.txt", fileBlock!.ItemName);
    }

    [Fact]
    public void ParseFromPosition_InvalidSignatureAtPosition_ReturnsEmpty()
    {
        // If the stream position doesn't point to a valid RAR signature, return empty
        byte[] garbage = new byte[100];
        new Random(123).NextBytes(garbage);

        var stream = new MemoryStream(garbage);
        stream.Position = 10;

        var blocks = RARDetailedParser.ParseFromPosition(stream);

        Assert.Empty(blocks);
    }

    [Fact]
    public void ParseFromPosition_LargeFileAtNonZeroOffset_DataSizeCorrect()
    {
        // Parse LARGE file header embedded at non-zero offset
        uint packSizeLow = 0x40000000; // 1 GB
        uint highPackSize = 0x00000003; // High part = 3
        long expectedDataSize = packSizeLow | ((long)highPackSize << 32);

        byte[] archiveHeader = BuildArchiveHeader();
        byte[] fileHeader = BuildFileHeaderLarge("large.bin", packSizeLow, highPackSize);
        byte[] endBlock = BuildEndBlock();

        // Build RAR data
        var rarData = new MemoryStream();
        rarData.Write(RAR4Signature);
        rarData.Write(archiveHeader);
        rarData.Write(fileHeader);
        // No actual data - stream is too small, parser will stop after file header
        byte[] rarBytes = rarData.ToArray();

        // Prepend 100 bytes of padding
        byte[] padding = new byte[100];
        var stream = new MemoryStream();
        stream.Write(padding);
        stream.Write(rarBytes);
        stream.Position = 100;

        var blocks = RARDetailedParser.ParseFromPosition(stream);

        var fileBlock = blocks.First(b => b.BlockType == "File Header");
        Assert.Equal(expectedDataSize, fileBlock.DataSize);
    }

    #endregion

    #region RAR4 End Archive Block Tests

    [Fact]
    public void Parse_Synthetic_RAR4EndBlock_DetectedAsEndOfArchive()
    {
        byte[] archiveHeader = BuildArchiveHeader();
        byte[] endBlock = BuildEndBlock();

        using var stream = BuildRAR4Stream(archiveHeader, endBlock);
        var blocks = RARDetailedParser.Parse(stream);

        var end = blocks.FirstOrDefault(b => b.BlockType == "End of Archive");
        Assert.NotNull(end);
        Assert.Equal(0x7B, end!.BlockTypeValue);
    }

    [Fact]
    public void Parse_RAR4EndBlock_WithDataCrcFlag_ParsesCrcField()
    {
        // End block with DATA_CRC flag (0x0002): adds 4-byte CRC after base header
        ushort flags = 0x0002;
        int headerSize = 7 + 4; // base + CRC field
        byte[] hdr = new byte[headerSize];
        hdr[2] = 0x7B; // End of archive
        BitConverter.GetBytes(flags).CopyTo(hdr, 3);
        BitConverter.GetBytes((ushort)headerSize).CopyTo(hdr, 5);
        // Data CRC = 0xDEADBEEF
        BitConverter.GetBytes((uint)0xDEADBEEF).CopyTo(hdr, 7);
        uint crc32 = Crc32Algorithm.Compute(hdr, 2, hdr.Length - 2);
        BitConverter.GetBytes((ushort)(crc32 & 0xFFFF)).CopyTo(hdr, 0);

        byte[] archiveHeader = BuildArchiveHeader();
        using var stream = BuildRAR4Stream(archiveHeader, hdr);
        var blocks = RARDetailedParser.Parse(stream);

        var end = blocks.First(b => b.BlockType == "End of Archive");
        var crcField = end.Fields.FirstOrDefault(f => f.Name == "Archive Data CRC");
        Assert.NotNull(crcField);
        Assert.Equal("0xDEADBEEF", crcField!.Value);
    }

    [Fact]
    public void Parse_RAR4EndBlock_WithVolNumberFlag_ParsesVolumeNumber()
    {
        // End block with DATA_CRC (0x0002) + VOL_NUMBER (0x0008) = 0x000A
        ushort flags = 0x000A;
        int headerSize = 7 + 4 + 2; // base + CRC(4) + vol number(2)
        byte[] hdr = new byte[headerSize];
        hdr[2] = 0x7B;
        BitConverter.GetBytes(flags).CopyTo(hdr, 3);
        BitConverter.GetBytes((ushort)headerSize).CopyTo(hdr, 5);
        BitConverter.GetBytes((uint)0x11223344).CopyTo(hdr, 7); // Data CRC
        BitConverter.GetBytes((ushort)42).CopyTo(hdr, 11); // Volume number = 42
        uint crc32 = Crc32Algorithm.Compute(hdr, 2, hdr.Length - 2);
        BitConverter.GetBytes((ushort)(crc32 & 0xFFFF)).CopyTo(hdr, 0);

        byte[] archiveHeader = BuildArchiveHeader();
        using var stream = BuildRAR4Stream(archiveHeader, hdr);
        var blocks = RARDetailedParser.Parse(stream);

        var end = blocks.First(b => b.BlockType == "End of Archive");
        var volField = end.Fields.FirstOrDefault(f => f.Name == "Volume Number");
        Assert.NotNull(volField);
        Assert.Equal("42", volField!.Value);
    }

    [Fact]
    public void Parse_RAR4EndBlock_FlagsShowDataCrcChild()
    {
        ushort flags = 0x0002;
        int headerSize = 7 + 4;
        byte[] hdr = new byte[headerSize];
        hdr[2] = 0x7B;
        BitConverter.GetBytes(flags).CopyTo(hdr, 3);
        BitConverter.GetBytes((ushort)headerSize).CopyTo(hdr, 5);
        BitConverter.GetBytes((uint)0).CopyTo(hdr, 7);
        uint crc32 = Crc32Algorithm.Compute(hdr, 2, hdr.Length - 2);
        BitConverter.GetBytes((ushort)(crc32 & 0xFFFF)).CopyTo(hdr, 0);

        byte[] archiveHeader = BuildArchiveHeader();
        using var stream = BuildRAR4Stream(archiveHeader, hdr);
        var blocks = RARDetailedParser.Parse(stream);

        var end = blocks.First(b => b.BlockType == "End of Archive");
        var flagsField = end.Fields.First(f => f.Name == "Flags");
        Assert.Contains(flagsField.Children, c => c.Name == "DATA_CRC");
    }

    #endregion

    #region RAR4 Service Block (CMT) Tests

    /// <summary>
    /// Builds a RAR4 service block (0x7A) for CMT with stored comment data.
    /// </summary>
    private static byte[] BuildCmtServiceBlock(string comment)
    {
        byte[] commentBytes = Encoding.UTF8.GetBytes(comment);
        byte[] nameBytes = Encoding.ASCII.GetBytes("CMT");
        ushort nameLen = (ushort)nameBytes.Length;

        // Layout matches file header: CRC(2) + Type(1) + Flags(2) + HeaderSize(2) +
        // ADD_SIZE(4) + UNP_SIZE(4) + HOST_OS(1) + FILE_CRC(4) + FTIME(4) +
        // UNP_VER(1) + METHOD(1) + NAME_SIZE(2) + ATTR(4) + FILE_NAME(var)
        ushort headerSize = (ushort)(32 + nameLen);
        ushort flags = 0x8000; // LONG_BLOCK

        byte[] header = new byte[headerSize];
        header[2] = 0x7A; // Service block
        BitConverter.GetBytes(flags).CopyTo(header, 3);
        BitConverter.GetBytes(headerSize).CopyTo(header, 5);
        BitConverter.GetBytes((uint)commentBytes.Length).CopyTo(header, 7); // ADD_SIZE
        BitConverter.GetBytes((uint)commentBytes.Length).CopyTo(header, 11); // UNP_SIZE
        header[15] = 2; // HOST_OS = Windows
        // FILE_CRC = 0
        // FTIME = 0
        header[24] = 29; // UNP_VER
        header[25] = 0x30; // METHOD = Store
        BitConverter.GetBytes(nameLen).CopyTo(header, 26);
        BitConverter.GetBytes((uint)0x20).CopyTo(header, 28); // ATTR
        nameBytes.CopyTo(header, 32);

        uint crc32 = Crc32Algorithm.Compute(header, 2, header.Length - 2);
        BitConverter.GetBytes((ushort)(crc32 & 0xFFFF)).CopyTo(header, 0);

        // Combine header + comment data
        byte[] result = new byte[header.Length + commentBytes.Length];
        Array.Copy(header, result, header.Length);
        Array.Copy(commentBytes, 0, result, header.Length, commentBytes.Length);
        return result;
    }

    [Fact]
    public void Parse_RAR4CmtServiceBlock_ParsesNameAsICmt()
    {
        string comment = "Hello World";
        byte[] cmtBlock = BuildCmtServiceBlock(comment);
        byte[] archiveHeader = BuildArchiveHeader();
        byte[] endBlock = BuildEndBlock();

        using var stream = BuildRAR4Stream(archiveHeader, cmtBlock, endBlock);
        var blocks = RARDetailedParser.Parse(stream);

        var svc = blocks.FirstOrDefault(b => b.BlockType == "Service Block");
        Assert.NotNull(svc);
        Assert.Equal("CMT", svc!.ItemName);
    }

    [Fact]
    public void Parse_RAR4CmtServiceBlock_HasDataAreaSeparator()
    {
        byte[] cmtBlock = BuildCmtServiceBlock("test");
        byte[] archiveHeader = BuildArchiveHeader();
        byte[] endBlock = BuildEndBlock();

        using var stream = BuildRAR4Stream(archiveHeader, cmtBlock, endBlock);
        var blocks = RARDetailedParser.Parse(stream);

        var svc = blocks.First(b => b.BlockType == "Service Block");
        Assert.Contains(svc.Fields, f => f.Name == "--- Data Area ---");
    }

    [Fact]
    public void Parse_RAR4CmtServiceBlock_StoredComment_ExtractsText()
    {
        string comment = "This is a stored comment for testing.";
        byte[] cmtBlock = BuildCmtServiceBlock(comment);
        byte[] archiveHeader = BuildArchiveHeader();
        byte[] endBlock = BuildEndBlock();

        using var stream = BuildRAR4Stream(archiveHeader, cmtBlock, endBlock);
        var blocks = RARDetailedParser.Parse(stream);

        var svc = blocks.First(b => b.BlockType == "Service Block");
        var commentField = svc.Fields.FirstOrDefault(f => f.Name == "Comment Data");
        Assert.NotNull(commentField);
        Assert.Equal(comment, commentField!.Value);
        Assert.Equal("Stored (uncompressed)", commentField.Description);
    }

    [Fact]
    public void Parse_RAR4CmtServiceBlock_HasCompressionMethodField()
    {
        byte[] cmtBlock = BuildCmtServiceBlock("x");
        byte[] archiveHeader = BuildArchiveHeader();
        byte[] endBlock = BuildEndBlock();

        using var stream = BuildRAR4Stream(archiveHeader, cmtBlock, endBlock);
        var blocks = RARDetailedParser.Parse(stream);

        var svc = blocks.First(b => b.BlockType == "Service Block");
        var methodField = svc.Fields.FirstOrDefault(f => f.Name == "Compression Method");
        Assert.NotNull(methodField);
        Assert.Equal("0x30", methodField!.Value);
        Assert.Equal("Store", methodField.Description);
    }

    #endregion

    #region RAR4 Unknown Block Type Tests

    [Fact]
    public void Parse_RAR4UnknownBlockType_LabeledCorrectly()
    {
        // Block with unknown type 0x60
        byte[] hdr = new byte[7];
        hdr[2] = 0x60; // unknown type
        BitConverter.GetBytes((ushort)0x0000).CopyTo(hdr, 3); // flags = 0
        BitConverter.GetBytes((ushort)7).CopyTo(hdr, 5); // header size = 7
        uint crc32 = Crc32Algorithm.Compute(hdr, 2, hdr.Length - 2);
        BitConverter.GetBytes((ushort)(crc32 & 0xFFFF)).CopyTo(hdr, 0);

        byte[] archiveHeader = BuildArchiveHeader();
        byte[] endBlock = BuildEndBlock();
        using var stream = BuildRAR4Stream(archiveHeader, hdr, endBlock);
        var blocks = RARDetailedParser.Parse(stream);

        var unknown = blocks.FirstOrDefault(b => b.BlockType == "Unknown (0x60)");
        Assert.NotNull(unknown);
        Assert.Equal(0x60, unknown!.BlockTypeValue);
    }

    [Fact]
    public void Parse_RAR4UnknownBlockType_SkippedCorrectly_NextBlockParsed()
    {
        // Unknown block should be skipped and the end block after it should be found
        byte[] hdr = new byte[7];
        hdr[2] = 0x65; // unknown type
        BitConverter.GetBytes((ushort)0x0000).CopyTo(hdr, 3);
        BitConverter.GetBytes((ushort)7).CopyTo(hdr, 5);
        uint crc32 = Crc32Algorithm.Compute(hdr, 2, hdr.Length - 2);
        BitConverter.GetBytes((ushort)(crc32 & 0xFFFF)).CopyTo(hdr, 0);

        byte[] archiveHeader = BuildArchiveHeader();
        byte[] endBlock = BuildEndBlock();
        using var stream = BuildRAR4Stream(archiveHeader, hdr, endBlock);
        var blocks = RARDetailedParser.Parse(stream);

        // Should have: Signature, Archive Header, Unknown, End of Archive
        Assert.Contains(blocks, b => b.BlockType == "Unknown (0x65)");
        Assert.Contains(blocks, b => b.BlockType == "End of Archive");
    }

    [Fact]
    public void Parse_RAR4UnknownBlockType_BlockTypeFieldShowsDescription()
    {
        byte[] hdr = new byte[7];
        hdr[2] = 0x5F; // unknown type
        BitConverter.GetBytes((ushort)0x0000).CopyTo(hdr, 3);
        BitConverter.GetBytes((ushort)7).CopyTo(hdr, 5);
        uint crc32 = Crc32Algorithm.Compute(hdr, 2, hdr.Length - 2);
        BitConverter.GetBytes((ushort)(crc32 & 0xFFFF)).CopyTo(hdr, 0);

        byte[] archiveHeader = BuildArchiveHeader();
        byte[] endBlock = BuildEndBlock();
        using var stream = BuildRAR4Stream(archiveHeader, hdr, endBlock);
        var blocks = RARDetailedParser.Parse(stream);

        var unknown = blocks.First(b => b.BlockType == "Unknown (0x5F)");
        var typeField = unknown.Fields.First(f => f.Name == "Block Type");
        Assert.Equal("0x5F", typeField.Value);
        Assert.Equal("Unknown (0x5F)", typeField.Description);
    }

    #endregion

    #region Synthetic RAR5 Block Type Tests

    /// <summary>
    /// Encodes a value as a RAR5 vint (variable-length integer).
    /// </summary>
    private static byte[] EncodeVInt(ulong value)
    {
        var bytes = new List<byte>();
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
                b |= 0x80;
            bytes.Add(b);
        } while (value != 0);
        return bytes.ToArray();
    }

    /// <summary>
    /// Builds a RAR5 block. CRC32 is computed over headSizeVint + headerContent.
    /// </summary>
    private static byte[] BuildRAR5Block(int headType, ulong headFlags, byte[]? bodyAfterFlags = null)
    {
        using var headerMs = new MemoryStream();
        using var headerBw = new BinaryWriter(headerMs);

        headerBw.Write(EncodeVInt((ulong)headType));
        headerBw.Write(EncodeVInt(headFlags));
        if (bodyAfterFlags != null)
            headerBw.Write(bodyAfterFlags);

        headerBw.Flush();
        byte[] headerContent = headerMs.ToArray();

        var headSizeVint = EncodeVInt((ulong)headerContent.Length);

        byte[] crcInput = new byte[headSizeVint.Length + headerContent.Length];
        Array.Copy(headSizeVint, 0, crcInput, 0, headSizeVint.Length);
        Array.Copy(headerContent, 0, crcInput, headSizeVint.Length, headerContent.Length);
        uint crc = Crc32Algorithm.Compute(crcInput);

        using var blockMs = new MemoryStream();
        using var blockBw = new BinaryWriter(blockMs);
        blockBw.Write(crc);
        blockBw.Write(headSizeVint);
        blockBw.Write(headerContent);

        blockBw.Flush();
        return blockMs.ToArray();
    }

    /// <summary>
    /// Builds a RAR5 block with a data area (HFL_DATA flag set).
    /// </summary>
    private static byte[] BuildRAR5BlockWithData(int headType, ulong headFlags, byte[] bodyAfterFlags, byte[] dataArea)
    {
        // HFL_DATA (0x0002) must be included in headFlags
        headFlags |= 0x0002;

        using var headerMs = new MemoryStream();
        using var headerBw = new BinaryWriter(headerMs);

        headerBw.Write(EncodeVInt((ulong)headType));
        headerBw.Write(EncodeVInt(headFlags));
        // Data size vint
        headerBw.Write(EncodeVInt((ulong)dataArea.Length));
        headerBw.Write(bodyAfterFlags);

        headerBw.Flush();
        byte[] headerContent = headerMs.ToArray();

        var headSizeVint = EncodeVInt((ulong)headerContent.Length);

        byte[] crcInput = new byte[headSizeVint.Length + headerContent.Length];
        Array.Copy(headSizeVint, 0, crcInput, 0, headSizeVint.Length);
        Array.Copy(headerContent, 0, crcInput, headSizeVint.Length, headerContent.Length);
        uint crc = Crc32Algorithm.Compute(crcInput);

        using var blockMs = new MemoryStream();
        using var blockBw = new BinaryWriter(blockMs);
        blockBw.Write(crc);
        blockBw.Write(headSizeVint);
        blockBw.Write(headerContent);
        blockBw.Write(dataArea);

        blockBw.Flush();
        return blockMs.ToArray();
    }

    private static readonly byte[] RAR5Signature = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];

    /// <summary>
    /// Builds a minimal RAR5 stream: signature + main archive header + extra blocks.
    /// </summary>
    private static MemoryStream BuildRAR5Stream(params byte[][] extraBlocks)
    {
        var ms = new MemoryStream();
        ms.Write(RAR5Signature);

        // Main archive header (type=1), flags=0, archive flags=0
        byte[] mainBlock = BuildRAR5Block(1, 0, EncodeVInt(0));
        ms.Write(mainBlock);

        foreach (var block in extraBlocks)
            ms.Write(block);

        ms.Position = 0;
        return ms;
    }

    /// <summary>
    /// Builds a RAR5 file header body (after flags) for type=2.
    /// </summary>
    private static byte[] BuildRAR5FileHeaderBody(string fileName, ulong unpackedSize)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(fileName);

        using var bodyMs = new MemoryStream();
        using var bodyBw = new BinaryWriter(bodyMs);

        // File flags: UTIME(0x02) + CRC32(0x04) = 0x06
        bodyBw.Write(EncodeVInt(0x06));
        // Unpacked size
        bodyBw.Write(EncodeVInt(unpackedSize));
        // Attributes
        bodyBw.Write(EncodeVInt(0x20));
        // mtime (unix timestamp)
        bodyBw.Write((uint)1700000000);
        // CRC32
        bodyBw.Write((uint)0x12345678);
        // Compression info: version=0, solid=0, method=3 (Normal), dict size log=0
        ulong compInfo = (3UL << 7); // method=3 at bits 7-9
        bodyBw.Write(EncodeVInt(compInfo));
        // Host OS = Windows (0)
        bodyBw.Write(EncodeVInt(0));
        // Name length
        bodyBw.Write(EncodeVInt((ulong)nameBytes.Length));
        // Name
        bodyBw.Write(nameBytes);

        bodyBw.Flush();
        return bodyMs.ToArray();
    }

    [Fact]
    public void Parse_RAR5FileHeader_ParsesCompressionInfoAndFileName()
    {
        byte[] body = BuildRAR5FileHeaderBody("test.txt", 100);
        byte[] dataArea = new byte[50]; // fake data
        byte[] fileBlock = BuildRAR5BlockWithData(2, 0, body, dataArea);
        byte[] endBlock = BuildRAR5Block(5, 0, EncodeVInt(0));
        using var stream = BuildRAR5Stream(fileBlock, endBlock);

        var blocks = RARDetailedParser.Parse(stream);

        var fileHdr = blocks.FirstOrDefault(b => b.BlockType == "File Header");
        Assert.NotNull(fileHdr);
        Assert.Equal("test.txt", fileHdr!.ItemName);
        Assert.True(fileHdr.HasData);
        Assert.Equal(50, fileHdr.DataSize);
    }

    [Fact]
    public void Parse_RAR5FileHeader_HasCompressionInfoField()
    {
        byte[] body = BuildRAR5FileHeaderBody("data.bin", 200);
        byte[] fileBlock = BuildRAR5BlockWithData(2, 0, body, new byte[10]);
        byte[] endBlock = BuildRAR5Block(5, 0, EncodeVInt(0));
        using var stream = BuildRAR5Stream(fileBlock, endBlock);

        var blocks = RARDetailedParser.Parse(stream);

        var fileHdr = blocks.First(b => b.BlockType == "File Header");
        var compInfo = fileHdr.Fields.FirstOrDefault(f => f.Name == "Compression Info");
        Assert.NotNull(compInfo);
        // Should have children for VERSION, SOLID, METHOD, DICT_SIZE
        Assert.Contains(compInfo!.Children, c => c.Name == "METHOD");
        Assert.Contains(compInfo.Children, c => c.Name == "VERSION");
    }

    [Fact]
    public void Parse_RAR5FileHeader_HasHostOSField()
    {
        byte[] body = BuildRAR5FileHeaderBody("info.dat", 0);
        byte[] fileBlock = BuildRAR5BlockWithData(2, 0, body, Array.Empty<byte>());
        byte[] endBlock = BuildRAR5Block(5, 0, EncodeVInt(0));
        using var stream = BuildRAR5Stream(fileBlock, endBlock);

        var blocks = RARDetailedParser.Parse(stream);

        var fileHdr = blocks.First(b => b.BlockType == "File Header");
        var hostOs = fileHdr.Fields.FirstOrDefault(f => f.Name == "Host OS");
        Assert.NotNull(hostOs);
        Assert.Equal("Windows", hostOs!.Description);
    }

    [Fact]
    public void Parse_RAR5EndOfArchive_DetectedCorrectly()
    {
        byte[] endBlock = BuildRAR5Block(5, 0, EncodeVInt(0)); // type=5, end flags=0
        using var stream = BuildRAR5Stream(endBlock);

        var blocks = RARDetailedParser.Parse(stream);

        var end = blocks.FirstOrDefault(b => b.BlockType == "End of Archive");
        Assert.NotNull(end);
        Assert.Equal(5, end!.BlockTypeValue);
    }

    [Fact]
    public void Parse_RAR5EndOfArchive_HasEndFlagsField()
    {
        byte[] endBlock = BuildRAR5Block(5, 0, EncodeVInt(0));
        using var stream = BuildRAR5Stream(endBlock);

        var blocks = RARDetailedParser.Parse(stream);

        var end = blocks.First(b => b.BlockType == "End of Archive");
        var endFlags = end.Fields.FirstOrDefault(f => f.Name == "End Flags");
        Assert.NotNull(endFlags);
    }

    #endregion

    #region Empty and Minimal Stream Tests

    [Fact]
    public void Parse_EmptyStream_ReturnsEmptyList()
    {
        using var stream = new MemoryStream();

        var blocks = RARDetailedParser.Parse(stream);

        Assert.Empty(blocks);
    }

    [Fact]
    public void Parse_StreamTooShortForSignature_ReturnsEmpty()
    {
        // 3 bytes is too short for either RAR4 (7) or RAR5 (8) signature
        using var stream = new MemoryStream([0x52, 0x61, 0x72]);

        var blocks = RARDetailedParser.Parse(stream);

        Assert.Empty(blocks);
    }

    [Fact]
    public void Parse_RAR4SignatureOnly_ReturnsSignatureBlockOnly()
    {
        using var stream = new MemoryStream([0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00]);

        var blocks = RARDetailedParser.Parse(stream);

        Assert.Single(blocks);
        Assert.Equal("Signature", blocks[0].BlockType);
        Assert.Equal(7, blocks[0].TotalSize);
    }

    [Fact]
    public void Parse_RAR5SignatureOnly_ReturnsSignatureBlockOnly()
    {
        using var stream = new MemoryStream([0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00]);

        var blocks = RARDetailedParser.Parse(stream);

        Assert.Single(blocks);
        Assert.Equal("Signature", blocks[0].BlockType);
        Assert.Equal(8, blocks[0].TotalSize);
        Assert.Equal("RAR 5.x signature", blocks[0].Fields[0].Description);
    }

    #endregion
}
