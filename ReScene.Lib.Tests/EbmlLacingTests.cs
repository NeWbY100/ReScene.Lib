namespace SRR.Tests;

/// <summary>
/// Tests for EBML lacing parsing, EBML VINT decoding, and header stripping detection.
/// All test data is synthetically constructed from the Matroska specification.
/// </summary>
public class EbmlLacingTests
{
    #region EbmlVInt.ReadUnsigned

    [Theory]
    [InlineData(new byte[] { 0x81 }, 1, 1)]       // 1-byte VINT: value = 1
    [InlineData(new byte[] { 0x82 }, 2, 1)]       // 1-byte VINT: value = 2
    [InlineData(new byte[] { 0xBF }, 63, 1)]      // 1-byte VINT: value = 63 (0x3F)
    [InlineData(new byte[] { 0x40, 0x80 }, 128, 2)] // 2-byte VINT: value = 128
    [InlineData(new byte[] { 0x40, 0x01 }, 1, 2)]   // 2-byte VINT: value = 1
    [InlineData(new byte[] { 0x41, 0x00 }, 256, 2)]  // 2-byte VINT: value = 256
    public void ReadUnsigned_ReturnsCorrectValue(byte[] data, long expectedValue, int expectedLength)
    {
        var (value, length) = EbmlVInt.ReadUnsigned(data);

        Assert.Equal(expectedValue, value);
        Assert.Equal(expectedLength, length);
    }

    [Fact]
    public void ReadUnsigned_EmptyData_ReturnsZero()
    {
        var (value, length) = EbmlVInt.ReadUnsigned(ReadOnlySpan<byte>.Empty);

        Assert.Equal(0, value);
        Assert.Equal(0, length);
    }

    [Fact]
    public void ReadUnsigned_ThreeByteVint()
    {
        // 3-byte VINT: 0x20 marker, value = 0x010000 = 65536
        byte[] data = [0x21, 0x00, 0x00];
        var (value, length) = EbmlVInt.ReadUnsigned(data);

        Assert.Equal(65536, value);
        Assert.Equal(3, length);
    }

    [Fact]
    public void ReadUnsigned_FourByteVint()
    {
        // 4-byte VINT: 0x10 marker, value = 0x01000000 = 16777216
        byte[] data = [0x11, 0x00, 0x00, 0x00];
        var (value, length) = EbmlVInt.ReadUnsigned(data);

        Assert.Equal(16777216, value);
        Assert.Equal(4, length);
    }

    #endregion

    #region EbmlVInt.ReadSigned

    [Theory]
    [InlineData(new byte[] { 0xBF }, 0, 1)]       // 1-byte signed: value 63 - bias 63 = 0
    [InlineData(new byte[] { 0xC0 }, 1, 1)]       // 1-byte signed: value 64 - bias 63 = 1
    [InlineData(new byte[] { 0xBE }, -1, 1)]      // 1-byte signed: value 62 - bias 63 = -1
    [InlineData(new byte[] { 0x80 }, -63, 1)]     // 1-byte signed: value 0 - bias 63 = -63
    [InlineData(new byte[] { 0xFE }, 63, 1)]      // 1-byte signed: value 126 - bias 63 = 63
    public void ReadSigned_OneByteVint(byte[] data, long expectedValue, int expectedLength)
    {
        var (value, length) = EbmlVInt.ReadSigned(data);

        Assert.Equal(expectedValue, value);
        Assert.Equal(expectedLength, length);
    }

    [Theory]
    [InlineData(new byte[] { 0x5F, 0xFF }, 0, 2)]     // 2-byte signed: value 8191 - bias 8191 = 0
    [InlineData(new byte[] { 0x60, 0x00 }, 1, 2)]     // 2-byte signed: value 8192 - bias 8191 = 1
    [InlineData(new byte[] { 0x5F, 0xFE }, -1, 2)]    // 2-byte signed: value 8190 - bias 8191 = -1
    public void ReadSigned_TwoByteVint(byte[] data, long expectedValue, int expectedLength)
    {
        var (value, length) = EbmlVInt.ReadSigned(data);

        Assert.Equal(expectedValue, value);
        Assert.Equal(expectedLength, length);
    }

