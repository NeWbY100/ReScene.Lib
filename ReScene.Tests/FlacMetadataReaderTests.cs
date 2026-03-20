using ReScene.SRS;

namespace ReScene.Tests;

/// <summary>
/// Tests for FlacMetadataReader: metadata block header reading,
/// ID3v2 wrapper detection, and FindFrameDataStart.
/// Uses synthetic MemoryStream data.
/// </summary>
public class FlacMetadataReaderTests
{
    #region ReadMetadataBlockHeader Tests

    [Fact]
    public void ReadMetadataBlockHeader_StreamInfo_ReturnsCorrectValues()
    {
        // STREAMINFO: type=0, not last, length=34
        byte typeByte = 0x00; // isLast=false, type=0
        byte[] sizeBytes = [0x00, 0x00, 0x22]; // 34

        var ms = new MemoryStream();
        ms.WriteByte(typeByte);
        ms.Write(sizeBytes);
        ms.Position = 0;

        using var reader = new BinaryReader(ms);
        var (isLast, type, length) = FlacMetadataReader.ReadMetadataBlockHeader(reader);

        Assert.False(isLast);
        Assert.Equal(0, type);
        Assert.Equal(34, length);
    }

    [Fact]
    public void ReadMetadataBlockHeader_LastBlock_SetsIsLast()
    {
        // VORBIS_COMMENT: type=4, last=true, length=256
        byte typeByte = 0x84; // isLast=true (0x80) | type=4
        byte[] sizeBytes = [0x00, 0x01, 0x00]; // 256

        var ms = new MemoryStream();
        ms.WriteByte(typeByte);
        ms.Write(sizeBytes);
        ms.Position = 0;

        using var reader = new BinaryReader(ms);
        var (isLast, type, length) = FlacMetadataReader.ReadMetadataBlockHeader(reader);

        Assert.True(isLast);
        Assert.Equal(4, type);
        Assert.Equal(256, length);
    }

    [Theory]
    [InlineData(0x00, false, 0)]  // STREAMINFO
    [InlineData(0x01, false, 1)]  // PADDING
    [InlineData(0x02, false, 2)]  // APPLICATION
    [InlineData(0x03, false, 3)]  // SEEKTABLE
    [InlineData(0x04, false, 4)]  // VORBIS_COMMENT
    [InlineData(0x05, false, 5)]  // CUESHEET
    [InlineData(0x06, false, 6)]  // PICTURE
    [InlineData(0x80, true, 0)]   // STREAMINFO, last
    [InlineData(0x84, true, 4)]   // VORBIS_COMMENT, last
    [InlineData(0x86, true, 6)]   // PICTURE, last
    public void ReadMetadataBlockHeader_TypeAndLastBit(byte typeByte, bool expectedLast, byte expectedType)
    {
        var ms = new MemoryStream();
        ms.WriteByte(typeByte);
        ms.Write([0x00, 0x00, 0x10]); // length = 16
        ms.Position = 0;

        using var reader = new BinaryReader(ms);
        var (isLast, type, length) = FlacMetadataReader.ReadMetadataBlockHeader(reader);

        Assert.Equal(expectedLast, isLast);
        Assert.Equal(expectedType, type);
        Assert.Equal(16, length);
    }

    [Fact]
    public void ReadMetadataBlockHeader_LargeSize_24BitBigEndian()
    {
        // Size = 0x123456 = 1193046
        var ms = new MemoryStream();
        ms.WriteByte(0x01); // PADDING, not last
        ms.Write([0x12, 0x34, 0x56]);
        ms.Position = 0;

        using var reader = new BinaryReader(ms);
        var (_, _, length) = FlacMetadataReader.ReadMetadataBlockHeader(reader);

        Assert.Equal(0x123456, length);
    }

    [Fact]
    public void ReadMetadataBlockHeader_TooShort_Throws()
    {
        var ms = new MemoryStream([0x00, 0x00]); // only 2 bytes
        ms.Position = 0;

        using var reader = new BinaryReader(ms);
        Assert.ThrowsAny<Exception>(() => FlacMetadataReader.ReadMetadataBlockHeader(reader));
    }

    #endregion

    #region DetectId3v2Wrapper Tests

    [Fact]
    public void DetectId3v2Wrapper_NoWrapper_ReturnsFalse()
    {
        // Starts with fLaC directly
        var ms = BuildSimpleFlac(hasId3Wrapper: false);

        var (found, _) = FlacMetadataReader.DetectId3v2Wrapper(ms);
        Assert.False(found);
    }

    [Fact]
    public void DetectId3v2Wrapper_WithWrapper_ReturnsCorrectSize()
    {
        var ms = BuildSimpleFlac(hasId3Wrapper: true, id3BodySize: 128);

        var (found, size) = FlacMetadataReader.DetectId3v2Wrapper(ms);

        Assert.True(found);
        Assert.Equal(138, size); // 10 header + 128 body
    }

