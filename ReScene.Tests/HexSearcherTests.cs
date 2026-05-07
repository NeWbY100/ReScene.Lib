using ReScene.Hex;

namespace ReScene.Tests;

public class HexSearcherTests
{
    #region In-memory data source

    private sealed class ByteArrayDataSource(byte[] data) : IHexDataSource
    {
        private readonly byte[] _data = data;

        public long Length => _data.Length;

        public int Read(long position, byte[] buffer, int offset, int count)
        {
            if (position < 0 || position >= _data.Length)
            {
                return 0;
            }

            int available = (int)Math.Min(count, _data.Length - position);
            Array.Copy(_data, position, buffer, offset, available);
            return available;
        }

        public void Dispose() { }
    }

    private static IHexDataSource Source(byte[] data) => new ByteArrayDataSource(data);

    private static HexSearchPattern Pattern(params byte[] bytes)
    {
        string hex = Convert.ToHexString(bytes);
        return HexSearchPattern.TryParse(hex, asHex: true)!;
    }

    #endregion

    #region FindForward

    [Fact]
    public void FindForward_NullSource_ReturnsMinusOne()
    {
        long result = HexSearcher.FindForward(null!, Pattern(0x52), 0);
        Assert.Equal(-1, result);
    }

    [Fact]
    public void FindForward_NullPattern_ReturnsMinusOne()
    {
        using IHexDataSource src = Source([0x01, 0x02, 0x03]);
        long result = HexSearcher.FindForward(src, null!, 0);
        Assert.Equal(-1, result);
    }

    [Fact]
    public void FindForward_EmptyPattern_ReturnsMinusOne()
    {
        using IHexDataSource src = Source([0x01, 0x02, 0x03]);
        var pattern = HexSearchPattern.TryParse(string.Empty, asHex: true);
        Assert.Null(pattern);
    }

    [Fact]
    public void FindForward_PatternAtOffsetZero_ReturnsZero()
    {
        using IHexDataSource src = Source([0x52, 0x61, 0x72, 0x21]);
        long result = HexSearcher.FindForward(src, Pattern(0x52, 0x61), 0);
        Assert.Equal(0, result);
    }

    [Fact]
    public void FindForward_PatternAtEnd_ReturnsCorrectOffset()
    {
        using IHexDataSource src = Source([0x00, 0x00, 0x00, 0x52, 0x61]);
        long result = HexSearcher.FindForward(src, Pattern(0x52, 0x61), 0);
        Assert.Equal(3, result);
    }

    [Fact]
    public void FindForward_PatternNotFound_ReturnsMinusOne()
    {
        using IHexDataSource src = Source([0x01, 0x02, 0x03, 0x04]);
        long result = HexSearcher.FindForward(src, Pattern(0xAB, 0xCD), 0);
        Assert.Equal(-1, result);
    }

    [Fact]
    public void FindForward_NegativeStartOffset_ClampsToZeroAndFindsMatch()
    {
        using IHexDataSource src = Source([0x52, 0x61, 0x72, 0x21]);
        long result = HexSearcher.FindForward(src, Pattern(0x52, 0x61), -5);
        Assert.Equal(0, result);
    }

    [Fact]
    public void FindForward_PatternStraddlesChunkBoundary_FindsMatch()
    {
        // 130 KB buffer with needle planted at offset 65530 (crosses 64 KB boundary)
        const int BufferSize = 130 * 1024;
        const int NeedleOffset = 65530;
        byte[] needle = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22, 0x33, 0x44];
        byte[] data = new byte[BufferSize];
        Array.Copy(needle, 0, data, NeedleOffset, needle.Length);