    [Fact]
    public void ReadSigned_EmptyData_ReturnsZero()
    {
        var (value, length) = EbmlVInt.ReadSigned(ReadOnlySpan<byte>.Empty);

        Assert.Equal(0, value);
        Assert.Equal(0, length);
    }

    [Fact]
    public void ReadSigned_ThreeByteVint_Zero()
    {
        // 3-byte signed: bias = 2^20 - 1 = 1048575
        // Unsigned value of 1048575 = 0x0FFFFF -> 0x2F 0xFF 0xFF (with 0x20 marker)
        byte[] data = [0x2F, 0xFF, 0xFF];
        var (value, length) = EbmlVInt.ReadSigned(data);

        Assert.Equal(0, value);
        Assert.Equal(3, length);
    }

    #endregion

    #region No Lacing

    [Fact]
    public void GetFrameLengths_NoLacing_SingleFrame()
    {
        // No lacing: the entire data block is one frame
        byte[] data = [];
        var (frameSizes, bytesConsumed) = EbmlLacing.GetFrameLengths(data, EbmlLaceType.None, 1000);

        Assert.Single(frameSizes);
        Assert.Equal(1000, frameSizes[0]);
        Assert.Equal(0, bytesConsumed);
    }

    [Fact]
    public void GetFrameLengths_NoLacing_ZeroLength()
    {
        byte[] data = [];
        var (frameSizes, bytesConsumed) = EbmlLacing.GetFrameLengths(data, EbmlLaceType.None, 0);

        Assert.Single(frameSizes);
        Assert.Equal(0, frameSizes[0]);
        Assert.Equal(0, bytesConsumed);
    }

    #endregion

    #region Fixed-Size Lacing

    [Fact]
    public void GetFrameLengths_FixedLacing_EqualFrames()
    {
        // Fixed lacing: first byte = frameCount - 1 = 3 (4 frames)
        // Total data = 1000, frame count = 4, each frame = 250
        // But totalDataLength does NOT include the lacing header itself in the data passed
        // totalDataLength = total block data after base header = 1 (frame count byte) + 4*250 = 1001
        // Actually totalDataLength is the data length BEFORE lacing header is parsed
        // The lacing parsing sees totalDataLength and subtracts the lacing header bytes consumed
        byte[] data = [3]; // 3+1 = 4 frames
        var (frameSizes, bytesConsumed) = EbmlLacing.GetFrameLengths(data, EbmlLaceType.Fixed, 1001);

        Assert.Equal(4, frameSizes.Length);
        // Fixed size: totalDataLength / frameCount = 1001 / 4 = 250 (integer division)
        Assert.Equal(250, frameSizes[0]);
        Assert.Equal(250, frameSizes[1]);
        Assert.Equal(250, frameSizes[2]);
        Assert.Equal(250, frameSizes[3]);
        Assert.Equal(1, bytesConsumed);
    }

    [Fact]
    public void GetFrameLengths_FixedLacing_TwoFrames()
    {
        byte[] data = [1]; // 1+1 = 2 frames
        var (frameSizes, bytesConsumed) = EbmlLacing.GetFrameLengths(data, EbmlLaceType.Fixed, 801);

        Assert.Equal(2, frameSizes.Length);
        Assert.Equal(400, frameSizes[0]); // 801 / 2 = 400
        Assert.Equal(400, frameSizes[1]);
        Assert.Equal(1, bytesConsumed);
    }

    [Fact]
    public void GetFrameLengths_FixedLacing_SingleFrame()
    {
        byte[] data = [0]; // 0+1 = 1 frame
        var (frameSizes, bytesConsumed) = EbmlLacing.GetFrameLengths(data, EbmlLaceType.Fixed, 501);

        Assert.Single(frameSizes);
        Assert.Equal(501, frameSizes[0]); // 501 / 1 = 501
        Assert.Equal(1, bytesConsumed);
    }

