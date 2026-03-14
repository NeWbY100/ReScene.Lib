namespace ReScene.RAR.Tests;

public class RAR5HeaderReaderTests
{
    private static readonly string TestDataPath = Path.Combine(
        AppContext.BaseDirectory, "TestData");

    #region IsRAR5 Tests

    [Fact]
    public void IsRAR5_ValidMarker_ReturnsTrue()
    {
        using var stream = new MemoryStream([0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00]);

        Assert.True(RAR5HeaderReader.IsRAR5(stream));
        Assert.Equal(0, stream.Position); // Position should be restored
    }

    [Fact]
    public void IsRAR5_RAR4Marker_ReturnsFalse()
    {
        using var stream = new MemoryStream([0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00]);

        Assert.False(RAR5HeaderReader.IsRAR5(stream));
    }

    [Fact]
    public void IsRAR5_TooShort_ReturnsFalse()
    {
        using var stream = new MemoryStream([0x52, 0x61, 0x72]);

        Assert.False(RAR5HeaderReader.IsRAR5(stream));
    }

    [Fact]
    public void IsRAR5_EmptyStream_ReturnsFalse()
    {
        using var stream = new MemoryStream();

        Assert.False(RAR5HeaderReader.IsRAR5(stream));
    }

    [Fact]
    public void IsRAR5_InvalidBytes_ReturnsFalse()
    {
        using var stream = new MemoryStream([0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);

        Assert.False(RAR5HeaderReader.IsRAR5(stream));
    }

    #endregion

    #region RAR5Marker Tests

    [Fact]
    public void RAR5Marker_HasCorrectLength()
    {
        Assert.Equal(8, RAR5HeaderReader.RAR5Marker.Length);
    }

    [Fact]
    public void RAR5Marker_HasCorrectBytes()
    {
        byte[] expected = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];
        Assert.Equal(expected, RAR5HeaderReader.RAR5Marker);
    }

    #endregion

    #region ReadVInt Tests

    [Fact]
    public void ReadVInt_SingleByte_ReturnsCorrectValue()
    {
        using var stream = new MemoryStream([0x0A]); // value = 10
        var reader = new RAR5HeaderReader(stream);

        ulong result = reader.ReadVInt();

        Assert.Equal(10ul, result);
    }

    [Fact]
    public void ReadVInt_MultiByte_ReturnsCorrectValue()
    {
        // Two bytes: 0x80 | 0x01 = first byte (continuation), 0x02 = second byte
        // Value: (0x01) | (0x02 << 7) = 1 + 256 = 257
        using var stream = new MemoryStream([0x81, 0x02]);
        var reader = new RAR5HeaderReader(stream);

        ulong result = reader.ReadVInt();

        Assert.Equal(257ul, result);
    }

    [Fact]
    public void ReadVInt_Zero_ReturnsZero()
    {
        using var stream = new MemoryStream([0x00]);
        var reader = new RAR5HeaderReader(stream);

        ulong result = reader.ReadVInt();

        Assert.Equal(0ul, result);
    }

    [Fact]
    public void ReadVInt_MaxSingleByte_Returns127()
    {
        using var stream = new MemoryStream([0x7F]); // 127
        var reader = new RAR5HeaderReader(stream);

        ulong result = reader.ReadVInt();

        Assert.Equal(127ul, result);
    }

    #endregion

    #region ReadBlock Tests with Real RAR5 Files

