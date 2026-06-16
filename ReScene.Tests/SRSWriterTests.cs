using System.Buffers.Binary;
using System.Text;
using ReScene.SRS;

namespace ReScene.Tests;

/// <summary>
/// Tests for SRSWriter and SRS round-trip (create + parse with SRSFile).
/// Uses synthetic sample files to test each container format.
/// </summary>
public class SRSWriterTests : TempDirTestBase
{

    #region Per-Format Create / Round-Trip Tests

    /// <summary>
    /// Maps a format key to its synthetic sample builder and expected container type.
    /// Drives the parameterized create/round-trip theories below.
    /// </summary>
    public static TheoryData<string, SRSContainerType> FormatCases() => new()
    {
        { "avi", SRSContainerType.AVI },
        { "mkv", SRSContainerType.MKV },
        { "mp4", SRSContainerType.MP4 },
        { "flac", SRSContainerType.FLAC },
        { "mp3", SRSContainerType.MP3 },
        { "stream", SRSContainerType.Stream },
    };

    private string BuildSyntheticByFormat(string format) => format switch
    {
        "avi" => BuildSyntheticAVI(),
        "mkv" => BuildSyntheticMKV(),
        "mp4" => BuildSyntheticMP4(),
        "flac" => BuildSyntheticFlac(),
        "mp3" => BuildSyntheticMP3(),
        "stream" => BuildSyntheticStream(),
        _ => throw new ArgumentException($"Unknown format: {format}"),
    };

