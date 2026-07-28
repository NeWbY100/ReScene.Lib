using ReScene.Core;
using ReScene.Core.Cryptography;
using ReScene.Core.IO;
using ReScene.RAR;
using ReScene.SRR;

namespace ReScene.Tests;

/// <summary>
/// Tests for <see cref="SRRReconstructor.PreflightSet"/> — the read-only guard that declines an
/// SRR before any output is created when a required payload (a recovery record, whether old-style
/// or a WinRAR service block, or any other embedded RAR block whose data was stripped) is not
/// actually present. Also covers the guard's wiring into <see
/// cref="SRRReconstructor.ReconstructAsync"/>.
/// </summary>
public class SRRPreflightTests : TempDirTestBase
{
    private static SRRReconstructor NewReconstructor() => new(NullReSceneLogger.Instance);

    private string BuildSrr(ushort sectionFlags, Action<RAR4HeaderBuilder> headers) =>
        new SRRTestDataBuilder().AddSRRHeader("t")
            .AddRARFileWithHeaders("a.rar", sectionFlags, headers)
            .BuildToFile(TempDir, "t.srr");

    [Fact]
    public void FlagOnlyRecoveryRemoved_IsEligible()
    {
        // The real-world default shape: every writer sets the flag, no RR exists.
        string srr = BuildSrr((ushort)SRRBlockFlags.RecoveryBlocksRemoved, h => h
            .AddArchiveHeader()
            .AddFileHeader("a.bin", packedSize: 8, unpackedSize: 8)
            .AddEndArchive());
        SRRReconstructionResult r = NewReconstructor().PreflightSet(srr, ["a.rar"]);
        Assert.Equal(SRRReconstructionStatus.Success, r.Status);
    }

    [Theory]
    [InlineData("protected", "recovery record")]
    [InlineData("protect78", "old-style recovery")]
    [InlineData("rrService", "RR")]
    [InlineData("avStripped", "AV")]
    public void RealEvidence_Declines_WithNamedDiagnostic(string shape, string expectInDiag)
    {
        string srr = BuildSrr(0, h =>
        {
            switch (shape)
            {
                case "protected": h.AddArchiveHeader(RARArchiveFlags.Protected); break;
                case "protect78": h.AddArchiveHeader().AddProtectBlock(64); break;
                case "rrService": h.AddArchiveHeader().AddServiceBlock("RR", 64, includeData: false); break;
                case "avStripped": h.AddArchiveHeader().AddServiceBlock("AV", 16, includeData: false); break;
            }
            h.AddEndArchive();
        });
        SRRReconstructionResult r = NewReconstructor().PreflightSet(srr, ["a.rar"]);
        Assert.Equal(SRRReconstructionStatus.UnsupportedSrr, r.Status);
        Assert.Contains(expectInDiag, r.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CmtWithPayload_IsEligible()
    {
        string srr = BuildSrr(0, h => h
            .AddArchiveHeader()
            .AddCmtServiceBlock("release notes")   // existing emitter, payload stored
            .AddFileHeader("a.bin", 8, 8)
            .AddEndArchive());
        Assert.Equal(SRRReconstructionStatus.Success,
            NewReconstructor().PreflightSet(srr, ["a.rar"]).Status);
    }

    [Fact]
    public void MalformedSrr_IsError_NotUnsupported()
    {
        string srr = Path.Combine(TempDir, "bad.srr");
        File.WriteAllBytes(srr, [0x01, 0x02, 0x03]);
        Assert.Equal(SRRReconstructionStatus.Error,
            NewReconstructor().PreflightSet(srr, ["a.rar"]).Status);
    }

    [Fact]
    public async Task ReconstructAsync_DeclinedSrr_CreatesNoOutput()
    {
        // The guard must fire before Directory.CreateDirectory (codex plan B5).
        string srr = BuildSrr(0, h => h.AddArchiveHeader(RARArchiveFlags.Protected).AddEndArchive());
        string outDir = Path.Combine(TempDir, "out");
        SRRReconstructionResult r = await NewReconstructor().ReconstructAsync(
            srr, new RecordingNoopSource(), TempDir, outDir, ["a.rar"], [], HashType.CRC32,
            CancellationToken.None);
        Assert.Equal(SRRReconstructionStatus.UnsupportedSrr, r.Status);
        Assert.False(Directory.Exists(outDir));
    }

    private sealed class RecordingNoopSource : IPackedSource
    {
        public Stream OpenPackedStream(string archivedFileName) => new MemoryStream();
        public void Dispose() { }
    }
}
