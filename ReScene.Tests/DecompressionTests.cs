using ReScene.RAR.Decompression;
using ReScene.RAR.Decompression.PPMd;

namespace ReScene.Tests;

public class DecompressionTests
{
    [Fact]
    public void SubAllocator_InitSubAllocator_PartitionsMemoryToUnrarSizing()
    {
        // Characterization test pinning the PPMd allocator partition math against unrar's
        // suballoc.cpp:
        //   size2 = FIXED_UNIT_SIZE * (SIZE / 8 / FIXED_UNIT_SIZE * 7)
        // The parentheses are load-bearing: integer division truncates, so hoisting the
        // leading FIXED_UNIT_SIZE multiply out of the group changes the result. With saMB=1
        // (_subAllocatorSize = 1 MiB, FIXED_UNIT_SIZE = 12): size2 = 917448, size1 = 131128,
        // hence _fakeUnitsStart lands at 131128. Dropping the parens shifts it to 131072.
        SubAllocator allocator = new();
        Assert.True(allocator.StartSubAllocator(1));

        allocator.InitSubAllocator();

        Assert.Equal(131128, allocator.FakeUnitsStart);
    }

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

    [Theory]
    [InlineData(20)] // tables parse, decode yields a partial "Test  " before starving
    [InlineData(16)] // tables parse, decode yields zero output before starving
    [InlineData(12)] // tables parse, decode yields zero output before starving
    public void DecompressComment_TruncatedStream_FailsCleanReturnsNull(int keepBytes)
    {
        // Regression (review finding #4): the LZ decode loop breaks when the input is exhausted
        // and previously returned the partially-filled (zero-padded) destination buffer as success.
        // A truncated stream that parses its tables but starves mid-decode must fail cleanly (null),
        // not hand back wrong bytes. Full stream = 24 bytes -> "Test comment." (13 bytes).
        //
        // All three prefixes here parse ReadTables30 and reach the new `destPtr < destSize` guard
        // (verified: without the fix they returned non-null partial buffers — "Test  ", "", "" —
        // NOT a null ReadTables failure), so each row genuinely exercises the fixed code path.
        byte[] full = Convert.FromHexString("0c0ccbecc92a2084d08325f307067fc1fff51ce2f5231cfa");
        byte[] truncated = full[..keepBytes];

        string? result = RARDecompressor.DecompressComment(truncated, 13, 0x33);

        Assert.Null(result);
    }

    [Fact]
    public void Decompress_RAR29_PpmFlaggedStream_FailsCleanReturnsNull()
    {
        // STOPGAP (audit #7/#23): PPMd decoding is unsupported — the ModelPPM port is an
        // incomplete stub that desyncs from WinRAR's encoder. A stream that selects PPM mode
        // (high bit 0x8000 of the first table word, i.e. high bit of the first byte set) must
        // fail cleanly with null instead of returning desynchronized garbage as success.
        byte[] ppmFlagged = new byte[64];
        ppmFlagged[0] = 0x80; // sets the 0x8000 PPM flag read by Unpack29.ReadTables30

        byte[]? result = RARDecompressor.Decompress(ppmFlagged, 100, RARMethod.Normal, RARVersion.RAR29);

        Assert.Null(result);
    }

    [Fact]
    public void Decompress_RAR50_MultiBlockStream_FailsCleanReturnsNull()
    {
        // STOPGAP (audit #24): Unpack50 decodes only the first block and never re-reads a
        // block header/tables at block boundaries, so a multi-block RAR5 stream would desync
        // into garbage. When the first block header clears the LastBlockInFile flag (bit 0x40)
        // — meaning more blocks follow — decompression must fail cleanly with null.
        // Block header bytes: blockFlags=0x80 (table present, LastBlockInFile CLEAR, byteCount=1),
        // savedCheckSum=0xDF, blockSize=0x05. checksum = 0x5A^0x80^0x05 = 0xDF matches.
        byte[] multiBlock = new byte[32];
        multiBlock[0] = 0x80;
        multiBlock[1] = 0xDF;
        multiBlock[2] = 0x05;

        byte[]? result = RARDecompressor.Decompress(multiBlock, 100, RARMethod.Normal, RARVersion.RAR50);

        Assert.Null(result);
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
