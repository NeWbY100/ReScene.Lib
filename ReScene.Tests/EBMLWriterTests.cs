using ReScene.SRS;

namespace ReScene.Tests;

/// <summary>
/// Known-vector tests for <see cref="EBMLWriter.MakeEBMLUInt"/> and <see cref="EBMLWriter.MakeEBMLId"/>.
/// Added before the Task 2 renaming of magic numbers to named constants so that
/// any inadvertent value change is caught immediately.
/// </summary>
public class EBMLWriterTests
{
    #region MakeEBMLUInt — tier thresholds

    [Theory]
    // 1-byte tier: value < 0x7F  → marker 0x80
    [InlineData(0L,    new byte[] { 0x80 })]                              // min 1-byte
    [InlineData(1L,    new byte[] { 0x81 })]
    [InlineData(0x7EL, new byte[] { 0xFE })]                             // max 1-byte (126)
    // 2-byte tier: 0x7F <= value < 0x3FFF  → marker 0x40
    [InlineData(0x7FL,   new byte[] { 0x40, 0x7F })]                     // first 2-byte (127)
    [InlineData(0x80L,   new byte[] { 0x40, 0x80 })]                     // 128
    [InlineData(0x1FFFL, new byte[] { 0x5F, 0xFF })]                     // 8191
    [InlineData(0x3FFEL, new byte[] { 0x7F, 0xFE })]                     // max 2-byte (16382)
    // 3-byte tier: 0x3FFF <= value < 0x1FFFFF  → marker 0x20
    [InlineData(0x3FFFL,  new byte[] { 0x20, 0x3F, 0xFF })]              // first 3-byte (16383)
    [InlineData(0x4000L,  new byte[] { 0x20, 0x40, 0x00 })]              // 16384
    [InlineData(0x1FFFFEL, new byte[] { 0x3F, 0xFF, 0xFE })]             // near-max 3-byte (2097150)
    // 4-byte tier: 0x1FFFFF <= value < 0x0FFFFFFF  → marker 0x10
    [InlineData(0x1FFFFFL,  new byte[] { 0x10, 0x1F, 0xFF, 0xFF })]      // first 4-byte (2097151)
    [InlineData(0x200000L,  new byte[] { 0x10, 0x20, 0x00, 0x00 })]      // 2097152
    [InlineData(0x0FFFFFFEL, new byte[] { 0x1F, 0xFF, 0xFF, 0xFE })]     // near-max 4-byte (268435454)
    // 5-byte tier: 0x0FFFFFFF <= value <= 0x07FFFFFFFF  → marker 0x08
    [InlineData(0x0FFFFFFFL,  new byte[] { 0x08, 0x0F, 0xFF, 0xFF, 0xFF })] // first 5-byte (268435455)
    [InlineData(0x10000000L,  new byte[] { 0x08, 0x10, 0x00, 0x00, 0x00 })] // 268435456
    [InlineData(0x07FFFFFFFEL, new byte[] { 0x0F, 0xFF, 0xFF, 0xFF, 0xFE })] // near-max 5-byte
    [InlineData(0x07FFFFFFFFL, new byte[] { 0x0F, 0xFF, 0xFF, 0xFF, 0xFF })] // max 5-byte (34359738367)
    public void MakeEBMLUInt_KnownVectors(long value, byte[] expected)
    {
        byte[] result = EBMLWriter.MakeEBMLUInt(value);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void MakeEBMLUInt_SixByte()
    {
        // 0x0800000000 is the first value requiring 6 bytes
        // (FiveByteSizeMax + 1 = 0x0800000000)
        byte[] result = EBMLWriter.MakeEBMLUInt(0x0800000000);
        Assert.Equal(6, result.Length);
        // Marker for 6-byte: 1 << (8-6) = 0x04
        Assert.Equal(0x04, result[0] & 0xFC); // top 6 bits contain marker
    }

    #endregion

    #region MakeEBMLId — byte-width tiers

    [Theory]
    // 1-byte: id < 0x100
    [InlineData(0xA3UL, new byte[] { 0xA3 })]                           // SimpleBlock
    [InlineData(0xD7UL, new byte[] { 0xD7 })]                           // TrackNumber
    [InlineData(0xFFUL, new byte[] { 0xFF })]                           // max 1-byte
    // 2-byte: 0x100 <= id < 0x10000
    [InlineData(0x100UL,  new byte[] { 0x01, 0x00 })]                   // first 2-byte
    [InlineData(0x6A75UL, new byte[] { 0x6A, 0x75 })]                   // ResampleFile (SRSF)
    [InlineData(0x6B75UL, new byte[] { 0x6B, 0x75 })]                   // ResampleTrack (SRST)
    [InlineData(0xFFFFUL, new byte[] { 0xFF, 0xFF })]                   // max 2-byte
    // 3-byte: 0x10000 <= id < 0x1000000
    [InlineData(0x10000UL,  new byte[] { 0x01, 0x00, 0x00 })]           // first 3-byte
    [InlineData(0xFFFFFFUL, new byte[] { 0xFF, 0xFF, 0xFF })]           // max 3-byte
    // 4-byte: id >= 0x1000000
    [InlineData(0x1000000UL,  new byte[] { 0x01, 0x00, 0x00, 0x00 })]  // first 4-byte
    [InlineData(0x1A45DFA3UL, new byte[] { 0x1A, 0x45, 0xDF, 0xA3 })]  // EBML header ID
    [InlineData(0x18538067UL, new byte[] { 0x18, 0x53, 0x80, 0x67 })]  // Segment ID
    [InlineData(0x1F43B675UL, new byte[] { 0x1F, 0x43, 0xB6, 0x75 })]  // Cluster ID
    [InlineData(0x1F697576UL, new byte[] { 0x1F, 0x69, 0x75, 0x76 })]  // ReSampleContainer ID
    public void MakeEBMLId_KnownVectors(ulong id, byte[] expected)
    {
        byte[] result = EBMLWriter.MakeEBMLId(id);
        Assert.Equal(expected, result);
    }

    #endregion

    #region Round-trip: MakeEBMLId → EBMLVInt.ReadId

    [Theory]
    [InlineData(0xA3UL)]
    [InlineData(0xD7UL)]
    [InlineData(0x6A75UL)]
    [InlineData(0x1A45DFA3UL)]
    [InlineData(0x18538067UL)]
    [InlineData(0x1F697576UL)]
    public void MakeEBMLId_RoundTripsViaReadId(ulong id)
    {
        byte[] encoded = EBMLWriter.MakeEBMLId(id);
        (ulong decoded, int len) = EBMLVInt.ReadId(encoded);
        Assert.Equal(id, decoded);
        Assert.Equal(encoded.Length, len);
    }

    #endregion
}
