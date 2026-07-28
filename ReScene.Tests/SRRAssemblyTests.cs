using System.Security.Cryptography;
using ReScene.Core;
using ReScene.Core.Cryptography;
using ReScene.Core.IO;

namespace ReScene.Tests;

/// <summary>
/// End-to-end proof that guided assembly reproduces the ORIGINAL release volumes byte-for-byte
/// from a differently-shaped "produced" (re-split) volume set plus the SRR that describes the
/// originals — the EXTTIME-divergence scenario that motivated the whole feature — and that set
/// filtering (<see cref="SRRReconstructor.ReconstructAsync"/>/<see
/// cref="SRRReconstructor.PreflightSet"/> via <see cref="SRRReconstructor.SectionMatchesSet"/> and
/// <see cref="SRRReconstructor.ValidateSetSelector"/>) correctly isolates one set from another in a
/// combined, multi-set SRR.
/// </summary>
public class SRRAssemblyTests : TempDirTestBase
{
    private static byte[] Payload(int n, int seed) =>
        [.. Enumerable.Range(0, n).Select(i => (byte)((i * 31 + seed) % 251))];

    private async Task<SRRReconstructionResult> AssembleAsync(AssemblyFixture f, string outSub,
        IReadOnlyList<string>? names = null)
    {
        using var source = new ProducedVolumesPackedSource(f.ProducedFirstVolumePath);
        return await new SRRReconstructor(NullReSceneLogger.Instance).ReconstructAsync(
            f.SrrPath, source, TempDir, Path.Combine(TempDir, outSub),
            names ?? f.OriginalVolumeNames, [], HashType.CRC32, CancellationToken.None);
    }

    private static void AssertByteIdentical(IReadOnlyList<string> originals, IReadOnlyList<string> assembled)
    {
        Assert.Equal(originals.Count, assembled.Count);
        for (int i = 0; i < originals.Count; i++)
        {
            byte[] o = File.ReadAllBytes(originals[i]);
            byte[] a = File.ReadAllBytes(assembled[i]);
            Assert.Equal(o.Length, a.Length);
            Assert.Equal(SHA256.HashData(o), SHA256.HashData(a));
        }
    }

    /// <summary>
    /// Concatenates two AssemblyFixtureBuilder-produced SRRs into one combined, multi-set SRR: one
    /// SRR header (reused from <paramref name="srr1Path"/>) followed by BOTH files' RARFile section
    /// runs (everything after their own leading 0x69 header block, found via its declared header
    /// size at fixed offset 5). Written alongside <paramref name="srr1Path"/>, so it is cleaned up
    /// by the same TempDirTestBase teardown.
    /// </summary>
    private static string ConcatenateSrrs(string srr1Path, string srr2Path)
    {
        byte[] bytes1 = File.ReadAllBytes(srr1Path);
        byte[] bytes2 = File.ReadAllBytes(srr2Path);
        ushort header1Size = BitConverter.ToUInt16(bytes1, 5);
        ushort header2Size = BitConverter.ToUInt16(bytes2, 5);

        using MemoryStream combined = new();
        combined.Write(bytes1, 0, header1Size);                           // one SRR header
        combined.Write(bytes1, header1Size, bytes1.Length - header1Size); // set 1's section run
        combined.Write(bytes2, header2Size, bytes2.Length - header2Size); // set 2's section run

        string combinedPath = Path.Combine(Path.GetDirectoryName(srr1Path)!, "combined.srr");
        File.WriteAllBytes(combinedPath, combined.ToArray());
        return combinedPath;
    }

    [Fact]
    public async Task ExtTimeDivergence_ByteIdenticalOutput() // THE bug
    {
        AssemblyFixture f = AssemblyFixtureBuilder.Build(TempDir, 15_000,
            [("a.bin", Payload(40_000, 1))], originalHasExtTime: true, producedHasExtTime: false);
        SRRReconstructionResult r = await AssembleAsync(f, "out");
        Assert.Equal(SRRReconstructionStatus.Success, r.Status);
        AssertByteIdentical(f.OriginalVolumePaths, r.WrittenPaths);
    }

