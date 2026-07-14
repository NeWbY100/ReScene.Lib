using System.Text;
using ReScene.Core;
using ReScene.Core.Cryptography;

namespace ReScene.Tests;

/// <summary>
/// Tests for <see cref="SRRReconstructor"/> — the direct (custom-packer) reconstruction path,
/// previously untested. Covers end-to-end reconstruction from a synthetic SRR, hash
/// match/mismatch reporting, the no-volumes case, cancellation, and the source-file resolution
/// and byte-copy helpers.
/// </summary>
public class SRRReconstructorTests : TempDirTestBase
{
    private readonly string _inputDir;
    private readonly string _outputDir;

    public SRRReconstructorTests()
    {
        _inputDir = Path.Combine(TempDir, "input");
        _outputDir = Path.Combine(TempDir, "output");
        Directory.CreateDirectory(_inputDir);
        Directory.CreateDirectory(_outputDir);
    }

    private static readonly byte[] SourcePayload = [.. Enumerable.Range(0, 64).Select(i => (byte)i)];

    /// <summary>
    /// Builds a one-volume SRR (archive header + file header for the archived name + end archive)
    /// and writes the matching source file, returning the SRR path.
    /// </summary>
    private string BuildSingleVolumeSRR(string rarName, string archivedName, byte[] sourceData)
    {
        File.WriteAllBytes(Path.Combine(_inputDir, archivedName), sourceData);

        SRRTestDataBuilder builder = new SRRTestDataBuilder()
            .AddSRRHeader("ReScene.Tests")
            .AddRARFileWithHeaders(rarName, h => h
                .AddArchiveHeader()
                .AddFileHeader(archivedName, packedSize: (uint)sourceData.Length, unpackedSize: (uint)sourceData.Length)
                .AddEndArchive());

        return builder.BuildToFile(TempDir, "test.srr");
    }

    /// <summary>
    /// Independently assembles the exact bytes a correct reconstruction must produce: the same RAR
    /// headers the SRR carries, with the source payload spliced in immediately after the file
    /// header (archive header + file header + payload + end archive). Built with the SAME builder
    /// calls/args as <see cref="BuildSingleVolumeSRR"/>, but not via the reconstructor — so it is a
    /// genuine oracle, not the reconstructor's own output fed back.
    /// </summary>
    private static byte[] ExpectedReconstructedBytes(string archivedName, byte[] sourceData)
    {
        byte[] prefix = BuildRARBytes(h => h
            .AddArchiveHeader()
            .AddFileHeader(archivedName, packedSize: (uint)sourceData.Length, unpackedSize: (uint)sourceData.Length));
        byte[] suffix = BuildRARBytes(h => h.AddEndArchive());
        return [.. prefix, .. sourceData, .. suffix];
    }

    private static byte[] BuildRARBytes(Action<RAR4HeaderBuilder> build)
    {
        using var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            build(new RAR4HeaderBuilder(writer));
        }

