using System.Text;
using Force.Crc32;

namespace ReScene.RAR.Tests;

public class RARHeaderReaderTests
{
    private static readonly byte[] RAR4Marker = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];

    /// <summary>
    /// Builds a minimal RAR4 archive header block with valid CRC.
    /// </summary>
    private static byte[] BuildArchiveHeader(RARArchiveFlags flags = RARArchiveFlags.None)
    {
        ushort headerSize = 13;
        byte[] header = new byte[headerSize];
        header[2] = 0x73; // ArchiveHeader
        BitConverter.GetBytes((ushort)flags).CopyTo(header, 3);
        BitConverter.GetBytes(headerSize).CopyTo(header, 5);

        uint crc32 = Crc32Algorithm.Compute(header, 2, header.Length - 2);
        ushort crc = (ushort)(crc32 & 0xFFFF);
        BitConverter.GetBytes(crc).CopyTo(header, 0);
        return header;
    }

    /// <summary>
    /// Builds a minimal RAR4 file header block with valid CRC.
    /// </summary>
    private static byte[] BuildFileHeader(string fileName, byte hostOS = 2, uint packedSize = 100,
        uint unpackedSize = 100, byte method = 0x33, byte unpVer = 29, uint fileCrc = 0,
        uint fileTimeDOS = 0x5A8E3100, uint fileAttributes = 0x20,
        RARFileFlags extraFlags = RARFileFlags.ExtTime)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(fileName);
        ushort nameSize = (ushort)nameBytes.Length;
        RARFileFlags flags = RARFileFlags.LongBlock | extraFlags;

        int extTimeSize = (extraFlags & RARFileFlags.ExtTime) != 0 ? 2 : 0;
        ushort headerSize = (ushort)(7 + 25 + nameSize + extTimeSize);

        byte[] header = new byte[headerSize];
        header[2] = 0x74;
        BitConverter.GetBytes((ushort)flags).CopyTo(header, 3);
        BitConverter.GetBytes(headerSize).CopyTo(header, 5);
        BitConverter.GetBytes(packedSize).CopyTo(header, 7);
        BitConverter.GetBytes(unpackedSize).CopyTo(header, 11);
        header[15] = hostOS;
        BitConverter.GetBytes(fileCrc).CopyTo(header, 16);
        BitConverter.GetBytes(fileTimeDOS).CopyTo(header, 20);
        header[24] = unpVer;
        header[25] = method;
        BitConverter.GetBytes(nameSize).CopyTo(header, 26);
        BitConverter.GetBytes(fileAttributes).CopyTo(header, 28);
        nameBytes.CopyTo(header, 32);

        if ((extraFlags & RARFileFlags.ExtTime) != 0)
        {
            ushort extFlags = 0x8000; // mtime present, no extra bytes
            BitConverter.GetBytes(extFlags).CopyTo(header, 32 + nameSize);
        }

        uint crc32Full = Crc32Algorithm.Compute(header, 2, header.Length - 2);
        ushort crc = (ushort)(crc32Full & 0xFFFF);
        BitConverter.GetBytes(crc).CopyTo(header, 0);
        return header;
    }

    /// <summary>
    /// Builds a minimal end-of-archive block with valid CRC.
    /// </summary>
    private static byte[] BuildEndArchive()
    {
        byte[] header = new byte[7];
        header[2] = 0x7B;
        BitConverter.GetBytes((ushort)7).CopyTo(header, 5);
        uint crc32 = Crc32Algorithm.Compute(header, 2, header.Length - 2);
        ushort crc = (ushort)(crc32 & 0xFFFF);
        BitConverter.GetBytes(crc).CopyTo(header, 0);
        return header;
    }

    private static MemoryStream BuildStreamWithBlocks(params byte[][] blocks)
    {
        var ms = new MemoryStream();
        foreach (var block in blocks)
        {
            ms.Write(block);
        }
        ms.Position = 0;
        return ms;
    }

    #region ReadBlock Tests

    [Fact]
    public void ReadBlock_ArchiveHeader_ParsesCorrectly()
    {
        using var stream = BuildStreamWithBlocks(BuildArchiveHeader());
        var reader = new RARHeaderReader(stream);

        var block = reader.ReadBlock(parseContents: true);

        Assert.NotNull(block);
        Assert.Equal(RAR4BlockType.ArchiveHeader, block!.BlockType);
        Assert.True(block.CrcValid);
        Assert.NotNull(block.ArchiveHeader);
    }

    [Fact]
    public void ReadBlock_ArchiveHeaderWithFlags_ParsesFlagsCorrectly()
    {
        var flags = RARArchiveFlags.Volume | RARArchiveFlags.Solid | RARArchiveFlags.Protected;
        using var stream = BuildStreamWithBlocks(BuildArchiveHeader(flags));
        var reader = new RARHeaderReader(stream);

        var block = reader.ReadBlock(parseContents: true);

        Assert.NotNull(block?.ArchiveHeader);
        Assert.True(block!.ArchiveHeader!.IsVolume);
        Assert.True(block.ArchiveHeader.IsSolid);
        Assert.True(block.ArchiveHeader.HasRecoveryRecord);
    }

    [Fact]
    public void ReadBlock_FileHeader_ParsesFields()
    {
        using var stream = BuildStreamWithBlocks(BuildFileHeader("testfile.txt", hostOS: 3, method: 0x35, unpVer: 29));
        var reader = new RARHeaderReader(stream);

        var block = reader.ReadBlock(parseContents: true);

        Assert.NotNull(block?.FileHeader);
        Assert.Equal(3, block!.FileHeader!.HostOS);     // Unix
        Assert.Equal(5, block.FileHeader.CompressionMethod); // Best (0x35 - 0x30)
        Assert.Equal(29, block.FileHeader.UnpackVersion);
        Assert.Equal("testfile.txt", block.FileHeader.FileName);
        Assert.True(block.CrcValid);
    }

    [Fact]
    public void ReadBlock_FileHeader_ParsesDOSTimestamp()
    {
        // DOS time 0x5A8E3100 encodes a specific date/time
        uint dosTime = 0x5A8E3100;
        using var stream = BuildStreamWithBlocks(BuildFileHeader("file.txt", fileTimeDOS: dosTime));
        var reader = new RARHeaderReader(stream);

        var block = reader.ReadBlock(parseContents: true);

        Assert.NotNull(block?.FileHeader);
        Assert.Equal(dosTime, block!.FileHeader!.FileTimeDOS);
        Assert.NotNull(block.FileHeader.ModifiedTime);
    }

    [Fact]
    public void ReadBlock_FileHeader_ZeroDOSTime_NullModifiedTime()
    {
        using var stream = BuildStreamWithBlocks(BuildFileHeader("file.txt", fileTimeDOS: 0));
        var reader = new RARHeaderReader(stream);

        var block = reader.ReadBlock(parseContents: true);

        Assert.NotNull(block?.FileHeader);
        Assert.Null(block!.FileHeader!.ModifiedTime);
    }

    [Fact]
    public void ReadBlock_FileHeader_DictionarySize()
    {
        // Default flags include no explicit dictionary, so 64KB
        using var stream = BuildStreamWithBlocks(BuildFileHeader("file.txt", extraFlags: RARFileFlags.ExtTime));
        var reader = new RARHeaderReader(stream);

        var block = reader.ReadBlock(parseContents: true);

        Assert.NotNull(block?.FileHeader);
        Assert.Equal(64, block!.FileHeader!.DictionarySizeKB);
    }

    [Fact]
    public void ReadBlock_ParseContentsDisabled_NoArchiveHeader()
    {
        using var stream = BuildStreamWithBlocks(BuildArchiveHeader());
        var reader = new RARHeaderReader(stream);

        var block = reader.ReadBlock(parseContents: false);

        Assert.NotNull(block);
        Assert.Equal(RAR4BlockType.ArchiveHeader, block!.BlockType);
        Assert.Null(block.ArchiveHeader);
    }

    [Fact]
    public void ReadBlock_EmptyStream_ReturnsNull()
    {
        using var stream = new MemoryStream();
        var reader = new RARHeaderReader(stream);

        var block = reader.ReadBlock();

        Assert.Null(block);
    }

    [Fact]
    public void ReadBlock_TruncatedHeader_ReturnsNull()
    {
        // Only 5 bytes, need at least 7
        using var stream = new MemoryStream([0x00, 0x00, 0x73, 0x00, 0x00]);
        var reader = new RARHeaderReader(stream);

        var block = reader.ReadBlock();

        Assert.Null(block);
    }

    [Fact]
    public void ReadBlock_InvalidCrc_DetectedAsInvalid()
    {
        byte[] header = BuildArchiveHeader();
        // Corrupt the CRC
        header[0] = 0xFF;
        header[1] = 0xFF;

        using var stream = BuildStreamWithBlocks(header);
        var reader = new RARHeaderReader(stream);

        var block = reader.ReadBlock(parseContents: true);

        Assert.NotNull(block);
        Assert.False(block!.CrcValid);
    }

    #endregion

    #region PeekBlockType Tests

    [Fact]
    public void PeekBlockType_ReturnsTypeWithoutAdvancing()
    {
        using var stream = BuildStreamWithBlocks(BuildArchiveHeader());
        var reader = new RARHeaderReader(stream);

        long posBefore = stream.Position;
        byte? type = reader.PeekBlockType();
        long posAfter = stream.Position;

        Assert.Equal((byte)0x73, type); // ArchiveHeader
        Assert.Equal(posBefore, posAfter);
    }

    [Fact]
    public void PeekBlockType_EmptyStream_ReturnsNull()
    {
        using var stream = new MemoryStream();
        var reader = new RARHeaderReader(stream);

        Assert.Null(reader.PeekBlockType());
    }

    [Fact]
    public void PeekBlockType_TooFewBytes_ReturnsNull()
    {
        using var stream = new MemoryStream([0x00, 0x00]); // Only 2 bytes, need 3
        var reader = new RARHeaderReader(stream);

        Assert.Null(reader.PeekBlockType());
    }

    #endregion

    #region CanReadBaseHeader Tests

    [Fact]
    public void CanReadBaseHeader_SufficientBytes_ReturnsTrue()
    {
        using var stream = BuildStreamWithBlocks(BuildArchiveHeader());
        var reader = new RARHeaderReader(stream);

        Assert.True(reader.CanReadBaseHeader);
    }

    [Fact]
    public void CanReadBaseHeader_InsufficientBytes_ReturnsFalse()
    {
        using var stream = new MemoryStream([0x00, 0x00, 0x00]);
        var reader = new RARHeaderReader(stream);

        Assert.False(reader.CanReadBaseHeader);
    }

    #endregion

    #region SkipBlock Tests

    [Fact]
    public void SkipBlock_AdvancesToNextBlock()
    {
        byte[] archHeader = BuildArchiveHeader();
        byte[] fileHeader = BuildFileHeader("test.txt");

        using var stream = BuildStreamWithBlocks(archHeader, fileHeader);
        var reader = new RARHeaderReader(stream);

        var firstBlock = reader.ReadBlock(parseContents: false);
        Assert.NotNull(firstBlock);
        reader.SkipBlock(firstBlock!, includeData: false);

        var secondBlock = reader.ReadBlock(parseContents: true);
        Assert.NotNull(secondBlock);
        Assert.Equal(RAR4BlockType.FileHeader, secondBlock!.BlockType);
    }

    #endregion

    #region Multiple Blocks Tests

    [Fact]
    public void ReadBlock_MultipleBlocks_ReadsInSequence()
    {
        byte[] archHeader = BuildArchiveHeader(RARArchiveFlags.FirstVolume);
        byte[] fileHeader = BuildFileHeader("data.bin");
        byte[] endBlock = BuildEndArchive();

        using var stream = BuildStreamWithBlocks(archHeader, fileHeader, endBlock);
        var reader = new RARHeaderReader(stream);

        var block1 = reader.ReadBlock(parseContents: true);
        Assert.NotNull(block1);
        Assert.Equal(RAR4BlockType.ArchiveHeader, block1!.BlockType);
        reader.SkipBlock(block1, includeData: false);

        var block2 = reader.ReadBlock(parseContents: true);
        Assert.NotNull(block2);
        Assert.Equal(RAR4BlockType.FileHeader, block2!.BlockType);
        Assert.Equal("data.bin", block2.FileHeader?.FileName);
        reader.SkipBlock(block2, includeData: false);

        var block3 = reader.ReadBlock(parseContents: true);
        Assert.NotNull(block3);
        Assert.Equal(RAR4BlockType.EndArchive, block3!.BlockType);
    }

    #endregion

    #region Constructor Tests

    [Fact]
    public void Constructor_NullStream_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new RARHeaderReader((Stream)null!));
    }

    [Fact]
    public void Constructor_NullBinaryReader_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new RARHeaderReader((BinaryReader)null!));
    }

    [Fact]
    public void Constructor_WithBinaryReader_Works()
    {
        using var stream = BuildStreamWithBlocks(BuildArchiveHeader());
        using var br = new BinaryReader(stream);
        var reader = new RARHeaderReader(br);

        var block = reader.ReadBlock();
        Assert.NotNull(block);
        Assert.Equal(RAR4BlockType.ArchiveHeader, block!.BlockType);
    }

    #endregion

    #region Service Block (CMT) Tests

    [Fact]
    public void ReadBlock_CmtServiceBlock_ParsesSubType()
    {
        // Build a CMT service block manually
        byte[] subTypeName = "CMT"u8.ToArray();
        byte[] commentData = "Hello"u8.ToArray();
        uint addSize = (uint)commentData.Length;

        ushort headerSize = (ushort)(7 + 25 + subTypeName.Length);
        byte[] header = new byte[headerSize];
        header[2] = 0x7A; // Service block
        ushort flags = (ushort)(RARFileFlags.LongBlock | RARFileFlags.SkipIfUnknown);
        BitConverter.GetBytes(flags).CopyTo(header, 3);
        BitConverter.GetBytes(headerSize).CopyTo(header, 5);
        BitConverter.GetBytes(addSize).CopyTo(header, 7);
        BitConverter.GetBytes((uint)commentData.Length).CopyTo(header, 11);
        header[15] = 2; // Windows
        header[24] = 29;
        header[25] = 0x30; // Store
        BitConverter.GetBytes((ushort)3).CopyTo(header, 26);
        BitConverter.GetBytes(0x00000020u).CopyTo(header, 28);
        subTypeName.CopyTo(header, 32);

        uint crc32 = Crc32Algorithm.Compute(header, 2, header.Length - 2);
        ushort crc = (ushort)(crc32 & 0xFFFF);
        BitConverter.GetBytes(crc).CopyTo(header, 0);

        using var stream = new MemoryStream();
        stream.Write(header);
        stream.Write(commentData);
        stream.Position = 0;

        var reader = new RARHeaderReader(stream);
        var block = reader.ReadBlock(parseContents: true);

        Assert.NotNull(block?.ServiceBlockInfo);
        Assert.Equal("CMT", block!.ServiceBlockInfo!.SubType);
        Assert.Equal(2, block.ServiceBlockInfo.HostOS);
        Assert.True(block.ServiceBlockInfo.IsStored);
        Assert.Equal((ulong)commentData.Length, block.ServiceBlockInfo.PackedSize);
    }

    [Fact]
    public void ReadServiceBlockData_CmtBlock_ReturnsData()
    {
        byte[] subTypeName = "CMT"u8.ToArray();
        byte[] commentData = "Test comment data"u8.ToArray();
        uint addSize = (uint)commentData.Length;

        ushort headerSize = (ushort)(7 + 25 + subTypeName.Length);
        byte[] header = new byte[headerSize];
        header[2] = 0x7A;
        ushort flags = (ushort)(RARFileFlags.LongBlock | RARFileFlags.SkipIfUnknown);
        BitConverter.GetBytes(flags).CopyTo(header, 3);
        BitConverter.GetBytes(headerSize).CopyTo(header, 5);
        BitConverter.GetBytes(addSize).CopyTo(header, 7);
        BitConverter.GetBytes((uint)commentData.Length).CopyTo(header, 11);
        header[15] = 2;
        header[24] = 29;
        header[25] = 0x30;
        BitConverter.GetBytes((ushort)3).CopyTo(header, 26);
        BitConverter.GetBytes(0x00000020u).CopyTo(header, 28);
        subTypeName.CopyTo(header, 32);

        uint crc32 = Crc32Algorithm.Compute(header, 2, header.Length - 2);
        ushort crc = (ushort)(crc32 & 0xFFFF);
        BitConverter.GetBytes(crc).CopyTo(header, 0);

        using var stream = new MemoryStream();
        stream.Write(header);
        stream.Write(commentData);
        stream.Position = 0;

        var reader = new RARHeaderReader(stream);
        var block = reader.ReadBlock(parseContents: true);

        byte[]? data = reader.ReadServiceBlockData(block!);
        Assert.NotNull(data);
        Assert.Equal(commentData, data);
    }

    [Fact]
    public void ReadServiceBlockData_NonServiceBlock_ReturnsNull()
    {
        using var stream = BuildStreamWithBlocks(BuildArchiveHeader());
        var reader = new RARHeaderReader(stream);

        var block = reader.ReadBlock(parseContents: true);
        byte[]? data = reader.ReadServiceBlockData(block!);

        Assert.Null(data);
    }

    #endregion

    #region Large File Header Tests

    /// <summary>
    /// Builds a RAR4 file header with the LARGE flag set, adding HIGH_PACK_SIZE and HIGH_UNP_SIZE fields.
    /// </summary>
    private static byte[] BuildLargeFileHeader(string fileName, uint packSizeLow, uint packSizeHigh,
        uint unpSizeLow, uint unpSizeHigh, byte hostOS = 2, byte method = 0x30, byte unpVer = 29,
        uint fileCrc = 0, uint fileTimeDOS = 0x5A8E3100, uint fileAttributes = 0x20)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(fileName);
        ushort nameSize = (ushort)nameBytes.Length;
        RARFileFlags flags = RARFileFlags.LongBlock | RARFileFlags.Large;

        // Header: base(7) + fields(25) + HIGH_PACK(4) + HIGH_UNP(4) + name
        ushort headerSize = (ushort)(7 + 25 + 8 + nameSize);

        byte[] header = new byte[headerSize];
        header[2] = 0x74; // FileHeader
        BitConverter.GetBytes((ushort)flags).CopyTo(header, 3);
        BitConverter.GetBytes(headerSize).CopyTo(header, 5);
        BitConverter.GetBytes(packSizeLow).CopyTo(header, 7);   // ADD_SIZE / PACK_SIZE low
        BitConverter.GetBytes(unpSizeLow).CopyTo(header, 11);   // UNP_SIZE low
        header[15] = hostOS;
        BitConverter.GetBytes(fileCrc).CopyTo(header, 16);
        BitConverter.GetBytes(fileTimeDOS).CopyTo(header, 20);
        header[24] = unpVer;
        header[25] = method;
        BitConverter.GetBytes(nameSize).CopyTo(header, 26);
        BitConverter.GetBytes(fileAttributes).CopyTo(header, 28);

        // HIGH_PACK_SIZE and HIGH_UNP_SIZE at offset 32
        BitConverter.GetBytes(packSizeHigh).CopyTo(header, 32);
        BitConverter.GetBytes(unpSizeHigh).CopyTo(header, 36);

        // File name after the high size fields
        nameBytes.CopyTo(header, 40);

        uint crc32Full = Crc32Algorithm.Compute(header, 2, header.Length - 2);
        ushort crc = (ushort)(crc32Full & 0xFFFF);
        BitConverter.GetBytes(crc).CopyTo(header, 0);
        return header;
    }

    [Fact]
    public void ReadBlock_LargeFileHeader_CombinesPackedSizeCorrectly()
    {
        // Pack size: high=0x00000001, low=0x00000100 → 0x0000000100000100 = 4294967552
        using var stream = BuildStreamWithBlocks(
            BuildLargeFileHeader("bigfile.bin", packSizeLow: 0x00000100, packSizeHigh: 0x00000001,
                unpSizeLow: 100, unpSizeHigh: 0));
        var reader = new RARHeaderReader(stream);

        var block = reader.ReadBlock(parseContents: true);

        Assert.NotNull(block?.FileHeader);
        Assert.Equal(0x0000000100000100UL, block!.FileHeader!.PackedSize);
    }

    [Fact]
    public void ReadBlock_LargeFileHeader_CombinesUnpackedSizeCorrectly()
    {
        // Unpack size: high=0x00000002, low=0x80000000 → 0x0000000280000000 = 10737418240
        using var stream = BuildStreamWithBlocks(
            BuildLargeFileHeader("bigfile.bin", packSizeLow: 100, packSizeHigh: 0,
                unpSizeLow: 0x80000000, unpSizeHigh: 0x00000002));
        var reader = new RARHeaderReader(stream);

        var block = reader.ReadBlock(parseContents: true);

        Assert.NotNull(block?.FileHeader);
        Assert.Equal(0x0000000280000000UL, block!.FileHeader!.UnpackedSize);
    }

    [Fact]
    public void ReadBlock_LargeFileHeader_HasLargeSizeIsTrue()
    {
        using var stream = BuildStreamWithBlocks(
            BuildLargeFileHeader("bigfile.bin", packSizeLow: 0, packSizeHigh: 1,
                unpSizeLow: 0, unpSizeHigh: 1));
        var reader = new RARHeaderReader(stream);

        var block = reader.ReadBlock(parseContents: true);

        Assert.NotNull(block?.FileHeader);
        Assert.True(block!.FileHeader!.HasLargeSize);
    }

    [Fact]
    public void ReadBlock_LargeFileHeader_HighSizeFieldsStored()
    {
        using var stream = BuildStreamWithBlocks(
            BuildLargeFileHeader("bigfile.bin", packSizeLow: 500, packSizeHigh: 3,
                unpSizeLow: 600, unpSizeHigh: 7));
        var reader = new RARHeaderReader(stream);

        var block = reader.ReadBlock(parseContents: true);

        Assert.NotNull(block?.FileHeader);
        Assert.Equal(3u, block!.FileHeader!.HighPackSize);
        Assert.Equal(7u, block.FileHeader.HighUnpSize);
    }

    [Fact]
    public void ReadBlock_LargeFileHeader_BothSizesMaxLow32()
    {
        // Both low 32 bits are max, high are non-zero
        using var stream = BuildStreamWithBlocks(
            BuildLargeFileHeader("file.dat", packSizeLow: 0xFFFFFFFF, packSizeHigh: 0x00000001,
                unpSizeLow: 0xFFFFFFFF, unpSizeHigh: 0x00000003));
        var reader = new RARHeaderReader(stream);

        var block = reader.ReadBlock(parseContents: true);

        Assert.NotNull(block?.FileHeader);
        ulong expectedPack = 0xFFFFFFFF | (1UL << 32);   // 0x00000001FFFFFFFF
        ulong expectedUnp = 0xFFFFFFFF | (3UL << 32);    // 0x00000003FFFFFFFF
        Assert.Equal(expectedPack, block!.FileHeader!.PackedSize);
        Assert.Equal(expectedUnp, block.FileHeader.UnpackedSize);
    }

    [Fact]
    public void ReadBlock_LargeFileHeader_FileNameParsedCorrectly()
    {
        using var stream = BuildStreamWithBlocks(
            BuildLargeFileHeader("largefile.rar", packSizeLow: 0, packSizeHigh: 1,
                unpSizeLow: 0, unpSizeHigh: 0));
        var reader = new RARHeaderReader(stream);

        var block = reader.ReadBlock(parseContents: true);

        Assert.NotNull(block?.FileHeader);
        Assert.Equal("largefile.rar", block!.FileHeader!.FileName);
    }

    #endregion

    #region SkipBlock File Header Tests

    [Fact]
    public void SkipBlock_FileHeader_SkipsHeaderOnly()
    {
        // File headers with includeData=false should only skip header, not data
        byte[] archHeader = BuildArchiveHeader();
        byte[] fileHeader = BuildFileHeader("test.txt", packedSize: 500);
        byte[] endBlock = BuildEndArchive();

        using var stream = BuildStreamWithBlocks(archHeader, fileHeader, endBlock);
        var reader = new RARHeaderReader(stream);

        // Read and skip archive header
        var block1 = reader.ReadBlock(parseContents: false);
        Assert.NotNull(block1);
        reader.SkipBlock(block1!, includeData: false);

        // Read file header
        var block2 = reader.ReadBlock(parseContents: true);
        Assert.NotNull(block2);
        Assert.Equal(RAR4BlockType.FileHeader, block2!.BlockType);

        // Skip file header - since includeData=false and this is a file header,
        // it skips just the header, not the ADD_SIZE data
        reader.SkipBlock(block2, includeData: false);

        // Stream position should be right after the file header bytes
        long expectedPos = archHeader.Length + fileHeader.Length;
        Assert.Equal(expectedPos, stream.Position);
    }

    [Fact]
    public void SkipBlock_FileHeader_IncludeDataTrue_StillSkipsHeaderOnly()
    {
        // For file headers, SkipBlock with includeData=true still skips only header
        // because file data is not present in SRR files
        byte[] fileHeader = BuildFileHeader("test.txt", packedSize: 1000);
        byte[] endBlock = BuildEndArchive();

        using var stream = BuildStreamWithBlocks(fileHeader, endBlock);
        var reader = new RARHeaderReader(stream);

        var block = reader.ReadBlock(parseContents: true);
        Assert.NotNull(block);
        Assert.Equal(RAR4BlockType.FileHeader, block!.BlockType);

        // For FileHeader type, includeData is ignored - SkipBlock never adds AddSize
        reader.SkipBlock(block, includeData: true);

        long expectedPos = fileHeader.Length;
        Assert.Equal(expectedPos, stream.Position);
    }

    #endregion

    #region Service Block SubType Tests

    /// <summary>
    /// Builds a service block (0x7A) with the given sub-type name and optional data.
    /// </summary>
    private static (byte[] header, byte[] data) BuildServiceBlock(string subType, byte[] serviceData,
        byte method = 0x30, byte hostOS = 2, byte unpVer = 29,
        uint fileTimeDOS = 0, uint fileAttributes = 0x20)
    {
        byte[] subTypeName = Encoding.ASCII.GetBytes(subType);
        uint addSize = (uint)serviceData.Length;

        ushort headerSize = (ushort)(7 + 25 + subTypeName.Length);
        byte[] header = new byte[headerSize];
        header[2] = 0x7A; // Service block
        ushort flags = (ushort)(RARFileFlags.LongBlock | RARFileFlags.SkipIfUnknown);
        BitConverter.GetBytes(flags).CopyTo(header, 3);
        BitConverter.GetBytes(headerSize).CopyTo(header, 5);
        BitConverter.GetBytes(addSize).CopyTo(header, 7);              // ADD_SIZE = PACK_SIZE
        BitConverter.GetBytes((uint)serviceData.Length).CopyTo(header, 11); // UNP_SIZE
        header[15] = hostOS;
        // DATA_CRC at offset 16 (leave zero)
        BitConverter.GetBytes(fileTimeDOS).CopyTo(header, 20);
        header[24] = unpVer;
        header[25] = method;
        BitConverter.GetBytes((ushort)subTypeName.Length).CopyTo(header, 26);
        BitConverter.GetBytes(fileAttributes).CopyTo(header, 28);
        subTypeName.CopyTo(header, 32);

        uint crc32 = Crc32Algorithm.Compute(header, 2, header.Length - 2);
        ushort crc = (ushort)(crc32 & 0xFFFF);
        BitConverter.GetBytes(crc).CopyTo(header, 0);

        return (header, serviceData);
    }

    [Fact]
    public void ReadBlock_RRServiceBlock_ParsesSubType()
    {
        byte[] rrData = new byte[100]; // Dummy recovery record data
        var (header, data) = BuildServiceBlock("RR", rrData);

        using var stream = new MemoryStream();
        stream.Write(header);
        stream.Write(data);
        stream.Position = 0;

        var reader = new RARHeaderReader(stream);
        var block = reader.ReadBlock(parseContents: true);

        Assert.NotNull(block?.ServiceBlockInfo);
        Assert.Equal("RR", block!.ServiceBlockInfo!.SubType);
        Assert.Equal((ulong)rrData.Length, block.ServiceBlockInfo.PackedSize);
    }

    [Fact]
    public void ReadBlock_AVServiceBlock_ParsesSubType()
    {
        byte[] avData = [0x01, 0x02, 0x03, 0x04];
        var (header, data) = BuildServiceBlock("AV", avData);

        using var stream = new MemoryStream();
        stream.Write(header);
        stream.Write(data);
        stream.Position = 0;

        var reader = new RARHeaderReader(stream);
        var block = reader.ReadBlock(parseContents: true);

        Assert.NotNull(block?.ServiceBlockInfo);
        Assert.Equal("AV", block!.ServiceBlockInfo!.SubType);
    }

    [Fact]
    public void ReadBlock_RRServiceBlock_ReadsDataCorrectly()
    {
        byte[] rrData = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE];
        var (header, data) = BuildServiceBlock("RR", rrData);

        using var stream = new MemoryStream();
        stream.Write(header);
        stream.Write(data);
        stream.Position = 0;

        var reader = new RARHeaderReader(stream);
        var block = reader.ReadBlock(parseContents: true);

        byte[]? readData = reader.ReadServiceBlockData(block!);
        Assert.NotNull(readData);
        Assert.Equal(rrData, readData);
    }

    [Fact]
    public void ReadBlock_ServiceBlock_CompressionMethodParsed()
    {
        byte[] dummyData = [0x01];
        var (header, data) = BuildServiceBlock("CMT", dummyData, method: 0x33);

        using var stream = new MemoryStream();
        stream.Write(header);
        stream.Write(data);
        stream.Position = 0;

        var reader = new RARHeaderReader(stream);
        var block = reader.ReadBlock(parseContents: true);

        Assert.NotNull(block?.ServiceBlockInfo);
        Assert.Equal(0x33, block!.ServiceBlockInfo!.CompressionMethod);
        Assert.False(block.ServiceBlockInfo.IsStored);
    }

    [Fact]
    public void ReadBlock_ServiceBlock_StoredMethodIsStored()
    {
        byte[] dummyData = [0x01];
        var (header, data) = BuildServiceBlock("RR", dummyData, method: 0x30);

        using var stream = new MemoryStream();
        stream.Write(header);
        stream.Write(data);
        stream.Position = 0;

        var reader = new RARHeaderReader(stream);
        var block = reader.ReadBlock(parseContents: true);

        Assert.NotNull(block?.ServiceBlockInfo);
        Assert.True(block!.ServiceBlockInfo!.IsStored);
    }

    [Fact]
    public void ReadBlock_ServiceBlock_HostOSParsed()
    {
        byte[] dummyData = [0x01];
        var (header, data) = BuildServiceBlock("RR", dummyData, hostOS: 3); // Unix

        using var stream = new MemoryStream();
        stream.Write(header);
        stream.Write(data);
        stream.Position = 0;

        var reader = new RARHeaderReader(stream);
        var block = reader.ReadBlock(parseContents: true);

        Assert.NotNull(block?.ServiceBlockInfo);
        Assert.Equal(3, block!.ServiceBlockInfo!.HostOS);
    }

    #endregion

    #region Truncated Header Tests

    [Fact]
    public void ReadBlock_TruncatedAtFourBytes_ReturnsNull()
    {
        // 4 bytes is not enough for even the 7-byte base header
        using var stream = new MemoryStream([0x00, 0x00, 0x74, 0x00]);
        var reader = new RARHeaderReader(stream);

        var block = reader.ReadBlock();
        Assert.Null(block);
    }

    [Fact]
    public void ReadBlock_TruncatedAtSixBytes_ReturnsNull()
    {
        // 6 bytes - one short of 7-byte base header
        using var stream = new MemoryStream([0x00, 0x00, 0x74, 0x00, 0x00, 0x00]);
        var reader = new RARHeaderReader(stream);

        var block = reader.ReadBlock();
        Assert.Null(block);
    }

    [Fact]
    public void ReadBlock_HeaderSizeLargerThanStream_ReturnsNull()
    {
        // 7-byte header that claims to be 100 bytes, but stream is only 7 bytes
        byte[] header = [0x00, 0x00, 0x73, 0x00, 0x00, 0x64, 0x00]; // headerSize=100
        using var stream = new MemoryStream(header);
        var reader = new RARHeaderReader(stream);

        var block = reader.ReadBlock();
        Assert.Null(block);
    }

    [Fact]
    public void ReadBlock_HeaderSizeTooSmall_ReturnsNull()
    {
        // headerSize < 7 should be rejected
        byte[] header = [0x00, 0x00, 0x73, 0x00, 0x00, 0x05, 0x00]; // headerSize=5
        using var stream = new MemoryStream(header);
        var reader = new RARHeaderReader(stream);

        var block = reader.ReadBlock();
        Assert.Null(block);
    }

    #endregion

    #region CRC Mismatch Tests

    [Fact]
    public void ReadBlock_FileHeaderCrcMismatch_IsValidCrcFalse()
    {
        byte[] header = BuildFileHeader("test.txt");
        // Corrupt the CRC bytes
        header[0] = 0xAA;
        header[1] = 0xBB;

        using var stream = BuildStreamWithBlocks(header);
        var reader = new RARHeaderReader(stream);

        var block = reader.ReadBlock(parseContents: true);

        Assert.NotNull(block);
        Assert.False(block!.CrcValid);
        // File header should still be parsed even with invalid CRC
        Assert.NotNull(block.FileHeader);
        Assert.Equal("test.txt", block.FileHeader!.FileName);
    }

    [Fact]
    public void ReadBlock_ServiceBlockCrcMismatch_IsValidCrcFalse()
    {
        byte[] dummyData = [0x01, 0x02, 0x03];
        var (header, data) = BuildServiceBlock("CMT", dummyData);
        // Corrupt the CRC
        header[0] = 0x12;
        header[1] = 0x34;

        using var stream = new MemoryStream();
        stream.Write(header);
        stream.Write(data);
        stream.Position = 0;

        var reader = new RARHeaderReader(stream);
        var block = reader.ReadBlock(parseContents: true);

        Assert.NotNull(block);
        Assert.False(block!.CrcValid);
    }

    [Fact]
    public void ReadBlock_EndArchiveCrcMismatch_IsValidCrcFalse()
    {
        byte[] header = BuildEndArchive();
        header[0] ^= 0xFF; // Flip CRC bits

        using var stream = BuildStreamWithBlocks(header);
        var reader = new RARHeaderReader(stream);

        var block = reader.ReadBlock(parseContents: true);

        Assert.NotNull(block);
        Assert.Equal(RAR4BlockType.EndArchive, block!.BlockType);
        Assert.False(block.CrcValid);
    }

    #endregion
}