    [Theory]
    [MemberData(nameof(FormatCases))]
    public async Task CreateAsync_Sample_ProducesValidSRS(string format, SRSContainerType expectedType)
    {
        string samplePath = BuildSyntheticByFormat(format);
        string srsPath = Path.Combine(TempDir, "test.srs");

        var writer = new SRSWriter();
        SRSCreationResult result = await writer.CreateAsync(srsPath, samplePath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(expectedType, result.ContainerType);
        Assert.True(result.TrackCount > 0);
        Assert.True(result.SRSFileSize > 0);
        Assert.True(File.Exists(srsPath));
    }

    [Theory]
    [InlineData("mkv", SRSContainerType.MKV)]
    [InlineData("mp4", SRSContainerType.MP4)]
    [InlineData("flac", SRSContainerType.FLAC)]
    [InlineData("mp3", SRSContainerType.MP3)]
    [InlineData("stream", SRSContainerType.Stream)]
    public async Task CreateAsync_Sample_RoundTripsViaSRSFile(string format, SRSContainerType expectedType)
    {
        string samplePath = BuildSyntheticByFormat(format);
        string srsPath = Path.Combine(TempDir, "test.srs");

        var writer = new SRSWriter();
        SRSCreationResult result = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(result.Success, result.ErrorMessage);

        var parsed = SRSFile.Load(srsPath);
        Assert.Equal(expectedType, parsed.ContainerType);
        Assert.NotNull(parsed.FileData);
        Assert.Equal(result.SampleCRC32, parsed.FileData!.CRC32);
        Assert.True(parsed.Tracks.Count > 0);
    }

    /// <summary>
    /// AVI keeps its own richer round-trip assertions (app name, file name,
    /// sample size, per-track signature checks).
    /// </summary>
    [Fact]
    public async Task CreateAsync_AVISample_RoundTripsViaSRSFile()
    {
        string samplePath = BuildSyntheticAVI();
        string srsPath = Path.Combine(TempDir, "test.srs");

        var writer = new SRSWriter();
        SRSCreationResult result = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(result.Success, result.ErrorMessage);

        var parsed = SRSFile.Load(srsPath);
        Assert.Equal(SRSContainerType.AVI, parsed.ContainerType);
        Assert.NotNull(parsed.FileData);
        Assert.Equal("ReScene.NET", parsed.FileData!.AppName);
        Assert.Contains("test_sample.avi", parsed.FileData.FileName, StringComparison.Ordinal);
        Assert.Equal(result.SampleCRC32, parsed.FileData.CRC32);
        Assert.Equal((ulong)result.SampleSize, parsed.FileData.SampleSize);
        Assert.True(parsed.Tracks.Count > 0);

        foreach (SRSTrackDataBlock track in parsed.Tracks)
        {
            Assert.True(track.DataLength > 0);
            Assert.True(track.SignatureSize > 0);
            Assert.Equal(track.SignatureSize, (ushort)track.Signature.Length);
        }
    }

    #endregion

    #region MP4 Edge-Case Tests

    [Fact]
    public async Task CreateAsync_MP4_ExtendedSizeMdat_RoundTripsViaSRSFile()
    {
        // mdat written with the extended 64-bit size form (size32 == 1), which
        // exercises the 16-byte atom header path in TryReadAtomHeader.
        string samplePath = BuildSyntheticMP4ExtendedSizeMdat();
        string srsPath = Path.Combine(TempDir, "test_ext.srs");

        var writer = new SRSWriter();
        SRSCreationResult result = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(SRSContainerType.MP4, result.ContainerType);
        Assert.True(result.TrackCount > 0);

        var parsed = SRSFile.Load(srsPath);
        Assert.Equal(SRSContainerType.MP4, parsed.ContainerType);
        Assert.NotNull(parsed.FileData);
        Assert.Equal(result.SampleCRC32, parsed.FileData!.CRC32);
        Assert.True(parsed.Tracks.Count > 0);
    }

    [Fact]
    public async Task CreateAsync_MP4_ZeroSizeMdat_RoundTripsViaSRSFile()
    {
        // mdat written with the to-EOF size form (size32 == 0), which exercises
        // the "extends to end of stream" path in TryReadAtomHeader.
        string samplePath = BuildSyntheticMP4ZeroSizeMdat();
        string srsPath = Path.Combine(TempDir, "test_zero.srs");

        var writer = new SRSWriter();
        SRSCreationResult result = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(SRSContainerType.MP4, result.ContainerType);
        Assert.True(result.TrackCount > 0);

        var parsed = SRSFile.Load(srsPath);
        Assert.Equal(SRSContainerType.MP4, parsed.ContainerType);
        Assert.NotNull(parsed.FileData);
        Assert.Equal(result.SampleCRC32, parsed.FileData!.CRC32);
        Assert.True(parsed.Tracks.Count > 0);
    }

    #endregion

    #region Edge Case Tests

    [Fact]
    public async Task CreateAsync_MissingFile_ReturnsError()
    {
        string srsPath = Path.Combine(TempDir, "test.srs");

        var writer = new SRSWriter();
        SRSCreationResult result = await writer.CreateAsync(srsPath, "/nonexistent/file.avi");

        Assert.False(result.Success);
        Assert.Contains("not found", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_CancellationToken_StopsCreation()
    {
        string samplePath = BuildSyntheticAVI();
        string srsPath = Path.Combine(TempDir, "test.srs");

        var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        var writer = new SRSWriter();
        SRSCreationResult result = await writer.CreateAsync(srsPath, samplePath, ct: cts.Token);

        Assert.False(result.Success);
        Assert.Contains("cancelled", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_CustomAppName_IsStoredInSRS()
    {
        string samplePath = BuildSyntheticAVI();
        string srsPath = Path.Combine(TempDir, "test.srs");

        var writer = new SRSWriter();
        var options = new SRSCreationOptions { AppName = "TestApp 1.0" };
        SRSCreationResult result = await writer.CreateAsync(srsPath, samplePath, options);
        Assert.True(result.Success, result.ErrorMessage);

        var parsed = SRSFile.Load(srsPath);
        Assert.Equal("TestApp 1.0", parsed.FileData!.AppName);
    }

    [Fact]
    public void DetectContainerType_AVI_DetectsCorrectly()
    {
        string path = BuildSyntheticAVI();
        SRSContainerType type = SRSWriter.DetectContainerType(path);
        Assert.Equal(SRSContainerType.AVI, type);
    }

    [Fact]
    public void DetectContainerType_MKV_DetectsCorrectly()
    {
        string path = BuildSyntheticMKV();
        SRSContainerType type = SRSWriter.DetectContainerType(path);
        Assert.Equal(SRSContainerType.MKV, type);
    }

    [Fact]
    public void DetectContainerType_MP4_DetectsCorrectly()
    {
        string path = BuildSyntheticMP4();
        SRSContainerType type = SRSWriter.DetectContainerType(path);
        Assert.Equal(SRSContainerType.MP4, type);
    }

    [Fact]
    public void DetectContainerType_Flac_DetectsCorrectly()
    {
        string path = BuildSyntheticFlac();
        SRSContainerType type = SRSWriter.DetectContainerType(path);
        Assert.Equal(SRSContainerType.FLAC, type);
    }

    [Fact]
    public void DetectContainerType_MP3_DetectsCorrectly()
    {
        string path = BuildSyntheticMP3();
        SRSContainerType type = SRSWriter.DetectContainerType(path);
        Assert.Equal(SRSContainerType.MP3, type);
    }

    [Fact]
    public void DetectContainerType_Stream_DetectsCorrectly()
    {
        string path = BuildSyntheticStream();
        SRSContainerType type = SRSWriter.DetectContainerType(path);
        Assert.Equal(SRSContainerType.Stream, type);
    }

    [Fact]
    public void DetectContainerType_VobStartingWithFF_DetectsAsStream()
    {
        // VOB files can start with 0xFF bytes which match the MP3 sync word (0xFFE0).
        // Ensure they are detected as Stream, not MP3.
        string path = Path.Combine(TempDir, "test_sample.vob");
        byte[] data = new byte[512];
        data[0] = 0xFF;
        data[1] = 0xFB; // would match MP3 sync word
        File.WriteAllBytes(path, data);

        SRSContainerType type = SRSWriter.DetectContainerType(path);
        Assert.Equal(SRSContainerType.Stream, type);
    }

    [Theory]
    [InlineData(".vob")]
    [InlineData(".mpeg")]
    [InlineData(".mpg")]
    [InlineData(".m2ts")]
    [InlineData(".ts")]
    [InlineData(".m2v")]
    [InlineData(".evo")]
    public void DetectContainerType_StreamExtensions_DetectAsStream(string extension)
    {
        string path = Path.Combine(TempDir, $"test_sample{extension}");
        File.WriteAllBytes(path, new byte[256]);
        SRSContainerType type = SRSWriter.DetectContainerType(path);
        Assert.Equal(SRSContainerType.Stream, type);
    }

    [Theory]
    [InlineData(".mov")]
    [InlineData(".m4v")]
    public void DetectContainerType_QuickTimeExtensions_DetectAsMP4(string extension)
    {
        // MOV/M4V files without ftyp atom fall back to extension-based detection
        string path = Path.Combine(TempDir, $"test_sample{extension}");
        byte[] data = new byte[256];
        data[0] = 0x00;
        data[1] = 0x00; // not ftyp
        File.WriteAllBytes(path, data);
        SRSContainerType type = SRSWriter.DetectContainerType(path);
        Assert.Equal(SRSContainerType.MP4, type);
    }

    [Fact]
    public async Task CreateAsync_MKV_WithMainFile_PopulatesMatchOffsets()
    {
        // Verify pyrescene-style -c behaviour: when a main file is provided,
        // each track's MatchOffset is set to its first-frame-data offset in
        // that main file. Using the sample as its own main file means the
        // expected offsets are the byte positions of each track's first
        // SimpleBlock frame data within the file.
        string samplePath = BuildSyntheticMKV();
        string srsPath = Path.Combine(TempDir, "test_matchoffset.srs");

        var writer = new SRSWriter();
        var options = new SRSCreationOptions { MainFilePath = samplePath };
        SRSCreationResult result = await writer.CreateAsync(srsPath, samplePath, options);
        Assert.True(result.Success, result.ErrorMessage);

        var parsed = SRSFile.Load(srsPath);
        Assert.True(parsed.Tracks.Count >= 2, "Expected at least 2 tracks");

        foreach (SRSTrackDataBlock t in parsed.Tracks)
        {
            Assert.True(t.MatchOffset > 0,
                $"Track {t.TrackNumber} MatchOffset should be > 0 when main file is provided, got 0x{t.MatchOffset:X}");
        }
    }

    [Fact]
    public async Task CreateAsync_MKV_WithoutMainFile_MatchOffsetsAreZero()
    {
        // Without -c, MatchOffset stays at 0 (mirrors pyrescene's default).
        string samplePath = BuildSyntheticMKV();
        string srsPath = Path.Combine(TempDir, "test_no_matchoffset.srs");

        var writer = new SRSWriter();
        SRSCreationResult result = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(result.Success, result.ErrorMessage);

        var parsed = SRSFile.Load(srsPath);
        foreach (SRSTrackDataBlock t in parsed.Tracks)
        {
            Assert.Equal(0UL, t.MatchOffset);
        }
    }

    [Fact]
    public async Task CreateAsync_MKV_WithNonexistentMainFile_WarnsButSucceeds()
    {
        // Main file path that doesn't exist should produce a warning, not a
        // failure — the SRS itself is still valid, just without MatchOffsets.
        string samplePath = BuildSyntheticMKV();
        string srsPath = Path.Combine(TempDir, "test_bad_main.srs");

        var writer = new SRSWriter();
        var options = new SRSCreationOptions { MainFilePath = @"Z:\does\not\exist.mkv" };
        SRSCreationResult result = await writer.CreateAsync(srsPath, samplePath, options);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains(result.Warnings, w => w.Contains("Main file not found", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateAsync_ProgressEvents_AreFired()
    {
        string samplePath = BuildSyntheticAVI();
        string srsPath = Path.Combine(TempDir, "test.srs");

        var writer = new SRSWriter();
        var messages = new List<string>();
        writer.Progress += (_, e) => messages.Add(e.Message);

        SRSCreationResult result = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(messages.Count > 0);
        Assert.Contains(messages, m => m.Contains("Detected", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("complete", StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Synthetic File Builders

    private string BuildSyntheticAVI() =>
        SyntheticSampleBuilder.BuildAvi(Path.Combine(TempDir, "test_sample.avi"));

    private string BuildSyntheticMKV() =>
        SyntheticSampleBuilder.BuildMkv(Path.Combine(TempDir, "test_sample.mkv"));

    private string BuildSyntheticMP4() =>
        SyntheticSampleBuilder.BuildMp4(Path.Combine(TempDir, "test_sample.mp4"));

    private string BuildSyntheticFlac() =>
        SyntheticSampleBuilder.BuildFlac(Path.Combine(TempDir, "test_sample.flac"));

    private string BuildSyntheticMP3() =>
        SyntheticSampleBuilder.BuildMp3(Path.Combine(TempDir, "test_sample.mp3"));

    private string BuildSyntheticStream() =>
        SyntheticSampleBuilder.BuildStream(Path.Combine(TempDir, "test_sample.vob"));

    /// <summary>
    /// Builds an MP4 whose mdat atom uses the extended 64-bit size form
    /// (32-bit size field == 1, followed by a 64-bit size). Exercises the
    /// 16-byte atom header path.
    /// </summary>
    private string BuildSyntheticMP4ExtendedSizeMdat()
    {
        string path = Path.Combine(TempDir, "test_ext_sample.mp4");
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        byte[] ftypData = Encoding.ASCII.GetBytes("isom\x00\x00\x02\x00isomiso2mp41");
        SyntheticSampleBuilder.WriteAtomBE(bw, "ftyp", ftypData);

        byte[] moovData = new byte[32];
        SyntheticSampleBuilder.WriteAtomBE(bw, "moov", moovData);

        // mdat atom with the extended 64-bit size form.
        byte[] mdatData = SyntheticSampleBuilder.CreateTestData(1024);
        WriteAtomExtendedSizeBE(bw, "mdat", mdatData);

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    /// <summary>
    /// Builds an MP4 whose final mdat atom uses the to-EOF size form
    /// (32-bit size field == 0, meaning the atom runs to the end of the file).
    /// </summary>
    private string BuildSyntheticMP4ZeroSizeMdat()
    {
        string path = Path.Combine(TempDir, "test_zero_sample.mp4");
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        byte[] ftypData = Encoding.ASCII.GetBytes("isom\x00\x00\x02\x00isomiso2mp41");
        SyntheticSampleBuilder.WriteAtomBE(bw, "ftyp", ftypData);

        byte[] moovData = new byte[32];
        SyntheticSampleBuilder.WriteAtomBE(bw, "moov", moovData);

        // mdat atom with the to-EOF size form: 32-bit size == 0, type, then payload.
        Span<byte> sizeBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(sizeBytes, 0);
        bw.Write(sizeBytes);
        bw.Write(Encoding.ASCII.GetBytes("mdat"));
        bw.Write(SyntheticSampleBuilder.CreateTestData(1024));

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Writes an atom using the extended 64-bit size form: a 32-bit size field of 1,
    /// the 4-character type, then a 64-bit size covering the full 16-byte header + payload.
    /// </summary>
    private static void WriteAtomExtendedSizeBE(BinaryWriter bw, string type, byte[] data)
    {
        Span<byte> size32 = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(size32, 1);
        bw.Write(size32);
        bw.Write(Encoding.ASCII.GetBytes(type));

        ulong totalSize = (ulong)(16 + data.Length);
        Span<byte> size64 = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(size64, totalSize);
        bw.Write(size64);

        bw.Write(data);
    }

    #endregion
}
