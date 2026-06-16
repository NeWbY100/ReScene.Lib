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
    private string BuildSingleVolumeSrr(string rarName, string archivedName, byte[] sourceData)
    {
        File.WriteAllBytes(Path.Combine(_inputDir, archivedName), sourceData);

        var builder = new SRRTestDataBuilder()
            .AddSRRHeader("ReScene.Tests")
            .AddRarFileWithHeaders(rarName, h => h
                .AddArchiveHeader()
                .AddFileHeader(archivedName, packedSize: (uint)sourceData.Length, unpackedSize: (uint)sourceData.Length)
                .AddEndArchive());

        return builder.BuildToFile(TempDir, "test.srr");
    }

    /// <summary>
    /// Independently assembles the exact bytes a correct reconstruction must produce: the same RAR
    /// headers the SRR carries, with the source payload spliced in immediately after the file
    /// header (archive header + file header + payload + end archive). Built with the SAME builder
    /// calls/args as <see cref="BuildSingleVolumeSrr"/>, but not via the reconstructor — so it is a
    /// genuine oracle, not the reconstructor's own output fed back.
    /// </summary>
    private static byte[] ExpectedReconstructedBytes(string archivedName, byte[] sourceData)
    {
        byte[] prefix = BuildRarBytes(h => h
            .AddArchiveHeader()
            .AddFileHeader(archivedName, packedSize: (uint)sourceData.Length, unpackedSize: (uint)sourceData.Length));
        byte[] suffix = BuildRarBytes(h => h.AddEndArchive());
        return [.. prefix, .. sourceData, .. suffix];
    }

    private static byte[] BuildRarBytes(Action<RAR4HeaderBuilder> build)
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
        string srr = BuildSingleVolumeSrr("test.rar", "movie.mkv", SourcePayload);

        var reconstructor = new SRRReconstructor();
        bool result = await reconstructor.ReconstructAsync(
            srr, _inputDir, _outputDir, ["test.rar"], [], HashType.CRC32, CancellationToken.None);

        Assert.True(result);
        // Byte-exact: headers replayed verbatim with the source payload spliced into place. This
        // catches a dropped/duplicated/misplaced payload, not just "a file was written".
        Assert.Equal(
            ExpectedReconstructedBytes("movie.mkv", SourcePayload),
            File.ReadAllBytes(Path.Combine(_outputDir, "test.rar")));
    }

    [Fact]
    public async Task ReconstructAsync_HashMatches_ReturnsTrue()
    {
        string srr = BuildSingleVolumeSrr("test.rar", "movie.mkv", SourcePayload);

        // Compute the expected CRC from the independently-assembled oracle bytes — not from the
        // reconstructor's output — so a match genuinely validates the verify path.
        string expectedRarPath = Path.Combine(TempDir, "oracle.rar");
        File.WriteAllBytes(expectedRarPath, ExpectedReconstructedBytes("movie.mkv", SourcePayload));
        string expectedCrc = CRC32.Calculate(expectedRarPath);

        var reconstructor = new SRRReconstructor();
        bool result = await reconstructor.ReconstructAsync(
            srr, _inputDir, _outputDir, ["test.rar"], [expectedCrc], HashType.CRC32, CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task ReconstructAsync_HashMismatch_ReturnsFalse()
    {
        string srr = BuildSingleVolumeSrr("test.rar", "movie.mkv", SourcePayload);

        // A hash guaranteed different from the real one (derived from the oracle, not hard-coded).
        string expectedRarPath = Path.Combine(TempDir, "oracle.rar");
        File.WriteAllBytes(expectedRarPath, ExpectedReconstructedBytes("movie.mkv", SourcePayload));
        string realCrc = CRC32.Calculate(expectedRarPath);
        string wrongCrc = realCrc == "00000000" ? "ffffffff" : "00000000";

        var reconstructor = new SRRReconstructor();
        bool result = await reconstructor.ReconstructAsync(
            srr, _inputDir, _outputDir, ["test.rar"], [wrongCrc], HashType.CRC32, CancellationToken.None);

        Assert.False(result);
        Assert.True(File.Exists(Path.Combine(_outputDir, "test.rar")));
    }

    [Fact]
    public async Task ReconstructAsync_NoRarFileBlocks_ReturnsFalse()
    {
        // Header + stored file only — no 0x71 RAR-file block, so no volume is produced.
        string srr = new SRRTestDataBuilder()
            .AddSRRHeader("ReScene.Tests")
            .AddStoredFile("info.nfo", [1, 2, 3, 4])
            .BuildToFile(TempDir, "test.srr");

        var reconstructor = new SRRReconstructor();
        bool result = await reconstructor.ReconstructAsync(
            srr, _inputDir, _outputDir, [], [], HashType.CRC32, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task ReconstructAsync_AlreadyCancelled_Throws()
    {
        byte[] source = [.. Enumerable.Range(0, 64).Select(i => (byte)i)];
        string srr = BuildSingleVolumeSrr("test.rar", "movie.mkv", source);

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
