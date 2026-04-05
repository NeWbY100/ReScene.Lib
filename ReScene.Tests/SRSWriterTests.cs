using System.Buffers.Binary;
using System.Text;
using ReScene.SRS;

namespace ReScene.Tests;

/// <summary>
/// Tests for SRSWriter and SRS round-trip (create + parse with SRSFile).
/// Uses synthetic sample files to test each container format.
/// </summary>
public class SRSWriterTests : IDisposable
{
    private readonly string _tempDir;

    public SRSWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"srs_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch { }
        GC.SuppressFinalize(this);
    }

    #region AVI Tests

    [Fact]
    public async Task CreateAsync_AviSample_ProducesValidSrs()
    {
        string samplePath = BuildSyntheticAvi();
        string srsPath = Path.Combine(_tempDir, "test.srs");

        var writer = new SRSWriter();
        SrsCreationResult result = await writer.CreateAsync(srsPath, samplePath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(SRSContainerType.AVI, result.ContainerType);
        Assert.True(result.TrackCount > 0);
        Assert.True(result.SrsFileSize > 0);
        Assert.True(File.Exists(srsPath));
    }

    [Fact]
    public async Task CreateAsync_AviSample_RoundTripsViaSrsFile()
    {
        string samplePath = BuildSyntheticAvi();
        string srsPath = Path.Combine(_tempDir, "test.srs");

        var writer = new SRSWriter();
        SrsCreationResult result = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(result.Success, result.ErrorMessage);

        var parsed = SRSFile.Load(srsPath);
        Assert.Equal(SRSContainerType.AVI, parsed.ContainerType);
        Assert.NotNull(parsed.FileData);
        Assert.Equal("ReScene.NET", parsed.FileData!.AppName);
        Assert.Contains("test_sample.avi", parsed.FileData.FileName, StringComparison.Ordinal);
        Assert.Equal(result.SampleCrc32, parsed.FileData.Crc32);
        Assert.Equal((ulong)result.SampleSize, parsed.FileData.SampleSize);
        Assert.True(parsed.Tracks.Count > 0);

        foreach (SrsTrackDataBlock track in parsed.Tracks)
        {
            Assert.True(track.DataLength > 0);
            Assert.True(track.SignatureSize > 0);
            Assert.Equal(track.SignatureSize, (ushort)track.Signature.Length);
        }
    }

    #endregion

    #region MKV Tests

    [Fact]
    public async Task CreateAsync_MkvSample_ProducesValidSrs()
    {
        string samplePath = BuildSyntheticMkv();
        string srsPath = Path.Combine(_tempDir, "test.srs");

        var writer = new SRSWriter();
        SrsCreationResult result = await writer.CreateAsync(srsPath, samplePath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(SRSContainerType.MKV, result.ContainerType);
        Assert.True(result.TrackCount > 0);
    }

    [Fact]
    public async Task CreateAsync_MkvSample_RoundTripsViaSrsFile()
    {
        string samplePath = BuildSyntheticMkv();
        string srsPath = Path.Combine(_tempDir, "test.srs");

        var writer = new SRSWriter();
        SrsCreationResult result = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(result.Success, result.ErrorMessage);

        var parsed = SRSFile.Load(srsPath);
        Assert.Equal(SRSContainerType.MKV, parsed.ContainerType);
        Assert.NotNull(parsed.FileData);
        Assert.Equal(result.SampleCrc32, parsed.FileData!.Crc32);
        Assert.True(parsed.Tracks.Count > 0);
    }

    #endregion

    #region MP4 Tests

    [Fact]
    public async Task CreateAsync_Mp4Sample_ProducesValidSrs()
    {
        string samplePath = BuildSyntheticMp4();
        string srsPath = Path.Combine(_tempDir, "test.srs");

        var writer = new SRSWriter();
        SrsCreationResult result = await writer.CreateAsync(srsPath, samplePath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(SRSContainerType.MP4, result.ContainerType);
        Assert.True(result.TrackCount > 0);
    }

    [Fact]
    public async Task CreateAsync_Mp4Sample_RoundTripsViaSrsFile()
    {
        string samplePath = BuildSyntheticMp4();
        string srsPath = Path.Combine(_tempDir, "test.srs");

        var writer = new SRSWriter();
        SrsCreationResult result = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(result.Success, result.ErrorMessage);

        var parsed = SRSFile.Load(srsPath);
        Assert.Equal(SRSContainerType.MP4, parsed.ContainerType);
        Assert.NotNull(parsed.FileData);
        Assert.Equal(result.SampleCrc32, parsed.FileData!.Crc32);
    }

    #endregion

    #region FLAC Tests

    [Fact]
    public async Task CreateAsync_FlacSample_ProducesValidSrs()
    {
        string samplePath = BuildSyntheticFlac();
        string srsPath = Path.Combine(_tempDir, "test.srs");

        var writer = new SRSWriter();
        SrsCreationResult result = await writer.CreateAsync(srsPath, samplePath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(SRSContainerType.FLAC, result.ContainerType);
        Assert.True(result.TrackCount > 0);
    }

    [Fact]
    public async Task CreateAsync_FlacSample_RoundTripsViaSrsFile()
    {
        string samplePath = BuildSyntheticFlac();
        string srsPath = Path.Combine(_tempDir, "test.srs");

        var writer = new SRSWriter();
        SrsCreationResult result = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(result.Success, result.ErrorMessage);

        var parsed = SRSFile.Load(srsPath);
        Assert.Equal(SRSContainerType.FLAC, parsed.ContainerType);
        Assert.NotNull(parsed.FileData);
        Assert.Equal(result.SampleCrc32, parsed.FileData!.Crc32);
        Assert.True(parsed.Tracks.Count > 0);
    }

    #endregion

    #region MP3 Tests

    [Fact]
    public async Task CreateAsync_Mp3Sample_ProducesValidSrs()
    {
        string samplePath = BuildSyntheticMp3();
        string srsPath = Path.Combine(_tempDir, "test.srs");

        var writer = new SRSWriter();
        SrsCreationResult result = await writer.CreateAsync(srsPath, samplePath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(SRSContainerType.MP3, result.ContainerType);
        Assert.True(result.TrackCount > 0);
    }

    [Fact]
    public async Task CreateAsync_Mp3Sample_RoundTripsViaSrsFile()
    {
        string samplePath = BuildSyntheticMp3();
        string srsPath = Path.Combine(_tempDir, "test.srs");

        var writer = new SRSWriter();
        SrsCreationResult result = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(result.Success, result.ErrorMessage);

        var parsed = SRSFile.Load(srsPath);
        Assert.Equal(SRSContainerType.MP3, parsed.ContainerType);
        Assert.NotNull(parsed.FileData);
        Assert.Equal(result.SampleCrc32, parsed.FileData!.Crc32);
    }

    #endregion

    #region Stream Tests

    [Fact]
    public async Task CreateAsync_StreamSample_ProducesValidSrs()
    {
        string samplePath = BuildSyntheticStream();
        string srsPath = Path.Combine(_tempDir, "test.srs");

        var writer = new SRSWriter();
        SrsCreationResult result = await writer.CreateAsync(srsPath, samplePath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(SRSContainerType.Stream, result.ContainerType);
        Assert.True(result.TrackCount > 0);
    }

    [Fact]
    public async Task CreateAsync_StreamSample_RoundTripsViaSrsFile()
    {
        string samplePath = BuildSyntheticStream();
        string srsPath = Path.Combine(_tempDir, "test.srs");

        var writer = new SRSWriter();
        SrsCreationResult result = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(result.Success, result.ErrorMessage);

        var parsed = SRSFile.Load(srsPath);
        Assert.Equal(SRSContainerType.Stream, parsed.ContainerType);
        Assert.NotNull(parsed.FileData);
        Assert.Equal(result.SampleCrc32, parsed.FileData!.Crc32);
    }

    #endregion

    #region Edge Case Tests

    [Fact]
    public async Task CreateAsync_MissingFile_ReturnsError()
    {
        string srsPath = Path.Combine(_tempDir, "test.srs");

        var writer = new SRSWriter();
        SrsCreationResult result = await writer.CreateAsync(srsPath, "/nonexistent/file.avi");

        Assert.False(result.Success);
        Assert.Contains("not found", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_CancellationToken_StopsCreation()
    {
        string samplePath = BuildSyntheticAvi();
        string srsPath = Path.Combine(_tempDir, "test.srs");

        var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        var writer = new SRSWriter();
        SrsCreationResult result = await writer.CreateAsync(srsPath, samplePath, ct: cts.Token);

        Assert.False(result.Success);
        Assert.Contains("cancelled", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_CustomAppName_IsStoredInSrs()
    {
        string samplePath = BuildSyntheticAvi();
        string srsPath = Path.Combine(_tempDir, "test.srs");

        var writer = new SRSWriter();
        var options = new SrsCreationOptions { AppName = "TestApp 1.0" };
        SrsCreationResult result = await writer.CreateAsync(srsPath, samplePath, options);
        Assert.True(result.Success, result.ErrorMessage);

        var parsed = SRSFile.Load(srsPath);
        Assert.Equal("TestApp 1.0", parsed.FileData!.AppName);
    }

    [Fact]
    public void DetectContainerType_Avi_DetectsCorrectly()
    {
        string path = BuildSyntheticAvi();
        SRSContainerType type = SRSWriter.DetectContainerType(path);
        Assert.Equal(SRSContainerType.AVI, type);
    }

    [Fact]
    public void DetectContainerType_Mkv_DetectsCorrectly()
    {
        string path = BuildSyntheticMkv();
        SRSContainerType type = SRSWriter.DetectContainerType(path);
        Assert.Equal(SRSContainerType.MKV, type);
    }

    [Fact]
    public void DetectContainerType_Mp4_DetectsCorrectly()
    {
        string path = BuildSyntheticMp4();
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
    public void DetectContainerType_Mp3_DetectsCorrectly()
    {
        string path = BuildSyntheticMp3();
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
        string path = Path.Combine(_tempDir, "test_sample.vob");
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
        string path = Path.Combine(_tempDir, $"test_sample{extension}");
        File.WriteAllBytes(path, new byte[256]);
        SRSContainerType type = SRSWriter.DetectContainerType(path);
        Assert.Equal(SRSContainerType.Stream, type);
    }

    [Theory]
    [InlineData(".mov")]
    [InlineData(".m4v")]
    public void DetectContainerType_QuickTimeExtensions_DetectAsMp4(string extension)
    {
        // MOV/M4V files without ftyp atom fall back to extension-based detection
        string path = Path.Combine(_tempDir, $"test_sample{extension}");
        byte[] data = new byte[256];
        data[0] = 0x00;
        data[1] = 0x00; // not ftyp
        File.WriteAllBytes(path, data);
        SRSContainerType type = SRSWriter.DetectContainerType(path);
        Assert.Equal(SRSContainerType.MP4, type);
    }

    [Fact]
    public async Task CreateAsync_ProgressEvents_AreFired()
    {
        string samplePath = BuildSyntheticAvi();
        string srsPath = Path.Combine(_tempDir, "test.srs");

        var writer = new SRSWriter();
        var messages = new List<string>();
        writer.Progress += (_, e) => messages.Add(e.Message);

        SrsCreationResult result = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(messages.Count > 0);
        Assert.Contains(messages, m => m.Contains("Detected", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("complete", StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Synthetic File Builders

    /// <summary>
    /// Builds a minimal valid AVI file:
    /// RIFF AVI { LIST hdrl { ... }, LIST movi { 00dc(data), 01wb(data) } }
    /// </summary>
    private string BuildSyntheticAvi()
    {
        string path = Path.Combine(_tempDir, "test_sample.avi");
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // Build inner content first to get sizes
        var moviContent = new MemoryStream();
        var moviWriter = new BinaryWriter(moviContent);

        // Video chunk 00dc
        byte[] videoData = CreateTestData(512);
        moviWriter.Write(Encoding.ASCII.GetBytes("00dc"));
        moviWriter.Write((uint)videoData.Length);
        moviWriter.Write(videoData);

        // Audio chunk 01wb
        byte[] audioData = CreateTestData(256);
        moviWriter.Write(Encoding.ASCII.GetBytes("01wb"));
        moviWriter.Write((uint)audioData.Length);
        moviWriter.Write(audioData);

        byte[] moviBytes = moviContent.ToArray();

        // Build hdrl (minimal: just an avih chunk)
        var hdrlContent = new MemoryStream();
        var hdrlWriter = new BinaryWriter(hdrlContent);
        byte[] avihData = new byte[56]; // minimal avih
        hdrlWriter.Write(Encoding.ASCII.GetBytes("avih"));
        hdrlWriter.Write((uint)avihData.Length);
        hdrlWriter.Write(avihData);
        byte[] hdrlBytes = hdrlContent.ToArray();

        // Calculate total sizes
        uint hdrlSize = (uint)(4 + hdrlBytes.Length); // "hdrl" + children
        uint moviSize = (uint)(4 + moviBytes.Length); // "movi" + children
        uint riffSize = (uint)(4 + 8 + hdrlSize + 8 + moviSize); // "AVI " + LIST headers

        // Write RIFF header
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(riffSize);
        bw.Write(Encoding.ASCII.GetBytes("AVI "));

        // LIST hdrl
        bw.Write(Encoding.ASCII.GetBytes("LIST"));
        bw.Write(hdrlSize);
        bw.Write(Encoding.ASCII.GetBytes("hdrl"));
        bw.Write(hdrlBytes);

        // LIST movi
        bw.Write(Encoding.ASCII.GetBytes("LIST"));
        bw.Write(moviSize);
        bw.Write(Encoding.ASCII.GetBytes("movi"));
        bw.Write(moviBytes);

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    /// <summary>
    /// Builds a minimal MKV file with EBML header + Segment + Cluster + SimpleBlock.
    /// </summary>
    private string BuildSyntheticMkv()
    {
        string path = Path.Combine(_tempDir, "test_sample.mkv");
        using var ms = new MemoryStream();

        // EBML Header element (ID: 0x1A45DFA3)
        byte[] ebmlContent = BuildEbmlHeaderContent();
        WriteEbmlElement(ms, 0x1A45DFA3, ebmlContent);

        // Segment (ID: 0x18538067) containing a Cluster with SimpleBlocks
        var segContent = new MemoryStream();

        // Cluster (ID: 0x1F43B675)
        var clusterContent = new MemoryStream();

        // SimpleBlock (ID: 0xA3): track=1, timecode=0, flags=0x80, then data
        byte[] blockData = CreateTestData(512);
        byte[] simpleBlockPayload = new byte[1 + 2 + 1 + blockData.Length]; // trackVint(1) + timecode(2) + flags(1) + data
        simpleBlockPayload[0] = 0x81; // Track 1 as VINT
        simpleBlockPayload[1] = 0; // Timecode MSB
        simpleBlockPayload[2] = 0; // Timecode LSB
        simpleBlockPayload[3] = 0x80; // Flags (keyframe)
        blockData.CopyTo(simpleBlockPayload, 4);
        WriteEbmlElement(clusterContent, 0xA3, simpleBlockPayload);

        // Second SimpleBlock for track 2
        byte[] blockData2 = CreateTestData(256);
        byte[] simpleBlockPayload2 = new byte[1 + 2 + 1 + blockData2.Length];
        simpleBlockPayload2[0] = 0x82; // Track 2 as VINT
        simpleBlockPayload2[1] = 0;
        simpleBlockPayload2[2] = 0;
        simpleBlockPayload2[3] = 0x80;
        blockData2.CopyTo(simpleBlockPayload2, 4);
        WriteEbmlElement(clusterContent, 0xA3, simpleBlockPayload2);

        WriteEbmlElement(segContent, 0x1F43B675, clusterContent.ToArray());

        WriteEbmlElement(ms, 0x18538067, segContent.ToArray());

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    /// <summary>
    /// Builds a minimal MP4 file: ftyp + moov + mdat
    /// </summary>
    private string BuildSyntheticMp4()
    {
        string path = Path.Combine(_tempDir, "test_sample.mp4");
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // ftyp atom
        byte[] ftypData = Encoding.ASCII.GetBytes("isom\x00\x00\x02\x00isomiso2mp41");
        WriteAtomBE(bw, "ftyp", ftypData);

        // moov atom (minimal)
        byte[] moovData = new byte[32];
        WriteAtomBE(bw, "moov", moovData);

        // mdat atom with stream data
        byte[] mdatData = CreateTestData(1024);
        WriteAtomBE(bw, "mdat", mdatData);

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    /// <summary>
    /// Builds a minimal FLAC file: fLaC marker + STREAMINFO block + frame data.
    /// </summary>
    private string BuildSyntheticFlac()
    {
        string path = Path.Combine(_tempDir, "test_sample.flac");
        using var ms = new MemoryStream();

        // fLaC marker
        ms.Write(Encoding.ASCII.GetBytes("fLaC"));

        // STREAMINFO metadata block (type=0, last=true)
        byte[] streamInfo = new byte[34]; // Standard STREAMINFO size
        byte header = 0x80; // is_last=1, type=0
        ms.WriteByte(header);
        ms.WriteByte(0);
        ms.WriteByte(0);
        ms.WriteByte(34); // BE24 size

        ms.Write(streamInfo);

        // Frame data (simulated)
        byte[] frameData = CreateTestData(512);
        ms.Write(frameData);

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    /// <summary>
    /// Builds a minimal MP3 file: ID3v2 header + audio sync frames.
    /// </summary>
    private string BuildSyntheticMp3()
    {
        string path = Path.Combine(_tempDir, "test_sample.mp3");
        using var ms = new MemoryStream();

        // ID3v2 header
        ms.Write(Encoding.ASCII.GetBytes("ID3"));
        ms.WriteByte(3); // version major
        ms.WriteByte(0); // version minor
        ms.WriteByte(0); // flags

        // ID3v2 size (syncsafe, 4 bytes) = 10 bytes of ID3 payload
        int id3Payload = 10;
        ms.WriteByte(0);
        ms.WriteByte(0);
        ms.WriteByte(0);
        ms.WriteByte((byte)id3Payload);

        // ID3 payload
        ms.Write(new byte[id3Payload]);

        // MP3 sync frames (0xFF 0xFB = MPEG1 Layer3)
        byte[] audioData = CreateTestData(512);
        audioData[0] = 0xFF;
        audioData[1] = 0xFB;
        ms.Write(audioData);

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    /// <summary>
    /// Builds a minimal VOB/stream file.
    /// </summary>
    private string BuildSyntheticStream()
    {
        string path = Path.Combine(_tempDir, "test_sample.vob");
        byte[] data = CreateTestData(1024);
        File.WriteAllBytes(path, data);
        return path;
    }

    #endregion

    #region Helpers

    private static byte[] CreateTestData(int size)
    {
        var data = new byte[size];
        var rng = new Random(42); // deterministic for reproducibility
        rng.NextBytes(data);
        return data;
    }

    private static void WriteEbmlElement(Stream stream, ulong id, byte[] data)
    {
        // Write ID
        byte[] idBytes = EncodeEbmlId(id);
        stream.Write(idBytes);

        // Write size as VINT
        byte[] sizeBytes = EncodeEbmlSize(data.Length);
        stream.Write(sizeBytes);

        // Write data
        stream.Write(data);
    }

    private static byte[] EncodeEbmlId(ulong id)
    {
        if (id < 0x100)
        {
            return [(byte)id];
        }

        if (id < 0x10000)
        {
            return [(byte)(id >> 8), (byte)(id & 0xFF)];
        }

        if (id < 0x1000000)
        {
            return [(byte)(id >> 16), (byte)((id >> 8) & 0xFF), (byte)(id & 0xFF)];
        }

        return [(byte)(id >> 24), (byte)((id >> 16) & 0xFF), (byte)((id >> 8) & 0xFF), (byte)(id & 0xFF)];
    }

    private static byte[] EncodeEbmlSize(long value)
    {
        if (value < 0x7F)
        {
            return [(byte)(0x80 | value)];
        }

        if (value < 0x3FFF)
        {
            return [(byte)(0x40 | (value >> 8)), (byte)(value & 0xFF)];
        }

        if (value < 0x1FFFFF)
        {
            return [(byte)(0x20 | (value >> 16)), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF)];
        }

        return [(byte)(0x10 | (value >> 24)), (byte)((value >> 16) & 0xFF), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF)];
    }

    private static void WriteAtomBE(BinaryWriter bw, string type, byte[] data)
    {
        uint totalSize = (uint)(8 + data.Length);
        Span<byte> sizeBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(sizeBytes, totalSize);
        bw.Write(sizeBytes);
        bw.Write(Encoding.ASCII.GetBytes(type));
        bw.Write(data);
    }

    private static byte[] BuildEbmlHeaderContent()
    {
        // Minimal EBML header: version=1, read version=1, max id length=4, max size length=8, doctype=matroska
        var ms = new MemoryStream();
        // EBMLVersion (0x4286) = 1
        WriteEbmlElement(ms, 0x4286, [1]);
        // EBMLReadVersion (0x42F7) = 1
        WriteEbmlElement(ms, 0x42F7, [1]);
        // EBMLMaxIDLength (0x42F2) = 4
        WriteEbmlElement(ms, 0x42F2, [4]);
        // EBMLMaxSizeLength (0x42F3) = 8
        WriteEbmlElement(ms, 0x42F3, [8]);
        // DocType (0x4282) = "matroska"
        WriteEbmlElement(ms, 0x4282, Encoding.ASCII.GetBytes("matroska"));
        return ms.ToArray();
    }

    #endregion
}
