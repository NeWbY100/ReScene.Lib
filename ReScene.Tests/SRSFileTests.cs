using ReScene.SRS;
using System.Buffers.Binary;
using System.Text;

namespace ReScene.Tests;

/// <summary>
/// Dedicated tests for SRSFile.Load() parsing across all container formats.
/// Uses SRSWriter to create temp SRS files, then validates SRSFile parses them correctly.
/// </summary>
public class SRSFileTests : IDisposable
{
    private readonly string _tempDir;

    public SRSFileTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"srsfile_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    #region Container Type Detection

    [Fact]
    public async Task Load_AviSrs_DetectsContainerType()
    {
        string srsPath = await CreateSrsFromSynthetic(BuildSyntheticAvi("avi_detect.avi"), "avi_detect.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.Equal(SRSContainerType.AVI, srs.ContainerType);
    }

    [Fact]
    public async Task Load_MkvSrs_DetectsContainerType()
    {
        string srsPath = await CreateSrsFromSynthetic(BuildSyntheticMkv("mkv_detect.mkv"), "mkv_detect.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.Equal(SRSContainerType.MKV, srs.ContainerType);
    }

    [Fact]
    public async Task Load_Mp4Srs_DetectsContainerType()
    {
        string srsPath = await CreateSrsFromSynthetic(BuildSyntheticMp4("mp4_detect.mp4"), "mp4_detect.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.Equal(SRSContainerType.MP4, srs.ContainerType);
    }

    [Fact]
    public async Task Load_FlacSrs_DetectsContainerType()
    {
        string srsPath = await CreateSrsFromSynthetic(BuildSyntheticFlac("flac_detect.flac"), "flac_detect.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.Equal(SRSContainerType.FLAC, srs.ContainerType);
    }

    [Fact]
    public async Task Load_Mp3Srs_DetectsContainerType()
    {
        string srsPath = await CreateSrsFromSynthetic(BuildSyntheticMp3("mp3_detect.mp3"), "mp3_detect.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.Equal(SRSContainerType.MP3, srs.ContainerType);
    }

    [Fact]
    public async Task Load_StreamSrs_DetectsContainerType()
    {
        string srsPath = await CreateSrsFromSynthetic(BuildSyntheticStream("stream_detect.vob"), "stream_detect.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.Equal(SRSContainerType.Stream, srs.ContainerType);
    }

    #endregion

    #region FileData Block Properties

    [Fact]
    public async Task Load_AviSrs_HasFileDataBlock()
    {
        string srsPath = await CreateSrsFromSynthetic(BuildSyntheticAvi("avi_fd.avi"), "avi_fd.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.NotNull(srs.FileData);
    }

    [Fact]
    public async Task Load_AviSrs_FileDataHasCorrectFileName()
    {
        string samplePath = BuildSyntheticAvi("avi_fname.avi");
        string srsPath = await CreateSrsFromSynthetic(samplePath, "avi_fname.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.NotNull(srs.FileData);
        Assert.Contains("avi_fname.avi", srs.FileData!.FileName);
    }

    [Fact]
    public async Task Load_AviSrs_FileDataHasCorrectAppName()
    {
        string samplePath = BuildSyntheticAvi("avi_app.avi");
        string srsPath = Path.Combine(_tempDir, "avi_app.srs");

        var writer = new SRSWriter();
        var options = new SrsCreationOptions { AppName = "TestSRSApp" };
        var result = await writer.CreateAsync(srsPath, samplePath, options);
        Assert.True(result.Success, result.ErrorMessage);

        var srs = SRSFile.Load(srsPath);

        Assert.NotNull(srs.FileData);
        Assert.Equal("TestSRSApp", srs.FileData!.AppName);
    }

    [Fact]
    public async Task Load_AviSrs_FileDataHasCorrectSampleSize()
    {
        string samplePath = BuildSyntheticAvi("avi_size.avi");
        long expectedSize = new FileInfo(samplePath).Length;
        string srsPath = await CreateSrsFromSynthetic(samplePath, "avi_size.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.NotNull(srs.FileData);
        Assert.Equal((ulong)expectedSize, srs.FileData!.SampleSize);
    }

    [Fact]
    public async Task Load_AviSrs_FileDataHasCorrectCrc32()
    {
        string samplePath = BuildSyntheticAvi("avi_crc.avi");
        string srsPath = Path.Combine(_tempDir, "avi_crc.srs");

        var writer = new SRSWriter();
        var result = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(result.Success, result.ErrorMessage);

        var srs = SRSFile.Load(srsPath);

        Assert.NotNull(srs.FileData);
        Assert.Equal(result.SampleCrc32, srs.FileData!.Crc32);
    }

    [Fact]
    public async Task Load_AviSrs_FileDataDefaultAppNameIsReSceneNET()
    {
        string srsPath = await CreateSrsFromSynthetic(BuildSyntheticAvi("avi_defapp.avi"), "avi_defapp.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.NotNull(srs.FileData);
        Assert.Equal("ReScene.NET", srs.FileData!.AppName);
    }

    #endregion

    #region Track Data Block Properties

    [Fact]
    public async Task Load_AviSrs_HasAtLeastOneTrack()
    {
        string srsPath = await CreateSrsFromSynthetic(BuildSyntheticAvi("avi_tracks.avi"), "avi_tracks.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.True(srs.Tracks.Count > 0);
    }

    [Fact]
    public async Task Load_AviSrs_TracksHaveNonZeroDataLength()
    {
        string srsPath = await CreateSrsFromSynthetic(BuildSyntheticAvi("avi_trklen.avi"), "avi_trklen.srs");

        var srs = SRSFile.Load(srsPath);

        foreach (var track in srs.Tracks)
        {
            Assert.True(track.DataLength > 0, $"Track {track.TrackNumber} has zero DataLength");
        }
    }

    [Fact]
    public async Task Load_AviSrs_TracksHave256ByteSignature()
    {
        string srsPath = await CreateSrsFromSynthetic(BuildSyntheticAvi("avi_sig.avi"), "avi_sig.srs");

        var srs = SRSFile.Load(srsPath);

        foreach (var track in srs.Tracks)
        {
            Assert.Equal(256, track.SignatureSize);
            Assert.Equal(256, track.Signature.Length);
        }
    }

    [Fact]
    public async Task Load_AviSrs_SignatureBytesAreNonEmpty()
    {
        string srsPath = await CreateSrsFromSynthetic(BuildSyntheticAvi("avi_sigdata.avi"), "avi_sigdata.srs");

        var srs = SRSFile.Load(srsPath);

        foreach (var track in srs.Tracks)
        {
            Assert.False(track.Signature.All(b => b == 0),
                $"Track {track.TrackNumber} signature is all zeros");
        }
    }

    [Fact]
    public async Task Load_MkvSrs_TracksHave256ByteSignature()
    {
        string srsPath = await CreateSrsFromSynthetic(BuildSyntheticMkv("mkv_sig.mkv"), "mkv_sig.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.True(srs.Tracks.Count > 0);
        foreach (var track in srs.Tracks)
        {
            Assert.Equal(256, track.SignatureSize);
            Assert.Equal(256, track.Signature.Length);
        }
    }

    [Fact]
    public async Task Load_FlacSrs_TracksHave256ByteSignature()
    {
        string srsPath = await CreateSrsFromSynthetic(BuildSyntheticFlac("flac_sig.flac"), "flac_sig.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.True(srs.Tracks.Count > 0);
        foreach (var track in srs.Tracks)
        {
            Assert.Equal(256, track.SignatureSize);
            Assert.Equal(256, track.Signature.Length);
        }
    }

    #endregion

    #region Container Chunks

    [Fact]
    public async Task Load_AviSrs_HasContainerChunks()
    {
        string srsPath = await CreateSrsFromSynthetic(BuildSyntheticAvi("avi_chunks.avi"), "avi_chunks.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.True(srs.ContainerChunks.Count > 0);
    }

    [Fact]
    public async Task Load_AviSrs_ContainerChunksHaveRiffLabel()
    {
        string srsPath = await CreateSrsFromSynthetic(BuildSyntheticAvi("avi_riff.avi"), "avi_riff.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.Contains(srs.ContainerChunks, c => c.Label.Contains("RIFF"));
    }

    [Fact]
    public async Task Load_MkvSrs_HasContainerChunks()
    {
        string srsPath = await CreateSrsFromSynthetic(BuildSyntheticMkv("mkv_chunks.mkv"), "mkv_chunks.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.True(srs.ContainerChunks.Count > 0);
    }

    [Fact]
    public async Task Load_MkvSrs_ContainerChunksHaveEbmlLabel()
    {
        string srsPath = await CreateSrsFromSynthetic(BuildSyntheticMkv("mkv_ebml.mkv"), "mkv_ebml.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.Contains(srs.ContainerChunks, c => c.Label == "EBML");
    }

    [Fact]
    public async Task Load_FlacSrs_ContainerChunksHaveFlacMarker()
    {
        string srsPath = await CreateSrsFromSynthetic(BuildSyntheticFlac("flac_marker.flac"), "flac_marker.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.Contains(srs.ContainerChunks, c => c.Label == "fLaC");
    }

    [Fact]
    public async Task Load_ContainerChunks_HaveValidPositionsAndSizes()
    {
        string srsPath = await CreateSrsFromSynthetic(BuildSyntheticAvi("avi_pos.avi"), "avi_pos.srs");

        var srs = SRSFile.Load(srsPath);

        foreach (var chunk in srs.ContainerChunks)
        {
            Assert.True(chunk.BlockPosition >= 0, $"Chunk '{chunk.Label}' has negative BlockPosition");
            Assert.True(chunk.BlockSize > 0, $"Chunk '{chunk.Label}' has non-positive BlockSize");
            Assert.True(chunk.HeaderSize > 0, $"Chunk '{chunk.Label}' has non-positive HeaderSize");
            Assert.True(chunk.PayloadSize >= 0, $"Chunk '{chunk.Label}' has negative PayloadSize");
            Assert.False(string.IsNullOrEmpty(chunk.ChunkId), $"Chunk '{chunk.Label}' has empty ChunkId");
        }
    }

    #endregion

    #region Cross-Format Round-Trip (Theory)

    [Theory]
    [InlineData("avi", SRSContainerType.AVI)]
    [InlineData("mkv", SRSContainerType.MKV)]
    [InlineData("mp4", SRSContainerType.MP4)]
    [InlineData("flac", SRSContainerType.FLAC)]
    [InlineData("mp3", SRSContainerType.MP3)]
    [InlineData("stream", SRSContainerType.Stream)]
    public async Task Load_AllFormats_RoundTripsCorrectly(string format, SRSContainerType expectedType)
    {
        string samplePath = BuildSyntheticByFormat(format);
        string srsPath = Path.Combine(_tempDir, $"roundtrip_{format}.srs");

        var writer = new SRSWriter();
        var result = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(result.Success, result.ErrorMessage);

        var srs = SRSFile.Load(srsPath);

        Assert.Equal(expectedType, srs.ContainerType);
        Assert.NotNull(srs.FileData);
        Assert.Equal(result.SampleCrc32, srs.FileData!.Crc32);
        Assert.Equal((ulong)result.SampleSize, srs.FileData.SampleSize);
        Assert.True(srs.Tracks.Count > 0, $"Expected tracks for format {format}");
        Assert.Equal(result.TrackCount, srs.Tracks.Count);
    }

    [Theory]
    [InlineData("avi", SRSContainerType.AVI)]
    [InlineData("mkv", SRSContainerType.MKV)]
    [InlineData("flac", SRSContainerType.FLAC)]
    [InlineData("mp3", SRSContainerType.MP3)]
    [InlineData("stream", SRSContainerType.Stream)]
    public async Task Load_AllFormats_TracksHaveValidSignatures(string format, SRSContainerType _)
    {
        string samplePath = BuildSyntheticByFormat(format);
        string srsPath = Path.Combine(_tempDir, $"sig_{format}.srs");

        var writer = new SRSWriter();
        var result = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(result.Success, result.ErrorMessage);

        var srs = SRSFile.Load(srsPath);

        foreach (var track in srs.Tracks)
        {
            Assert.Equal(256, track.SignatureSize);
            Assert.Equal(256, track.Signature.Length);
            Assert.True(track.DataLength > 0);
        }
    }

    #endregion

    #region Error Handling

    [Fact]
    public void Load_NonExistentFile_ThrowsFileNotFoundException()
    {
        string fakePath = Path.Combine(_tempDir, "does_not_exist.srs");

        Assert.Throws<FileNotFoundException>(() => SRSFile.Load(fakePath));
    }

    [Fact]
    public void Load_FileTooSmall_ThrowsInvalidDataException()
    {
        string path = Path.Combine(_tempDir, "tiny.srs");
        File.WriteAllBytes(path, [0x00, 0x01]);

        Assert.Throws<InvalidDataException>(() => SRSFile.Load(path));
    }

    #endregion

    #region FileData Block Position/Offset Fields

    [Fact]
    public async Task Load_AviSrs_FileDataHasValidOffsets()
    {
        string srsPath = await CreateSrsFromSynthetic(BuildSyntheticAvi("avi_offsets.avi"), "avi_offsets.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.NotNull(srs.FileData);
        var fd = srs.FileData!;
        Assert.True(fd.BlockPosition >= 0);
        Assert.True(fd.BlockSize > 0);
        Assert.True(fd.FrameHeaderSize > 0);
        Assert.True(fd.AppNameSize > 0);
        Assert.True(fd.FileNameSize > 0);
    }

    [Fact]
    public async Task Load_AviSrs_TrackDataHasValidOffsets()
    {
        string srsPath = await CreateSrsFromSynthetic(BuildSyntheticAvi("avi_trkoff.avi"), "avi_trkoff.srs");

        var srs = SRSFile.Load(srsPath);

        foreach (var track in srs.Tracks)
        {
            Assert.True(track.BlockPosition >= 0);
            Assert.True(track.BlockSize > 0);
            Assert.True(track.FrameHeaderSize > 0);
            Assert.True(track.TrackNumberFieldSize is 2 or 4);
            Assert.True(track.DataLengthFieldSize is 4 or 8);
        }
    }

    #endregion

    #region Multiple Load Calls

    [Fact]
    public async Task Load_CalledTwice_ReturnsSameResults()
    {
        string srsPath = await CreateSrsFromSynthetic(BuildSyntheticAvi("avi_multi.avi"), "avi_multi.srs");

        var srs1 = SRSFile.Load(srsPath);
        var srs2 = SRSFile.Load(srsPath);

        Assert.Equal(srs1.ContainerType, srs2.ContainerType);
        Assert.Equal(srs1.FileData!.Crc32, srs2.FileData!.Crc32);
        Assert.Equal(srs1.FileData.SampleSize, srs2.FileData.SampleSize);
        Assert.Equal(srs1.FileData.FileName, srs2.FileData.FileName);
        Assert.Equal(srs1.FileData.AppName, srs2.FileData.AppName);
        Assert.Equal(srs1.Tracks.Count, srs2.Tracks.Count);
        Assert.Equal(srs1.ContainerChunks.Count, srs2.ContainerChunks.Count);
    }

    #endregion

    #region Helpers

    private async Task<string> CreateSrsFromSynthetic(string samplePath, string srsFileName)
    {
        string srsPath = Path.Combine(_tempDir, srsFileName);
        var writer = new SRSWriter();
        var result = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(result.Success, result.ErrorMessage);
        return srsPath;
    }

    private string BuildSyntheticByFormat(string format) => format switch
    {
        "avi" => BuildSyntheticAvi($"rt_{format}.avi"),
        "mkv" => BuildSyntheticMkv($"rt_{format}.mkv"),
        "mp4" => BuildSyntheticMp4($"rt_{format}.mp4"),
        "flac" => BuildSyntheticFlac($"rt_{format}.flac"),
        "mp3" => BuildSyntheticMp3($"rt_{format}.mp3"),
        "stream" => BuildSyntheticStream($"rt_{format}.vob"),
        _ => throw new ArgumentException($"Unknown format: {format}")
    };

    private static byte[] CreateTestData(int size)
    {
        var data = new byte[size];
        var rng = new Random(42);
        rng.NextBytes(data);
        return data;
    }

    #endregion

    #region Synthetic File Builders

    private string BuildSyntheticAvi(string fileName)
    {
        string path = Path.Combine(_tempDir, fileName);
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        var moviContent = new MemoryStream();
        var moviWriter = new BinaryWriter(moviContent);

        byte[] videoData = CreateTestData(512);
        moviWriter.Write(Encoding.ASCII.GetBytes("00dc"));
        moviWriter.Write((uint)videoData.Length);
        moviWriter.Write(videoData);

        byte[] audioData = CreateTestData(256);
        moviWriter.Write(Encoding.ASCII.GetBytes("01wb"));
        moviWriter.Write((uint)audioData.Length);
        moviWriter.Write(audioData);

        byte[] moviBytes = moviContent.ToArray();

        var hdrlContent = new MemoryStream();
        var hdrlWriter = new BinaryWriter(hdrlContent);
        byte[] avihData = new byte[56];
        hdrlWriter.Write(Encoding.ASCII.GetBytes("avih"));
        hdrlWriter.Write((uint)avihData.Length);
        hdrlWriter.Write(avihData);
        byte[] hdrlBytes = hdrlContent.ToArray();

        uint hdrlSize = (uint)(4 + hdrlBytes.Length);
        uint moviSize = (uint)(4 + moviBytes.Length);
        uint riffSize = (uint)(4 + 8 + hdrlSize + 8 + moviSize);

        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(riffSize);
        bw.Write(Encoding.ASCII.GetBytes("AVI "));

        bw.Write(Encoding.ASCII.GetBytes("LIST"));
        bw.Write(hdrlSize);
        bw.Write(Encoding.ASCII.GetBytes("hdrl"));
        bw.Write(hdrlBytes);

        bw.Write(Encoding.ASCII.GetBytes("LIST"));
        bw.Write(moviSize);
        bw.Write(Encoding.ASCII.GetBytes("movi"));
        bw.Write(moviBytes);

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    private string BuildSyntheticMkv(string fileName)
    {
        string path = Path.Combine(_tempDir, fileName);
        using var ms = new MemoryStream();

        byte[] ebmlContent = BuildEbmlHeaderContent();
        WriteEbmlElement(ms, 0x1A45DFA3, ebmlContent);

        var segContent = new MemoryStream();
        var clusterContent = new MemoryStream();

        byte[] blockData = CreateTestData(512);
        byte[] simpleBlockPayload = new byte[1 + 2 + 1 + blockData.Length];
        simpleBlockPayload[0] = 0x81;
        simpleBlockPayload[3] = 0x80;
        blockData.CopyTo(simpleBlockPayload, 4);
        WriteEbmlElement(clusterContent, 0xA3, simpleBlockPayload);

        byte[] blockData2 = CreateTestData(256);
        byte[] simpleBlockPayload2 = new byte[1 + 2 + 1 + blockData2.Length];
        simpleBlockPayload2[0] = 0x82;
        simpleBlockPayload2[3] = 0x80;
        blockData2.CopyTo(simpleBlockPayload2, 4);
        WriteEbmlElement(clusterContent, 0xA3, simpleBlockPayload2);

        WriteEbmlElement(segContent, 0x1F43B675, clusterContent.ToArray());
        WriteEbmlElement(ms, 0x18538067, segContent.ToArray());

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    private string BuildSyntheticMp4(string fileName)
    {
        string path = Path.Combine(_tempDir, fileName);
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        byte[] ftypData = Encoding.ASCII.GetBytes("isom\x00\x00\x02\x00isomiso2mp41");
        WriteAtomBE(bw, "ftyp", ftypData);

        byte[] moovData = new byte[32];
        WriteAtomBE(bw, "moov", moovData);

        byte[] mdatData = CreateTestData(1024);
        WriteAtomBE(bw, "mdat", mdatData);

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    private string BuildSyntheticFlac(string fileName)
    {
        string path = Path.Combine(_tempDir, fileName);
        using var ms = new MemoryStream();

        ms.Write(Encoding.ASCII.GetBytes("fLaC"));

        byte[] streamInfo = new byte[34];
        byte header = 0x80;
        ms.WriteByte(header);
        ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(34);
        ms.Write(streamInfo);

        byte[] frameData = CreateTestData(512);
        ms.Write(frameData);

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    private string BuildSyntheticMp3(string fileName)
    {
        string path = Path.Combine(_tempDir, fileName);
        using var ms = new MemoryStream();

        ms.Write(Encoding.ASCII.GetBytes("ID3"));
        ms.WriteByte(3);
        ms.WriteByte(0);
        ms.WriteByte(0);

        int id3Payload = 10;
        ms.WriteByte(0);
        ms.WriteByte(0);
        ms.WriteByte(0);
        ms.WriteByte((byte)id3Payload);

        ms.Write(new byte[id3Payload]);

        byte[] audioData = CreateTestData(512);
        audioData[0] = 0xFF;
        audioData[1] = 0xFB;
        ms.Write(audioData);

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    private string BuildSyntheticStream(string fileName)
    {
        string path = Path.Combine(_tempDir, fileName);
        byte[] data = CreateTestData(1024);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static void WriteEbmlElement(Stream stream, ulong id, byte[] data)
    {
        byte[] idBytes = EncodeEbmlId(id);
        stream.Write(idBytes);
        byte[] sizeBytes = EncodeEbmlSize(data.Length);
        stream.Write(sizeBytes);
        stream.Write(data);
    }

    private static byte[] EncodeEbmlId(ulong id)
    {
        if (id < 0x100) return [(byte)id];
        if (id < 0x10000) return [(byte)(id >> 8), (byte)(id & 0xFF)];
        if (id < 0x1000000) return [(byte)(id >> 16), (byte)((id >> 8) & 0xFF), (byte)(id & 0xFF)];
        return [(byte)(id >> 24), (byte)((id >> 16) & 0xFF), (byte)((id >> 8) & 0xFF), (byte)(id & 0xFF)];
    }

    private static byte[] EncodeEbmlSize(long value)
    {
        if (value < 0x7F) return [(byte)(0x80 | value)];
        if (value < 0x3FFF) return [(byte)(0x40 | (value >> 8)), (byte)(value & 0xFF)];
        if (value < 0x1FFFFF) return [(byte)(0x20 | (value >> 16)), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF)];
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
        var ms = new MemoryStream();
        WriteEbmlElement(ms, 0x4286, [1]);
        WriteEbmlElement(ms, 0x42F7, [1]);
        WriteEbmlElement(ms, 0x42F2, [4]);
        WriteEbmlElement(ms, 0x42F3, [8]);
        WriteEbmlElement(ms, 0x4282, Encoding.ASCII.GetBytes("matroska"));
        return ms.ToArray();
    }

    #endregion
}