    [Fact]
    public async Task MirrorShift_ReadsAcrossProducedBoundary()
    {
        AssemblyFixture f = AssemblyFixtureBuilder.Build(TempDir, 15_000,
            [("a.bin", Payload(40_000, 1))], originalHasExtTime: false, producedHasExtTime: true);
        SRRReconstructionResult r = await AssembleAsync(f, "out");
        Assert.Equal(SRRReconstructionStatus.Success, r.Status);
        AssertByteIdentical(f.OriginalVolumePaths, r.WrittenPaths);
    }

    [Fact]
    public async Task MultiFile_SplitAcrossVolumes()
    {
        AssemblyFixture f = AssemblyFixtureBuilder.Build(TempDir, 15_000,
            [("a.bin", Payload(20_000, 1)), ("b.bin", Payload(18_000, 2))], true, false);
        SRRReconstructionResult r = await AssembleAsync(f, "out");
        Assert.Equal(SRRReconstructionStatus.Success, r.Status);
        AssertByteIdentical(f.OriginalVolumePaths, r.WrittenPaths);
    }

    [Fact]
    public async Task MultiSet_SameBasenames_FiltersByQualifiedName()
    {
        AssemblyFixture cd1 = AssemblyFixtureBuilder.Build(Path.Combine(TempDir, "s1"), 15_000,
            [("a.bin", Payload(20_000, 1))], true, false, directoryPrefix: "CD1");
        AssemblyFixture cd2 = AssemblyFixtureBuilder.Build(Path.Combine(TempDir, "s2"), 15_000,
            [("a.bin", Payload(20_000, 9))], true, false, directoryPrefix: "CD2");
        string combinedSrr = ConcatenateSrrs(cd1.SrrPath, cd2.SrrPath); // header + both section runs

        using var source = new ProducedVolumesPackedSource(cd2.ProducedFirstVolumePath);
        SRRReconstructionResult r = await new SRRReconstructor(NullReSceneLogger.Instance)
            .ReconstructAsync(combinedSrr, source, TempDir, Path.Combine(TempDir, "out"),
                cd2.OriginalVolumeNames /* "CD2/t.rar"… */, [], HashType.CRC32, CancellationToken.None);

        Assert.Equal(SRRReconstructionStatus.Success, r.Status);
        AssertByteIdentical(cd2.OriginalVolumePaths, r.WrittenPaths);
        Assert.DoesNotContain(r.WrittenPaths, p => p.Contains("CD1", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AmbiguousBareName_Fails()
    {
        AssemblyFixture cd1 = AssemblyFixtureBuilder.Build(Path.Combine(TempDir, "q1"), 15_000,
            [("a.bin", Payload(20_000, 1))], true, false, directoryPrefix: "CD1");
        AssemblyFixture cd2 = AssemblyFixtureBuilder.Build(Path.Combine(TempDir, "q2"), 15_000,
            [("a.bin", Payload(20_000, 9))], true, false, directoryPrefix: "CD2");
        string combinedSrr = ConcatenateSrrs(cd1.SrrPath, cd2.SrrPath);

        using var source = new ProducedVolumesPackedSource(cd2.ProducedFirstVolumePath);
        SRRReconstructionResult r = await new SRRReconstructor(NullReSceneLogger.Instance)
            .ReconstructAsync(combinedSrr, source, TempDir, Path.Combine(TempDir, "outq"),
                ["t.rar"], [], HashType.CRC32, CancellationToken.None);

        Assert.Equal(SRRReconstructionStatus.Error, r.Status);
        Assert.Contains("t.rar", r.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("ambiguous", r.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(TempDir, "outq"))); // validated pre-output
    }

    [Fact]
    public async Task PaddingBlocks_Preserved()
    {
        AssemblyFixture f = AssemblyFixtureBuilder.Build(TempDir, 15_000,
            [("a.bin", Payload(40_000, 1))], originalHasExtTime: true, producedHasExtTime: false,
            insertPadding: true);
        SRRReconstructionResult r = await AssembleAsync(f, "outpad");
        Assert.Equal(SRRReconstructionStatus.Success, r.Status);
        AssertByteIdentical(f.OriginalVolumePaths, r.WrittenPaths);
    }
}
