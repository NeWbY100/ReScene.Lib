using ReScene.Core.IO;
using ReScene.RAR;

namespace ReScene.Tests;

public class ProducedVolumesPackedSourceTests : TempDirTestBase
{
    private AssemblyFixture BuildTwoFileFixture() =>
        AssemblyFixtureBuilder.Build(TempDir, 15_000,
            [("a.bin", MakePayload(20_000, seed: 1)), ("b.bin", MakePayload(9_000, seed: 2))],
            originalHasExtTime: true, producedHasExtTime: false);

    private static byte[] MakePayload(int n, int seed) =>
        [.. Enumerable.Range(0, n).Select(i => (byte)((i * 31 + seed) % 251))];

    [Fact]
    public void OpenPackedStream_ConcatenatesSplitPieces_AcrossVolumes()
    {
        AssemblyFixture f = BuildTwoFileFixture();
        using var source = new ProducedVolumesPackedSource(f.ProducedFirstVolumePath);
        using Stream s = source.OpenPackedStream("a.bin");
        byte[] all = new byte[20_000];
        s.ReadExactly(all);
        Assert.Equal(MakePayload(20_000, 1), all);
    }

    [Fact]
    public void OpenPackedStream_SecondFile_StartsAtItsOwnByteZero()
    {
        AssemblyFixture f = BuildTwoFileFixture();
        using var source = new ProducedVolumesPackedSource(f.ProducedFirstVolumePath);
        using Stream s = source.OpenPackedStream("b.bin");
        byte[] head = new byte[16];
        s.ReadExactly(head);
        Assert.Equal(MakePayload(9_000, 2).AsSpan(0, 16).ToArray(), head);
    }

    [Fact]
    public void OpenedStream_IsSingleSnapshot_LateVolumeInvisible()
    {
        // The snapshot is taken when the RARStream is OPENED (inside OpenPackedStream),
        // so the continuation volume must be hidden AT OPEN TIME and restored after —
        // hiding before source construction alone proves nothing. Pick the continuation
        // volume by its volume-naming successor, NOT lexicographically (".rar" does not
        // sort before ".r00").
        AssemblyFixture f = BuildTwoFileFixture();
        string vol2 = RARVolumeNaming.GetNextVolumePath(f.ProducedFirstVolumePath, isOldNaming: true)!;
        string hidden = vol2 + ".hidden";
        File.Move(vol2, hidden);
        using var source = new ProducedVolumesPackedSource(f.ProducedFirstVolumePath);
        using Stream s = source.OpenPackedStream("a.bin");   // snapshot taken HERE, vol2 absent
        File.Move(hidden, vol2);                              // "appears" after the snapshot
        byte[] buf = new byte[20_000];
        Assert.ThrowsAny<Exception>(() => s.ReadExactly(buf)); // stream never sees the late volume
    }
}
