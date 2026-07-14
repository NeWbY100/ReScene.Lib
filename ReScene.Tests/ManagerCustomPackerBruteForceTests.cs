using System.Text;
using ReScene.Core;
using ReScene.Core.Cryptography;

namespace ReScene.Tests;

/// <summary>
/// Tests for <see cref="Manager.BruteForceRARVersionAsync"/>'s direct SRR (custom-packer)
/// reconstruction branch — the one path that never launches rar.exe, so it can be exercised
/// end-to-end. Verifies the branch surfaces <see cref="BruteForceRunResult.CustomPackerFiles"/>
/// (the full written set) with <see cref="BruteForceRunResult.Matches"/> empty and
/// <see cref="BruteForceRunResult.Combo"/> null, on both success and failure.
/// </summary>
public class ManagerCustomPackerBruteForceTests : TempDirTestBase
{
    private readonly string _inputDir;
    private readonly string _outputDir;

    public ManagerCustomPackerBruteForceTests()
    {
        _inputDir = Path.Combine(TempDir, "input");
        _outputDir = Path.Combine(TempDir, "output");
        Directory.CreateDirectory(_inputDir);
        Directory.CreateDirectory(_outputDir);
    }

    private static readonly byte[] SourcePayload = [.. Enumerable.Range(0, 64).Select(i => (byte)i)];

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

    private BruteForceOptions MakeCustomPackerOptions(string srrPath, HashSet<string> hashes)
    {
        var options = new BruteForceOptions("unused-winrar-dir", _inputDir, _outputDir)
        {
            RAROptions = new RAROptions
            {
                CustomPackerDetected = ReScene.SRR.CustomPackerType.AllOnesWithLargeFlag,
                SRRFilePath = srrPath,
                OriginalRARFileNames = ["test.rar"],
            },
            HashType = HashType.CRC32,
        };
        foreach (string hash in hashes)
        {
            options.Hashes.Add(hash);
        }

        return options;
    }

    [Fact]
    public async Task BruteForceRARVersionAsync_CustomPackerSuccess_ReturnsCustomPackerFilesWithNoMatchesOrCombo()
    {
        string srr = BuildSingleVolumeSRR("test.rar", "movie.mkv", SourcePayload);
        string expectedRARPath = Path.Combine(TempDir, "oracle.rar");
        File.WriteAllBytes(expectedRARPath, ExpectedReconstructedBytes("movie.mkv", SourcePayload));
        string expectedCrc = CRC32.Calculate(expectedRARPath);

        BruteForceOptions options = MakeCustomPackerOptions(srr, [expectedCrc]);

        using var manager = new Manager();
        BruteForceRunResult result = await manager.BruteForceRARVersionAsync(options);

        Assert.True(result.Success);
        Assert.Null(result.Combo);
        Assert.Empty(result.Matches);
        Assert.Equal([Path.Combine(_outputDir, "test.rar")], result.CustomPackerFiles);
    }

    [Fact]
    public async Task BruteForceRARVersionAsync_CustomPackerHashMismatch_ReturnsEmptyCustomPackerFilesAndFailure()
    {
        string srr = BuildSingleVolumeSRR("test.rar", "movie.mkv", SourcePayload);

        BruteForceOptions options = MakeCustomPackerOptions(srr, ["ffffffff"]); // guaranteed wrong

        using var manager = new Manager();
        BruteForceRunResult result = await manager.BruteForceRARVersionAsync(options);

        Assert.False(result.Success);
        Assert.Null(result.Combo);
        Assert.Empty(result.Matches);
        Assert.Empty(result.CustomPackerFiles);
    }
}