        using IHexDataSource src = Source(data);
        long result = HexSearcher.FindForward(src, Pattern(needle), 0);
        Assert.Equal(NeedleOffset, result);
    }

    #endregion

    #region FindBackward

    [Fact]
    public void FindBackward_NullSource_ReturnsMinusOne()
    {
        long result = HexSearcher.FindBackward(null!, Pattern(0x52), 100);
        Assert.Equal(-1, result);
    }

    [Fact]
    public void FindBackward_NullPattern_ReturnsMinusOne()
    {
        using IHexDataSource src = Source([0x01, 0x02, 0x03]);
        long result = HexSearcher.FindBackward(src, null!, 3);
        Assert.Equal(-1, result);
    }

    [Fact]
    public void FindBackward_EmptySource_ReturnsMinusOne()
    {
        using IHexDataSource src = Source([]);
        long result = HexSearcher.FindBackward(src, Pattern(0x52), 0);
        Assert.Equal(-1, result);
    }

    [Fact]
    public void FindBackward_BeforeOffsetZero_ReturnsMinusOne()
    {
        using IHexDataSource src = Source([0x52, 0x61, 0x72, 0x21]);
        long result = HexSearcher.FindBackward(src, Pattern(0x52), 0);
        Assert.Equal(-1, result);
    }

    [Fact]
    public void FindBackward_FindsLastOccurrenceBeforeOffset()
    {
        // Two matches: at offset 0 and offset 5. Search before offset 7 should find the one at 5.
        using IHexDataSource src = Source([0x52, 0x61, 0x00, 0x00, 0x00, 0x52, 0x61, 0x00]);
        long result = HexSearcher.FindBackward(src, Pattern(0x52, 0x61), 7);
        Assert.Equal(5, result);
    }

    [Fact]
    public void FindBackward_BeforeOffsetLargerThanSource_ClampsToSourceLength()
    {
        // Pattern at offset 0 in a 4-byte source; beforeOffset=9999 should still find it.
        using IHexDataSource src = Source([0x52, 0x61, 0x00, 0x00]);
        long result = HexSearcher.FindBackward(src, Pattern(0x52, 0x61), 9999);
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task FindBackward_AtStartOfFileWithNoMatch_ReturnsMinusOneWithoutHanging()
    {
        // Regression test for the infinite-loop bug.
        // Pattern exists ONLY at offset 3 in a 5-byte source.
        // beforeOffset=4 means needle (len=2) would need to end at <=4 (i.e. start at <=2),
        // but the match starts at 3, so match+length=5 > 4: it can't satisfy the constraint.
        // The old code would re-enter the loop forever once chunkStart==0 without breaking.
        byte[] data = [0x00, 0x00, 0x00, 0xAB, 0xCD];
        using IHexDataSource src = Source(data);
        HexSearchPattern pattern = Pattern(0xAB, 0xCD);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        long result = await Task.Run(() => HexSearcher.FindBackward(src, pattern, 4), cts.Token);
        Assert.Equal(-1, result);
    }

    [Fact]
    public void FindBackward_PatternStraddlesChunkBoundary_FindsMatch()
    {
        // 130 KB buffer with needle planted at offset 65530 (crosses 64 KB boundary)
        const int BufferSize = 130 * 1024;
        const int NeedleOffset = 65530;
        byte[] needle = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22, 0x33, 0x44];
        byte[] data = new byte[BufferSize];
        Array.Copy(needle, 0, data, NeedleOffset, needle.Length);

        using IHexDataSource src = Source(data);
        long result = HexSearcher.FindBackward(src, Pattern(needle), BufferSize);
        Assert.Equal(NeedleOffset, result);
    }

    #endregion

    #region FindAll

    [Fact]
    public void FindAll_NullSource_ReturnsEmptyList()
    {
        IReadOnlyList<long> result = HexSearcher.FindAll(null!, Pattern(0x52));
        Assert.Empty(result);
    }

    [Fact]
    public void FindAll_NullPattern_ReturnsEmptyList()
    {
        using IHexDataSource src = Source([0x01, 0x02]);
        IReadOnlyList<long> result = HexSearcher.FindAll(src, null!);
        Assert.Empty(result);
    }

    [Fact]
    public void FindAll_NoMatches_ReturnsEmptyList()
    {
        using IHexDataSource src = Source([0x01, 0x02, 0x03]);
        IReadOnlyList<long> result = HexSearcher.FindAll(src, Pattern(0xAB, 0xCD));
        Assert.Empty(result);
    }

    [Fact]
    public void FindAll_ReturnsAllMatchesInOrder()
    {
        // Matches at offsets 0, 3, 6
        using IHexDataSource src = Source([0x52, 0x61, 0x00, 0x52, 0x61, 0x00, 0x52, 0x61]);
        IReadOnlyList<long> result = HexSearcher.FindAll(src, Pattern(0x52, 0x61));
        Assert.Equal(3, result.Count);
        Assert.Equal(0, result[0]);
        Assert.Equal(3, result[1]);
        Assert.Equal(6, result[2]);
    }

    [Fact]
    public void FindAll_CapsAtMaxResults()
    {
        // 6 matches in the source, but we cap at 2
        using IHexDataSource src = Source([0x52, 0x61, 0x00, 0x52, 0x61, 0x00, 0x52, 0x61, 0x00, 0x52, 0x61, 0x00, 0x52, 0x61, 0x00, 0x52, 0x61]);
        IReadOnlyList<long> result = HexSearcher.FindAll(src, Pattern(0x52, 0x61), maxResults: 2);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void FindAll_NonOverlapping_NextMatchStartsAfterPreviousEnd()
    {
        // Pattern AA AA appears at 0 and 2 if overlapping, but non-overlapping means only 0 and 2 are separate.
        // Data: AA AA AA AA => matches at 0 and 2 (non-overlapping, step by needle length=2 each time)
        using IHexDataSource src = Source([0xAA, 0xAA, 0xAA, 0xAA]);
        IReadOnlyList<long> result = HexSearcher.FindAll(src, Pattern(0xAA, 0xAA));
        Assert.Equal(2, result.Count);
        Assert.Equal(0, result[0]);
        Assert.Equal(2, result[1]);

        // Confirm that result[1] - result[0] >= needle length (no overlap)
        int needleLength = 2;
        Assert.True(result[1] - result[0] >= needleLength);
    }

    #endregion
}
