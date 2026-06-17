namespace ReScene.Tests;

/// <summary>
/// Unit tests for <see cref="StreamUtilities"/> — the byte-accurate stream I/O primitives
/// (<see cref="StreamUtilities.CopyBytes(Stream, Stream, long)"/>,
/// <see cref="StreamUtilities.CopyBytesStrict"/>, <see cref="StreamUtilities.ReadExactly"/>,
/// <see cref="StreamUtilities.ReadAtMost"/>, <see cref="StreamUtilities.ReadFully"/>,
/// <see cref="StreamUtilities.SkipBytes"/>, <see cref="StreamUtilities.TryDeleteFile"/>) used on the
/// SRR/SRS rebuild hot path. The focus is on partial-read handling, which is exercised with a nested
/// "drip" stream that hands back at most one byte per <c>Read</c> call.
/// </summary>
public class StreamUtilitiesTests : TempDirTestBase
{
    #region Test doubles

    /// <summary>
    /// A read-only stream over a byte array that returns at most one byte per <see cref="Read"/> call,
    /// modelling pipe/network streams that satisfy reads partially. This forces the loop-based
    /// helpers to iterate rather than completing in a single read.
    /// </summary>
    private sealed class DripStream : Stream
    {
        private readonly byte[] _data;
        private int _position;

        public DripStream(byte[] data) => _data = data;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count <= 0 || _position >= _data.Length)
            {
                return 0;
            }

            // Deliberately hand back a single byte so multi-read code paths are tested.
            buffer[offset] = _data[_position];
            _position++;
            return 1;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static byte[] Bytes(int count)
    {
        byte[] data = new byte[count];
        for (int i = 0; i < count; i++)
        {
            data[i] = (byte)(i + 1);
        }

        return data;
    }

    #endregion

    #region ReadExactly

    [Fact]
    public void ReadExactly_ShortStream_ThrowsEndOfStream()
    {
        // Only 3 bytes available but 8 requested -> must throw rather than return a short array.
        using var ms = new MemoryStream([1, 2, 3]);
        using var reader = new BinaryReader(ms);

        var ex = Assert.Throws<EndOfStreamException>(() => StreamUtilities.ReadExactly(reader, 8));
        Assert.Contains("Expected 8 bytes", ex.Message);
        Assert.Contains("got 3", ex.Message);
    }

    [Fact]
    public void ReadExactly_SufficientStream_ReturnsRequestedBytes()
    {
        // Exactly enough bytes: returns the requested count and its content, in order.
        using var ms = new MemoryStream([10, 20, 30, 40, 50]);
        using var reader = new BinaryReader(ms);

        byte[] result = StreamUtilities.ReadExactly(reader, 4);

        Assert.Equal([10, 20, 30, 40], result);
    }

    #endregion

    #region ReadAtMost

    [Fact]
    public void ReadAtMost_StreamShorterThanCount_ReturnsTruncatedArray()
    {
        // Drip stream holds 5 bytes, 10 requested: result is right-sized to the 5 actually read.
        using var drip = new DripStream(Bytes(5));

        byte[] result = StreamUtilities.ReadAtMost(drip, 10);

        Assert.Equal(5, result.Length);
        Assert.Equal([1, 2, 3, 4, 5], result);
    }

    [Fact]
    public void ReadAtMost_DripStreamWithEnoughBytes_ReturnsExactCount()
    {
        // One-byte-per-read source must still be fully consumed up to the requested count.
        using var drip = new DripStream(Bytes(6));

        byte[] result = StreamUtilities.ReadAtMost(drip, 4);

        Assert.Equal([1, 2, 3, 4], result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void ReadAtMost_NonPositiveCount_ReturnsEmpty(int count)
    {
        // count <= 0 short-circuits to an empty array without touching the stream.
        using var ms = new MemoryStream([1, 2, 3]);

        byte[] result = StreamUtilities.ReadAtMost(ms, count);

        Assert.Empty(result);
        Assert.Equal(0, ms.Position); // stream untouched
    }

    #endregion

    #region ReadFully

    [Fact]
    public void ReadFully_DripStream_ReturnsFullRequestedCount()
    {
        // Across a one-byte-at-a-time stream, ReadFully must loop until all 7 bytes land in the buffer.
        using var drip = new DripStream(Bytes(7));
        byte[] buffer = new byte[7];

        int read = StreamUtilities.ReadFully(drip, buffer, 0, 7);

        Assert.Equal(7, read);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], buffer);
    }

    [Fact]
    public void ReadFully_EarlyEof_ReturnsPartialCount()
    {
        // Source has only 3 bytes but 6 are asked for: returns 3 and fills only that prefix of the buffer.
        using var drip = new DripStream(Bytes(3));
        byte[] buffer = new byte[6];

        int read = StreamUtilities.ReadFully(drip, buffer, 0, 6);

        Assert.Equal(3, read);
        Assert.Equal([1, 2, 3, 0, 0, 0], buffer);
    }