    #endregion

    #region Xiph Lacing

    [Fact]
    public void GetFrameLengths_XiphLacing_SmallFrames()
    {
        // Xiph lacing: first byte = 2 (3 frames)
        // Frame 0 size: 100 (single byte < 255)
        // Frame 1 size: 200 (single byte < 255)
        // Frame 2 size: remaining
        // Total data = 1 + 1 + 1 + 100 + 200 + 300 = 603
        byte[] data = [2, 100, 200]; // 3 frames, sizes 100, 200
        int totalDataLength = 3 + 100 + 200 + 300; // = 603
        var (frameSizes, bytesConsumed) = EbmlLacing.GetFrameLengths(data, EbmlLaceType.Xiph, totalDataLength);

        Assert.Equal(3, frameSizes.Length);
        Assert.Equal(100, frameSizes[0]);
        Assert.Equal(200, frameSizes[1]);
        Assert.Equal(300, frameSizes[2]); // remaining = 603 - 3 - 100 - 200 = 300
        Assert.Equal(3, bytesConsumed);
    }

    [Fact]
    public void GetFrameLengths_XiphLacing_LargeFrame()
    {
        // Xiph lacing with a frame > 255 bytes
        // First byte = 1 (2 frames)
        // Frame 0 size: 255 + 100 = 355 (one 0xFF byte + 100)
        byte[] data = [1, 0xFF, 100];
        int totalDataLength = 3 + 355 + 500; // = 858
        var (frameSizes, bytesConsumed) = EbmlLacing.GetFrameLengths(data, EbmlLaceType.Xiph, totalDataLength);

        Assert.Equal(2, frameSizes.Length);
        Assert.Equal(355, frameSizes[0]); // 255 + 100
        Assert.Equal(500, frameSizes[1]); // remaining = 858 - 3 - 355
        Assert.Equal(3, bytesConsumed);
    }

    [Fact]
    public void GetFrameLengths_XiphLacing_ExactMultipleOf255()
    {
        // Frame size exactly 255: 0xFF followed by 0x00
        byte[] data = [1, 0xFF, 0x00];
        int totalDataLength = 3 + 255 + 200; // = 458
        var (frameSizes, bytesConsumed) = EbmlLacing.GetFrameLengths(data, EbmlLaceType.Xiph, totalDataLength);

        Assert.Equal(2, frameSizes.Length);
        Assert.Equal(255, frameSizes[0]); // 255 + 0
        Assert.Equal(200, frameSizes[1]); // remaining
        Assert.Equal(3, bytesConsumed);
    }

    [Fact]
    public void GetFrameLengths_XiphLacing_VeryLargeFrame()
    {
        // Frame size 600: 0xFF + 0xFF + 90 = 255 + 255 + 90 = 600
        byte[] data = [1, 0xFF, 0xFF, 90];
        int totalDataLength = 4 + 600 + 100; // = 704
        var (frameSizes, bytesConsumed) = EbmlLacing.GetFrameLengths(data, EbmlLaceType.Xiph, totalDataLength);

        Assert.Equal(2, frameSizes.Length);
        Assert.Equal(600, frameSizes[0]);
        Assert.Equal(100, frameSizes[1]); // remaining = 704 - 4 - 600
        Assert.Equal(4, bytesConsumed);
    }

    #endregion

    #region EBML Lacing

