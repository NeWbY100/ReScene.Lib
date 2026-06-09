using ReScene.SRS;

namespace ReScene.Tests;

/// <summary>
/// Tests the variable MKV signature-size heuristic (a port of pyrescene's minimum_signature_size):
/// the signature grows in 256-byte steps while the last 64 bytes of each step are ASCII (codec
/// parameter sets), stopping at the first step with real binary frame data, capped at 40 steps.
/// </summary>
public class MKVSignatureSizeTests
{
    private static byte[] Filled(int length, byte value)
    {
        byte[] b = new byte[length];
        Array.Fill(b, value);
        return b;
    }

    [Fact]
    public void AllAscii_FallsBackToOneStep()
    {
        // Never hits non-ASCII within the 40-step cap → minimal 256-byte signature.
        byte[] content = Filled(3000, 0x41);   // 'A', all < 0x80
        Assert.Equal(256, MKVContainerHandler.MinimumSignatureSize(content, 0));
    }

    [Fact]
    public void NonAsciiInFirstStep_StopsAt256()
    {
        byte[] content = Filled(1024, 0x41);
        content[200] = 0xFF;   // within the first step's last-64 window [192,256)
        Assert.Equal(256, MKVContainerHandler.MinimumSignatureSize(content, 0));
    }

    [Fact]
    public void NonAsciiInTenthStep_GrowsTo2560()
    {
        // ASCII parameter-set data through nine steps; real frame data appears in the tenth.
        byte[] content = Filled(4096, 0x41);
        content[2500] = 0x90;   // within the tenth step's last-64 window [2496,2560)
        Assert.Equal(2560, MKVContainerHandler.MinimumSignatureSize(content, 0));
    }

    [Fact]
    public void NonAsciiInSecondStep_GrowsTo512()
    {
        byte[] content = Filled(1024, 0x41);
        content[500] = 0x80;   // within the second step's last-64 window [448,512)
        Assert.Equal(512, MKVContainerHandler.MinimumSignatureSize(content, 0));
    }

    [Fact]
    public void ReturnValueIsRemainingBytes_WhenAlreadyAccumulated()
    {
        // 100 bytes already in the signature; non-ASCII falls in the second cumulative step
        // (offset 256*2 - 100 = 412 into the new content; its last-64 window is [348,412)).
        byte[] content = Filled(1024, 0x41);
        content[400] = 0xFF;
        Assert.Equal(512 - 100, MKVContainerHandler.MinimumSignatureSize(content, 100));
    }

    [Fact]
    public void NegativeOffsetWindow_IndexesFromEnd_LikePythonSlicing()
    {
        // With 200 bytes already accumulated, the first step's window is content[-8:56] — in Python an
        // empty slice for real (>=64-byte) frame data, so a non-ASCII byte at the very start must NOT
        // cut the signature short; the real boundary is the second step's window [248,312).
        byte[] content = Filled(1024, 0x41);
        content[10] = 0xFF;    // inside a naive [0,56) clamp — must be ignored (Python sees an empty slice)
        content[300] = 0xFF;   // inside the second step's window [248,312) → the real boundary
        Assert.Equal(512 - 200, MKVContainerHandler.MinimumSignatureSize(content, 200));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    [InlineData(128)]
    public void IsAsciiRange_TrueForAllAscii(int start)
    {
        byte[] content = Filled(256, 0x7F);   // 0x7F is the highest ASCII byte
        Assert.True(MKVContainerHandler.IsAsciiRange(content, start, 256));
    }

    [Fact]
    public void IsAsciiRange_FalseWhenAnyByteIs0x80OrAbove()
    {
        byte[] content = Filled(256, 0x41);
        content[200] = 0x80;
        Assert.False(MKVContainerHandler.IsAsciiRange(content, 192, 256));
    }

    [Fact]
    public void IsAsciiRange_TrueForEmptyOrOutOfRangeSlice()
    {
        byte[] content = Filled(100, 0x41);
        Assert.True(MKVContainerHandler.IsAsciiRange(content, 192, 256));   // entirely past the end
        Assert.True(MKVContainerHandler.IsAsciiRange(content, -8, 0));       // empty after clamping
    }
}
