using ReScene.RAR.Decompression;

namespace ReScene.Tests;

public class DecompressionTests
{
    [Fact]
    public void DecompressComment_StoreMethod_ReturnsOriginalData()
    {
        // Arrange
        byte[] data = "Test comment."u8.ToArray();

        // Act
        string? result = RARDecompressor.DecompressComment(data, data.Length, 0x30);

        // Assert
        Assert.Equal("Test comment.", result);
    }

    [Fact]
    public void DecompressComment_CompressedMethod33_DecompressesCorrectly()
    {
        // Arrange - compressed comment data from store_utf8_comment.srr
        // method: 0x33 (Normal), pack_size: 24, unp_size: 13
        // Expected result: "Test comment."
        byte[] compressedData = Convert.FromHexString("0c0ccbecc92a2084d08325f307067fc1fff51ce2f5231cfa");
        int uncompressedSize = 13;

        // Act
        string? result = RARDecompressor.DecompressComment(compressedData, uncompressedSize, 0x33);

        // Assert - the native RAR 2.9 LZSS decompressor must reproduce the original
        // comment text exactly. (Previously this assertion was guarded by a
        // null-check that turned the test tautological; the decompressor is now
        // verified to work, so the round-trip is asserted unconditionally.)
        Assert.Equal("Test comment.", result);
    }

    [Fact]
    public void Unpack29_Decompress_DoesNotThrow()
    {
        // Arrange - test that the Unpack29 class doesn't throw on invalid data
        var unpacker = new Unpack29();
        byte[] invalidData = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05];

        // Act & Assert - should not throw
        Exception exception = Record.Exception(() => unpacker.Decompress(invalidData, 100));
        Assert.Null(exception);
    }

    [Fact]
    public void Unpack50_Decompress_DoesNotThrow()
    {
        // Arrange - test that the Unpack50 class doesn't throw on invalid data
        var unpacker = new Unpack50();
        byte[] invalidData = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05];

        // Act & Assert - should not throw
        Exception exception = Record.Exception(() => unpacker.Decompress(invalidData, 100));
        Assert.Null(exception);
    }

    [Fact]
    public void Decompress_StoreMethod_ReturnsOriginalData()
    {
        // Arrange
        byte[] data = [0x48, 0x65, 0x6c, 0x6c, 0x6f]; // "Hello"

        // Act
        byte[]? result = RARDecompressor.Decompress(data, 5, RARMethod.Store);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(data, result);
    }

    [Fact]
    public void Decompress_NullData_ReturnsNull()
    {
        // Act
        byte[]? result = RARDecompressor.Decompress(null!, 10, RARMethod.Normal);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void Decompress_EmptyData_ReturnsNull()
    {
        // Act
        byte[]? result = RARDecompressor.Decompress([], 10, RARMethod.Normal);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void Decompress_ZeroUncompressedSize_ReturnsNull()
    {
        // Arrange
        byte[] data = [0x01, 0x02, 0x03];

        // Act
        byte[]? result = RARDecompressor.Decompress(data, 0, RARMethod.Normal);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void SetBuffer_PayloadLargerThan32KB_IsNotTruncated()
    {
        // Regression: BitInput used a fixed 32 KB buffer and SetBuffer capped the
        // copy at MaxSize, silently truncating any compressed payload larger than
        // 32 KB. RARArchive.TryReadAllBytes feeds the entire packed file body
        // through this path, so a >32 KB compressed member decoded from a buffer
        // that returned zeros past 0x8000 produced wrong output. Bytes past the
        // old cap must remain readable.
        var input = new BitInput();
        byte[] data = new byte[40000];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)((i * 31 + 7) & 0xFF);
        }

        input.SetBuffer(data);

        // The buffer must grow to hold the whole payload (was fixed at MaxSize).
        Assert.True(input.InBuf.Length >= data.Length);

        // Position the bit cursor well past the old 32 KB cap and read 16 bits.
        // GetBits returns the top byte first: (data[n] << 8) | data[n+1].
        input.InAddr = 35000;
        input.InBit = 0;
        uint expected = (uint)((data[35000] << 8) | data[35001]);
        Assert.Equal(expected, input.GetBits()); // returned 0 (truncated) before the fix
    }
}