    [Fact]
    public void GetFrameLengths_EbmlLacing_TwoFramesSameSize()
    {
        // EBML lacing: first byte = 1 (2 frames)
        // Frame 0: unsigned VINT = 400 -> 0x82 prefix? No. Let's calculate.
        // 1-byte VINT max = 126 (0x7E). Need 2-byte VINT for 400.
        // 2-byte VINT: 0x40 | (400 >> 8) = 0x41, 400 & 0xFF = 0x90
        // Frame 1: last frame, remaining bytes
        byte[] data = [1, 0x41, 0x90]; // frameCount-1=1, first frame=400 (2-byte VINT)
        // totalDataLength = lacing header (3 bytes) + frame0 (400) + frame1 (remaining)
        int totalDataLength = 3 + 400 + 400;
        var (frameSizes, bytesConsumed) = EbmlLacing.GetFrameLengths(data, EbmlLaceType.Ebml, totalDataLength);

        Assert.Equal(2, frameSizes.Length);
        Assert.Equal(400, frameSizes[0]);
        Assert.Equal(400, frameSizes[1]); // remaining = 803 - 3 - 400
        Assert.Equal(3, bytesConsumed);
    }

    [Fact]
    public void GetFrameLengths_EbmlLacing_ThreeFramesDelta()
    {
        // EBML lacing: first byte = 2 (3 frames)
        // Frame 0: unsigned VINT = 100 -> 1-byte: 0x80 | 100 = 0xE4
        // Frame 1: signed delta = 0 -> 1-byte signed: unsigned 63 = 0xBF (bias=63, 63-63=0)
        // Frame 2: last frame, remaining
        byte[] data = [2, 0xE4, 0xBF]; // 3 frames, first=100, delta=0
        int totalDataLength = 3 + 100 + 100 + 50; // = 253
        var (frameSizes, bytesConsumed) = EbmlLacing.GetFrameLengths(data, EbmlLaceType.Ebml, totalDataLength);

        Assert.Equal(3, frameSizes.Length);
        Assert.Equal(100, frameSizes[0]);
        Assert.Equal(100, frameSizes[1]); // 100 + 0
        Assert.Equal(50, frameSizes[2]);  // remaining = 253 - 3 - 100 - 100
        Assert.Equal(3, bytesConsumed);
    }

    [Fact]
    public void GetFrameLengths_EbmlLacing_ThreeFramesPositiveDelta()
    {
        // EBML lacing: first byte = 2 (3 frames)
        // Frame 0: unsigned VINT = 100 -> 1-byte: 0x80 | 100 = 0xE4
        // Frame 1: signed delta = +10 -> 1-byte signed: unsigned 73 = 0x80 | 73 = 0xC9 (bias=63, 73-63=10)
        // Frame 2: last frame, remaining
        byte[] data = [2, 0xE4, 0xC9]; // 3 frames, first=100, delta=+10
        int totalDataLength = 3 + 100 + 110 + 200; // = 413
        var (frameSizes, bytesConsumed) = EbmlLacing.GetFrameLengths(data, EbmlLaceType.Ebml, totalDataLength);

        Assert.Equal(3, frameSizes.Length);
        Assert.Equal(100, frameSizes[0]);
        Assert.Equal(110, frameSizes[1]); // 100 + 10
        Assert.Equal(200, frameSizes[2]); // remaining = 413 - 3 - 100 - 110
        Assert.Equal(3, bytesConsumed);
    }

    [Fact]
    public void GetFrameLengths_EbmlLacing_ThreeFramesNegativeDelta()
    {
        // EBML lacing: first byte = 2 (3 frames)
        // Frame 0: unsigned VINT = 200 -> 2-byte: 0x40 | (200 >> 8) = 0x40, 200 & 0xFF = 0xC8
        // Frame 1: signed delta = -50 -> 1-byte signed: unsigned 13 = 0x80 | 13 = 0x8D (bias=63, 13-63=-50)
        // Frame 2: last frame, remaining
        byte[] data = [2, 0x40, 0xC8, 0x8D]; // 3 frames, first=200, delta=-50
        int totalDataLength = 4 + 200 + 150 + 100; // = 454
        var (frameSizes, bytesConsumed) = EbmlLacing.GetFrameLengths(data, EbmlLaceType.Ebml, totalDataLength);

        Assert.Equal(3, frameSizes.Length);
        Assert.Equal(200, frameSizes[0]);
        Assert.Equal(150, frameSizes[1]); // 200 + (-50)
        Assert.Equal(100, frameSizes[2]); // remaining = 454 - 4 - 200 - 150
        Assert.Equal(4, bytesConsumed);
    }

