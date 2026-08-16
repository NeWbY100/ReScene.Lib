using Force.Crc32;
using ReScene.RAR;

namespace ReScene.Tests;

/// <summary>
/// Regression tests for three parsing defects in <see cref="RARDetailedParser"/> and one in
/// <see cref="RARPatcher"/>, each of which produced silently wrong output rather than an error.
/// </summary>
public class RARDetailedParserRegressionTests
{
    /// <summary>RAR5 vint: 7 data bits per byte, bit 7 continues.</summary>
    private static byte[] EncodeVInt(ulong value)
    {
        var bytes = new List<byte>();
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value > 0)
            {
                b |= 0x80;
            }

            bytes.Add(b);
        }
        while (value > 0);

        return [.. bytes];
    }

    /// <summary>A RAR4 file header (0x74) with LONG_BLOCK set and the given ADD_SIZE.</summary>
    private static byte[] BuildRAR4FileHeaderWithAddSize(uint addSize)
    {
        const ushort headerSize = 32;
        byte[] header = new byte[headerSize];

        header[2] = 0x74;                                                   // FileHeader
        BitConverter.GetBytes((ushort)RARFileFlags.LongBlock).CopyTo(header, 3);
        BitConverter.GetBytes(headerSize).CopyTo(header, 5);
        BitConverter.GetBytes(addSize).CopyTo(header, 7);                   // ADD_SIZE

        uint crc32 = Crc32Algorithm.Compute(header, 2, header.Length - 2);
        BitConverter.GetBytes((ushort)(crc32 & 0xFFFF)).CopyTo(header, 0);
        return header;
    }

    [Fact]
    public void ParseRAR4_NearFourGigabyteAddSize_DoesNotWrapTotalSize()
    {
        // headSize is a ushort and addSize a uint, so "headSize + addSize" was evaluated in uint
        // and WRAPPED: 32 + 0xFFFFFFF0 became 16. The block walker advances by
        // StartOffset + TotalSize, so it then resumed INSIDE the header it had just read.
        const uint addSize = 0xFFFFFFF0;

        using var ms = new MemoryStream();
        ms.Write(RARUtils.RAR4Marker);
        ms.Write(BuildRAR4FileHeaderWithAddSize(addSize));
        ms.Position = 0;

        IReadOnlyList<RARDetailedBlock> blocks = RARDetailedParser.Parse(ms);

        RARDetailedBlock fileBlock = Assert.Single(blocks, b => b.HeaderSize == 32);
        Assert.Equal(32L + addSize, fileBlock.TotalSize);
        Assert.True(fileBlock.TotalSize > uint.MaxValue - 32, "TotalSize wrapped through uint.");
    }

    [Fact]
    public void ParseFromPosition_EmbeddedRAR5AtNonZeroOffset_IsNotParsedAsRAR4()
    {
        // ParseFromPosition validates the signature at the CURRENT position but used to decide
        // RAR4-vs-RAR5 by probing offset 0. An SRR embeds RAR data at nonzero offsets, and the
        // file often begins with a RAR4 stream — so a RAR5 volume later in the same file was
        // classified RAR4 and every following block misparsed.
        using var ms = new MemoryStream();

        // A RAR4 archive at offset 0, so offset-0 probing answers "RAR4".
        ms.Write(RARUtils.RAR4Marker);
        ms.Write(BuildRAR4FileHeaderWithAddSize(0));

        long embeddedAt = ms.Position;

        // A RAR5 archive embedded after it.
        ms.Write(RARUtils.RAR5Marker);
        byte[] headerContent = [.. EncodeVInt(1UL), .. EncodeVInt(0UL)];    // MainArchive, no flags
        ms.Write(new byte[4]);                                              // header CRC (unchecked here)
        ms.Write(EncodeVInt((ulong)headerContent.Length));
        ms.Write(headerContent);

        ms.Position = embeddedAt;
        IReadOnlyList<RARDetailedBlock> blocks = RARDetailedParser.ParseFromPosition(ms);

        Assert.NotEmpty(blocks);

        // The RAR5 marker block is 8 bytes; the RAR4 path emits a 7-byte marker block. That
        // single number is the cleanest witness of which branch ran.
        RARDetailedBlock marker = blocks[0];
        Assert.Equal(8, marker.TotalSize);
    }

    [Theory]
    [InlineData(ushort.MaxValue - 8, false)]   // 65527: grows to exactly 65535 — still valid
    [InlineData(ushort.MaxValue - 7, true)]    // 65528: grows to 65536 — cannot be represented
    public void PatchLargeFlags_AtTheHeadSizeBoundary_RefusesOnlyWhatCannotGrow(int headerSize, bool expectThrow)
    {
        // Pins the BOUNDARY, not just a comfortably-oversized header: a single-value test passed
        // for any threshold between MaxValue-8 and MaxValue-4, so an off-by-one in the guard
        // would have survived it.
        byte[] header = new byte[headerSize];

        header[2] = 0x74;
        BitConverter.GetBytes((ushort)RARFileFlags.LongBlock).CopyTo(header, 3);
        BitConverter.GetBytes((ushort)headerSize).CopyTo(header, 5);
        BitConverter.GetBytes(0u).CopyTo(header, 7);

        uint boundaryCrc = Crc32Algorithm.Compute(header, 2, header.Length - 2);
        BitConverter.GetBytes((ushort)(boundaryCrc & 0xFFFF)).CopyTo(header, 0);

        using var boundaryStream = new MemoryStream();
        boundaryStream.Write(RARUtils.RAR4Marker);
        boundaryStream.Write(header);
        boundaryStream.Position = 0;

        var boundaryOptions = new PatchOptions { SetLargeFlag = true, HighPackSize = 1, HighUnpSize = 1 };

        if (expectThrow)
        {
            Assert.Throws<InvalidDataException>(() => { RARPatcher.PatchLargeFlags(boundaryStream, boundaryOptions); });
        }
        else
        {
            RARPatcher.PatchLargeFlags(boundaryStream, boundaryOptions);
        }
    }

    [Fact]
    public void PatchFile_HeaderTooLargeToGrow_ThrowsInsteadOfTruncatingHeadSize()
    {
        // HEAD_SIZE is a ushort, so adding the 8 LARGE bytes to a 65530-byte header produced
        // 65538, which the (ushort) cast truncated to 2 — desynchronizing the archive
        // irrecoverably. Refusing is the only correct outcome.
        const int headerSize = ushort.MaxValue - 4;   // 65531: cannot grow by 8
        byte[] header = new byte[headerSize];

        header[2] = 0x74;                                                   // FileHeader
        BitConverter.GetBytes((ushort)RARFileFlags.LongBlock).CopyTo(header, 3);
        BitConverter.GetBytes((ushort)headerSize).CopyTo(header, 5);
        BitConverter.GetBytes(0u).CopyTo(header, 7);                        // ADD_SIZE

        uint crc32 = Crc32Algorithm.Compute(header, 2, header.Length - 2);
        BitConverter.GetBytes((ushort)(crc32 & 0xFFFF)).CopyTo(header, 0);

        using var stream = new MemoryStream();
        stream.Write(RARUtils.RAR4Marker);
        stream.Write(header);
        stream.Position = 0;

        var options = new PatchOptions
        {
            SetLargeFlag = true,
            HighPackSize = 1,
            HighUnpSize = 1,
        };

        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => { RARPatcher.PatchLargeFlags(stream, options); });

        Assert.Contains("HEAD_SIZE", ex.Message, StringComparison.Ordinal);
    }
}
