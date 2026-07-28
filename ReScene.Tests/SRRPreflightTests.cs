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

    [Fact]
    public void ZeroLengthRRService_StillDeclines()
    {
        // Rule 3 (Service named "RR") is unconditional on name alone — unlike rules 4/5, which
        // are gated on a declared-but-absent payload. A zero-declared-size RR service block must
        // still be treated as recovery-record evidence, not silently pass just because there is
        // no ADD_SIZE to call "stripped".
        string srr = BuildSrr(0, h => h
            .AddArchiveHeader()
            .AddServiceBlock("RR", 0, includeData: false)
            .AddEndArchive());
        SRRReconstructionResult r = NewReconstructor().PreflightSet(srr, ["a.rar"]);
        Assert.Equal(SRRReconstructionStatus.UnsupportedSrr, r.Status);
        Assert.Contains("RR", r.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RARFileSectionWithHeaderPadding_DoesNotDesyncTheWalk()
    {
        // The RARFile (0x71) block's declared header size can legitimately include bytes beyond
        // the name (padding this codebase's own builder never emits, but a real-world or
        // malformed SRR could). The walk must seek to the declared end of the header — not
        // "wherever reading the name happened to leave the stream" — or the next block gets
        // misparsed from padding bytes.
        string srr = new SRRTestDataBuilder().AddSRRHeader("t")
            .AddRARFileWithHeaders("a.rar", 0, extraHeaderPadding: 4, h => h
                .AddArchiveHeader()
                .AddFileHeader("a.bin", packedSize: 8, unpackedSize: 8)
                .AddEndArchive())
            .BuildToFile(TempDir, "padded.srr");

        SRRReconstructionResult r = NewReconstructor().PreflightSet(srr, ["a.rar"]);
        Assert.Equal(SRRReconstructionStatus.Success, r.Status);
    }

    [Fact]
    public void EmptyFile_IsError()
    {
        string srr = Path.Combine(TempDir, "empty.srr");
        File.WriteAllBytes(srr, []);
        Assert.Equal(SRRReconstructionStatus.Error,
            NewReconstructor().PreflightSet(srr, ["a.rar"]).Status);
    }

    [Fact]
    public void NoSrrHeaderBlock_IsError_EvenWithOtherwiseValidContent()
    {
        // A well-formed sequence of blocks that simply never includes the SRR header (0x69) —
        // e.g. one that starts directly with a RAR-file section — is not a valid SRR, matching
        // SRRVerifier's "Missing SRR header block (0x69)" stance. It must not be treated as
        // assemblable just because nothing else looked wrong.
        string srr = new SRRTestDataBuilder()
            .AddRARFileWithHeaders("a.rar", h => h
                .AddArchiveHeader()
                .AddFileHeader("a.bin", packedSize: 8, unpackedSize: 8)
                .AddEndArchive())
            .BuildToFile(TempDir, "noheader.srr");

        Assert.Equal(SRRReconstructionStatus.Error,
            NewReconstructor().PreflightSet(srr, ["a.rar"]).Status);
    }

    [Fact]
    public void EndArchiveWithMalformedAddSize_IsError()
    {
        // EndArchive (0x7B) has no ADD_SIZE field (RAR4HeaderLayout); a LONG_BLOCK EndArchive
        // declaring one is a malformed shape, not evidence of anything specific — Error, not
        // UnsupportedSrr.
        string srr = BuildSrr(0, h => h
            .AddArchiveHeader()
            .AddMalformedEndArchiveWithAddSize(64));
        SRRReconstructionResult r = NewReconstructor().PreflightSet(srr, ["a.rar"]);
        Assert.Equal(SRRReconstructionStatus.Error, r.Status);
    }

    [Fact]
    public async Task ReconstructAsync_EndArchiveWithMalformedAddSize_ReturnsErrorAndCreatesNoOutput()
    {
        // Pins the pair: PreflightSet declines this malformed shape before ReconstructAsync's own
        // (now equally strict) EndArchive handling would ever see it.
        string srr = BuildSrr(0, h => h
            .AddArchiveHeader()
            .AddMalformedEndArchiveWithAddSize(64));
        string outDir = Path.Combine(TempDir, "out");
        SRRReconstructionResult r = await NewReconstructor().ReconstructAsync(
            srr, new RecordingNoopSource(), TempDir, outDir, ["a.rar"], [], HashType.CRC32,
            CancellationToken.None);
        Assert.Equal(SRRReconstructionStatus.Error, r.Status);
        Assert.False(Directory.Exists(outDir));
    }

    [Fact]
    public void LongBlockRARFileWithUndersizedHeader_IsError_NotBackwardSeek()
    {
        // A LONG_BLOCK RARFile section consumes a 4-byte ADD_SIZE field BEFORE the name — a
        // declared header size that only accounts for base+nameLen+name (not that extra 4 bytes)
        // must be rejected, not silently accepted and then seeked backward into the name bytes
        // just read.
        string srr = new SRRTestDataBuilder().AddSRRHeader("t")
            .AddLongBlockRARFileWithUndersizedHeader("a.rar", addSize: 0, declaredHeaderSize: 14, h => h
                .AddArchiveHeader()
                .AddFileHeader("a.bin", packedSize: 8, unpackedSize: 8)
                .AddEndArchive())
            .BuildToFile(TempDir, "undersized.srr");

        SRRReconstructionResult r = NewReconstructor().PreflightSet(srr, ["a.rar"]);
        Assert.Equal(SRRReconstructionStatus.Error, r.Status);
        // The status alone isn't discriminating here: an unfixed 7+2+nameLen formula still
        // returns Error for this input, but via a DIFFERENT, later check ("embedded RAR header
        // extends past end of file") after the wrong backward seek corrupts the stream position —
        // not via the name-overflow check this test targets. Assert on the diagnostic itself so a
        // regression back to the wrong formula is caught even though it "coincidentally" still
        // errors.
        Assert.Contains("overflows its declared header size", r.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReconstructAsync_LongBlockRARFileWithUndersizedHeader_ReturnsErrorAndCreatesNoOutput()
    {
        string srr = new SRRTestDataBuilder().AddSRRHeader("t")
            .AddLongBlockRARFileWithUndersizedHeader("a.rar", addSize: 0, declaredHeaderSize: 14, h => h
                .AddArchiveHeader()
                .AddFileHeader("a.bin", packedSize: 8, unpackedSize: 8)
                .AddEndArchive())
            .BuildToFile(TempDir, "undersized2.srr");

        string outDir = Path.Combine(TempDir, "out");
        SRRReconstructionResult r = await NewReconstructor().ReconstructAsync(
            srr, new RecordingNoopSource(), TempDir, outDir, ["a.rar"], [], HashType.CRC32,
            CancellationToken.None);
        Assert.Equal(SRRReconstructionStatus.Error, r.Status);
        Assert.False(Directory.Exists(outDir));
        // See PreflightSet's sibling test: the status alone doesn't discriminate an unfixed
        // formula (still errors, just later and for a different reason) from the fix.
        Assert.Contains("overflows its declared header size", r.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoSrrHeaderBlock_WithEvidenceContent_IsError_NotUnsupportedSrr()
    {
        // A file with NO SRR header block (0x69) is not a valid SRR at all — even when its
        // content happens to look like recovery-record evidence (here: a Protected archive
        // header), that must not be reported as UnsupportedSrr (which implies "this IS a valid
        // SRR, just unassemblable"). It simply is not an SRR: Error.
        string srr = new SRRTestDataBuilder()
            .AddRARFileWithHeaders("a.rar", h => h
                .AddArchiveHeader(RARArchiveFlags.Protected)
                .AddEndArchive())
            .BuildToFile(TempDir, "headerless-evidence.srr");

        SRRReconstructionResult r = NewReconstructor().PreflightSet(srr, ["a.rar"]);
        Assert.Equal(SRRReconstructionStatus.Error, r.Status);
    }

    [Fact]
    public void EndArchiveWithLongBlockAndZeroAddSize_IsError()
    {
        // The malformed condition is LONG_BLOCK being set on EndArchive at all (it has no
        // ADD_SIZE field per RAR4HeaderLayout) — not merely a nonzero declared value. A
        // LONG_BLOCK EndArchive declaring addSize=0 is just as malformed and must not slip
        // through a "declared value > 0" check.
        string srr = BuildSrr(0, h => h
            .AddArchiveHeader()
            .AddMalformedEndArchiveWithAddSize(0));
        SRRReconstructionResult r = NewReconstructor().PreflightSet(srr, ["a.rar"]);
        Assert.Equal(SRRReconstructionStatus.Error, r.Status);
    }

    [Fact]
    public async Task ReconstructAsync_EndArchiveWithLongBlockAndZeroAddSize_ReturnsErrorAndCreatesNoOutput()
    {
        string srr = BuildSrr(0, h => h
            .AddArchiveHeader()
            .AddMalformedEndArchiveWithAddSize(0));
        string outDir = Path.Combine(TempDir, "out");
        SRRReconstructionResult r = await NewReconstructor().ReconstructAsync(
            srr, new RecordingNoopSource(), TempDir, outDir, ["a.rar"], [], HashType.CRC32,
            CancellationToken.None);
        Assert.Equal(SRRReconstructionStatus.Error, r.Status);
        Assert.False(Directory.Exists(outDir));
    }

    private sealed class RecordingNoopSource : IPackedSource
    {
        public Stream OpenPackedStream(string archivedFileName) => new MemoryStream();
        public void Dispose() { }
    }
}