    [Fact]
    public void GetFrameLengths_EbmlLacing_FourFramesMultipleDeltas()
    {
        // EBML lacing: 4 frames
        // Frame 0: 100 -> 1-byte VINT: 0x80 | 100 = 0xE4
        // Frame 1: delta = +20 -> unsigned 83 = 0x80 | 83 = 0xD3 (bias=63, 83-63=20)
        // Frame 2: delta = -10 -> unsigned 53 = 0x80 | 53 = 0xB5 (bias=63, 53-63=-10)
        // Frame 3: last frame = remaining
        byte[] data = [3, 0xE4, 0xD3, 0xB5]; // 4 frames
        int totalDataLength = 4 + 100 + 120 + 110 + 66; // = 400
        var (frameSizes, bytesConsumed) = EbmlLacing.GetFrameLengths(data, EbmlLaceType.Ebml, totalDataLength);

        Assert.Equal(4, frameSizes.Length);
        Assert.Equal(100, frameSizes[0]);
        Assert.Equal(120, frameSizes[1]); // 100 + 20
        Assert.Equal(110, frameSizes[2]); // 120 + (-10)
        Assert.Equal(66, frameSizes[3]);  // remaining = 400 - 4 - 100 - 120 - 110
        Assert.Equal(4, bytesConsumed);
    }

    [Fact]
    public void GetFrameLengths_EbmlLacing_SingleFrameInLacing()
    {
        // Edge case: EBML lacing with frame count = 1 (first byte = 0)
        // Per the Matroska spec and pyrescene behavior, the single frame (i=0)
        // reads its size as an unsigned VINT, even though it's also the last frame.
        // Here frame size VINT = 0x82 (1-byte VINT, value=2), and remaining = actual frame data.
        // In practice, no encoder uses EBML lacing with a single frame.
        byte[] data = [0, 0x82]; // 0+1 = 1 frame, VINT size = 2
        int totalDataLength = 2 + 2; // 2 bytes header + 2 bytes frame data = 4
        var (frameSizes, bytesConsumed) = EbmlLacing.GetFrameLengths(data, EbmlLaceType.Ebml, totalDataLength);

        Assert.Single(frameSizes);
        Assert.Equal(2, frameSizes[0]); // VINT value = 2
        Assert.Equal(2, bytesConsumed); // 1 (frame count) + 1 (VINT)
    }

    [Fact]
    public void GetFrameLengths_EbmlLacing_TwoByteSignedDelta()
    {
        // Test with 2-byte signed VINT delta
        // Frame 0: 1000 -> 2-byte VINT: 0x43, 0xE8 (0x4000 | 1000 = 0x43E8)
        // Frame 1: delta = +500 -> 2-byte signed: unsigned = 8691 = bias(8191)+500
        //   0x4000 | 8691 = 0x4000 | 0x21F3 = 0x61F3 -> bytes 0x61, 0xF3
        // Frame 2: last frame = remaining
        byte[] data = [2, 0x43, 0xE8, 0x61, 0xF3];
        int totalDataLength = 5 + 1000 + 1500 + 500; // = 3005
        var (frameSizes, bytesConsumed) = EbmlLacing.GetFrameLengths(data, EbmlLaceType.Ebml, totalDataLength);

        Assert.Equal(3, frameSizes.Length);
        Assert.Equal(1000, frameSizes[0]);
        Assert.Equal(1500, frameSizes[1]); // 1000 + 500
        Assert.Equal(500, frameSizes[2]);  // remaining = 3005 - 5 - 1000 - 1500
        Assert.Equal(5, bytesConsumed);
    }

    #endregion

    #region EbmlHeaderStripping