        return ms.ToArray();
    }

    [Fact]
    public async Task ReconstructAsync_NoHashes_ProducesExactExpectedBytes()
    {
        string srr = BuildSingleVolumeSRR("test.rar", "movie.mkv", SourcePayload);

        var reconstructor = new SRRReconstructor();
        (bool success, IReadOnlyList<string> writtenPaths) = await reconstructor.ReconstructAsync(
            srr, _inputDir, _outputDir, ["test.rar"], [], HashType.CRC32, CancellationToken.None);

        Assert.True(success);
        Assert.Equal([Path.Combine(_outputDir, "test.rar")], writtenPaths);
        // Byte-exact: headers replayed verbatim with the source payload spliced into place. This
        // catches a dropped/duplicated/misplaced payload, not just "a file was written".
        Assert.Equal(
            ExpectedReconstructedBytes("movie.mkv", SourcePayload),
            File.ReadAllBytes(Path.Combine(_outputDir, "test.rar")));
    }

    [Fact]
    public async Task ReconstructAsync_HashMatches_ReturnsTrue()
    {
        string srr = BuildSingleVolumeSRR("test.rar", "movie.mkv", SourcePayload);

        // Compute the expected CRC from the independently-assembled oracle bytes — not from the
        // reconstructor's output — so a match genuinely validates the verify path.
        string expectedRARPath = Path.Combine(TempDir, "oracle.rar");
        File.WriteAllBytes(expectedRARPath, ExpectedReconstructedBytes("movie.mkv", SourcePayload));
        string expectedCrc = CRC32.Calculate(expectedRARPath);

        var reconstructor = new SRRReconstructor();
        (bool success, IReadOnlyList<string> writtenPaths) = await reconstructor.ReconstructAsync(
            srr, _inputDir, _outputDir, ["test.rar"], [expectedCrc], HashType.CRC32, CancellationToken.None);

        Assert.True(success);
        Assert.Equal([Path.Combine(_outputDir, "test.rar")], writtenPaths);
    }

    [Fact]
    public async Task ReconstructAsync_HashMismatch_ReturnsFalse()
    {
        string srr = BuildSingleVolumeSRR("test.rar", "movie.mkv", SourcePayload);

        // A hash guaranteed different from the real one (derived from the oracle, not hard-coded).
        string expectedRARPath = Path.Combine(TempDir, "oracle.rar");
        File.WriteAllBytes(expectedRARPath, ExpectedReconstructedBytes("movie.mkv", SourcePayload));
        string realCrc = CRC32.Calculate(expectedRARPath);
        string wrongCrc = realCrc == "00000000" ? "ffffffff" : "00000000";

        var reconstructor = new SRRReconstructor();
        (bool success, IReadOnlyList<string> writtenPaths) = await reconstructor.ReconstructAsync(
            srr, _inputDir, _outputDir, ["test.rar"], [wrongCrc], HashType.CRC32, CancellationToken.None);

        Assert.False(success);
        Assert.True(File.Exists(Path.Combine(_outputDir, "test.rar")));
        // Written paths are still reported even on failure, so a caller can inspect/clean up.
        Assert.Equal([Path.Combine(_outputDir, "test.rar")], writtenPaths);
    }

    [Fact]
    public async Task ReconstructAsync_FewerVolumesWrittenThanExpected_ReturnsFalseEvenWhenAllWrittenHashesMatch()
    {
        // Regression for the bug this task fixes: the OLD condition was `allMatched &&
        // completedVolumes > 0`, which passed once ANY volumes were produced and verified — even
        // if the release actually expects MORE volumes than the SRR/reconstruction produced here.
        // Two volumes are written and both hash-verify, but the caller says the release expects
        // THREE — that must be reported as failure.
        File.WriteAllBytes(Path.Combine(_inputDir, "movie.cd1"), SourcePayload);
        File.WriteAllBytes(Path.Combine(_inputDir, "movie.cd2"), SourcePayload);

        string srr = new SRRTestDataBuilder()
            .AddSRRHeader("ReScene.Tests")
            .AddRARFileWithHeaders("vol1.rar", h => h
                .AddArchiveHeader()
                .AddFileHeader("movie.cd1", packedSize: (uint)SourcePayload.Length, unpackedSize: (uint)SourcePayload.Length)
                .AddEndArchive())
            .AddRARFileWithHeaders("vol2.rar", h => h
                .AddArchiveHeader()
                .AddFileHeader("movie.cd2", packedSize: (uint)SourcePayload.Length, unpackedSize: (uint)SourcePayload.Length)
                .AddEndArchive())
            .BuildToFile(TempDir, "partial.srr");

        var reconstructor = new SRRReconstructor();
        (bool success, IReadOnlyList<string> writtenPaths) = await reconstructor.ReconstructAsync(
            srr, _inputDir, _outputDir, ["vol1.rar", "vol2.rar", "vol3.rar"], [], HashType.CRC32, CancellationToken.None);

        Assert.False(success);
        Assert.Equal(
            [Path.Combine(_outputDir, "vol1.rar"), Path.Combine(_outputDir, "vol2.rar")],
            writtenPaths);
    }

    [Fact]
    public async Task ReconstructAsync_WrittenVolumeNameDoesNotMatchExpected_ReturnsFalseDespiteMatchingCount()
    {
        // Count alone is not sufficient: the expected name(s) must also match (normalized), so a
        // caller passing a mismatched/wrong expected-names list is caught rather than silently
        // accepted just because the COUNT happens to line up.
        string srr = BuildSingleVolumeSRR("test.rar", "movie.mkv", SourcePayload);

        var reconstructor = new SRRReconstructor();
        (bool success, IReadOnlyList<string> writtenPaths) = await reconstructor.ReconstructAsync(
            srr, _inputDir, _outputDir, ["completely-different-name.rar"], [], HashType.CRC32, CancellationToken.None);

        Assert.False(success);
        Assert.Equal([Path.Combine(_outputDir, "test.rar")], writtenPaths);
    }

    [Fact]
    public async Task ReconstructAsync_NoRARFileBlocks_ReturnsFalse()
    {
        // Header + stored file only — no 0x71 RAR-file block, so no volume is produced.
        string srr = new SRRTestDataBuilder()
            .AddSRRHeader("ReScene.Tests")
            .AddStoredFile("info.nfo", [1, 2, 3, 4])
            .BuildToFile(TempDir, "test.srr");

        var reconstructor = new SRRReconstructor();
        (bool success, IReadOnlyList<string> writtenPaths) = await reconstructor.ReconstructAsync(
            srr, _inputDir, _outputDir, [], [], HashType.CRC32, CancellationToken.None);

        Assert.False(success);
        Assert.Empty(writtenPaths);
    }

    [Fact]
    public async Task ReconstructAsync_TraversalRARName_RejectsAndWritesNothingOutsideOutputDir()
    {
        // A malicious SRR naming its volume "..\evil.rar" must not write outside the output
        // directory (path traversal / Zip-Slip). The escape target resolves to TempDir (the parent
        // of _outputDir), which the harness cleans up.
        const string archivedName = "movie.mkv";
        File.WriteAllBytes(Path.Combine(_inputDir, archivedName), SourcePayload);

        string srr = new SRRTestDataBuilder()
            .AddSRRHeader("ReScene.Tests")
            .AddRARFileWithHeaders(@"..\evil.rar", h => h
                .AddArchiveHeader()
                .AddFileHeader(archivedName, packedSize: (uint)SourcePayload.Length, unpackedSize: (uint)SourcePayload.Length)
                .AddEndArchive())
            .BuildToFile(TempDir, "traversal.srr");

        string escapedPath = Path.Combine(TempDir, "evil.rar");

        var reconstructor = new SRRReconstructor();
        await Assert.ThrowsAsync<InvalidDataException>(() => reconstructor.ReconstructAsync(
            srr, _inputDir, _outputDir, [@"..\evil.rar"], [], HashType.CRC32, CancellationToken.None));

        Assert.False(File.Exists(escapedPath),
            "SRR reconstruction wrote a RAR volume outside the output directory (path traversal).");
    }

    [Fact]
    public async Task ReconstructAsync_AlreadyCancelled_Throws()
    {
        byte[] source = [.. Enumerable.Range(0, 64).Select(i => (byte)i)];
        string srr = BuildSingleVolumeSRR("test.rar", "movie.mkv", source);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var reconstructor = new SRRReconstructor();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reconstructor.ReconstructAsync(
            srr, _inputDir, _outputDir, ["test.rar"], [], HashType.CRC32, cts.Token));
    }

    [Fact]
    public void FindSourceFile_DirectPath_Found()
    {
        File.WriteAllText(Path.Combine(_inputDir, "movie.mkv"), "data");
        Assert.Equal(
            Path.Combine(_inputDir, "movie.mkv"),
            SRRReconstructor.FindSourceFile(_inputDir, "movie.mkv"));
    }

    [Fact]
    public void FindSourceFile_FlatFallback_FindsByFileName()
    {
        // Archived with a subdir prefix, but the file sits flat in the input root.
        File.WriteAllText(Path.Combine(_inputDir, "movie.mkv"), "data");
        Assert.Equal(
            Path.Combine(_inputDir, "movie.mkv"),
            SRRReconstructor.FindSourceFile(_inputDir, "CD1/movie.mkv"));
    }

    [Fact]
    public void FindSourceFile_RecursiveSearch_FindsNested()
    {
        string nested = Path.Combine(_inputDir, "deep", "nested");
        Directory.CreateDirectory(nested);
        string expected = Path.Combine(nested, "movie.mkv");
        File.WriteAllText(expected, "data");

        Assert.Equal(expected, SRRReconstructor.FindSourceFile(_inputDir, "movie.mkv"));
    }

    [Fact]
    public void FindSourceFile_Missing_Throws()
        => Assert.Throws<FileNotFoundException>(() => SRRReconstructor.FindSourceFile(_inputDir, "absent.mkv"));

    [Fact]
    public void FindSourceFile_SubdirName_ResolvesThroughGuardedDirectPath()
    {
        // A legit subdirectory archived name must still resolve to the file inside the input dir
        // through the (now guard-gated) direct-path branch — the guard must not regress this.
        string cd1Dir = Path.Combine(_inputDir, "CD1");
        Directory.CreateDirectory(cd1Dir);
        string expected = Path.Combine(cd1Dir, "movie.mkv");
        File.WriteAllText(expected, "data");

        Assert.Equal(expected, SRRReconstructor.FindSourceFile(_inputDir, "CD1/movie.mkv"));
    }

    [Fact]
    public void FindSourceFile_AbsolutePathName_DoesNotResolveOutsideInputDir()
    {
        // An absolute archived name must not be honored verbatim (Path.Combine would return the
        // rooted path, discarding the input dir) — the containment guard rejects it.
        string outsidePath = Path.Combine(TempDir, "secret.key");
        File.WriteAllText(outsidePath, "sensitive");

        Assert.Throws<FileNotFoundException>(() =>
            SRRReconstructor.FindSourceFile(_inputDir, outsidePath));
    }

    [Fact]
    public void FindSourceFile_TraversalName_DoesNotResolveOutsideInputDir()
    {
        // A malicious archived name escaping the input directory must not resolve to (and later
        // have its bytes spliced from) a file outside it (path traversal / arbitrary read). The
        // external file exists — _inputDir is TempDir\input, so "..\secret.key" points at
        // TempDir\secret.key — but FindSourceFile must not return it.
        File.WriteAllText(Path.Combine(TempDir, "secret.key"), "sensitive");

        Assert.Throws<FileNotFoundException>(() =>
            SRRReconstructor.FindSourceFile(_inputDir, @"..\secret.key"));
    }

    [Fact]
    public async Task CopyBytesAsync_CopiesExactCount()
    {
        byte[] data = [.. Enumerable.Range(0, 100).Select(i => (byte)i)];
        using var source = new MemoryStream(data);
        using var dest = new MemoryStream();

        await SRRReconstructor.CopyBytesAsync(source, dest, 40, CancellationToken.None);

        Assert.Equal(40, dest.Length);
        Assert.Equal(40, source.Position);
        Assert.Equal(data[..40], dest.ToArray());
    }

    [Fact]
    public async Task CopyBytesAsync_SourceTooShort_Throws()
    {
        using var source = new MemoryStream([1, 2, 3, 4, 5]);
        using var dest = new MemoryStream();

        await Assert.ThrowsAsync<EndOfStreamException>(
            () => SRRReconstructor.CopyBytesAsync(source, dest, 40, CancellationToken.None));
    }

    [Fact]
    public async Task CopyBytesAsync_AlreadyCancelled_Throws()
    {
        using var source = new MemoryStream([.. Enumerable.Range(0, 100).Select(i => (byte)i)]);
        using var dest = new MemoryStream();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SRRReconstructor.CopyBytesAsync(source, dest, 40, cts.Token));
    }
}
