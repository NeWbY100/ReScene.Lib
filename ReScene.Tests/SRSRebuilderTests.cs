using System.Buffers.Binary;
using System.Text;
using ReScene.SRS;

namespace ReScene.Tests;

/// <summary>
/// Tests for SRSRebuilder - reconstruction of sample files from SRS + original media.
/// </summary>
public class SRSRebuilderTests : IDisposable
{
    private readonly string _tempDir;

    public SRSRebuilderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"srs_rebuild_test_{Guid.NewGuid():N}");
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

    #region Signature Matching Tests

    [Fact]
    public void FindSignature_ExactOffsetHit_ReturnsOffset()
    {
        // Build a stream with known data
        byte[] data = new byte[4096];
        new Random(42).NextBytes(data);
        byte[] signature = new byte[256];
        Array.Copy(data, 1000, signature, 0, 256);

        using var ms = new MemoryStream(data);
        long found = new SRSRebuilder().FindSignature(ms, signature, 1000);
        Assert.Equal(1000, found);
    }

    [Fact]
    public void FindSignature_OffsetMissFoundNearby_ReturnsCorrectOffset()
    {
        byte[] data = new byte[8192];
        new Random(42).NextBytes(data);
        byte[] signature = new byte[256];
        Array.Copy(data, 2000, signature, 0, 256);

        using var ms = new MemoryStream(data);
        // Give a wrong hint offset (nearby)
        long found = new SRSRebuilder().FindSignature(ms, signature, 1800);
        Assert.Equal(2000, found);
    }

    [Fact]
    public void FindSignature_OffsetMissFoundByFullScan_ReturnsCorrectOffset()
    {
        byte[] data = new byte[256 * 1024]; // 256KB
        new Random(42).NextBytes(data);
        byte[] signature = new byte[256];
        Array.Copy(data, 200000, signature, 0, 256);

        using var ms = new MemoryStream(data);
        // Give a completely wrong hint offset
        long found = new SRSRebuilder().FindSignature(ms, signature, 500);
        Assert.Equal(200000, found);
    }

    [Fact]
    public void FindSignature_NotFound_ReturnsNegative()
    {
        byte[] data = new byte[4096];
        new Random(42).NextBytes(data);
        byte[] signature = [0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE, 0xBA, 0xBE];

        using var ms = new MemoryStream(data);
        long found = new SRSRebuilder().FindSignature(ms, signature, 0);
        Assert.Equal(-1, found);
    }

    [Fact]
    public void FindSignature_AtStartOfFile_ReturnsZero()
    {
        byte[] data = new byte[4096];
        new Random(42).NextBytes(data);
        byte[] signature = new byte[256];
        Array.Copy(data, 0, signature, 0, 256);

        using var ms = new MemoryStream(data);
        long found = new SRSRebuilder().FindSignature(ms, signature, 0);
        Assert.Equal(0, found);
    }

    [Fact]
    public void FindSignature_EmptySignature_ReturnsHintOffset()
    {
        byte[] data = new byte[100];
        using var ms = new MemoryStream(data);
        long found = new SRSRebuilder().FindSignature(ms, [], 42);
        Assert.Equal(42, found);
    }

    #endregion

    #region AVI Round-Trip Tests

    [Fact]
    public async Task Rebuild_AVISample_RoundTrip_CRCMatches()
    {
        // Build a synthetic AVI "sample"
        string samplePath = BuildSyntheticAVI();

        // Also build a synthetic "full movie" that contains the same data
        // For testing, the sample IS the media file (the signature will match)
        string mediaPath = samplePath;

        // Create SRS
        string srsPath = Path.Combine(_tempDir, "test.srs");
        var writer = new SRSWriter();
        SRSCreationResult createResult = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(createResult.Success, createResult.ErrorMessage);

        // Rebuild
        string outputPath = Path.Combine(_tempDir, "rebuilt_sample.avi");
        var rebuilder = new SRSRebuilder();
        SRSReconstructionResult result = await rebuilder.RebuildAsync(srsPath, mediaPath, outputPath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.CRCMatch, $"CRC mismatch: expected 0x{result.ExpectedCRC:X8}, got 0x{result.ActualCRC:X8}");
        Assert.Equal(result.ExpectedSize, result.ActualSize);
    }

    [Fact]
    public async Task Rebuild_AVISample_OutputFileMatchesOriginal()
    {
        string samplePath = BuildSyntheticAVI();
        string mediaPath = samplePath;

        string srsPath = Path.Combine(_tempDir, "test.srs");
        var writer = new SRSWriter();
        SRSCreationResult createResult = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(createResult.Success, createResult.ErrorMessage);

        string outputPath = Path.Combine(_tempDir, "rebuilt_sample.avi");
        var rebuilder = new SRSRebuilder();
        SRSReconstructionResult result = await rebuilder.RebuildAsync(srsPath, mediaPath, outputPath);

        Assert.True(result.Success, result.ErrorMessage);

        // Compare file contents
        byte[] originalBytes = File.ReadAllBytes(samplePath);
        byte[] rebuiltBytes = File.ReadAllBytes(outputPath);
        Assert.Equal(originalBytes.Length, rebuiltBytes.Length);
        Assert.True(originalBytes.AsSpan().SequenceEqual(rebuiltBytes),
            "Rebuilt AVI file content does not match original.");
    }

    [Fact]
    public async Task Rebuild_AVISample_WithMultipleTracks_RoundTrip()
    {
        string samplePath = BuildSyntheticAVIMultiTrack();

        string srsPath = Path.Combine(_tempDir, "test_multi.srs");
        var writer = new SRSWriter();
        SRSCreationResult createResult = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(createResult.Success, createResult.ErrorMessage);
        Assert.True(createResult.TrackCount >= 2, "Expected multiple tracks");

        // Rebuild from the same file (round-trip)
        string outputPath = Path.Combine(_tempDir, "rebuilt_multi.avi");
        var rebuilder = new SRSRebuilder();
        SRSReconstructionResult result = await rebuilder.RebuildAsync(srsPath, samplePath, outputPath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.CRCMatch, $"CRC mismatch: expected 0x{result.ExpectedCRC:X8}, got 0x{result.ActualCRC:X8}");
        Assert.Equal(result.ExpectedSize, result.ActualSize);

        // Verify file content matches byte-for-byte
        byte[] originalBytes = File.ReadAllBytes(samplePath);
        byte[] rebuiltBytes = File.ReadAllBytes(outputPath);
        Assert.True(originalBytes.AsSpan().SequenceEqual(rebuiltBytes),
            "Rebuilt multi-track AVI does not match original.");
    }

    #endregion

    #region Stream Round-Trip Tests

    [Fact]
    public async Task Rebuild_StreamSample_RoundTrip_CRCMatches()
    {
        string samplePath = BuildSyntheticStream();
        string mediaPath = samplePath;

        string srsPath = Path.Combine(_tempDir, "test.srs");
        var writer = new SRSWriter();
        SRSCreationResult createResult = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(createResult.Success, createResult.ErrorMessage);

        string outputPath = Path.Combine(_tempDir, "rebuilt_sample.vob");
        var rebuilder = new SRSRebuilder();
        SRSReconstructionResult result = await rebuilder.RebuildAsync(srsPath, mediaPath, outputPath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.CRCMatch);
        Assert.Equal(result.ExpectedSize, result.ActualSize);

        byte[] originalBytes = File.ReadAllBytes(samplePath);
        byte[] rebuiltBytes = File.ReadAllBytes(outputPath);
        Assert.True(originalBytes.AsSpan().SequenceEqual(rebuiltBytes),
            "Rebuilt stream file does not match original.");
    }

    #endregion

    #region MKV Round-Trip Tests

    [Fact]
    public async Task Rebuild_MKVSample_RoundTrip_CRCMatches()
    {
        string samplePath = BuildSyntheticMKV();
        string mediaPath = samplePath;

        string srsPath = Path.Combine(_tempDir, "test.srs");
        var writer = new SRSWriter();
        SRSCreationResult createResult = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(createResult.Success, createResult.ErrorMessage);

        string outputPath = Path.Combine(_tempDir, "rebuilt_sample.mkv");
        var rebuilder = new SRSRebuilder();
        SRSReconstructionResult result = await rebuilder.RebuildAsync(srsPath, mediaPath, outputPath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.CRCMatch,
            $"CRC mismatch: expected 0x{result.ExpectedCRC:X8}, got 0x{result.ActualCRC:X8}");
    }

    [Fact]
    public async Task Rebuild_MKVWithAttachments_RoundTrip_CRCMatches()
    {
        string samplePath = BuildSyntheticMKVWithAttachments();
        string mediaPath = samplePath;

        string srsPath = Path.Combine(_tempDir, "test_att.srs");
        var writer = new SRSWriter();
        SRSCreationResult createResult = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(createResult.Success, createResult.ErrorMessage);

        string outputPath = Path.Combine(_tempDir, "rebuilt_att.mkv");
        var rebuilder = new SRSRebuilder();
        SRSReconstructionResult result = await rebuilder.RebuildAsync(srsPath, mediaPath, outputPath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.CRCMatch,
            $"CRC mismatch: expected 0x{result.ExpectedCRC:X8}, got 0x{result.ActualCRC:X8}");
        Assert.Equal(result.ExpectedSize, result.ActualSize);

        byte[] originalBytes = File.ReadAllBytes(samplePath);
        byte[] rebuiltBytes = File.ReadAllBytes(outputPath);
        Assert.True(originalBytes.AsSpan().SequenceEqual(rebuiltBytes),
            "Rebuilt MKV with attachments does not match original.");
    }

    [Fact]
    public async Task Rebuild_MKVFromLargerMediaFile_CRCMatches()
    {
        // Simulate the real scenario: SRS is from a small sample,
        // media file is a larger "movie" that contains the same data
        string samplePath = BuildSyntheticMKV();
        string mediaPath = BuildSyntheticMovieMKV(samplePath);

        string srsPath = Path.Combine(_tempDir, "test.srs");
        var writer = new SRSWriter();
        SRSCreationResult createResult = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(createResult.Success, createResult.ErrorMessage);

        string outputPath = Path.Combine(_tempDir, "rebuilt_sample.mkv");
        var rebuilder = new SRSRebuilder();
        SRSReconstructionResult result = await rebuilder.RebuildAsync(srsPath, mediaPath, outputPath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.CRCMatch,
            $"CRC mismatch: expected 0x{result.ExpectedCRC:X8}, got 0x{result.ActualCRC:X8}");
        Assert.Equal(result.ExpectedSize, result.ActualSize);

        byte[] originalBytes = File.ReadAllBytes(samplePath);
        byte[] rebuiltBytes = File.ReadAllBytes(outputPath);
        Assert.True(originalBytes.AsSpan().SequenceEqual(rebuiltBytes),
            "Rebuilt MKV from larger media file does not match original sample.");
    }

    [Fact]
    public async Task Rebuild_MKVFromRealisticMediaFile_CRCMatches()
    {
        // Simulates a real MKV movie: Segment with unknown size,
        // SeekHead, Void, Info, Tracks, many clusters, Cues at end
        string samplePath = BuildSyntheticMKV();
        string mediaPath = BuildRealisticMovieMKV();

        string srsPath = Path.Combine(_tempDir, "test_realistic.srs");
        var writer = new SRSWriter();
        SRSCreationResult createResult = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(createResult.Success, createResult.ErrorMessage);

        string outputPath = Path.Combine(_tempDir, "rebuilt_realistic.mkv");
        var rebuilder = new SRSRebuilder();
        SRSReconstructionResult result = await rebuilder.RebuildAsync(srsPath, mediaPath, outputPath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.CRCMatch,
            $"CRC mismatch: expected 0x{result.ExpectedCRC:X8}, got 0x{result.ActualCRC:X8}");
        Assert.Equal(result.ExpectedSize, result.ActualSize);
    }

    [Fact]
    public async Task Rebuild_MKVSubtitleStyleTrack_FromLargerMediaFile_CRCMatches()
    {
        // A track whose individual block payloads are smaller than the 256-byte
        // signature size — like real-world MKV subtitle tracks — produces a
        // signature that is the concatenation of many non-contiguous blocks in
        // the file. A raw byte scan can never find such a signature; only an
        // EBML-aware finder can. This test exercises that path.
        string samplePath = BuildSyntheticMKVWithSubtitleStyleTrack();
        string mediaPath = BuildLargerMKVWithSubtitleStyleTrack();

        string srsPath = Path.Combine(_tempDir, "test_subs.srs");
        var writer = new SRSWriter();
        SRSCreationResult createResult = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(createResult.Success, createResult.ErrorMessage);

        string outputPath = Path.Combine(_tempDir, "rebuilt_subs.mkv");
        var rebuilder = new SRSRebuilder();
        SRSReconstructionResult result = await rebuilder.RebuildAsync(srsPath, mediaPath, outputPath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.CRCMatch,
            $"CRC mismatch: expected 0x{result.ExpectedCRC:X8}, got 0x{result.ActualCRC:X8}");
        Assert.Equal(result.ExpectedSize, result.ActualSize);

        byte[] originalBytes = File.ReadAllBytes(samplePath);
        byte[] rebuiltBytes = File.ReadAllBytes(outputPath);
        Assert.True(originalBytes.AsSpan().SequenceEqual(rebuiltBytes),
            "Rebuilt MKV with subtitle-style track does not match original.");
    }

    [Fact]
    public async Task MKVFindSampleStreams_SubtitleStyleTrack_LocatesAllTracks()
    {
        // Direct test of the container-specific finder. Verifies it returns a
        // valid offset for a track whose signature spans many small blocks,
        // where the generic raw byte scan cannot find it.
        string samplePath = BuildSyntheticMKVWithSubtitleStyleTrack();
        string mediaPath = BuildLargerMKVWithSubtitleStyleTrack();

        var writer = new SRSWriter();
        string srsPath = Path.Combine(_tempDir, "test_subs_finder.srs");
        SRSCreationResult cr = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(cr.Success, cr.ErrorMessage);

        var srs = SRSFile.Load(srsPath);
        var trackDict = new Dictionary<uint, SRSTrackDataBlock>();
        foreach (SRSTrackDataBlock t in srs.Tracks)
        {
            trackDict[t.TrackNumber] = t;
        }

        var mkvRebuilder = new MKVContainerRebuilder();
        Dictionary<uint, long>? offsets = mkvRebuilder.FindSampleStreams(
            mediaPath, trackDict, null, null, CancellationToken.None);

        Assert.NotNull(offsets);
        Assert.True(offsets!.ContainsKey(1), "Video track (1) was not located.");
        Assert.True(offsets.ContainsKey(2), "Subtitle-style track (2) was not located.");

        // Sanity-check: the generic raw byte scan from SRSRebuilder cannot find
        // the subtitle-style track's signature in the larger media file.
        using var fs = new FileStream(mediaPath, FileMode.Open, FileAccess.Read);
        byte[] subSig = trackDict[2].Signature;
        long rawScanResult = new SRSRebuilder().FindSignature(fs, subSig, 0);
        Assert.Equal(-1, rawScanResult);
    }

    #endregion

    #region MP4 Round-Trip Tests

    [Fact]
    public async Task Rebuild_MP4Sample_RoundTrip_CRCMatches()
    {
        string samplePath = BuildSyntheticMP4();
        string mediaPath = samplePath;

        string srsPath = Path.Combine(_tempDir, "test.srs");
        var writer = new SRSWriter();
        SRSCreationResult createResult = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(createResult.Success, createResult.ErrorMessage);

        string outputPath = Path.Combine(_tempDir, "rebuilt_sample.mp4");
        var rebuilder = new SRSRebuilder();
        SRSReconstructionResult result = await rebuilder.RebuildAsync(srsPath, mediaPath, outputPath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.CRCMatch, $"CRC mismatch: expected 0x{result.ExpectedCRC:X8}, got 0x{result.ActualCRC:X8}");
    }

    #endregion

    #region FLAC Round-Trip Tests

    [Fact]
    public async Task Rebuild_FlacSample_RoundTrip_CRCMatches()
    {
        string samplePath = BuildSyntheticFlac();
        string mediaPath = samplePath;

        string srsPath = Path.Combine(_tempDir, "test.srs");
        var writer = new SRSWriter();
        SRSCreationResult createResult = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(createResult.Success, createResult.ErrorMessage);

        string outputPath = Path.Combine(_tempDir, "rebuilt_sample.flac");
        var rebuilder = new SRSRebuilder();
        SRSReconstructionResult result = await rebuilder.RebuildAsync(srsPath, mediaPath, outputPath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.CRCMatch, $"CRC mismatch: expected 0x{result.ExpectedCRC:X8}, got 0x{result.ActualCRC:X8}");
    }

    #endregion

    #region MP3 Round-Trip Tests

    [Fact]
    public async Task Rebuild_MP3Sample_RoundTrip_CRCMatches()
    {
        string samplePath = BuildSyntheticMP3();
        string mediaPath = samplePath;

        string srsPath = Path.Combine(_tempDir, "test.srs");
        var writer = new SRSWriter();
        SRSCreationResult createResult = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(createResult.Success, createResult.ErrorMessage);

        string outputPath = Path.Combine(_tempDir, "rebuilt_sample.mp3");
        var rebuilder = new SRSRebuilder();
        SRSReconstructionResult result = await rebuilder.RebuildAsync(srsPath, mediaPath, outputPath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.CRCMatch, $"CRC mismatch: expected 0x{result.ExpectedCRC:X8}, got 0x{result.ActualCRC:X8}");
    }

    #endregion

    #region CRC Verification Tests

    [Fact]
    public async Task Rebuild_WithCorruptedMedia_CRCMismatch()
    {
        string samplePath = BuildSyntheticStream();

        // Create SRS from original
        string srsPath = Path.Combine(_tempDir, "test.srs");
        var writer = new SRSWriter();
        SRSCreationResult createResult = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(createResult.Success, createResult.ErrorMessage);

        // Create a corrupted "media" file (same size, different content)
        string corruptMediaPath = Path.Combine(_tempDir, "corrupt_media.vob");
        byte[] originalData = File.ReadAllBytes(samplePath);
        byte[] corruptData = new byte[originalData.Length + 1024]; // larger file
        originalData.CopyTo(corruptData, 0);

        // Corrupt the data while keeping the signature intact
        var parsed = SRSFile.Load(srsPath);
        int sigLen = parsed.Tracks[0].SignatureSize;

        // Put the correct signature at offset 0 so it matches
        // but corrupt the rest of the data
        Array.Copy(originalData, 0, corruptData, 0, sigLen);
        for (int i = sigLen; i < originalData.Length; i++)
        {
            corruptData[i] = (byte)(originalData[i] ^ 0xFF);
        }

        File.WriteAllBytes(corruptMediaPath, corruptData);

        // Rebuild from corrupted media
        string outputPath = Path.Combine(_tempDir, "rebuilt_corrupt.vob");
        var rebuilder = new SRSRebuilder();
        SRSReconstructionResult result = await rebuilder.RebuildAsync(srsPath, corruptMediaPath, outputPath);

        Assert.False(result.CRCMatch);
        Assert.NotEqual(result.ExpectedCRC, result.ActualCRC);
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public async Task Rebuild_MissingSRSFile_ReturnsError()
    {
        var rebuilder = new SRSRebuilder();
        SRSReconstructionResult result = await rebuilder.RebuildAsync(
            Path.Combine(_tempDir, "nonexistent.srs"),
            Path.Combine(_tempDir, "media.avi"),
            Path.Combine(_tempDir, "output.avi"));

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task Rebuild_MissingMediaFile_ReturnsError()
    {
        // Create a valid SRS first
        string samplePath = BuildSyntheticStream();
        string srsPath = Path.Combine(_tempDir, "test.srs");
        var writer = new SRSWriter();
        await writer.CreateAsync(srsPath, samplePath);

        var rebuilder = new SRSRebuilder();
        SRSReconstructionResult result = await rebuilder.RebuildAsync(
            srsPath,
            Path.Combine(_tempDir, "nonexistent_media.vob"),
            Path.Combine(_tempDir, "output.vob"));

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task Rebuild_CancellationToken_CancelsOperation()
    {
        string samplePath = BuildSyntheticStream();
        string srsPath = Path.Combine(_tempDir, "test.srs");
        var writer = new SRSWriter();
        await writer.CreateAsync(srsPath, samplePath);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        var rebuilder = new SRSRebuilder();
        SRSReconstructionResult result = await rebuilder.RebuildAsync(
            srsPath, samplePath,
            Path.Combine(_tempDir, "output.vob"),
            cts.Token);

        Assert.False(result.Success);
        Assert.Contains("cancel", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Progress Events Tests

    [Fact]
    public async Task Rebuild_ReportsProgress()
    {
        string samplePath = BuildSyntheticStream();
        string srsPath = Path.Combine(_tempDir, "test.srs");
        var writer = new SRSWriter();
        await writer.CreateAsync(srsPath, samplePath);

        var phases = new List<string>();
        var rebuilder = new SRSRebuilder();
        rebuilder.Progress += (_, e) => phases.Add(e.Phase);

        string outputPath = Path.Combine(_tempDir, "rebuilt.vob");
        await rebuilder.RebuildAsync(srsPath, samplePath, outputPath);

        Assert.Contains("Loading SRS", phases);
        Assert.Contains("Finding tracks", phases);
        Assert.Contains("Rebuilding", phases);
        Assert.Contains("Verifying CRC", phases);
        Assert.Contains("Complete", phases);
    }

    #endregion

    #region MKV Lacing Tests

    [Fact]
    public async Task Rebuild_MKVWithXiphLacing_RoundTrip_ByteMatch()
    {
        string samplePath = BuildSyntheticMKVWithXiphLacing();

        string srsPath = Path.Combine(_tempDir, "test_xiph.srs");
        var writer = new SRSWriter();
        SRSCreationResult createResult = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(createResult.Success, createResult.ErrorMessage);

        string outputPath = Path.Combine(_tempDir, "rebuilt_xiph.mkv");
        var rebuilder = new SRSRebuilder();
        SRSReconstructionResult result = await rebuilder.RebuildAsync(srsPath, samplePath, outputPath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.CRCMatch,
            $"CRC mismatch: expected 0x{result.ExpectedCRC:X8}, got 0x{result.ActualCRC:X8}");

        byte[] original = File.ReadAllBytes(samplePath);
        byte[] rebuilt = File.ReadAllBytes(outputPath);
        Assert.True(original.AsSpan().SequenceEqual(rebuilt),
            "Rebuilt MKV with Xiph lacing does not match original.");
    }

    [Fact]
    public async Task Rebuild_MKVWithFixedSizeLacing_RoundTrip_ByteMatch()
    {
        string samplePath = BuildSyntheticMKVWithFixedLacing();

        string srsPath = Path.Combine(_tempDir, "test_fixed.srs");
        var writer = new SRSWriter();
        SRSCreationResult createResult = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(createResult.Success, createResult.ErrorMessage);

        string outputPath = Path.Combine(_tempDir, "rebuilt_fixed.mkv");
        var rebuilder = new SRSRebuilder();
        SRSReconstructionResult result = await rebuilder.RebuildAsync(srsPath, samplePath, outputPath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.CRCMatch,
            $"CRC mismatch: expected 0x{result.ExpectedCRC:X8}, got 0x{result.ActualCRC:X8}");

        byte[] original = File.ReadAllBytes(samplePath);
        byte[] rebuilt = File.ReadAllBytes(outputPath);
        Assert.True(original.AsSpan().SequenceEqual(rebuilt),
            "Rebuilt MKV with fixed-size lacing does not match original.");
    }

    [Fact]
    public async Task Rebuild_MKVWithEBMLLacing_RoundTrip_ByteMatch()
    {
        string samplePath = BuildSyntheticMKVWithEBMLLacing();

        string srsPath = Path.Combine(_tempDir, "test_ebml.srs");
        var writer = new SRSWriter();
        SRSCreationResult createResult = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(createResult.Success, createResult.ErrorMessage);

        string outputPath = Path.Combine(_tempDir, "rebuilt_ebml.mkv");
        var rebuilder = new SRSRebuilder();
        SRSReconstructionResult result = await rebuilder.RebuildAsync(srsPath, samplePath, outputPath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.CRCMatch,
            $"CRC mismatch: expected 0x{result.ExpectedCRC:X8}, got 0x{result.ActualCRC:X8}");

        byte[] original = File.ReadAllBytes(samplePath);
        byte[] rebuilt = File.ReadAllBytes(outputPath);
        Assert.True(original.AsSpan().SequenceEqual(rebuilt),
            "Rebuilt MKV with EBML lacing does not match original.");
    }

    [Fact]
    public async Task Rebuild_MKVWithMixedLacing_RoundTrip_ByteMatch()
    {
        // Mix of non-laced video blocks and Xiph-laced audio blocks
        string samplePath = BuildSyntheticMKVWithMixedLacing();

        string srsPath = Path.Combine(_tempDir, "test_mixed.srs");
        var writer = new SRSWriter();
        SRSCreationResult createResult = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(createResult.Success, createResult.ErrorMessage);
        Assert.True(createResult.TrackCount >= 2, "Expected multiple tracks");

        string outputPath = Path.Combine(_tempDir, "rebuilt_mixed.mkv");
        var rebuilder = new SRSRebuilder();
        SRSReconstructionResult result = await rebuilder.RebuildAsync(srsPath, samplePath, outputPath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.CRCMatch,
            $"CRC mismatch: expected 0x{result.ExpectedCRC:X8}, got 0x{result.ActualCRC:X8}");

        byte[] original = File.ReadAllBytes(samplePath);
        byte[] rebuilt = File.ReadAllBytes(outputPath);
        Assert.True(original.AsSpan().SequenceEqual(rebuilt),
            "Rebuilt MKV with mixed lacing does not match original.");
    }

    [Fact]
    public async Task Rebuild_MKVWithXiphLacing_FromLargerMediaFile()
    {
        string samplePath = BuildSyntheticMKVWithXiphLacing();
        string mediaPath = BuildLargerMKVWithXiphLacing();

        string srsPath = Path.Combine(_tempDir, "test_xiph_media.srs");
        var writer = new SRSWriter();
        SRSCreationResult createResult = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(createResult.Success, createResult.ErrorMessage);

        string outputPath = Path.Combine(_tempDir, "rebuilt_xiph_media.mkv");
        var rebuilder = new SRSRebuilder();
        SRSReconstructionResult result = await rebuilder.RebuildAsync(srsPath, mediaPath, outputPath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.CRCMatch,
            $"CRC mismatch: expected 0x{result.ExpectedCRC:X8}, got 0x{result.ActualCRC:X8}");
        Assert.Equal(result.ExpectedSize, result.ActualSize);
    }

    #endregion

    #region MKV Multi-Cluster and Many-Block Tests

    [Fact]
    public async Task Rebuild_MKVWithMultipleClusters_RoundTrip_ByteMatch()
    {
        string samplePath = BuildSyntheticMKVMultiCluster();

        string srsPath = Path.Combine(_tempDir, "test_multi_cluster.srs");
        var writer = new SRSWriter();
        SRSCreationResult createResult = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(createResult.Success, createResult.ErrorMessage);

        string outputPath = Path.Combine(_tempDir, "rebuilt_multi_cluster.mkv");
        var rebuilder = new SRSRebuilder();
        SRSReconstructionResult result = await rebuilder.RebuildAsync(srsPath, samplePath, outputPath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.CRCMatch,
            $"CRC mismatch: expected 0x{result.ExpectedCRC:X8}, got 0x{result.ActualCRC:X8}");

        byte[] original = File.ReadAllBytes(samplePath);
        byte[] rebuilt = File.ReadAllBytes(outputPath);
        Assert.True(original.AsSpan().SequenceEqual(rebuilt),
            "Rebuilt MKV with multiple clusters does not match original.");
    }

    [Fact]
    public async Task Rebuild_MKVWithManyBlocks_RoundTrip_CRCMatches()
    {
        // 50 blocks across 5 clusters — stress tests the SRS walk loop
        string samplePath = BuildSyntheticMKVManyBlocks(clusterCount: 5, blocksPerCluster: 10);

        string srsPath = Path.Combine(_tempDir, "test_many.srs");
        var writer = new SRSWriter();
        SRSCreationResult createResult = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(createResult.Success, createResult.ErrorMessage);

        string outputPath = Path.Combine(_tempDir, "rebuilt_many.mkv");
        var rebuilder = new SRSRebuilder();
        SRSReconstructionResult result = await rebuilder.RebuildAsync(srsPath, samplePath, outputPath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.CRCMatch,
            $"CRC mismatch: expected 0x{result.ExpectedCRC:X8}, got 0x{result.ActualCRC:X8}");
        Assert.Equal(result.ExpectedSize, result.ActualSize);
    }

    [Fact]
    public async Task Rebuild_MKVWithTimestampElements_RoundTrip_ByteMatch()
    {
        string samplePath = BuildSyntheticMKVWithTimestamps();

        string srsPath = Path.Combine(_tempDir, "test_ts.srs");
        var writer = new SRSWriter();
        SRSCreationResult createResult = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(createResult.Success, createResult.ErrorMessage);

        string outputPath = Path.Combine(_tempDir, "rebuilt_ts.mkv");
        var rebuilder = new SRSRebuilder();
        SRSReconstructionResult result = await rebuilder.RebuildAsync(srsPath, samplePath, outputPath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.CRCMatch,
            $"CRC mismatch: expected 0x{result.ExpectedCRC:X8}, got 0x{result.ActualCRC:X8}");

        byte[] original = File.ReadAllBytes(samplePath);
        byte[] rebuilt = File.ReadAllBytes(outputPath);
        Assert.True(original.AsSpan().SequenceEqual(rebuilt),
            "Rebuilt MKV with Timestamp elements does not match original.");
    }

    [Fact]
    public async Task Rebuild_MKVWithBlockGroup_RoundTrip_ByteMatch()
    {
        // Uses Block (0xA1) inside BlockGroup (0xA0) instead of SimpleBlock
        string samplePath = BuildSyntheticMKVWithBlockGroup();

        string srsPath = Path.Combine(_tempDir, "test_bg.srs");
        var writer = new SRSWriter();
        SRSCreationResult createResult = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(createResult.Success, createResult.ErrorMessage);

        string outputPath = Path.Combine(_tempDir, "rebuilt_bg.mkv");
        var rebuilder = new SRSRebuilder();
        SRSReconstructionResult result = await rebuilder.RebuildAsync(srsPath, samplePath, outputPath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.CRCMatch,
            $"CRC mismatch: expected 0x{result.ExpectedCRC:X8}, got 0x{result.ActualCRC:X8}");

        byte[] original = File.ReadAllBytes(samplePath);
        byte[] rebuilt = File.ReadAllBytes(outputPath);
        Assert.True(original.AsSpan().SequenceEqual(rebuilt),
            "Rebuilt MKV with BlockGroup does not match original.");
    }

    #endregion

    #region Byte-for-Byte Verification Tests

    [Fact]
    public async Task Rebuild_MP4Sample_OutputFileMatchesOriginal()
    {
        string samplePath = BuildSyntheticMP4();

        string srsPath = Path.Combine(_tempDir, "test.srs");
        var writer = new SRSWriter();
        SRSCreationResult createResult = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(createResult.Success, createResult.ErrorMessage);

        string outputPath = Path.Combine(_tempDir, "rebuilt_sample.mp4");
        var rebuilder = new SRSRebuilder();
        SRSReconstructionResult result = await rebuilder.RebuildAsync(srsPath, samplePath, outputPath);

        Assert.True(result.Success, result.ErrorMessage);

        byte[] original = File.ReadAllBytes(samplePath);
        byte[] rebuilt = File.ReadAllBytes(outputPath);
        Assert.Equal(original.Length, rebuilt.Length);
        Assert.True(original.AsSpan().SequenceEqual(rebuilt),
            "Rebuilt MP4 file does not match original.");
    }

    [Fact]
    public async Task Rebuild_FlacSample_OutputFileMatchesOriginal()
    {
        string samplePath = BuildSyntheticFlac();

        string srsPath = Path.Combine(_tempDir, "test.srs");
        var writer = new SRSWriter();
        SRSCreationResult createResult = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(createResult.Success, createResult.ErrorMessage);

        string outputPath = Path.Combine(_tempDir, "rebuilt_sample.flac");
        var rebuilder = new SRSRebuilder();
        SRSReconstructionResult result = await rebuilder.RebuildAsync(srsPath, samplePath, outputPath);

        Assert.True(result.Success, result.ErrorMessage);

        byte[] original = File.ReadAllBytes(samplePath);
        byte[] rebuilt = File.ReadAllBytes(outputPath);
        Assert.Equal(original.Length, rebuilt.Length);
        Assert.True(original.AsSpan().SequenceEqual(rebuilt),
            "Rebuilt FLAC file does not match original.");
    }

    [Fact]
    public async Task Rebuild_MP3Sample_OutputFileMatchesOriginal()
    {
        string samplePath = BuildSyntheticMP3();

        string srsPath = Path.Combine(_tempDir, "test.srs");
        var writer = new SRSWriter();
        SRSCreationResult createResult = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(createResult.Success, createResult.ErrorMessage);

        string outputPath = Path.Combine(_tempDir, "rebuilt_sample.mp3");
        var rebuilder = new SRSRebuilder();
        SRSReconstructionResult result = await rebuilder.RebuildAsync(srsPath, samplePath, outputPath);

        Assert.True(result.Success, result.ErrorMessage);

        byte[] original = File.ReadAllBytes(samplePath);
        byte[] rebuilt = File.ReadAllBytes(outputPath);
        Assert.Equal(original.Length, rebuilt.Length);
        Assert.True(original.AsSpan().SequenceEqual(rebuilt),
            "Rebuilt MP3 file does not match original.");
    }

    [Fact]
    public async Task Rebuild_StreamSample_OutputFileMatchesOriginal()
    {
        string samplePath = BuildSyntheticStream();

        string srsPath = Path.Combine(_tempDir, "test.srs");
        var writer = new SRSWriter();
        SRSCreationResult createResult = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(createResult.Success, createResult.ErrorMessage);

        string outputPath = Path.Combine(_tempDir, "rebuilt_sample.vob");
        var rebuilder = new SRSRebuilder();
        SRSReconstructionResult result = await rebuilder.RebuildAsync(srsPath, samplePath, outputPath);

        Assert.True(result.Success, result.ErrorMessage);

        byte[] original = File.ReadAllBytes(samplePath);
        byte[] rebuilt = File.ReadAllBytes(outputPath);
        Assert.Equal(original.Length, rebuilt.Length);
        Assert.True(original.AsSpan().SequenceEqual(rebuilt),
            "Rebuilt stream file does not match original.");
    }

    #endregion

    #region Separate Media File Tests

    [Fact]
    public async Task Rebuild_AVISample_FromSeparateMediaFile()
    {
        string samplePath = BuildSyntheticAVI();

        string srsPath = Path.Combine(_tempDir, "test.srs");
        var writer = new SRSWriter();
        SRSCreationResult createResult = await writer.CreateAsync(srsPath, samplePath);
        Assert.True(createResult.Success, createResult.ErrorMessage);

        string outputPath = Path.Combine(_tempDir, "rebuilt_sample.avi");
        var rebuilder = new SRSRebuilder();
        SRSReconstructionResult result = await rebuilder.RebuildAsync(srsPath, samplePath, outputPath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.CRCMatch);
    }

    #endregion

    #region Synthetic File Builders

    /// <summary>
    /// Builds a minimal valid AVI file with video (00dc) and audio (01wb) tracks.
    /// </summary>
    private string BuildSyntheticAVI()
    {
        string path = Path.Combine(_tempDir, "test_sample.avi");
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

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

        // Build hdrl
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

    /// <summary>
    /// Builds an AVI with multiple interleaved video/audio chunks.
    /// Each chunk must be at least 256 bytes so the track signature fits
    /// within a single contiguous chunk (enabling raw-scan signature matching).
    /// </summary>
    private string BuildSyntheticAVIMultiTrack()
    {
        string path = Path.Combine(_tempDir, "test_multi_sample.avi");
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        var moviContent = new MemoryStream();
        var moviWriter = new BinaryWriter(moviContent);

        // Interleaved: video, audio, video, audio
        // Each chunk >= 256 bytes so signatures are contiguous
        byte[] video1 = CreateTestData(512, seed: 10);
        moviWriter.Write(Encoding.ASCII.GetBytes("00dc"));
        moviWriter.Write((uint)video1.Length);
        moviWriter.Write(video1);

        byte[] audio1 = CreateTestData(300, seed: 20);
        moviWriter.Write(Encoding.ASCII.GetBytes("01wb"));
        moviWriter.Write((uint)audio1.Length);
        moviWriter.Write(audio1);

        byte[] video2 = CreateTestData(512, seed: 30);
        moviWriter.Write(Encoding.ASCII.GetBytes("00dc"));
        moviWriter.Write((uint)video2.Length);
        moviWriter.Write(video2);

        byte[] audio2 = CreateTestData(300, seed: 40);
        moviWriter.Write(Encoding.ASCII.GetBytes("01wb"));
        moviWriter.Write((uint)audio2.Length);
        moviWriter.Write(audio2);

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

    private string BuildSyntheticMKV()
    {
        string path = Path.Combine(_tempDir, "test_sample.mkv");
        using var ms = new MemoryStream();

        byte[] ebmlContent = BuildEBMLHeaderContent();
        WriteEBMLElement(ms, 0x1A45DFA3, ebmlContent);

        var segContent = new MemoryStream();
        var clusterContent = new MemoryStream();

        byte[] blockData = CreateTestData(512);
        byte[] simpleBlockPayload = new byte[1 + 2 + 1 + blockData.Length];
        simpleBlockPayload[0] = 0x81; // Track 1
        simpleBlockPayload[3] = 0x80; // Keyframe
        blockData.CopyTo(simpleBlockPayload, 4);
        WriteEBMLElement(clusterContent, 0xA3, simpleBlockPayload);

        byte[] blockData2 = CreateTestData(256, seed: 99);
        byte[] simpleBlockPayload2 = new byte[1 + 2 + 1 + blockData2.Length];
        simpleBlockPayload2[0] = 0x82; // Track 2
        simpleBlockPayload2[3] = 0x80;
        blockData2.CopyTo(simpleBlockPayload2, 4);
        WriteEBMLElement(clusterContent, 0xA3, simpleBlockPayload2);

        WriteEBMLElement(segContent, 0x1F43B675, clusterContent.ToArray());

        WriteEBMLElement(ms, 0x18538067, segContent.ToArray());

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    private string BuildSyntheticMKVWithAttachments()
    {
        string path = Path.Combine(_tempDir, "test_sample_att.mkv");
        using var ms = new MemoryStream();

        byte[] ebmlContent = BuildEBMLHeaderContent();
        WriteEBMLElement(ms, 0x1A45DFA3, ebmlContent);

        var segContent = new MemoryStream();

        // Cluster with two tracks
        var clusterContent = new MemoryStream();

        byte[] blockData = CreateTestData(512);
        byte[] simpleBlockPayload = new byte[1 + 2 + 1 + blockData.Length];
        simpleBlockPayload[0] = 0x81; // Track 1
        simpleBlockPayload[3] = 0x80; // Keyframe
        blockData.CopyTo(simpleBlockPayload, 4);
        WriteEBMLElement(clusterContent, 0xA3, simpleBlockPayload);

        byte[] blockData2 = CreateTestData(256, seed: 99);
        byte[] simpleBlockPayload2 = new byte[1 + 2 + 1 + blockData2.Length];
        simpleBlockPayload2[0] = 0x82; // Track 2
        simpleBlockPayload2[3] = 0x80;
        blockData2.CopyTo(simpleBlockPayload2, 4);
        WriteEBMLElement(clusterContent, 0xA3, simpleBlockPayload2);

        WriteEBMLElement(segContent, 0x1F43B675, clusterContent.ToArray());

        // Attachments container (0x1941A469) with two AttachedFile children
        var attachmentsContent = new MemoryStream();

        // First attachment: font.ttf (1024 bytes)
        var attachedFile1 = new MemoryStream();
        WriteEBMLElement(attachedFile1, 0x466E, Encoding.UTF8.GetBytes("font.ttf"));
        WriteEBMLElement(attachedFile1, 0x4660, Encoding.UTF8.GetBytes("font/sfnt"));
        WriteEBMLElement(attachedFile1, 0x465C, CreateTestData(1024, seed: 77));
        WriteEBMLElement(attachmentsContent, 0x61A7, attachedFile1.ToArray());

        // Second attachment: subtitle.ass (512 bytes)
        var attachedFile2 = new MemoryStream();
        WriteEBMLElement(attachedFile2, 0x466E, Encoding.UTF8.GetBytes("subtitle.ass"));
        WriteEBMLElement(attachedFile2, 0x4660, Encoding.UTF8.GetBytes("text/x-ssa"));
        WriteEBMLElement(attachedFile2, 0x465C, CreateTestData(512, seed: 88));
        WriteEBMLElement(attachmentsContent, 0x61A7, attachedFile2.ToArray());

        WriteEBMLElement(segContent, 0x1941A469, attachmentsContent.ToArray());

        WriteEBMLElement(ms, 0x18538067, segContent.ToArray());

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    /// <summary>
    /// Builds a "movie" MKV that has extra clusters before and after
    /// the same block data that appears in the sample. This simulates
    /// a real media file where the sample data is embedded deep in the file.
    /// </summary>
    private string BuildSyntheticMovieMKV(string samplePath)
    {
        string path = Path.Combine(_tempDir, "test_movie.mkv");
        using var ms = new MemoryStream();

        byte[] ebmlContent = BuildEBMLHeaderContent();
        WriteEBMLElement(ms, 0x1A45DFA3, ebmlContent);

        var segContent = new MemoryStream();

        // Add Tracks metadata element (0x1654AE6B) like a real MKV
        var tracksContent = new MemoryStream();
        var track1Entry = new MemoryStream();
        WriteEBMLElement(track1Entry, 0xD7, [1]); // TrackNumber = 1
        WriteEBMLElement(track1Entry, 0x73C5, [1]); // TrackUID
        WriteEBMLElement(track1Entry, 0x83, [1]); // TrackType = video
        WriteEBMLElement(track1Entry, 0x86, Encoding.UTF8.GetBytes("V_MS/VFW/FOURCC")); // CodecID
        WriteEBMLElement(tracksContent, 0xAE, track1Entry.ToArray());
        var track2Entry = new MemoryStream();
        WriteEBMLElement(track2Entry, 0xD7, [2]); // TrackNumber = 2
        WriteEBMLElement(track2Entry, 0x73C5, [2]); // TrackUID
        WriteEBMLElement(track2Entry, 0x83, [2]); // TrackType = audio
        WriteEBMLElement(track2Entry, 0x86, Encoding.UTF8.GetBytes("A_AC3")); // CodecID
        WriteEBMLElement(tracksContent, 0xAE, track2Entry.ToArray());
        WriteEBMLElement(segContent, 0x1654AE6B, tracksContent.ToArray());

        // Pre-sample clusters with different data (10 clusters)
        for (int c = 0; c < 10; c++)
        {
            var cluster = new MemoryStream();
            WriteEBMLElement(cluster, 0xE7, [(byte)c]); // Timecode

            byte[] preData1 = CreateTestData(512, seed: 200 + c);
            byte[] prePayload1 = new byte[1 + 2 + 1 + preData1.Length];
            prePayload1[0] = 0x81; // Track 1
            prePayload1[3] = 0x80;
            preData1.CopyTo(prePayload1, 4);
            WriteEBMLElement(cluster, 0xA3, prePayload1);

            byte[] preData2 = CreateTestData(256, seed: 300 + c);
            byte[] prePayload2 = new byte[1 + 2 + 1 + preData2.Length];
            prePayload2[0] = 0x82; // Track 2
            prePayload2[3] = 0x80;
            preData2.CopyTo(prePayload2, 4);
            WriteEBMLElement(cluster, 0xA3, prePayload2);

            WriteEBMLElement(segContent, 0x1F43B675, cluster.ToArray());
        }

        // The cluster containing the SAME data as the sample
        // (uses the same seeds as BuildSyntheticMKV)
        var sampleCluster = new MemoryStream();
        WriteEBMLElement(sampleCluster, 0xE7, [10]); // Timecode

        byte[] blockData = CreateTestData(512); // seed: 42 — matches sample
        byte[] simpleBlockPayload = new byte[1 + 2 + 1 + blockData.Length];
        simpleBlockPayload[0] = 0x81; // Track 1
        simpleBlockPayload[3] = 0x80;
        blockData.CopyTo(simpleBlockPayload, 4);
        WriteEBMLElement(sampleCluster, 0xA3, simpleBlockPayload);

        byte[] blockData2 = CreateTestData(256, seed: 99); // matches sample
        byte[] simpleBlockPayload2 = new byte[1 + 2 + 1 + blockData2.Length];
        simpleBlockPayload2[0] = 0x82; // Track 2
        simpleBlockPayload2[3] = 0x80;
        blockData2.CopyTo(simpleBlockPayload2, 4);
        WriteEBMLElement(sampleCluster, 0xA3, simpleBlockPayload2);

        WriteEBMLElement(segContent, 0x1F43B675, sampleCluster.ToArray());

        // Post-sample clusters with different data (5 clusters)
        for (int c = 0; c < 5; c++)
        {
            var cluster = new MemoryStream();
            WriteEBMLElement(cluster, 0xE7, [(byte)(11 + c)]); // Timecode

            byte[] postData1 = CreateTestData(512, seed: 400 + c);
            byte[] postPayload1 = new byte[1 + 2 + 1 + postData1.Length];
            postPayload1[0] = 0x81;
            postPayload1[3] = 0x80;
            postData1.CopyTo(postPayload1, 4);
            WriteEBMLElement(cluster, 0xA3, postPayload1);

            byte[] postData2 = CreateTestData(256, seed: 500 + c);
            byte[] postPayload2 = new byte[1 + 2 + 1 + postData2.Length];
            postPayload2[0] = 0x82;
            postPayload2[3] = 0x80;
            postData2.CopyTo(postPayload2, 4);
            WriteEBMLElement(cluster, 0xA3, postPayload2);

            WriteEBMLElement(segContent, 0x1F43B675, cluster.ToArray());
        }

        WriteEBMLElement(ms, 0x18538067, segContent.ToArray());

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    /// <summary>
    /// Builds a realistic MKV movie file with Segment using unknown size,
    /// SeekHead, Void padding, Info, Tracks, many clusters, and Cues.
    /// The sample data (seed 42 / seed 99) is embedded in a middle cluster.
    /// </summary>
    private string BuildRealisticMovieMKV()
    {
        string path = Path.Combine(_tempDir, "test_realistic_movie.mkv");
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);

        // EBML Header
        byte[] ebmlContent = BuildEBMLHeaderContent();
        WriteEBMLElement(fs, 0x1A45DFA3, ebmlContent);

        // Segment with unknown size (0xFF = 1-byte unknown VINT)
        byte[] segId = EncodeEBMLId(0x18538067);
        fs.Write(segId);
        fs.WriteByte(0xFF); // Unknown size (1 byte, all data bits set)

        // SeekHead (0x114D9B74) — 64 bytes of dummy data
        WriteEBMLElement(fs, 0x114D9B74, new byte[64]);

        // Void element (0xEC) — 256 bytes of padding
        WriteEBMLElement(fs, 0xEC, new byte[256]);

        // Info (0x1549A966) — 32 bytes of dummy data
        WriteEBMLElement(fs, 0x1549A966, new byte[32]);

        // Tracks (0x1654AE6B)
        var tracksContent = new MemoryStream();
        var track1Entry = new MemoryStream();
        WriteEBMLElement(track1Entry, 0xD7, [1]); // TrackNumber
        WriteEBMLElement(track1Entry, 0x73C5, [1]); // TrackUID
        WriteEBMLElement(track1Entry, 0x83, [1]); // TrackType = video
        WriteEBMLElement(track1Entry, 0x86, "V_MPEG4/ISO/AVC"u8.ToArray());
        WriteEBMLElement(tracksContent, 0xAE, track1Entry.ToArray());
        var track2Entry = new MemoryStream();
        WriteEBMLElement(track2Entry, 0xD7, [2]); // TrackNumber
        WriteEBMLElement(track2Entry, 0x73C5, [2]); // TrackUID
        WriteEBMLElement(track2Entry, 0x83, [2]); // TrackType = audio
        WriteEBMLElement(track2Entry, 0x86, "A_AC3"u8.ToArray());
        WriteEBMLElement(tracksContent, 0xAE, track2Entry.ToArray());
        WriteEBMLElement(fs, 0x1654AE6B, tracksContent.ToArray());

        // 50 pre-sample clusters with different data
        for (int c = 0; c < 50; c++)
        {
            var cluster = new MemoryStream();
            WriteEBMLElement(cluster, 0xE7, [(byte)(c & 0xFF)]); // Timecode

            byte[] preData1 = CreateTestData(512, seed: 1000 + c);
            byte[] prePayload1 = new byte[1 + 2 + 1 + preData1.Length];
            prePayload1[0] = 0x81; // Track 1
            prePayload1[3] = 0x80; // Keyframe
            preData1.CopyTo(prePayload1, 4);
            WriteEBMLElement(cluster, 0xA3, prePayload1);

            byte[] preData2 = CreateTestData(256, seed: 2000 + c);
            byte[] prePayload2 = new byte[1 + 2 + 1 + preData2.Length];
            prePayload2[0] = 0x82; // Track 2
            prePayload2[3] = 0x80;
            preData2.CopyTo(prePayload2, 4);
            WriteEBMLElement(cluster, 0xA3, prePayload2);

            WriteEBMLElement(fs, 0x1F43B675, cluster.ToArray());
        }

        // The cluster with sample data (same seeds as BuildSyntheticMKV)
        var sampleCluster = new MemoryStream();
        WriteEBMLElement(sampleCluster, 0xE7, [50]); // Timecode
        byte[] blockData = CreateTestData(512); // seed: 42
        byte[] sbPayload = new byte[1 + 2 + 1 + blockData.Length];
        sbPayload[0] = 0x81;
        sbPayload[3] = 0x80;
        blockData.CopyTo(sbPayload, 4);
        WriteEBMLElement(sampleCluster, 0xA3, sbPayload);
        byte[] blockData2 = CreateTestData(256, seed: 99);
        byte[] sbPayload2 = new byte[1 + 2 + 1 + blockData2.Length];
        sbPayload2[0] = 0x82;
        sbPayload2[3] = 0x80;
        blockData2.CopyTo(sbPayload2, 4);
        WriteEBMLElement(sampleCluster, 0xA3, sbPayload2);
        WriteEBMLElement(fs, 0x1F43B675, sampleCluster.ToArray());

        // 20 post-sample clusters
        for (int c = 0; c < 20; c++)
        {
            var cluster = new MemoryStream();
            WriteEBMLElement(cluster, 0xE7, [(byte)((51 + c) & 0xFF)]);

            byte[] postData1 = CreateTestData(512, seed: 3000 + c);
            byte[] postPayload1 = new byte[1 + 2 + 1 + postData1.Length];
            postPayload1[0] = 0x81;
            postPayload1[3] = 0x80;
            postData1.CopyTo(postPayload1, 4);
            WriteEBMLElement(cluster, 0xA3, postPayload1);

            WriteEBMLElement(fs, 0x1F43B675, cluster.ToArray());
        }

        // Cues (0x1C53BB6B) — 128 bytes of dummy data at the end
        WriteEBMLElement(fs, 0x1C53BB6B, new byte[128]);

        return path;
    }

    private string BuildSyntheticMP4()
    {
        string path = Path.Combine(_tempDir, "test_sample.mp4");
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

    private string BuildSyntheticFlac()
    {
        string path = Path.Combine(_tempDir, "test_sample.flac");
        using var ms = new MemoryStream();

        ms.Write(Encoding.ASCII.GetBytes("fLaC"));

        byte[] streamInfo = new byte[34];
        byte header = 0x80; // is_last=1, type=0
        ms.WriteByte(header);
        ms.WriteByte(0);
        ms.WriteByte(0);
        ms.WriteByte(34);
        ms.Write(streamInfo);

        byte[] frameData = CreateTestData(512);
        ms.Write(frameData);

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    private string BuildSyntheticMP3()
    {
        string path = Path.Combine(_tempDir, "test_sample.mp3");
        using var ms = new MemoryStream();

        // ID3v2 header
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

        // MP3 sync frames
        byte[] audioData = CreateTestData(512);
        audioData[0] = 0xFF;
        audioData[1] = 0xFB;
        ms.Write(audioData);

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    private string BuildSyntheticStream()
    {
        string path = Path.Combine(_tempDir, "test_sample.vob");
        byte[] data = CreateTestData(1024);
        File.WriteAllBytes(path, data);
        return path;
    }

    /// <summary>
    /// Builds an MKV with a SimpleBlock using Xiph lacing (2 frames per block).
    /// </summary>
    private string BuildSyntheticMKVWithXiphLacing()
    {
        string path = Path.Combine(_tempDir, "test_xiph_lacing.mkv");
        using var ms = new MemoryStream();

        WriteEBMLElement(ms, 0x1A45DFA3, BuildEBMLHeaderContent());

        var segContent = new MemoryStream();
        var clusterContent = new MemoryStream();

        // Track 1: non-laced video block (>= 256 bytes for signature)
        byte[] videoData = CreateTestData(512, seed: 60);
        byte[] videoPayload = new byte[1 + 2 + 1 + videoData.Length];
        videoPayload[0] = 0x81; // Track 1
        videoPayload[3] = 0x80; // Keyframe, no lacing
        videoData.CopyTo(videoPayload, 4);
        WriteEBMLElement(clusterContent, 0xA3, videoPayload);

        // Track 2: Xiph-laced audio block with 2 frames
        byte[] frame1 = CreateTestData(128, seed: 61);
        byte[] frame2 = CreateTestData(200, seed: 62);
        byte[] lacedPayload = BuildXiphLacedBlock(trackNum: 2, [frame1, frame2]);
        WriteEBMLElement(clusterContent, 0xA3, lacedPayload);

        // Track 2: second laced block so signature is at least 256 bytes
        byte[] frame3 = CreateTestData(128, seed: 63);
        byte[] frame4 = CreateTestData(150, seed: 64);
        byte[] lacedPayload2 = BuildXiphLacedBlock(trackNum: 2, [frame3, frame4]);
        WriteEBMLElement(clusterContent, 0xA3, lacedPayload2);

        WriteEBMLElement(segContent, 0x1F43B675, clusterContent.ToArray());
        WriteEBMLElement(ms, 0x18538067, segContent.ToArray());

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    /// <summary>
    /// Builds an MKV with a SimpleBlock using fixed-size lacing (3 equal frames).
    /// </summary>
    private string BuildSyntheticMKVWithFixedLacing()
    {
        string path = Path.Combine(_tempDir, "test_fixed_lacing.mkv");
        using var ms = new MemoryStream();

        WriteEBMLElement(ms, 0x1A45DFA3, BuildEBMLHeaderContent());

        var segContent = new MemoryStream();
        var clusterContent = new MemoryStream();

        // Track 1: non-laced video (>= 256 bytes for signature)
        byte[] videoData = CreateTestData(512, seed: 70);
        byte[] videoPayload = new byte[1 + 2 + 1 + videoData.Length];
        videoPayload[0] = 0x81;
        videoPayload[3] = 0x80;
        videoData.CopyTo(videoPayload, 4);
        WriteEBMLElement(clusterContent, 0xA3, videoPayload);

        // Track 2: fixed-size laced block with 3 frames of 100 bytes each
        byte[] f1 = CreateTestData(100, seed: 71);
        byte[] f2 = CreateTestData(100, seed: 72);
        byte[] f3 = CreateTestData(100, seed: 73);
        byte[] lacedPayload = BuildFixedLacedBlock(trackNum: 2, [f1, f2, f3]);
        WriteEBMLElement(clusterContent, 0xA3, lacedPayload);

        WriteEBMLElement(segContent, 0x1F43B675, clusterContent.ToArray());
        WriteEBMLElement(ms, 0x18538067, segContent.ToArray());

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    /// <summary>
    /// Builds an MKV with a SimpleBlock using EBML lacing (2 frames).
    /// </summary>
    private string BuildSyntheticMKVWithEBMLLacing()
    {
        string path = Path.Combine(_tempDir, "test_ebml_lacing.mkv");
        using var ms = new MemoryStream();

        WriteEBMLElement(ms, 0x1A45DFA3, BuildEBMLHeaderContent());

        var segContent = new MemoryStream();
        var clusterContent = new MemoryStream();

        // Track 1: non-laced video (>= 256 bytes for signature)
        byte[] videoData = CreateTestData(512, seed: 80);
        byte[] videoPayload = new byte[1 + 2 + 1 + videoData.Length];
        videoPayload[0] = 0x81;
        videoPayload[3] = 0x80;
        videoData.CopyTo(videoPayload, 4);
        WriteEBMLElement(clusterContent, 0xA3, videoPayload);

        // Track 2: EBML-laced block with 2 frames
        byte[] f1 = CreateTestData(150, seed: 81);
        byte[] f2 = CreateTestData(200, seed: 82);
        byte[] lacedPayload = BuildEBMLLacedBlock(trackNum: 2, [f1, f2]);
        WriteEBMLElement(clusterContent, 0xA3, lacedPayload);

        WriteEBMLElement(segContent, 0x1F43B675, clusterContent.ToArray());
        WriteEBMLElement(ms, 0x18538067, segContent.ToArray());

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    /// <summary>
    /// Builds an MKV with non-laced video (Track 1) and Xiph-laced audio (Track 2).
    /// Multiple interleaved blocks ensure each track has >= 256 bytes for signatures.
    /// </summary>
    private string BuildSyntheticMKVWithMixedLacing()
    {
        string path = Path.Combine(_tempDir, "test_mixed_lacing.mkv");
        using var ms = new MemoryStream();

        WriteEBMLElement(ms, 0x1A45DFA3, BuildEBMLHeaderContent());

        var segContent = new MemoryStream();
        var clusterContent = new MemoryStream();

        // Video block 1 (non-laced, track 1)
        byte[] v1 = CreateTestData(400, seed: 90);
        byte[] vp1 = new byte[1 + 2 + 1 + v1.Length];
        vp1[0] = 0x81;
        vp1[3] = 0x80;
        v1.CopyTo(vp1, 4);
        WriteEBMLElement(clusterContent, 0xA3, vp1);

        // Audio block 1 (Xiph-laced, track 2, 2 frames)
        byte[] a1f1 = CreateTestData(128, seed: 91);
        byte[] a1f2 = CreateTestData(160, seed: 92);
        WriteEBMLElement(clusterContent, 0xA3,
            BuildXiphLacedBlock(trackNum: 2, [a1f1, a1f2]));

        // Video block 2 (non-laced, track 1)
        byte[] v2 = CreateTestData(400, seed: 93);
        byte[] vp2 = new byte[1 + 2 + 1 + v2.Length];
        vp2[0] = 0x81;
        vp2[3] = 0x80;
        v2.CopyTo(vp2, 4);
        WriteEBMLElement(clusterContent, 0xA3, vp2);

        // Audio block 2 (Xiph-laced, track 2, 3 frames)
        byte[] a2f1 = CreateTestData(100, seed: 94);
        byte[] a2f2 = CreateTestData(100, seed: 95);
        byte[] a2f3 = CreateTestData(120, seed: 96);
        WriteEBMLElement(clusterContent, 0xA3,
            BuildXiphLacedBlock(trackNum: 2, [a2f1, a2f2, a2f3]));

        WriteEBMLElement(segContent, 0x1F43B675, clusterContent.ToArray());
        WriteEBMLElement(ms, 0x18538067, segContent.ToArray());

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    /// <summary>
    /// Builds a larger MKV with the same Xiph-laced data as the sample
    /// embedded in a middle cluster, surrounded by other clusters with different data.
    /// </summary>
    private string BuildLargerMKVWithXiphLacing()
    {
        string path = Path.Combine(_tempDir, "test_xiph_movie.mkv");
        using var ms = new MemoryStream();

        WriteEBMLElement(ms, 0x1A45DFA3, BuildEBMLHeaderContent());

        var segContent = new MemoryStream();

        // 5 pre-sample clusters with different data
        for (int c = 0; c < 5; c++)
        {
            var cluster = new MemoryStream();
            WriteEBMLElement(cluster, 0xE7, [(byte)c]);

            byte[] preVideo = CreateTestData(512, seed: 500 + c);
            byte[] prePl = new byte[1 + 2 + 1 + preVideo.Length];
            prePl[0] = 0x81;
            prePl[3] = 0x80;
            preVideo.CopyTo(prePl, 4);
            WriteEBMLElement(cluster, 0xA3, prePl);

            byte[] preF1 = CreateTestData(128, seed: 600 + c);
            byte[] preF2 = CreateTestData(200, seed: 700 + c);
            WriteEBMLElement(cluster, 0xA3,
                BuildXiphLacedBlock(trackNum: 2, [preF1, preF2]));

            WriteEBMLElement(segContent, 0x1F43B675, cluster.ToArray());
        }

        // The sample cluster — uses same seeds as BuildSyntheticMKVWithXiphLacing
        var sampleCluster = new MemoryStream();
        WriteEBMLElement(sampleCluster, 0xE7, [5]);

        byte[] videoData = CreateTestData(512, seed: 60);
        byte[] videoPayload = new byte[1 + 2 + 1 + videoData.Length];
        videoPayload[0] = 0x81;
        videoPayload[3] = 0x80;
        videoData.CopyTo(videoPayload, 4);
        WriteEBMLElement(sampleCluster, 0xA3, videoPayload);

        byte[] frame1 = CreateTestData(128, seed: 61);
        byte[] frame2 = CreateTestData(200, seed: 62);
        WriteEBMLElement(sampleCluster, 0xA3,
            BuildXiphLacedBlock(trackNum: 2, [frame1, frame2]));

        byte[] frame3 = CreateTestData(128, seed: 63);
        byte[] frame4 = CreateTestData(150, seed: 64);
        WriteEBMLElement(sampleCluster, 0xA3,
            BuildXiphLacedBlock(trackNum: 2, [frame3, frame4]));

        WriteEBMLElement(segContent, 0x1F43B675, sampleCluster.ToArray());

        // 3 post-sample clusters
        for (int c = 0; c < 3; c++)
        {
            var cluster = new MemoryStream();
            WriteEBMLElement(cluster, 0xE7, [(byte)(6 + c)]);

            byte[] postVideo = CreateTestData(512, seed: 800 + c);
            byte[] postPl = new byte[1 + 2 + 1 + postVideo.Length];
            postPl[0] = 0x81;
            postPl[3] = 0x80;
            postVideo.CopyTo(postPl, 4);
            WriteEBMLElement(cluster, 0xA3, postPl);

            WriteEBMLElement(segContent, 0x1F43B675, cluster.ToArray());
        }

        WriteEBMLElement(ms, 0x18538067, segContent.ToArray());
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    /// <summary>
    /// Builds an MKV with 3 clusters, each containing a Timestamp element
    /// followed by interleaved video and audio blocks.
    /// </summary>
    private string BuildSyntheticMKVMultiCluster()
    {
        string path = Path.Combine(_tempDir, "test_multi_cluster.mkv");
        using var ms = new MemoryStream();

        WriteEBMLElement(ms, 0x1A45DFA3, BuildEBMLHeaderContent());

        var segContent = new MemoryStream();

        for (int c = 0; c < 3; c++)
        {
            var cluster = new MemoryStream();
            WriteEBMLElement(cluster, 0xE7, [(byte)c]); // Timestamp

            byte[] videoData = CreateTestData(512, seed: 100 + c);
            byte[] videoPayload = new byte[1 + 2 + 1 + videoData.Length];
            videoPayload[0] = 0x81;
            videoPayload[3] = 0x80;
            videoData.CopyTo(videoPayload, 4);
            WriteEBMLElement(cluster, 0xA3, videoPayload);

            byte[] audioData = CreateTestData(256, seed: 200 + c);
            byte[] audioPayload = new byte[1 + 2 + 1 + audioData.Length];
            audioPayload[0] = 0x82;
            audioPayload[3] = 0x80;
            audioData.CopyTo(audioPayload, 4);
            WriteEBMLElement(cluster, 0xA3, audioPayload);

            WriteEBMLElement(segContent, 0x1F43B675, cluster.ToArray());
        }

        WriteEBMLElement(ms, 0x18538067, segContent.ToArray());
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    /// <summary>
    /// Builds an MKV with many blocks for stress testing.
    /// </summary>
    private string BuildSyntheticMKVManyBlocks(int clusterCount, int blocksPerCluster)
    {
        string path = Path.Combine(_tempDir, "test_many_blocks.mkv");
        using var ms = new MemoryStream();

        WriteEBMLElement(ms, 0x1A45DFA3, BuildEBMLHeaderContent());

        var segContent = new MemoryStream();

        for (int c = 0; c < clusterCount; c++)
        {
            var cluster = new MemoryStream();
            WriteEBMLElement(cluster, 0xE7, [(byte)(c & 0xFF)]); // Timestamp

            for (int b = 0; b < blocksPerCluster; b++)
            {
                int seed = 1000 + c * 100 + b;
                int dataSize = (b % 2 == 0) ? 512 : 256; // alternate sizes
                byte trackNum = (byte)(b % 2 == 0 ? 1 : 2); // alternate tracks

                byte[] data = CreateTestData(dataSize, seed: seed);
                byte[] payload = new byte[1 + 2 + 1 + data.Length];
                payload[0] = (byte)(0x80 | trackNum);
                payload[3] = 0x80; // Keyframe
                data.CopyTo(payload, 4);
                WriteEBMLElement(cluster, 0xA3, payload);
            }

            WriteEBMLElement(segContent, 0x1F43B675, cluster.ToArray());
        }

        WriteEBMLElement(ms, 0x18538067, segContent.ToArray());
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    /// <summary>
    /// Builds an MKV with Timestamp elements in each cluster to verify
    /// that non-block elements between blocks are handled correctly.
    /// </summary>
    private string BuildSyntheticMKVWithTimestamps()
    {
        string path = Path.Combine(_tempDir, "test_timestamps.mkv");
        using var ms = new MemoryStream();

        WriteEBMLElement(ms, 0x1A45DFA3, BuildEBMLHeaderContent());

        var segContent = new MemoryStream();

        // Two clusters with Timestamp + multiple blocks each
        for (int c = 0; c < 2; c++)
        {
            var cluster = new MemoryStream();
            // Timestamp with 2-byte value
            byte[] tsValue = [(byte)(c >> 8), (byte)(c & 0xFF)];
            WriteEBMLElement(cluster, 0xE7, tsValue);

            // 3 video blocks per cluster
            for (int b = 0; b < 3; b++)
            {
                byte[] data = CreateTestData(400, seed: 110 + c * 10 + b);
                byte[] payload = new byte[1 + 2 + 1 + data.Length];
                payload[0] = 0x81; // Track 1
                byte[] timecode = BitConverter.GetBytes((short)(b * 33));
                if (BitConverter.IsLittleEndian)
                {
                    Array.Reverse(timecode);
                }
                payload[1] = timecode[0];
                payload[2] = timecode[1];
                payload[3] = 0x80; // Keyframe
                data.CopyTo(payload, 4);
                WriteEBMLElement(cluster, 0xA3, payload);
            }

            // 2 audio blocks per cluster
            for (int b = 0; b < 2; b++)
            {
                byte[] data = CreateTestData(256, seed: 210 + c * 10 + b);
                byte[] payload = new byte[1 + 2 + 1 + data.Length];
                payload[0] = 0x82; // Track 2
                payload[3] = 0x80;
                data.CopyTo(payload, 4);
                WriteEBMLElement(cluster, 0xA3, payload);
            }

            WriteEBMLElement(segContent, 0x1F43B675, cluster.ToArray());
        }

        WriteEBMLElement(ms, 0x18538067, segContent.ToArray());
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    /// <summary>
    /// Builds an MKV using Block (0xA1) inside BlockGroup (0xA0) instead of SimpleBlock.
    /// </summary>
    /// <summary>
    /// Builds an MKV with two tracks where track 2 has many small block payloads
    /// (30 bytes each) — mimicking a real-world subtitle track whose signature
    /// must be reconstructed from the concatenation of multiple non-contiguous
    /// blocks. The subtitle blocks are interleaved with track 1's video blocks
    /// inside each cluster, so the concatenated signature does NOT appear as a
    /// contiguous byte sequence anywhere in the file.
    /// </summary>
    private string BuildSyntheticMKVWithSubtitleStyleTrack()
    {
        string path = Path.Combine(_tempDir, "test_sub_sample.mkv");
        using var ms = new MemoryStream();

        WriteEBMLElement(ms, 0x1A45DFA3, BuildEBMLHeaderContent());

        var segContent = new MemoryStream();

        // 4 clusters; each holds 1 video block + 3 subtitle blocks (interleaved).
        // 12 subtitle blocks × 30 bytes = 360 bytes total, so the 256-byte
        // signature for track 2 spans 9 separate blocks across ~3 clusters.
        int subBlockIndex = 0;
        for (int c = 0; c < 4; c++)
        {
            var cluster = new MemoryStream();
            WriteEBMLElement(cluster, 0xE7, [(byte)c]); // Timecode

            // Video block (track 1)
            byte[] videoData = CreateTestData(512, seed: 1000 + c);
            byte[] videoPayload = new byte[1 + 2 + 1 + videoData.Length];
            videoPayload[0] = 0x81; // Track 1
            videoPayload[3] = 0x80; // Keyframe
            videoData.CopyTo(videoPayload, 4);
            WriteEBMLElement(cluster, 0xA3, videoPayload);

            // 3 subtitle-style blocks (track 2), interleaved AFTER the video
            for (int i = 0; i < 3; i++)
            {
                byte[] subData = CreateTestData(30, seed: 2000 + subBlockIndex);
                subBlockIndex++;
                byte[] subPayload = new byte[1 + 2 + 1 + subData.Length];
                subPayload[0] = 0x82; // Track 2
                subPayload[3] = 0x00;
                subData.CopyTo(subPayload, 4);
                WriteEBMLElement(cluster, 0xA3, subPayload);
            }

            WriteEBMLElement(segContent, 0x1F43B675, cluster.ToArray());
        }

        WriteEBMLElement(ms, 0x18538067, segContent.ToArray());

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    /// <summary>
    /// Builds a larger "movie" MKV that contains the same sample clusters as
    /// <see cref="BuildSyntheticMKVWithSubtitleStyleTrack"/>, plus surrounding
    /// pre/post clusters with different data. Used to verify the EBML-aware
    /// finder can locate a subtitle-style track's signature inside a media file
    /// far larger than the original sample.
    /// </summary>
    private string BuildLargerMKVWithSubtitleStyleTrack()
    {
        string path = Path.Combine(_tempDir, "test_sub_movie.mkv");
        using var ms = new MemoryStream();

        WriteEBMLElement(ms, 0x1A45DFA3, BuildEBMLHeaderContent());

        var segContent = new MemoryStream();

        // Tracks metadata (mirrors a real MKV)
        var tracksContent = new MemoryStream();
        var track1Entry = new MemoryStream();
        WriteEBMLElement(track1Entry, 0xD7, [1]);
        WriteEBMLElement(track1Entry, 0x73C5, [1]);
        WriteEBMLElement(track1Entry, 0x83, [1]);
        WriteEBMLElement(track1Entry, 0x86, Encoding.UTF8.GetBytes("V_MS/VFW/FOURCC"));
        WriteEBMLElement(tracksContent, 0xAE, track1Entry.ToArray());
        var track2Entry = new MemoryStream();
        WriteEBMLElement(track2Entry, 0xD7, [2]);
        WriteEBMLElement(track2Entry, 0x73C5, [2]);
        WriteEBMLElement(track2Entry, 0x83, [0x11]); // TrackType = subtitle
        WriteEBMLElement(track2Entry, 0x86, Encoding.UTF8.GetBytes("S_TEXT/UTF8"));
        WriteEBMLElement(tracksContent, 0xAE, track2Entry.ToArray());
        WriteEBMLElement(segContent, 0x1654AE6B, tracksContent.ToArray());

        // Pre-sample clusters with different data
        for (int c = 0; c < 3; c++)
        {
            var cluster = new MemoryStream();
            WriteEBMLElement(cluster, 0xE7, [(byte)c]);

            byte[] preVid = CreateTestData(512, seed: 9000 + c);
            byte[] preVidPayload = new byte[1 + 2 + 1 + preVid.Length];
            preVidPayload[0] = 0x81;
            preVidPayload[3] = 0x80;
            preVid.CopyTo(preVidPayload, 4);
            WriteEBMLElement(cluster, 0xA3, preVidPayload);

            for (int i = 0; i < 3; i++)
            {
                byte[] preSub = CreateTestData(30, seed: 9500 + c * 10 + i);
                byte[] preSubPayload = new byte[1 + 2 + 1 + preSub.Length];
                preSubPayload[0] = 0x82;
                preSubPayload[3] = 0x00;
                preSub.CopyTo(preSubPayload, 4);
                WriteEBMLElement(cluster, 0xA3, preSubPayload);
            }

            WriteEBMLElement(segContent, 0x1F43B675, cluster.ToArray());
        }

        // The sample clusters — same seeds as BuildSyntheticMKVWithSubtitleStyleTrack
        int subBlockIndex = 0;
        for (int c = 0; c < 4; c++)
        {
            var cluster = new MemoryStream();
            WriteEBMLElement(cluster, 0xE7, [(byte)(100 + c)]);

            byte[] videoData = CreateTestData(512, seed: 1000 + c);
            byte[] videoPayload = new byte[1 + 2 + 1 + videoData.Length];
            videoPayload[0] = 0x81;
            videoPayload[3] = 0x80;
            videoData.CopyTo(videoPayload, 4);
            WriteEBMLElement(cluster, 0xA3, videoPayload);

            for (int i = 0; i < 3; i++)
            {
                byte[] subData = CreateTestData(30, seed: 2000 + subBlockIndex);
                subBlockIndex++;
                byte[] subPayload = new byte[1 + 2 + 1 + subData.Length];
                subPayload[0] = 0x82;
                subPayload[3] = 0x00;
                subData.CopyTo(subPayload, 4);
                WriteEBMLElement(cluster, 0xA3, subPayload);
            }

            WriteEBMLElement(segContent, 0x1F43B675, cluster.ToArray());
        }

        // Post-sample clusters with different data
        for (int c = 0; c < 3; c++)
        {
            var cluster = new MemoryStream();
            WriteEBMLElement(cluster, 0xE7, [(byte)(200 + c)]);

            byte[] postVid = CreateTestData(512, seed: 8000 + c);
            byte[] postVidPayload = new byte[1 + 2 + 1 + postVid.Length];
            postVidPayload[0] = 0x81;
            postVidPayload[3] = 0x80;
            postVid.CopyTo(postVidPayload, 4);
            WriteEBMLElement(cluster, 0xA3, postVidPayload);

            for (int i = 0; i < 3; i++)
            {
                byte[] postSub = CreateTestData(30, seed: 8500 + c * 10 + i);
                byte[] postSubPayload = new byte[1 + 2 + 1 + postSub.Length];
                postSubPayload[0] = 0x82;
                postSubPayload[3] = 0x00;
                postSub.CopyTo(postSubPayload, 4);
                WriteEBMLElement(cluster, 0xA3, postSubPayload);
            }

            WriteEBMLElement(segContent, 0x1F43B675, cluster.ToArray());
        }

        WriteEBMLElement(ms, 0x18538067, segContent.ToArray());

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    private string BuildSyntheticMKVWithBlockGroup()
    {
        string path = Path.Combine(_tempDir, "test_blockgroup.mkv");
        using var ms = new MemoryStream();

        WriteEBMLElement(ms, 0x1A45DFA3, BuildEBMLHeaderContent());

        var segContent = new MemoryStream();
        var clusterContent = new MemoryStream();

        // Track 1: Block inside BlockGroup
        byte[] videoData = CreateTestData(512, seed: 120);
        byte[] blockPayload = new byte[1 + 2 + 1 + videoData.Length];
        blockPayload[0] = 0x81; // Track 1
        blockPayload[3] = 0x00; // No flags for Block (no keyframe flag in Block)
        videoData.CopyTo(blockPayload, 4);

        var blockGroupContent = new MemoryStream();
        WriteEBMLElement(blockGroupContent, 0xA1, blockPayload); // Block
        WriteEBMLElement(blockGroupContent, 0x9B, [0x00, 0x21]); // BlockDuration = 33ms
        WriteEBMLElement(clusterContent, 0xA0, blockGroupContent.ToArray());

        // Track 2: also in BlockGroup
        byte[] audioData = CreateTestData(256, seed: 121);
        byte[] blockPayload2 = new byte[1 + 2 + 1 + audioData.Length];
        blockPayload2[0] = 0x82; // Track 2
        blockPayload2[3] = 0x00;
        audioData.CopyTo(blockPayload2, 4);

        var blockGroupContent2 = new MemoryStream();
        WriteEBMLElement(blockGroupContent2, 0xA1, blockPayload2);
        WriteEBMLElement(clusterContent, 0xA0, blockGroupContent2.ToArray());

        WriteEBMLElement(segContent, 0x1F43B675, clusterContent.ToArray());
        WriteEBMLElement(ms, 0x18538067, segContent.ToArray());

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    #endregion

    #region Helpers

    private static byte[] CreateTestData(int size, int seed = 42)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static void WriteEBMLElement(Stream stream, ulong id, byte[] data)
    {
        byte[] idBytes = EncodeEBMLId(id);
        stream.Write(idBytes);
        byte[] sizeBytes = EncodeEBMLSize(data.Length);
        stream.Write(sizeBytes);
        stream.Write(data);
    }

    private static byte[] EncodeEBMLId(ulong id)
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

    private static byte[] EncodeEBMLSize(long value)
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

    private static byte[] BuildEBMLHeaderContent()
    {
        var ms = new MemoryStream();
        WriteEBMLElement(ms, 0x4286, [1]);
        WriteEBMLElement(ms, 0x42F7, [1]);
        WriteEBMLElement(ms, 0x42F2, [4]);
        WriteEBMLElement(ms, 0x42F3, [8]);
        WriteEBMLElement(ms, 0x4282, Encoding.ASCII.GetBytes("matroska"));
        return ms.ToArray();
    }

    /// <summary>
    /// Builds a SimpleBlock payload with Xiph lacing.
    /// Flags byte: 0x82 = keyframe + Xiph lacing (bits 1-2 = 01).
    /// </summary>
    private static byte[] BuildXiphLacedBlock(int trackNum, byte[][] frames)
    {
        using var ms = new MemoryStream();
        ms.WriteByte((byte)(0x80 | trackNum)); // Track VINT
        ms.WriteByte(0);
        ms.WriteByte(0);      // Timecode = 0
        ms.WriteByte(0x82);                    // Keyframe + Xiph lacing

        // Lace count = number of frames - 1
        ms.WriteByte((byte)(frames.Length - 1));

        // Xiph frame sizes (all except last)
        for (int i = 0; i < frames.Length - 1; i++)
        {
            int size = frames[i].Length;
            while (size >= 255)
            {
                ms.WriteByte(0xFF);
                size -= 255;
            }
            ms.WriteByte((byte)size);
        }

        // Frame data
        foreach (byte[] frame in frames)
        {
            ms.Write(frame);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Builds a SimpleBlock payload with fixed-size lacing.
    /// Flags byte: 0x84 = keyframe + fixed-size lacing (bits 1-2 = 10).
    /// All frames must be the same size.
    /// </summary>
    private static byte[] BuildFixedLacedBlock(int trackNum, byte[][] frames)
    {
        using var ms = new MemoryStream();
        ms.WriteByte((byte)(0x80 | trackNum));
        ms.WriteByte(0);
        ms.WriteByte(0);
        ms.WriteByte(0x84); // Keyframe + fixed-size lacing

        ms.WriteByte((byte)(frames.Length - 1)); // Lace count

        foreach (byte[] frame in frames)
        {
            ms.Write(frame);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Builds a SimpleBlock payload with EBML lacing.
    /// Flags byte: 0x86 = keyframe + EBML lacing (bits 1-2 = 11).
    /// </summary>
    private static byte[] BuildEBMLLacedBlock(int trackNum, byte[][] frames)
    {
        using var ms = new MemoryStream();
        ms.WriteByte((byte)(0x80 | trackNum));
        ms.WriteByte(0);
        ms.WriteByte(0);
        ms.WriteByte(0x86); // Keyframe + EBML lacing

        ms.WriteByte((byte)(frames.Length - 1)); // Lace count

        // First frame size as unsigned EBML VINT
        WriteEBMLSizeVint(ms, frames[0].Length);

        // Delta sizes for subsequent frames (not last)
        for (int i = 1; i < frames.Length - 1; i++)
        {
            int delta = frames[i].Length - frames[i - 1].Length;
            WriteEBMLSignedVint(ms, delta);
        }

        foreach (byte[] frame in frames)
        {
            ms.Write(frame);
        }

        return ms.ToArray();
    }

    private static void WriteEBMLSizeVint(Stream stream, long value)
    {
        byte[] encoded = EncodeEBMLSize(value);
        stream.Write(encoded);
    }

    private static void WriteEBMLSignedVint(Stream stream, int value)
    {
        // Encode as signed VINT: value + bias
        // 1-byte: bias = 63, range = -63..63
        // 2-byte: bias = 8191, range = -8191..8191
        if (value is >= -63 and <= 63)
        {
            int encoded = value + 63;
            stream.WriteByte((byte)(0x80 | encoded));
        }
        else if (value is >= -8191 and <= 8191)
        {
            int encoded = value + 8191;
            stream.WriteByte((byte)(0x40 | (encoded >> 8)));
            stream.WriteByte((byte)(encoded & 0xFF));
        }
        else
        {
            int encoded = value + 1048575;
            stream.WriteByte((byte)(0x20 | (encoded >> 16)));
            stream.WriteByte((byte)((encoded >> 8) & 0xFF));
            stream.WriteByte((byte)(encoded & 0xFF));
        }
    }

    #endregion
}