    [Fact]
    public void DetectStrippedHeader_NoCompression_ReturnsNull()
    {
        // TrackEntry with just a TrackNumber element (0xD7), no ContentEncodings
        byte[] trackEntryData = BuildEbmlElement(0xD7, [0x01]); // TrackNumber = 1
        var result = EbmlHeaderStripping.DetectStrippedHeader(trackEntryData);

        Assert.Null(result);
    }

    [Fact]
    public void DetectStrippedHeader_HeaderStripping_ReturnsSettings()
    {
        // Build a TrackEntry with ContentEncodings containing header stripping
        byte[] strippedHeader = [0x01, 0x00, 0x00, 0x00]; // 4 bytes of stripped header

        // Build from innermost out:
        // ContentCompAlgo = 3 (header stripping)
        byte[] compAlgo = BuildEbmlElement(0x4254, [0x03]);
        // ContentCompSettings = strippedHeader
        byte[] compSettings = BuildEbmlElement(0x4255, strippedHeader);
        // ContentCompression containing algo + settings
        byte[] compression = BuildEbmlElement(0x5034, [.. compAlgo, .. compSettings]);
        // ContentEncoding containing compression
        byte[] encoding = BuildEbmlElement(0x6240, compression);
        // ContentEncodings containing encoding
        byte[] encodings = BuildEbmlElement(0x6D80, encoding);

        var result = EbmlHeaderStripping.DetectStrippedHeader(encodings);

        Assert.NotNull(result);
        Assert.Equal(strippedHeader, result);
    }

    [Fact]
    public void DetectStrippedHeader_ZlibCompression_ReturnsNull()
    {
        // ContentCompAlgo = 0 (zlib), not header stripping
        byte[] compAlgo = BuildEbmlElement(0x4254, [0x00]);
        byte[] compSettings = BuildEbmlElement(0x4255, [0x01, 0x02, 0x03]);
        byte[] compression = BuildEbmlElement(0x5034, [.. compAlgo, .. compSettings]);
        byte[] encoding = BuildEbmlElement(0x6240, compression);
        byte[] encodings = BuildEbmlElement(0x6D80, encoding);

        var result = EbmlHeaderStripping.DetectStrippedHeader(encodings);

        Assert.Null(result);
    }

    [Fact]
    public void DetectStrippedHeader_HeaderStripping_WithPrecedingElements()
    {
        // TrackEntry with TrackNumber before ContentEncodings
        byte[] strippedHeader = [0xAA, 0xBB];

        byte[] trackNumber = BuildEbmlElement(0xD7, [0x01]);
        byte[] codecId = BuildEbmlElement(0x86, "V_MPEG4/ISO/AVC"u8.ToArray());
        byte[] compAlgo = BuildEbmlElement(0x4254, [0x03]);
        byte[] compSettings = BuildEbmlElement(0x4255, strippedHeader);
        byte[] compression = BuildEbmlElement(0x5034, [.. compAlgo, .. compSettings]);
        byte[] encoding = BuildEbmlElement(0x6240, compression);
        byte[] encodings = BuildEbmlElement(0x6D80, encoding);

        byte[] fullData = [.. trackNumber, .. codecId, .. encodings];
        var result = EbmlHeaderStripping.DetectStrippedHeader(fullData);

        Assert.NotNull(result);
        Assert.Equal(strippedHeader, result);
    }

    [Fact]
    public void RestoreFrame_PrependsHeader()
    {
        byte[] header = [0x01, 0x00, 0x00, 0x00];
        byte[] frame = [0xAA, 0xBB, 0xCC];

        byte[] restored = EbmlHeaderStripping.RestoreFrame(header, frame);

        Assert.Equal(7, restored.Length);
        Assert.Equal(new byte[] { 0x01, 0x00, 0x00, 0x00, 0xAA, 0xBB, 0xCC }, restored);
    }

