using ReScene.RAR;
using ReScene.RAR.Decompression;

namespace ReScene.Tests;

public class RARArchiveTests
{
    private static readonly string TestDataPath = Path.Combine(
        AppContext.BaseDirectory, "TestData");

    private const string ExpectedComment = "Test comment for RARLib.\r\nThis is a compressed comment test.\r\n";

    /// <summary>
    /// Helper method to extract comment from a RAR file using RAR.
    /// </summary>
    private static string? ExtractCommentFromRar(string rarFilePath)
    {
        using var fs = new FileStream(rarFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(fs);

        // Skip RAR 4.x marker: "Rar!\x1a\x07\x00" (7 bytes)
        byte[] marker = br.ReadBytes(7);
        if (marker.Length != 7)
            return null;

        // Verify RAR 4.x signature
        if (marker[0] != 0x52 || marker[1] != 0x61 || marker[2] != 0x72 || marker[3] != 0x21 ||
            marker[4] != 0x1A || marker[5] != 0x07 || marker[6] != 0x00)
        {
            return null; // Not a RAR 4.x file
        }

        var reader = new RARHeaderReader(fs);

        while (fs.Position < fs.Length)
        {
            var block = reader.ReadBlock(parseContents: true);
            if (block == null)
                break;

            // Check for archive header with embedded comment (MHD_COMMENT flag)
            if (block.BlockType == RAR4BlockType.ArchiveHeader &&
                (block.Flags & 0x0002) != 0) // MHD_COMMENT flag
            {
                string? comment = ExtractEmbeddedComment(fs, block);
                if (comment != null)
                    return comment;
            }

            // Check for old-style comment block (0x75) - used in RAR 2.x
            if (block.BlockType == RAR4BlockType.Comment)
            {
                return ExtractOldStyleComment(fs, block);
            }

            // Found CMT comment service block (RAR 3.x+)
            if (block.BlockType == RAR4BlockType.Service &&
                block.ServiceBlockInfo != null &&
                string.Equals(block.ServiceBlockInfo.SubType, "CMT", StringComparison.OrdinalIgnoreCase))
            {
                byte[]? commentData = reader.ReadServiceBlockData(block);
                if (commentData == null || commentData.Length == 0)
                    return null;

                if (block.ServiceBlockInfo.IsStored)
                {
                    // Stored (uncompressed) comment
                    return System.Text.Encoding.UTF8.GetString(commentData);
                }
                else
                {
                    // Compressed comment
                    return RARDecompressor.DecompressComment(
                        commentData,
                        (int)block.ServiceBlockInfo.UnpackedSize,
                        block.ServiceBlockInfo.CompressionMethod,
                        isRAR5: false);
                }
            }

            // Skip to next block (include data for all blocks except file headers)
            reader.SkipBlock(block, includeData: block.BlockType != RAR4BlockType.FileHeader);
        }

        return null;
    }

    /// <summary>
    /// Extracts embedded comment from archive header with MHD_COMMENT flag.
    /// Used in RAR 2.x archives where comment block is embedded in archive header.
    /// </summary>
    private static string? ExtractEmbeddedComment(FileStream fs, RARBlockReadResult block)
    {
        // Archive header with MHD_COMMENT has comment block embedded after base fields
        // Base header: CRC(2) + Type(1) + Flags(2) + HeaderSize(2) + Reserved1(2) + Reserved2(4) = 13 bytes
        // Then embedded comment sub-block follows

        long commentBlockStart = block.BlockPosition + 13;
        fs.Seek(commentBlockStart, SeekOrigin.Begin);
        using var br = new BinaryReader(fs, System.Text.Encoding.UTF8, leaveOpen: true);

        // Read embedded comment block header
        ushort commCrc = br.ReadUInt16();
        byte commType = br.ReadByte();
        if (commType != 0x75) // Not a comment block
            return null;

        ushort commFlags = br.ReadUInt16();
        ushort commSize = br.ReadUInt16();
        ushort unpSize = br.ReadUInt16();
        byte unpVer = br.ReadByte();
        byte method = br.ReadByte();
        ushort dataCrc = br.ReadUInt16();

        // Comment data follows the 13-byte comment header
        int dataSize = commSize - 13;
        if (dataSize <= 0)
            return null;

        byte[] compressedData = br.ReadBytes(dataSize);
        if (compressedData.Length != dataSize)
            return null;

        return RARDecompressor.DecompressComment(compressedData, unpSize, method, isRAR5: false);
    }

    /// <summary>
    /// Extracts comment from old-style comment block (type 0x75).
    /// Used in RAR 2.x archives.
    /// </summary>
    private static string? ExtractOldStyleComment(FileStream fs, RARBlockReadResult block)
    {
        // Old comment block structure (after base header):
        // UNP_SIZE (2) - Uncompressed size
        // UNP_VER (1) - Version to unpack
        // METHOD (1) - Compression method
        // COMM_CRC (2) - CRC of comment
        // Then compressed data

        long headerStart = block.BlockPosition;
        long dataStart = headerStart + 7 + 6; // Base header (7) + extension (6)
        int dataSize = block.HeaderSize - 7 - 6;

        if (dataSize <= 0)
            return null;

        // Seek to data position
        fs.Seek(headerStart + 7, SeekOrigin.Begin);
        using var br = new BinaryReader(fs, System.Text.Encoding.UTF8, leaveOpen: true);

        ushort unpSize = br.ReadUInt16();
        byte unpVer = br.ReadByte();
        byte method = br.ReadByte();
        ushort commCrc = br.ReadUInt16();

        // Read compressed data
        dataSize = block.HeaderSize - 13; // Total - base header - extension fields
        if (dataSize <= 0)
            return null;

        byte[] compressedData = br.ReadBytes(dataSize);
        if (compressedData.Length != dataSize)
            return null;

        // Decompress
        return RARDecompressor.DecompressComment(compressedData, unpSize, method, isRAR5: false);
    }

    [Fact]
    public void WinRAR29_NormalCompression_ExtractsComment()
    {
        // WinRAR 2.9 creates archives with unpVer=20 (RAR 2.0 format).
        // This tests the RAR 2.0 decompression algorithm (Unpack20).

        // Arrange
        string rarPath = Path.Combine(TestDataPath, "test_wrar29_m3.rar");
        if (!File.Exists(rarPath))
        {
            Assert.Fail($"Test file not found: {rarPath}");
            return;
        }

        // Read embedded comment from archive header
        using var fs = new FileStream(rarPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(7, SeekOrigin.Begin); // Skip marker

        var reader = new RARHeaderReader(fs);
        var block = reader.ReadBlock(parseContents: true);

        Assert.NotNull(block);
        Assert.Equal(RAR4BlockType.ArchiveHeader, block!.BlockType);
        Assert.True((block.Flags & 0x0002) != 0, $"MHD_COMMENT flag not set. Flags=0x{block.Flags:X4}");

        // Read embedded comment header
        fs.Seek(block.BlockPosition + 13, SeekOrigin.Begin);
        using var br = new BinaryReader(fs, System.Text.Encoding.UTF8, leaveOpen: true);

        ushort commCrc = br.ReadUInt16();
        byte commType = br.ReadByte();
        Assert.Equal(0x75, commType); // Old comment block type

        ushort commFlags = br.ReadUInt16();
        ushort commSize = br.ReadUInt16();
        ushort unpSize = br.ReadUInt16();
        byte unpVer = br.ReadByte();
        byte method = br.ReadByte();
        ushort dataCrc = br.ReadUInt16();

        // WinRAR 2.9 creates RAR 2.0 format (unpVer=20)
        Assert.Equal(20, unpVer);
        Assert.Equal(62, unpSize); // Expected uncompressed size

        // Read compressed data
        int dataSize = commSize - 13;
        byte[] compressedData = br.ReadBytes(dataSize);

        string debugInfo = $"unpVer={unpVer}, method=0x{method:X2}, unpSize={unpSize}, dataSize={dataSize}";

        // Act - decompress using RAR 2.0 algorithm
        RARMethod rarMethod = (RARMethod)method;
        string? comment = RARDecompressor.DecompressComment(compressedData, unpSize, rarMethod, RARVersion.RAR20);

        // Assert
        Assert.True(comment != null, $"Decompression returned null. {debugInfo}");
        Assert.Equal(ExpectedComment, comment);
    }

    [Fact]
    public void WinRAR35_NormalCompression_ExtractsComment()
    {
        // Arrange
        string rarPath = Path.Combine(TestDataPath, "test_wrar35_m3.rar");
        if (!File.Exists(rarPath))
        {
            Assert.Fail($"Test file not found: {rarPath}");
            return;
        }

        // Act
        string? comment = ExtractCommentFromRar(rarPath);

        // Assert
        Assert.NotNull(comment);
        Assert.Equal(ExpectedComment, comment);
    }

    [Fact]
    public void WinRAR40_NormalCompression_ExtractsComment()
    {
        // Arrange
        string rarPath = Path.Combine(TestDataPath, "test_wrar40_m3.rar");
        if (!File.Exists(rarPath))
        {
            Assert.Fail($"Test file not found: {rarPath}");
            return;
        }

        // Act
        string? comment = ExtractCommentFromRar(rarPath);

        // Assert
        Assert.NotNull(comment);
        Assert.Equal(ExpectedComment, comment);
    }

    [Fact]
    public void WinRAR40_StoredCompression_ExtractsComment()
    {
        // Arrange
        string rarPath = Path.Combine(TestDataPath, "test_wrar40_m0.rar");
        if (!File.Exists(rarPath))
        {
            Assert.Fail($"Test file not found: {rarPath}");
            return;
        }

        // Act - debug by reading the raw data first
        using var fs = new FileStream(rarPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(7, SeekOrigin.Begin); // Skip marker
        var reader = new RARHeaderReader(fs);

        while (fs.Position < fs.Length)
        {
            var block = reader.ReadBlock(parseContents: true);
            if (block == null) break;

            if (block.BlockType == RAR4BlockType.Service &&
                block.ServiceBlockInfo != null &&
                string.Equals(block.ServiceBlockInfo.SubType, "CMT", StringComparison.OrdinalIgnoreCase))
            {
                byte[]? data = reader.ReadServiceBlockData(block);
                string debugInfo = $"Method=0x{block.ServiceBlockInfo.CompressionMethod:X2}, " +
                    $"IsStored={block.ServiceBlockInfo.IsStored}, " +
                    $"PackedSize={block.ServiceBlockInfo.PackedSize}, " +
                    $"UnpackedSize={block.ServiceBlockInfo.UnpackedSize}, " +
                    $"DataLen={data?.Length ?? 0}";

                if (data != null && data.Length > 0)
                {
                    debugInfo += $", DataHex={BitConverter.ToString(data).Replace("-", "")[..Math.Min(40, data.Length * 2)]}";
                }

                // Try to decompress
                string? comment = RARDecompressor.DecompressComment(
                    data!,
                    (int)block.ServiceBlockInfo.UnpackedSize,
                    block.ServiceBlockInfo.CompressionMethod,
                    isRAR5: false);

                Assert.True(comment != null, $"Decompression returned null. {debugInfo}");
                Assert.True(comment!.Length > 0, $"Decompression returned empty. {debugInfo}");
                Assert.Equal(ExpectedComment, comment);
                return;
            }

            reader.SkipBlock(block, includeData: block.BlockType != RAR4BlockType.FileHeader);
        }

        Assert.Fail("CMT block not found");
    }

    [Fact]
    public void WinRAR40_BestCompression_ExtractsComment()
    {
        // Arrange
        string rarPath = Path.Combine(TestDataPath, "test_wrar40_m5.rar");
        if (!File.Exists(rarPath))
        {
            Assert.Fail($"Test file not found: {rarPath}");
            return;
        }

        // Act
        string? comment = ExtractCommentFromRar(rarPath);

        // Assert
        Assert.NotNull(comment);
        Assert.Equal(ExpectedComment, comment);
    }

    [Fact]
    public void ExtractComment_ReadsServiceBlockCorrectly()
    {
        // Arrange - test that we can read service block info
        string rarPath = Path.Combine(TestDataPath, "test_wrar40_m3.rar");
        if (!File.Exists(rarPath))
        {
            Assert.Fail($"Test file not found: {rarPath}");
            return;
        }

        // Act
        using var fs = new FileStream(rarPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        // Skip RAR 4.x marker
        fs.Seek(7, SeekOrigin.Begin);
        var reader = new RARHeaderReader(fs);

        var blockList = new List<string>();
        RARServiceBlockInfo? foundServiceInfo = null;
        while (fs.Position < fs.Length)
        {
            var block = reader.ReadBlock(parseContents: true);
            if (block == null)
            {
                blockList.Add($"null at position {fs.Position}");
                break;
            }

            string blockInfo = $"Type={block.BlockType}, Flags=0x{block.Flags:X4}, HeaderSize={block.HeaderSize}, AddSize={block.AddSize}";
            if (block.ServiceBlockInfo != null)
            {
                blockInfo += $", SubType={block.ServiceBlockInfo.SubType}, Method=0x{block.ServiceBlockInfo.CompressionMethod:X2}";
            }
            blockList.Add(blockInfo);

            if (block.BlockType == RAR4BlockType.Service &&
                block.ServiceBlockInfo != null &&
                string.Equals(block.ServiceBlockInfo.SubType, "CMT", StringComparison.OrdinalIgnoreCase))
            {
                foundServiceInfo = block.ServiceBlockInfo;
                break;
            }

            reader.SkipBlock(block, includeData: block.BlockType != RAR4BlockType.FileHeader);
        }

        // Output block list for debugging
        string debugInfo = string.Join("\n", blockList);
        Assert.True(foundServiceInfo != null, $"CMT block not found. Blocks found:\n{debugInfo}");
        Assert.Equal("CMT", foundServiceInfo!.SubType);
        Assert.True(foundServiceInfo.PackedSize > 0);
        Assert.True(foundServiceInfo.UnpackedSize > 0);
        Assert.Equal(0x33, foundServiceInfo.CompressionMethod); // Normal compression
    }

    // ================== RAR 5.0 Tests ==================

    /// <summary>
    /// Helper method to extract comment from a RAR 5.0 file using RAR.
    /// </summary>
    private static string? ExtractCommentFromRar5(string rarFilePath)
    {
        using var fs = new FileStream(rarFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        // Check for RAR 5.0 marker
        if (!RAR5HeaderReader.IsRAR5(fs))
            return null;

        // Skip RAR 5.0 marker (8 bytes)
        fs.Seek(8, SeekOrigin.Begin);

        var reader = new RAR5HeaderReader(fs);

        while (fs.Position < fs.Length)
        {
            var block = reader.ReadBlock();
            if (block == null)
                break;

            // Found CMT comment service block
            if (block.BlockType == RAR5BlockType.Service &&
                block.ServiceBlockInfo != null &&
                string.Equals(block.ServiceBlockInfo.SubType, "CMT", StringComparison.OrdinalIgnoreCase))
            {
                byte[]? commentData = reader.ReadServiceBlockData(block);
                if (commentData == null || commentData.Length == 0)
                    return null;

                string? text;
                if (block.ServiceBlockInfo.IsStored)
                {
                    // Stored (uncompressed) comment
                    text = System.Text.Encoding.UTF8.GetString(commentData);
                }
                else
                {
                    // Compressed comment
                    text = RARDecompressor.DecompressComment(
                        commentData,
                        (int)block.ServiceBlockInfo.UnpackedSize,
                        (byte)(0x30 + block.ServiceBlockInfo.CompressionMethod),
                        isRAR5: true);
                }
                // Trim trailing nulls
                return text?.TrimEnd('\0');
            }

            reader.SkipBlock(block);
        }

        return null;
    }

    [Fact]
    public void RAR5_NormalCompression_ExtractsComment()
    {
        // Arrange
        string rarPath = Path.Combine(TestDataPath, "test_rar5_m3.rar");
        if (!File.Exists(rarPath))
        {
            Assert.Fail($"Test file not found: {rarPath}");
            return;
        }

        // Act
        string? comment = ExtractCommentFromRar5(rarPath);

        // Assert
        Assert.NotNull(comment);
        Assert.Equal(ExpectedComment, comment);
    }

    [Fact]
    public void RAR5_BestCompression_ExtractsComment()
    {
        // Arrange
        string rarPath = Path.Combine(TestDataPath, "test_rar5_m5.rar");
        if (!File.Exists(rarPath))
        {
            Assert.Fail($"Test file not found: {rarPath}");
            return;
        }

        // Act
        string? comment = ExtractCommentFromRar5(rarPath);

        // Assert
        Assert.NotNull(comment);
        Assert.Equal(ExpectedComment, comment);
    }

    [Fact]
    public void WinRAR35_PPMdMode_ExtractsComment()
    {
        // Test archive created with -mc+ (PPMd text compression enabled)
        // Arrange
        string rarPath = Path.Combine(TestDataPath, "test_ppm_m5.rar");
        if (!File.Exists(rarPath))
        {
            Assert.Fail($"Test file not found: {rarPath}");
            return;
        }

        // Act
        string? comment = ExtractCommentFromRar(rarPath);

        // Assert
        Assert.NotNull(comment);
        Assert.Equal(ExpectedComment, comment);
    }

    [Fact]
    public void RAR5_VerifyRawPositions()
    {
        // Debug test to verify exact byte positions
        string rarPath = Path.Combine(TestDataPath, "test_rar5_m3.rar");
        using var fs = new FileStream(rarPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(fs);

        // Position 27 should be 0x13 (header size vint for Service block)
        fs.Seek(27, SeekOrigin.Begin);
        byte byteAt27 = br.ReadByte();

        // Position 47 should be 'T' (start of "Test comment...")
        fs.Seek(47, SeekOrigin.Begin);
        byte byteAt47 = br.ReadByte();

        // Position 108 should be newline (end of comment)
        fs.Seek(108, SeekOrigin.Begin);
        byte byteAt108 = br.ReadByte();

        Assert.Equal(0x13, byteAt27); // Header size = 19
        Assert.Equal((byte)'T', byteAt47); // Start of comment
        Assert.Equal(0x0a, byteAt108); // End of comment (newline)

        // Now verify RAR5HeaderReader reads correctly
        fs.Seek(8, SeekOrigin.Begin);
        var reader = new RAR5HeaderReader(fs);

        // Read Main header
        var mainBlock = reader.ReadBlock();
        Assert.NotNull(mainBlock);
        Assert.Equal(RAR5BlockType.Main, mainBlock!.BlockType);
        Assert.Equal(10ul, mainBlock.HeaderSize); // Should be 10
        long posAfterMain = fs.Position;

        // Skip to next block
        reader.SkipBlock(mainBlock);
        long posBeforeService = fs.Position;

        // Read Service header
        var serviceBlock = reader.ReadBlock();
        Assert.NotNull(serviceBlock);
        Assert.Equal(RAR5BlockType.Service, serviceBlock!.BlockType);

        // Key assertion: HeaderSize should be 19, not 56
        Assert.True(serviceBlock.HeaderSize == 19,
            $"Service HeaderSize should be 19, but was {serviceBlock.HeaderSize}. " +
            $"Position after Main={posAfterMain}, Position before Service={posBeforeService}, " +
            $"BlockPosition={serviceBlock.BlockPosition}");

        // Data should start at BlockPosition + HeaderSize = 28 + 19 = 47
        long expectedDataStart = serviceBlock.BlockPosition + (long)serviceBlock.HeaderSize;
        Assert.Equal(47, expectedDataStart);
    }

    [Fact]
    public void RAR5_ReadsHeaderCorrectly()
    {
        // Arrange
        string rarPath = Path.Combine(TestDataPath, "test_rar5_m3.rar");
        if (!File.Exists(rarPath))
        {
            Assert.Fail($"Test file not found: {rarPath}");
            return;
        }

        // Act
        using var fs = new FileStream(rarPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.True(RAR5HeaderReader.IsRAR5(fs), "Not a RAR 5.0 file");

        // Skip marker
        fs.Seek(8, SeekOrigin.Begin);
        var reader = new RAR5HeaderReader(fs);

        var blockList = new List<string>();
        RAR5ServiceBlockInfo? foundServiceInfo = null;

        while (fs.Position < fs.Length)
        {
            var block = reader.ReadBlock();
            if (block == null)
            {
                blockList.Add($"null at position {fs.Position}");
                break;
            }

            string blockInfo = $"Type={block.BlockType}, Flags=0x{block.Flags:X4}, " +
                $"HeaderSize={block.HeaderSize}, DataSize={block.DataSize}";

            if (block.ServiceBlockInfo != null)
            {
                blockInfo += $", SubType={block.ServiceBlockInfo.SubType}, " +
                    $"Method={block.ServiceBlockInfo.CompressionMethod}, " +
                    $"IsStored={block.ServiceBlockInfo.IsStored}";
            }

            blockList.Add(blockInfo);

            if (block.BlockType == RAR5BlockType.Service &&
                block.ServiceBlockInfo != null &&
                string.Equals(block.ServiceBlockInfo.SubType, "CMT", StringComparison.OrdinalIgnoreCase))
            {
                foundServiceInfo = block.ServiceBlockInfo;
            }

            reader.SkipBlock(block);
        }

        // Output block list for debugging
        string debugInfo = string.Join("\n", blockList);
        Assert.True(foundServiceInfo != null, $"CMT block not found. Blocks found:\n{debugInfo}");
        Assert.Equal("CMT", foundServiceInfo!.SubType);
    }
}