    [Fact]
    public void ReadFully_HonoursOffset()
    {
        // The offset argument places read bytes mid-buffer, leaving the prefix untouched.
        using var ms = new MemoryStream([9, 8, 7]);
        byte[] buffer = new byte[5]; // all zero

        int read = StreamUtilities.ReadFully(ms, buffer, 2, 3);

        Assert.Equal(3, read);
        Assert.Equal([0, 0, 9, 8, 7], buffer);
    }

    #endregion

    #region CopyBytes (lenient)

    [Fact]
    public void CopyBytes_SourceShorterThanCount_CopiesAvailableWithoutThrowing()
    {
        // Source holds 4 bytes but 10 requested: the lenient overload stops silently at EOF.
        using var source = new MemoryStream([1, 2, 3, 4]);
        using var dest = new MemoryStream();

        StreamUtilities.CopyBytes(source, dest, 10L);

        Assert.Equal([1, 2, 3, 4], dest.ToArray());
    }

    [Fact]
    public void CopyBytes_DripSource_CopiesExactCount()
    {
        // One byte per read still produces a byte-accurate copy of the requested length.
        using var drip = new DripStream(Bytes(8));
        using var dest = new MemoryStream();

        StreamUtilities.CopyBytes(drip, dest, 5L);

        Assert.Equal([1, 2, 3, 4, 5], dest.ToArray());
    }

    [Fact]
    public void CopyBytes_ZeroCount_CopiesNothing()
    {
        // count 0 must write nothing and must not throw even though the source has data.
        using var source = new MemoryStream([1, 2, 3]);
        using var dest = new MemoryStream();

        StreamUtilities.CopyBytes(source, dest, 0L);

        Assert.Empty(dest.ToArray());
    }

    [Fact]
    public void CopyBytes_UlongOverload_DelegatesToLong()
    {
        // The ulong convenience overload copies the same bytes as the long path.
        using var source = new MemoryStream([7, 7, 7, 7]);
        using var dest = new MemoryStream();

        StreamUtilities.CopyBytes(source, dest, 3UL);

        Assert.Equal([7, 7, 7], dest.ToArray());
    }

    #endregion

    #region CopyBytesStrict

    [Fact]
    public void CopyBytesStrict_SourceShort_ThrowsDefaultMessage()
    {
        // 4 available, 10 demanded: the strict overload must throw rather than silently truncate.
        using var source = new MemoryStream([1, 2, 3, 4]);
        using var dest = new MemoryStream();

        var ex = Assert.Throws<EndOfStreamException>(
            () => StreamUtilities.CopyBytesStrict(source, dest, 10L));
        Assert.Equal("Unexpected end of stream while copying data.", ex.Message);
    }

    [Fact]
    public void CopyBytesStrict_SourceShort_ThrowsCustomMessage()
    {
        // The optional errorMessage is surfaced verbatim when EOF is hit early.
        using var source = new MemoryStream([1, 2]);
        using var dest = new MemoryStream();

        var ex = Assert.Throws<EndOfStreamException>(
            () => StreamUtilities.CopyBytesStrict(source, dest, 5L, "boom while rebuilding"));
        Assert.Equal("boom while rebuilding", ex.Message);
    }

    [Fact]
    public void CopyBytesStrict_ExactSource_CopiesAllBytes()
    {
        // When the source has exactly the requested bytes (delivered one at a time), it copies fully.
        using var drip = new DripStream(Bytes(6));
        using var dest = new MemoryStream();

        StreamUtilities.CopyBytesStrict(drip, dest, 6L);

        Assert.Equal([1, 2, 3, 4, 5, 6], dest.ToArray());
    }

    [Fact]
    public void CopyBytesStrict_ZeroCount_CopiesNothingWithoutThrowing()
    {
        // count 0 never enters the loop, so no EOF check fires even with an empty source.
        using var source = new MemoryStream([]);
        using var dest = new MemoryStream();

        StreamUtilities.CopyBytesStrict(source, dest, 0L);

        Assert.Empty(dest.ToArray());
    }

    #endregion

    #region SkipBytes

    [Fact]
    public void SkipBytes_AdvancesStreamPositionForward()
    {
        // SkipBytes seeks forward from the current position; subsequent reads start past the skip.
        using var ms = new MemoryStream([1, 2, 3, 4, 5, 6]);
        ms.Position = 1; // start mid-stream to prove it is relative (SeekOrigin.Current)

        StreamUtilities.SkipBytes(ms, 2UL);

        Assert.Equal(3, ms.Position);
        Assert.Equal(4, ms.ReadByte());
    }

    #endregion

    #region TryDeleteFile

    [Fact]
    public void TryDeleteFile_ExistingFile_IsRemoved()
    {
        // Best-effort delete actually removes a real file on disk.
        string path = Path.Combine(TempDir, "to_delete.bin");
        File.WriteAllBytes(path, [1, 2, 3]);
        Assert.True(File.Exists(path));

        StreamUtilities.TryDeleteFile(path);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TryDeleteFile_MissingFile_DoesNotThrow()
    {
        // A non-existent path is a no-op rather than an error (suppressed cleanup).
        string path = Path.Combine(TempDir, "never_existed.bin");

        StreamUtilities.TryDeleteFile(path); // must not throw

        Assert.False(File.Exists(path));
    }

    #endregion
}