    [Fact]
    public void RestoreFrame_EmptyFrame()
    {
        byte[] header = [0x01, 0x02];
        byte[] frame = [];

        byte[] restored = EbmlHeaderStripping.RestoreFrame(header, frame);

        Assert.Equal(header, restored);
    }

    [Fact]
    public void RestoreFrame_EmptyHeader()
    {
        byte[] header = [];
        byte[] frame = [0xAA, 0xBB];

        byte[] restored = EbmlHeaderStripping.RestoreFrame(header, frame);

        Assert.Equal(frame, restored);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void GetFrameLengths_XiphLacing_ZeroLengthFirstFrame()
    {
        // Xiph lacing where first frame is 0 bytes
        byte[] data = [1, 0x00]; // 2 frames, first frame = 0 bytes
        int totalDataLength = 2 + 0 + 500; // = 502
        var (frameSizes, bytesConsumed) = EbmlLacing.GetFrameLengths(data, EbmlLaceType.Xiph, totalDataLength);

        Assert.Equal(2, frameSizes.Length);
        Assert.Equal(0, frameSizes[0]);
        Assert.Equal(500, frameSizes[1]); // remaining = 502 - 2 - 0
        Assert.Equal(2, bytesConsumed);
    }

    [Fact]
    public void GetFrameLengths_FixedLacing_SingleByteFrames()
    {
        // Fixed lacing with 5 frames of 1 byte each
        byte[] data = [4]; // 5 frames
        int totalDataLength = 1 + 5; // 6
        var (frameSizes, bytesConsumed) = EbmlLacing.GetFrameLengths(data, EbmlLaceType.Fixed, totalDataLength);

        Assert.Equal(5, frameSizes.Length);
        // 6 / 5 = 1 (integer division)
        foreach (var size in frameSizes)
            Assert.Equal(1, size);
        Assert.Equal(1, bytesConsumed);
    }

    [Fact]
    public void GetFrameLengths_EmptyDataForNonNoneLacing_ReturnsDefaultFrame()
    {
        // Edge case: empty data with non-None lacing
        byte[] data = [];
        var (frameSizes, bytesConsumed) = EbmlLacing.GetFrameLengths(data, EbmlLaceType.Xiph, 0);

        // Should return totalDataLength as single frame when data is too short
        Assert.Single(frameSizes);
        Assert.Equal(0, frameSizes[0]);
        Assert.Equal(0, bytesConsumed);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Builds a raw EBML element (ID + size VINT + data).
    /// </summary>
    private static byte[] BuildEbmlElement(ulong id, byte[] data)
    {
        byte[] idBytes = EncodeEbmlId(id);
        byte[] sizeBytes = EncodeEbmlSize(data.Length);
        byte[] result = new byte[idBytes.Length + sizeBytes.Length + data.Length];
        idBytes.CopyTo(result, 0);
        sizeBytes.CopyTo(result, idBytes.Length);
        data.CopyTo(result, idBytes.Length + sizeBytes.Length);
        return result;
    }

    private static byte[] EncodeEbmlId(ulong id)
    {
        if (id < 0x100) return [(byte)id];
        if (id < 0x10000) return [(byte)(id >> 8), (byte)(id & 0xFF)];
        if (id < 0x1000000) return [(byte)(id >> 16), (byte)((id >> 8) & 0xFF), (byte)(id & 0xFF)];
        return [(byte)(id >> 24), (byte)((id >> 16) & 0xFF), (byte)((id >> 8) & 0xFF), (byte)(id & 0xFF)];
    }

    private static byte[] EncodeEbmlSize(long value)
    {
        if (value < 0x7F) return [(byte)(0x80 | value)];
        if (value < 0x3FFF) return [(byte)(0x40 | (value >> 8)), (byte)(value & 0xFF)];
        if (value < 0x1FFFFF) return [(byte)(0x20 | (value >> 16)), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF)];
        return [(byte)(0x10 | (value >> 24)), (byte)((value >> 16) & 0xFF), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF)];
    }

    #endregion
}