    [Fact]
    public void DetectId3v2Wrapper_EmptyStream_ReturnsFalse()
    {
        var ms = new MemoryStream();

        var (found, _) = FlacMetadataReader.DetectId3v2Wrapper(ms);
        Assert.False(found);
    }

    #endregion

    #region FindFrameDataStart Tests

    [Fact]
    public void FindFrameDataStart_SimpleFlac_AfterLastMetadataBlock()
    {
        // Build: fLaC + STREAMINFO(34) as last + frame data
        var ms = new MemoryStream();

        // fLaC marker
        ms.Write("fLaC"u8);

        // STREAMINFO: type=0, last=true, length=34
        ms.WriteByte(0x80); // last=true, type=0
        ms.Write([0x00, 0x00, 0x22]); // length=34
        ms.Write(new byte[34]); // STREAMINFO payload

        long expectedFrameStart = ms.Position; // = 4 + 4 + 34 = 42

        // Frame data
        ms.Write([0xFF, 0xF8]); // FLAC frame sync
        ms.Write(new byte[100]);

        long result = FlacMetadataReader.FindFrameDataStart(ms);
        Assert.Equal(expectedFrameStart, result);
    }

    [Fact]
    public void FindFrameDataStart_MultipleMetadataBlocks()
    {
        var ms = new MemoryStream();

        // fLaC marker
        ms.Write("fLaC"u8);

        // STREAMINFO: not last, length=34
        ms.WriteByte(0x00);
        ms.Write([0x00, 0x00, 0x22]);
        ms.Write(new byte[34]);

        // PADDING: not last, length=100
        ms.WriteByte(0x01);
        ms.Write([0x00, 0x00, 0x64]);
        ms.Write(new byte[100]);

        // VORBIS_COMMENT: last, length=20
        ms.WriteByte(0x84);
        ms.Write([0x00, 0x00, 0x14]);
        ms.Write(new byte[20]);

        long expectedFrameStart = ms.Position;

        // Frame data
        ms.Write(new byte[200]);

        long result = FlacMetadataReader.FindFrameDataStart(ms);
        Assert.Equal(expectedFrameStart, result);
    }

    [Fact]
    public void FindFrameDataStart_WithId3v2Wrapper()
    {
        // ID3v2 wrapper (50 bytes body) + fLaC + STREAMINFO(34)
        var ms = new MemoryStream();

        // ID3v2 wrapper
        ms.Write("ID3"u8);
        ms.Write([0x04, 0x00, 0x00]);
        ms.Write(Mp3TagReader.EncodeSyncSafeInt(50));
        ms.Write(new byte[50]); // body = 60 total

        // fLaC marker
        ms.Write("fLaC"u8);

        // STREAMINFO: last, length=34
        ms.WriteByte(0x80);
        ms.Write([0x00, 0x00, 0x22]);
        ms.Write(new byte[34]);

        long expectedFrameStart = ms.Position; // 60 + 4 + 4 + 34 = 102

        // Frame data
        ms.Write(new byte[100]);

        long result = FlacMetadataReader.FindFrameDataStart(ms);
        Assert.Equal(expectedFrameStart, result);
    }

    [Fact]
    public void FindFrameDataStart_NoFlacMarker_Throws()
    {
        var ms = new MemoryStream([0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);

        Assert.Throws<InvalidDataException>(() => FlacMetadataReader.FindFrameDataStart(ms));
    }

    #endregion

    #region GetBlockTypeName Tests

    [Theory]
    [InlineData(0, "STREAMINFO")]
    [InlineData(1, "PADDING")]
    [InlineData(2, "APPLICATION")]
    [InlineData(3, "SEEKTABLE")]
    [InlineData(4, "VORBIS_COMMENT")]
    [InlineData(5, "CUESHEET")]
    [InlineData(6, "PICTURE")]
    [InlineData(7, "UNKNOWN(7)")]
    [InlineData(127, "UNKNOWN(127)")]
    public void GetBlockTypeName_ReturnsExpected(byte type, string expected)
    {
        string result = FlacMetadataReader.GetBlockTypeName(type);
        Assert.Equal(expected, result);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Builds a simple FLAC stream with optional ID3v2 wrapper.
    /// </summary>
    private static MemoryStream BuildSimpleFlac(bool hasId3Wrapper, int id3BodySize = 64)
    {
        var ms = new MemoryStream();

        if (hasId3Wrapper)
        {
            ms.Write("ID3"u8);
            ms.Write([0x04, 0x00, 0x00]);
            ms.Write(Mp3TagReader.EncodeSyncSafeInt(id3BodySize));
            ms.Write(new byte[id3BodySize]);
        }

        // fLaC marker
        ms.Write("fLaC"u8);

        // STREAMINFO: last, length=34
        ms.WriteByte(0x80);
        ms.Write([0x00, 0x00, 0x22]);
        ms.Write(new byte[34]);

        // Frame data
        ms.Write([0xFF, 0xF8]);
        ms.Write(new byte[100]);

        ms.Position = 0;
        return ms;
    }

    #endregion
}
