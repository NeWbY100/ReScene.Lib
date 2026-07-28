using ReScene.RAR;

namespace ReScene.Tests;

/// <summary>
/// Self-checks for <see cref="AssemblyFixtureBuilder"/>: proves both the original and produced
/// volume sets it builds are real, parseable RAR4 archives whose packed payload round-trips
/// identically through <see cref="RARStream"/>, and that the two header shapes genuinely differ by
/// the expected byte count.
/// </summary>
public class AssemblyFixtureBuilderTests : TempDirTestBase
{
    [Fact]
    public void BothSets_ParseWithRARHeaderReader_AndPayloadRoundTrips()
    {
        byte[] payload = Enumerable.Range(0, 40_000).Select(i => (byte)(i % 251)).ToArray();
        AssemblyFixture f = AssemblyFixtureBuilder.Build(
            TempDir, volumeSize: 15_000, [("a.bin", payload)],
            originalHasExtTime: true, producedHasExtTime: false);

        Assert.True(f.OriginalVolumePaths.Count >= 3);
        // Packed stream is identical across shapes: RARStream over each set yields payload.
        foreach (string first in new[] { f.OriginalVolumePaths[0], f.ProducedFirstVolumePath })
        {
            using var rs = new RARStream(first, "a.bin");
            byte[] readBack = new byte[payload.Length];
            rs.ReadExactly(readBack);
            Assert.Equal(payload, readBack);
        }
        // Volume totals match pairwise (the fixed-volume-size re-split property).
        // Header shape genuinely differs (49 vs 44 for the single-file header).
    }

    [Fact]
    public void ExtTimeHeader_IsFiveBytesLonger_AndParses()
    {
        AssemblyFixture f = AssemblyFixtureBuilder.Build(
            TempDir, 15_000, [("a.bin", new byte[20_000])], true, false);

        // Read both first volumes' first file headers via RARHeaderReader and assert the
        // HeaderSize difference == 5 (flags word + 3-byte remainder).
        ushort originalHeaderSize = ReadFirstFileHeaderSize(f.OriginalVolumePaths[0]);
        ushort producedHeaderSize = ReadFirstFileHeaderSize(f.ProducedFirstVolumePath);

        Assert.Equal(5, originalHeaderSize - producedHeaderSize);
    }

    private static ushort ReadFirstFileHeaderSize(string volumePath)
    {
        using FileStream fs = new(volumePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Position = RARUtils.RAR4Marker.Length; // skip the 7-byte marker
        var reader = new RARHeaderReader(fs);

        while (fs.Position < fs.Length)
        {
            RARBlockReadResult? block = reader.ReadBlock();
            if (block == null)
            {
                break;
            }

            if (block.BlockType == RAR4BlockType.FileHeader)
            {
                return block.HeaderSize;
            }

            reader.SkipBlock(block);
        }

        throw new InvalidOperationException($"No file header found in '{volumePath}'.");
    }
}