    [Fact]
    public void ReadBlock_RAR5File_ParsesMainHeader()
    {
        string rarPath = Path.Combine(TestDataPath, "test_rar5_m3.rar");
        if (!File.Exists(rarPath))
        {
            Assert.Fail($"Test file not found: {rarPath}");
            return;
        }

        using var fs = new FileStream(rarPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.True(RAR5HeaderReader.IsRAR5(fs));
        fs.Seek(8, SeekOrigin.Begin); // Skip marker

        var reader = new RAR5HeaderReader(fs);
        var block = reader.ReadBlock();

        Assert.NotNull(block);
        Assert.Equal(RAR5BlockType.Main, block!.BlockType);
        Assert.True(block.CrcValid);
    }

    [Fact]
    public void ReadBlock_RAR5File_ParsesAllBlockTypes()
    {
        string rarPath = Path.Combine(TestDataPath, "test_rar5_m3.rar");
        if (!File.Exists(rarPath))
        {
            Assert.Fail($"Test file not found: {rarPath}");
            return;
        }

        using var fs = new FileStream(rarPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(8, SeekOrigin.Begin);

        var reader = new RAR5HeaderReader(fs);
        var blockTypes = new List<RAR5BlockType>();

        while (fs.Position < fs.Length)
        {
            var block = reader.ReadBlock();
            if (block == null) break;

            blockTypes.Add(block.BlockType);
            reader.SkipBlock(block);
        }

        Assert.Contains(RAR5BlockType.Main, blockTypes);
        // A RAR5 file with comment should have Service block
    }

    [Fact]
    public void ReadBlock_RAR5File_ServiceBlockHasCmtSubType()
    {
        string rarPath = Path.Combine(TestDataPath, "test_rar5_m3.rar");
        if (!File.Exists(rarPath))
        {
            Assert.Fail($"Test file not found: {rarPath}");
            return;
        }

        using var fs = new FileStream(rarPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(8, SeekOrigin.Begin);

        var reader = new RAR5HeaderReader(fs);
        RAR5ServiceBlockInfo? cmtBlock = null;

        while (fs.Position < fs.Length)
        {
            var block = reader.ReadBlock();
            if (block == null) break;

            if (block.BlockType == RAR5BlockType.Service && block.ServiceBlockInfo?.SubType == "CMT")
            {
                cmtBlock = block.ServiceBlockInfo;
                break;
            }
            reader.SkipBlock(block);
        }

        Assert.NotNull(cmtBlock);
        Assert.Equal("CMT", cmtBlock!.SubType);
    }

    #endregion

    #region CanReadBaseHeader Tests

    [Fact]
    public void CanReadBaseHeader_SufficientData_ReturnsTrue()
    {
        using var stream = new MemoryStream(new byte[10]);
        var reader = new RAR5HeaderReader(stream);

        Assert.True(reader.CanReadBaseHeader);
    }

    [Fact]
    public void CanReadBaseHeader_InsufficientData_ReturnsFalse()
    {
        using var stream = new MemoryStream(new byte[3]);
        var reader = new RAR5HeaderReader(stream);

        Assert.False(reader.CanReadBaseHeader);
    }

    [Fact]
    public void CanReadBaseHeader_EmptyStream_ReturnsFalse()
    {
        using var stream = new MemoryStream();
        var reader = new RAR5HeaderReader(stream);

        Assert.False(reader.CanReadBaseHeader);
    }

    #endregion

    #region PeekBlockType Tests

    [Fact]
    public void PeekBlockType_EmptyStream_ReturnsNull()
    {
        using var stream = new MemoryStream();
        var reader = new RAR5HeaderReader(stream);

        Assert.Null(reader.PeekBlockType());
    }

    [Fact]
    public void PeekBlockType_DoesNotAdvancePosition()
    {
        string rarPath = Path.Combine(TestDataPath, "test_rar5_m3.rar");
        if (!File.Exists(rarPath)) return;

        using var fs = new FileStream(rarPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(8, SeekOrigin.Begin);

        var reader = new RAR5HeaderReader(fs);
        long posBefore = fs.Position;
        _ = reader.PeekBlockType();
        long posAfter = fs.Position;

        Assert.Equal(posBefore, posAfter);
    }

    #endregion

    #region RAR5 Enum and Class Tests

    [Fact]
    public void RAR5BlockType_HasExpectedValues()
    {
        Assert.Equal(0x00, (byte)RAR5BlockType.Marker);
        Assert.Equal(0x01, (byte)RAR5BlockType.Main);
        Assert.Equal(0x02, (byte)RAR5BlockType.File);
        Assert.Equal(0x03, (byte)RAR5BlockType.Service);
        Assert.Equal(0x04, (byte)RAR5BlockType.Crypt);
        Assert.Equal(0x05, (byte)RAR5BlockType.EndArchive);
    }

    [Fact]
    public void RAR5ArchiveInfo_IsVolume_Flag()
    {
        var info = new RAR5ArchiveInfo { ArchiveFlags = 0x0001 };
        Assert.True(info.IsVolume);
    }

    [Fact]
    public void RAR5ArchiveInfo_IsSolid_Flag()
    {
        var info = new RAR5ArchiveInfo { ArchiveFlags = 0x0004 };
        Assert.True(info.IsSolid);
    }

    [Fact]
    public void RAR5FileInfo_IsStored_WhenMethodZero()
    {
        // Compression info: method=0 at bits 7-9
        var info = new RAR5FileInfo { CompressionInfo = 0 };
        Assert.True(info.IsStored);
    }

    [Fact]
    public void RAR5FileInfo_CompressionMethod_ExtractsCorrectly()
    {
        // Method stored at bits 7-9: method=3 means 0x03 << 7 = 0x180
        var info = new RAR5FileInfo { CompressionInfo = 0x180 };
        Assert.Equal(3, info.CompressionMethod);
    }

    [Fact]
    public void RAR5FileInfo_IsDirectory_WhenFlagSet()
    {
        var info = new RAR5FileInfo { FileFlags = 0x0001 };
        Assert.True(info.IsDirectory);
    }

    [Fact]
    public void RAR5ServiceBlockInfo_IsStored_WhenMethodZero()
    {
        var info = new RAR5ServiceBlockInfo { IsStored = true };
        Assert.True(info.IsStored);
    }

    #endregion

    #region Edge Case Tests

    /// <summary>
    /// Encodes a value as a RAR5 variable-length integer (vint).
    /// Each byte uses bits 0-6 for data and bit 7 as continuation flag.
    /// </summary>
    private static byte[] EncodeVInt(ulong value)
    {
        var bytes = new List<byte>();
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value > 0)
                b |= 0x80; // continuation bit
            bytes.Add(b);
        } while (value > 0);
        return bytes.ToArray();
    }

    /// <summary>
    /// Builds a synthetic RAR5 block in a MemoryStream.
    /// Returns the stream positioned at the start of the block (after the CRC field).
    /// CRC is set to 0 so CrcValid will be false - we're testing parsing, not CRC.
    /// </summary>
    private static MemoryStream BuildRAR5Block(
        RAR5BlockType blockType,
        ulong flags,
        ulong? extraAreaSize = null,
        ulong? dataSize = null,
        byte[]? additionalHeaderContent = null)
    {
        // Build header content: type + flags + optional extra + optional data + additional
        var headerContent = new MemoryStream();
        headerContent.Write(EncodeVInt((ulong)blockType));
        headerContent.Write(EncodeVInt(flags));
        if (extraAreaSize.HasValue)
            headerContent.Write(EncodeVInt(extraAreaSize.Value));
        if (dataSize.HasValue)
            headerContent.Write(EncodeVInt(dataSize.Value));
        if (additionalHeaderContent != null)
            headerContent.Write(additionalHeaderContent);

        byte[] headerBytes = headerContent.ToArray();

        // Build the full block: CRC(4) + HeaderSize(vint) + headerBytes
        var block = new MemoryStream();
        // CRC placeholder (0 - we're not testing CRC validity here)
        block.Write(new byte[4]);
        // Header size = length of headerBytes
        block.Write(EncodeVInt((ulong)headerBytes.Length));
        block.Write(headerBytes);
        block.Position = 0;
        return block;
    }

    [Fact]
    public void ReadVInt_ThreeByteValue_ReturnsCorrectValue()
    {
        // Encode a value that requires 3 bytes: 16384 (= 0x4000)
        // byte 0: 0x80 | (16384 & 0x7F) = 0x80 (continuation, lower 7 bits = 0)
        // byte 1: 0x80 | ((16384 >> 7) & 0x7F) = 0x80 (continuation, lower 7 bits = 0)
        // byte 2: (16384 >> 14) & 0x7F = 0x01 (no continuation)
        using var stream = new MemoryStream([0x80, 0x80, 0x01]);
        var reader = new RAR5HeaderReader(stream);

        ulong result = reader.ReadVInt();

        Assert.Equal(16384ul, result);
    }

    [Fact]
    public void ReadVInt_FourByteValue_ReturnsCorrectValue()
    {
        // Encode 2_097_152 (0x200000 = 1 << 21), requires 4 bytes
        // Each byte contributes 7 bits, so 4 bytes cover bits 0-27
        // 2097152 = 1 << 21, so byte0..byte2 are continuation with 0 data, byte3 = 1
        using var stream = new MemoryStream([0x80, 0x80, 0x80, 0x01]);
        var reader = new RAR5HeaderReader(stream);

        ulong result = reader.ReadVInt();

        Assert.Equal(2_097_152ul, result);
    }

    [Fact]
    public void ReadVInt_LargeMultiByteValue_DecodesCorrectly()
    {
        // Use EncodeVInt helper to encode and then decode a known large value
        ulong expected = 123_456_789ul;
        byte[] encoded = EncodeVInt(expected);
        using var stream = new MemoryStream(encoded);
        var reader = new RAR5HeaderReader(stream);

        ulong result = reader.ReadVInt();

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ReadVInt_EndOfStream_ThrowsEndOfStreamException()
    {
        using var stream = new MemoryStream();
        var reader = new RAR5HeaderReader(stream);

        Assert.Throws<EndOfStreamException>(() => reader.ReadVInt());
    }

    [Fact]
    public void ReadVInt_TruncatedMultiByte_ThrowsEndOfStreamException()
    {
        // First byte has continuation bit set but no second byte follows
        using var stream = new MemoryStream([0x80]);
        var reader = new RAR5HeaderReader(stream);

        Assert.Throws<EndOfStreamException>(() => reader.ReadVInt());
    }

    [Fact]
    public void ReadBlock_WithLargeDataSize_ParsesCorrectly()
    {
        // Build a block with DataArea flag and a >4GB data size
        ulong largeDataSize = 5_000_000_000ul; // ~4.66 GB
        ulong flags = (ulong)RAR5HeaderFlags.DataArea;

        using var stream = BuildRAR5Block(
            RAR5BlockType.EndArchive,
            flags,
            dataSize: largeDataSize);

        var reader = new RAR5HeaderReader(stream);
        var block = reader.ReadBlock();

        Assert.NotNull(block);
        Assert.Equal(RAR5BlockType.EndArchive, block!.BlockType);
        Assert.Equal(largeDataSize, block.DataSize);
    }

    [Fact]
    public void ReadBlock_FileHeaderWithLargeUnpackedSize_ParsesCorrectly()
    {
        // Build file-specific content: fileFlags(vint) + unpackedSize(vint) + attributes(vint)
        //   + compressionInfo(vint) + hostOS(vint) + nameLen(vint) + name(bytes)
        ulong largeUnpackedSize = 10_000_000_000ul; // ~9.3 GB
        ulong fileFlags = (ulong)RAR5FileFlags.Crc32Present;
        string fileName = "big.bin";
        byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(fileName);

        var fileContent = new MemoryStream();
        fileContent.Write(EncodeVInt(fileFlags));
        fileContent.Write(EncodeVInt(largeUnpackedSize));
        fileContent.Write(EncodeVInt(0x20)); // attributes (normal file)
        // No mtime (TimePresent not set)
        // CRC32 present: write 4 bytes
        fileContent.Write(BitConverter.GetBytes((uint)0xDEADBEEF));
        fileContent.Write(EncodeVInt(0)); // compressionInfo (stored, method=0)
        fileContent.Write(EncodeVInt(0)); // hostOS (Windows=0)
        fileContent.Write(EncodeVInt((ulong)nameBytes.Length));
        fileContent.Write(nameBytes);

        byte[] additionalContent = fileContent.ToArray();
        ulong flags = 0; // No extra area, no data area (just header)

        using var stream = BuildRAR5Block(
            RAR5BlockType.File,
            flags,
            additionalHeaderContent: additionalContent);

        var reader = new RAR5HeaderReader(stream);
        var block = reader.ReadBlock();

        Assert.NotNull(block);
        Assert.Equal(RAR5BlockType.File, block!.BlockType);
        Assert.NotNull(block.FileInfo);
        Assert.Equal(largeUnpackedSize, block.FileInfo!.UnpackedSize);
        Assert.Equal(fileName, block.FileInfo.FileName);
        Assert.True(block.FileInfo.IsStored);
    }

    [Fact]
    public void Constructor_NullStream_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new RAR5HeaderReader(null!));
    }

    [Fact]
    public void PeekBlockType_ValidBlock_ReturnsCorrectTypeAndRestoresPosition()
    {
        // Build a Main archive block (type = 0x01)
        using var stream = BuildRAR5Block(RAR5BlockType.Main, flags: 0,
            additionalHeaderContent: EncodeVInt(0)); // archive flags = 0

        var reader = new RAR5HeaderReader(stream);
        long posBefore = stream.Position;

        byte? blockType = reader.PeekBlockType();

        Assert.NotNull(blockType);
        Assert.Equal((byte)RAR5BlockType.Main, blockType!.Value);
        Assert.Equal(posBefore, stream.Position);
    }

    [Fact]
    public void PeekBlockType_InsufficientData_ReturnsNull()
    {
        // Only 5 bytes - PeekBlockType needs at least 6 (position + 6 <= length)
        using var stream = new MemoryStream(new byte[5]);
        var reader = new RAR5HeaderReader(stream);

        Assert.Null(reader.PeekBlockType());
    }

    [Fact]
    public void ReadBlock_WithExtraAreaAndDataArea_ParsesBothSizes()
    {
        ulong extraSize = 42ul;
        ulong dataSize = 1024ul;
        ulong flags = (ulong)RAR5HeaderFlags.ExtraArea | (ulong)RAR5HeaderFlags.DataArea;

        using var stream = BuildRAR5Block(
            RAR5BlockType.EndArchive,
            flags,
            extraAreaSize: extraSize,
            dataSize: dataSize);

        var reader = new RAR5HeaderReader(stream);
        var block = reader.ReadBlock();

        Assert.NotNull(block);
        Assert.Equal(extraSize, block!.ExtraAreaSize);
        Assert.Equal(dataSize, block.DataSize);
    }

    [Fact]
    public void ReadBlock_EmptyStream_ReturnsNull()
    {
        using var stream = new MemoryStream();
        var reader = new RAR5HeaderReader(stream);

        var block = reader.ReadBlock();

        Assert.Null(block);
    }

    #endregion
}
