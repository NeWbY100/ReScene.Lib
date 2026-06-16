using ReScene.SRS;

namespace ReScene.Tests;

/// <summary>
/// Dedicated tests for SRSFile.Load() parsing across all container formats.
/// Uses SRSWriter to create temp SRS files, then validates SRSFile parses them correctly.
/// </summary>
public class SRSFileTests : TempDirTestBase
{
    #region Container Type Detection

    [Fact]
    public async Task Load_AVISRS_DetectsContainerType()
    {
        string srsPath = await CreateSRSFromSynthetic(BuildSyntheticAVI("avi_detect.avi"), "avi_detect.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.Equal(SRSContainerType.AVI, srs.ContainerType);
    }

    [Fact]
    public async Task Load_MKVSRS_DetectsContainerType()
    {
        string srsPath = await CreateSRSFromSynthetic(BuildSyntheticMKV("mkv_detect.mkv"), "mkv_detect.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.Equal(SRSContainerType.MKV, srs.ContainerType);
    }

    [Fact]
    public async Task Load_MP4SRS_DetectsContainerType()
    {
        string srsPath = await CreateSRSFromSynthetic(BuildSyntheticMP4("mp4_detect.mp4"), "mp4_detect.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.Equal(SRSContainerType.MP4, srs.ContainerType);
    }

    [Fact]
    public async Task Load_FlacSRS_DetectsContainerType()
    {
        string srsPath = await CreateSRSFromSynthetic(BuildSyntheticFlac("flac_detect.flac"), "flac_detect.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.Equal(SRSContainerType.FLAC, srs.ContainerType);
    }

    [Fact]
    public async Task Load_MP3SRS_DetectsContainerType()
    {
        string srsPath = await CreateSRSFromSynthetic(BuildSyntheticMP3("mp3_detect.mp3"), "mp3_detect.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.Equal(SRSContainerType.MP3, srs.ContainerType);
    }

    [Fact]
    public async Task Load_StreamSRS_DetectsContainerType()
    {
        string srsPath = await CreateSRSFromSynthetic(BuildSyntheticStream("stream_detect.vob"), "stream_detect.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.Equal(SRSContainerType.Stream, srs.ContainerType);
    }

    #endregion

    #region FileData Block Properties

    [Fact]
    public async Task Load_AVISRS_HasFileDataBlock()
    {
        string srsPath = await CreateSRSFromSynthetic(BuildSyntheticAVI("avi_fd.avi"), "avi_fd.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.NotNull(srs.FileData);
    }

    [Fact]
    public async Task Load_AVISRS_FileDataHasCorrectFileName()
    {
        string samplePath = BuildSyntheticAVI("avi_fname.avi");
        string srsPath = await CreateSRSFromSynthetic(samplePath, "avi_fname.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.NotNull(srs.FileData);
        Assert.Contains("avi_fname.avi", srs.FileData!.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_AVISRS_FileDataHasCorrectAppName()
    {
        string samplePath = BuildSyntheticAVI("avi_app.avi");
        string srsPath = Path.Combine(TempDir, "avi_app.srs");

        var writer = new SRSWriter();
        var options = new SRSCreationOptions { AppName = "TestSRSApp" };
        SRSCreationResult result = await writer.CreateAsync(srsPath, samplePath, options);
        Assert.True(result.Success, result.ErrorMessage);

        var srs = SRSFile.Load(srsPath);

        Assert.NotNull(srs.FileData);
        Assert.Equal("TestSRSApp", srs.FileData!.AppName);
    }

    [Fact]
    public async Task Load_AVISRS_FileDataHasCorrectSampleSize()
    {
        string samplePath = BuildSyntheticAVI("avi_size.avi");
        long expectedSize = new FileInfo(samplePath).Length;
        string srsPath = await CreateSRSFromSynthetic(samplePath, "avi_size.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.NotNull(srs.FileData);
        Assert.Equal((ulong)expectedSize, srs.FileData!.SampleSize);
    }

    [Fact]
    public async Task Load_AVISRS_FileDataHasCorrectCRC32()
    {
        string samplePath = BuildSyntheticAVI("avi_crc.avi");
        string srsPath = Path.Combine(TempDir, "avi_crc.srs");

        var writer = new SRSWriter();
        SRSCreationResult result = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(result.Success, result.ErrorMessage);

        var srs = SRSFile.Load(srsPath);

        Assert.NotNull(srs.FileData);
        Assert.Equal(result.SampleCRC32, srs.FileData!.CRC32);
    }

    [Fact]
    public async Task Load_AVISRS_FileDataDefaultAppNameIsReSceneNET()
    {
        string srsPath = await CreateSRSFromSynthetic(BuildSyntheticAVI("avi_defapp.avi"), "avi_defapp.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.NotNull(srs.FileData);
        Assert.Equal("ReScene.NET", srs.FileData!.AppName);
    }

    #endregion

    #region Track Data Block Properties

    [Fact]
    public async Task Load_AVISRS_HasAtLeastOneTrack()
    {
        string srsPath = await CreateSRSFromSynthetic(BuildSyntheticAVI("avi_tracks.avi"), "avi_tracks.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.True(srs.Tracks.Count > 0);
    }

    [Fact]
    public async Task Load_AVISRS_TracksHaveNonZeroDataLength()
    {
        string srsPath = await CreateSRSFromSynthetic(BuildSyntheticAVI("avi_trklen.avi"), "avi_trklen.srs");

        var srs = SRSFile.Load(srsPath);

        foreach (SRSTrackDataBlock track in srs.Tracks)
        {
            Assert.True(track.DataLength > 0, $"Track {track.TrackNumber} has zero DataLength");
        }
    }

    [Fact]
    public async Task Load_AVISRS_TracksHave256ByteSignature()
    {
        string srsPath = await CreateSRSFromSynthetic(BuildSyntheticAVI("avi_sig.avi"), "avi_sig.srs");

        var srs = SRSFile.Load(srsPath);

        foreach (SRSTrackDataBlock track in srs.Tracks)
        {
            Assert.Equal(256, track.SignatureSize);
            Assert.Equal(256, track.Signature.Length);
        }
    }

    [Fact]
    public async Task Load_AVISRS_SignatureBytesAreNonEmpty()
    {
        string srsPath = await CreateSRSFromSynthetic(BuildSyntheticAVI("avi_sigdata.avi"), "avi_sigdata.srs");

        var srs = SRSFile.Load(srsPath);

        foreach (SRSTrackDataBlock track in srs.Tracks)
        {
            Assert.False(track.Signature.ToArray().All(b => b == 0),
                $"Track {track.TrackNumber} signature is all zeros");
        }
    }

    [Fact]
    public async Task Load_MKVSRS_TracksHave256ByteSignature()
    {
        string srsPath = await CreateSRSFromSynthetic(BuildSyntheticMKV("mkv_sig.mkv"), "mkv_sig.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.True(srs.Tracks.Count > 0);
        foreach (SRSTrackDataBlock track in srs.Tracks)
        {
            Assert.Equal(256, track.SignatureSize);
            Assert.Equal(256, track.Signature.Length);
        }
    }

    [Fact]
    public async Task Load_FlacSRS_TracksHave256ByteSignature()
    {
        string srsPath = await CreateSRSFromSynthetic(BuildSyntheticFlac("flac_sig.flac"), "flac_sig.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.True(srs.Tracks.Count > 0);
        foreach (SRSTrackDataBlock track in srs.Tracks)
        {
            Assert.Equal(256, track.SignatureSize);
            Assert.Equal(256, track.Signature.Length);
        }
    }

    #endregion

    #region Container Chunks

    [Fact]
    public async Task Load_AVISRS_HasContainerChunks()
    {
        string srsPath = await CreateSRSFromSynthetic(BuildSyntheticAVI("avi_chunks.avi"), "avi_chunks.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.True(srs.ContainerChunks.Count > 0);
    }

    [Fact]
    public async Task Load_AVISRS_ContainerChunksHaveRiffLabel()
    {
        string srsPath = await CreateSRSFromSynthetic(BuildSyntheticAVI("avi_riff.avi"), "avi_riff.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.Contains(srs.ContainerChunks, c => c.Label.Contains("RIFF", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Load_MKVSRS_HasContainerChunks()
    {
        string srsPath = await CreateSRSFromSynthetic(BuildSyntheticMKV("mkv_chunks.mkv"), "mkv_chunks.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.True(srs.ContainerChunks.Count > 0);
    }

    [Fact]
    public async Task Load_MKVSRS_ContainerChunksHaveEBMLLabel()
    {
        string srsPath = await CreateSRSFromSynthetic(BuildSyntheticMKV("mkv_ebml.mkv"), "mkv_ebml.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.Contains(srs.ContainerChunks, c => c.Label == "EBML");
    }

    [Fact]
    public async Task Load_FlacSRS_ContainerChunksHaveFlacMarker()
    {
        string srsPath = await CreateSRSFromSynthetic(BuildSyntheticFlac("flac_marker.flac"), "flac_marker.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.Contains(srs.ContainerChunks, c => c.Label == "fLaC");
    }

    [Fact]
    public async Task Load_ContainerChunks_HaveValidPositionsAndSizes()
    {
        string srsPath = await CreateSRSFromSynthetic(BuildSyntheticAVI("avi_pos.avi"), "avi_pos.srs");

        var srs = SRSFile.Load(srsPath);

        foreach (SRSContainerChunk chunk in srs.ContainerChunks)
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
        string srsPath = Path.Combine(TempDir, $"roundtrip_{format}.srs");

        var writer = new SRSWriter();
        SRSCreationResult result = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(result.Success, result.ErrorMessage);

        var srs = SRSFile.Load(srsPath);

        Assert.Equal(expectedType, srs.ContainerType);
        Assert.NotNull(srs.FileData);
        Assert.Equal(result.SampleCRC32, srs.FileData!.CRC32);
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
        string srsPath = Path.Combine(TempDir, $"sig_{format}.srs");

        var writer = new SRSWriter();
        SRSCreationResult result = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(result.Success, result.ErrorMessage);

        var srs = SRSFile.Load(srsPath);

        foreach (SRSTrackDataBlock track in srs.Tracks)
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
        string fakePath = Path.Combine(TempDir, "does_not_exist.srs");

        Assert.Throws<FileNotFoundException>(() => SRSFile.Load(fakePath));
    }

    [Fact]
    public void Load_FileTooSmall_ThrowsInvalidDataException()
    {
        string path = Path.Combine(TempDir, "tiny.srs");
        File.WriteAllBytes(path, [0x00, 0x01]);

        Assert.Throws<InvalidDataException>(() => SRSFile.Load(path));
    }

    #endregion

    #region FileData Block Position/Offset Fields

    [Fact]
    public async Task Load_AVISRS_FileDataHasValidOffsets()
    {
        string srsPath = await CreateSRSFromSynthetic(BuildSyntheticAVI("avi_offsets.avi"), "avi_offsets.srs");

        var srs = SRSFile.Load(srsPath);

        Assert.NotNull(srs.FileData);
        SRSFileDataBlock fd = srs.FileData!;
        Assert.True(fd.BlockPosition >= 0);
        Assert.True(fd.BlockSize > 0);
        Assert.True(fd.FrameHeaderSize > 0);
        Assert.True(fd.AppNameSize > 0);
        Assert.True(fd.FileNameSize > 0);
    }

    [Fact]
    public async Task Load_AVISRS_TrackDataHasValidOffsets()
    {
        string srsPath = await CreateSRSFromSynthetic(BuildSyntheticAVI("avi_trkoff.avi"), "avi_trkoff.srs");

        var srs = SRSFile.Load(srsPath);

        foreach (SRSTrackDataBlock track in srs.Tracks)
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
        string srsPath = await CreateSRSFromSynthetic(BuildSyntheticAVI("avi_multi.avi"), "avi_multi.srs");

        var srs1 = SRSFile.Load(srsPath);
        var srs2 = SRSFile.Load(srsPath);

        Assert.Equal(srs1.ContainerType, srs2.ContainerType);
        Assert.Equal(srs1.FileData!.CRC32, srs2.FileData!.CRC32);
        Assert.Equal(srs1.FileData.SampleSize, srs2.FileData.SampleSize);
        Assert.Equal(srs1.FileData.FileName, srs2.FileData.FileName);
        Assert.Equal(srs1.FileData.AppName, srs2.FileData.AppName);
        Assert.Equal(srs1.Tracks.Count, srs2.Tracks.Count);
        Assert.Equal(srs1.ContainerChunks.Count, srs2.ContainerChunks.Count);
    }

    #endregion

    #region Helpers

    private async Task<string> CreateSRSFromSynthetic(string samplePath, string srsFileName)
    {
        string srsPath = Path.Combine(TempDir, srsFileName);
        var writer = new SRSWriter();
        SRSCreationResult result = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(result.Success, result.ErrorMessage);
        return srsPath;
    }

    private string BuildSyntheticByFormat(string format) => format switch
    {
        "avi" => BuildSyntheticAVI($"rt_{format}.avi"),
        "mkv" => BuildSyntheticMKV($"rt_{format}.mkv"),
        "mp4" => BuildSyntheticMP4($"rt_{format}.mp4"),
        "flac" => BuildSyntheticFlac($"rt_{format}.flac"),
        "mp3" => BuildSyntheticMP3($"rt_{format}.mp3"),
        "stream" => BuildSyntheticStream($"rt_{format}.vob"),
        _ => throw new ArgumentException($"Unknown format: {format}")
    };

    #endregion

    #region Synthetic File Builders

    private string BuildSyntheticAVI(string fileName) =>
        SyntheticSampleBuilder.BuildAvi(Path.Combine(TempDir, fileName));

    private string BuildSyntheticMKV(string fileName) =>
        SyntheticSampleBuilder.BuildMkv(Path.Combine(TempDir, fileName));

    private string BuildSyntheticMP4(string fileName) =>
        SyntheticSampleBuilder.BuildMp4(Path.Combine(TempDir, fileName));

    private string BuildSyntheticFlac(string fileName) =>
        SyntheticSampleBuilder.BuildFlac(Path.Combine(TempDir, fileName));

    private string BuildSyntheticMP3(string fileName) =>
        SyntheticSampleBuilder.BuildMp3(Path.Combine(TempDir, fileName));

    private string BuildSyntheticStream(string fileName) =>
        SyntheticSampleBuilder.BuildStream(Path.Combine(TempDir, fileName));

    #endregion
}
