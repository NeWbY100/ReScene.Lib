using ReScene.SRR;
using ReScene.SRS;

namespace ReScene.Tests;

public class SRSFileFromSrrTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly string _tempDir;

    public SRSFileFromSrrTests()
    {
        _testDataDir = Path.Combine(AppContext.BaseDirectory, "TestData");
        _tempDir = Path.Combine(Path.GetTempPath(), $"srs_from_srr_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    #region SRS Extraction and Loading

    [Fact]
    public void Load_SrsFromSrr_DetectsContainerType()
    {
        string srrPath = Path.Combine(_testDataDir,
            "bug_detected_as_being_different3",
            "Akte.2012.08.01.German.Doku.WS.dTV.XViD-FiXTv_f4n4t.srr");
        var srr = SRRFile.Load(srrPath);

        var srsFiles = srr.StoredFiles
            .Where(s => s.FileName.EndsWith(".srs", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(srsFiles);

        foreach (var stored in srsFiles)
        {
            string outputDir = Path.Combine(_tempDir, "extract_container");
            string? extracted = srr.ExtractStoredFile(srrPath, outputDir,
                name => name == stored.FileName);
            Assert.NotNull(extracted);

            var srs = SRSFile.Load(extracted!);

            Assert.True(Enum.IsDefined(srs.ContainerType),
                $"ContainerType {srs.ContainerType} is not a valid enum value");
        }
    }

    [Fact]
    public void Load_SrsFromSrr_HasFileData()
    {
        string srrPath = Path.Combine(_testDataDir,
            "bug_detected_as_being_different3",
            "Akte.2012.08.01.German.Doku.WS.dTV.XViD-FiXTv_f4n4t.srr");
        var srr = SRRFile.Load(srrPath);

        var srsFiles = srr.StoredFiles
            .Where(s => s.FileName.EndsWith(".srs", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(srsFiles);

        foreach (var stored in srsFiles)
        {
            string outputDir = Path.Combine(_tempDir, "extract_filedata");
            string? extracted = srr.ExtractStoredFile(srrPath, outputDir,
                name => name == stored.FileName);
            Assert.NotNull(extracted);

            var srs = SRSFile.Load(extracted!);

            Assert.NotNull(srs.FileData);
        }
    }

    [Fact]
    public void Load_SrsFromSrr_FileDataHasNonEmptyFileName()
    {
        string srrPath = Path.Combine(_testDataDir,
            "bug_detected_as_being_different3",
            "Akte.2012.08.01.German.Doku.WS.dTV.XViD-FiXTv_f4n4t.srr");
        var srr = SRRFile.Load(srrPath);

        var srsFiles = srr.StoredFiles
            .Where(s => s.FileName.EndsWith(".srs", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(srsFiles);

        foreach (var stored in srsFiles)
        {
            string outputDir = Path.Combine(_tempDir, "extract_fname");
            string? extracted = srr.ExtractStoredFile(srrPath, outputDir,
                name => name == stored.FileName);
            Assert.NotNull(extracted);

            var srs = SRSFile.Load(extracted!);

            Assert.False(string.IsNullOrEmpty(srs.FileData!.FileName),
                $"SRS from {stored.FileName} has empty FileName");
        }
    }

    [Fact]
    public void Load_SrsFromSrr_FileDataHasNonZeroSampleSize()
    {
        string srrPath = Path.Combine(_testDataDir,
            "bug_detected_as_being_different3",
            "Akte.2012.08.01.German.Doku.WS.dTV.XViD-FiXTv_f4n4t.srr");
        var srr = SRRFile.Load(srrPath);

        var srsFiles = srr.StoredFiles
            .Where(s => s.FileName.EndsWith(".srs", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(srsFiles);

        foreach (var stored in srsFiles)
        {
            string outputDir = Path.Combine(_tempDir, "extract_size");
            string? extracted = srr.ExtractStoredFile(srrPath, outputDir,
                name => name == stored.FileName);
            Assert.NotNull(extracted);

            var srs = SRSFile.Load(extracted!);

            Assert.True(srs.FileData!.SampleSize > 0,
                $"SRS from {stored.FileName} has zero SampleSize");
        }
    }

    [Fact]
    public void Load_SrsFromSrr_FileDataHasNonZeroCrc32()
    {
        string srrPath = Path.Combine(_testDataDir,
            "bug_detected_as_being_different3",
            "Akte.2012.08.01.German.Doku.WS.dTV.XViD-FiXTv_f4n4t.srr");
        var srr = SRRFile.Load(srrPath);

        var srsFiles = srr.StoredFiles
            .Where(s => s.FileName.EndsWith(".srs", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(srsFiles);

        foreach (var stored in srsFiles)
        {
            string outputDir = Path.Combine(_tempDir, "extract_crc");
            string? extracted = srr.ExtractStoredFile(srrPath, outputDir,
                name => name == stored.FileName);
            Assert.NotNull(extracted);

            var srs = SRSFile.Load(extracted!);

            Assert.True(srs.FileData!.Crc32 != 0,
                $"SRS from {stored.FileName} has zero Crc32");
        }
    }

    [Fact]
    public void Load_SrsFromSrr_FileDataHasNonEmptyAppName()
    {
        string srrPath = Path.Combine(_testDataDir,
            "bug_detected_as_being_different3",
            "Akte.2012.08.01.German.Doku.WS.dTV.XViD-FiXTv_f4n4t.srr");
        var srr = SRRFile.Load(srrPath);

        var srsFiles = srr.StoredFiles
            .Where(s => s.FileName.EndsWith(".srs", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(srsFiles);

        foreach (var stored in srsFiles)
        {
            string outputDir = Path.Combine(_tempDir, "extract_appname");
            string? extracted = srr.ExtractStoredFile(srrPath, outputDir,
                name => name == stored.FileName);
            Assert.NotNull(extracted);

            var srs = SRSFile.Load(extracted!);

            Assert.False(string.IsNullOrEmpty(srs.FileData!.AppName),
                $"SRS from {stored.FileName} has empty AppName");
        }
    }

    [Fact]
    public void Load_SrsFromSrr_HasTracks()
    {
        string srrPath = Path.Combine(_testDataDir,
            "bug_detected_as_being_different3",
            "Akte.2012.08.01.German.Doku.WS.dTV.XViD-FiXTv_f4n4t.srr");
        var srr = SRRFile.Load(srrPath);

        var srsFiles = srr.StoredFiles
            .Where(s => s.FileName.EndsWith(".srs", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(srsFiles);

        foreach (var stored in srsFiles)
        {
            string outputDir = Path.Combine(_tempDir, "extract_tracks");
            string? extracted = srr.ExtractStoredFile(srrPath, outputDir,
                name => name == stored.FileName);
            Assert.NotNull(extracted);

            var srs = SRSFile.Load(extracted!);

            Assert.True(srs.Tracks.Count > 0,
                $"SRS from {stored.FileName} has no tracks");
        }
    }

    [Fact]
    public void Load_SrsFromSrr_TracksHaveNonZeroDataLength()
    {
        string srrPath = Path.Combine(_testDataDir,
            "bug_detected_as_being_different3",
            "Akte.2012.08.01.German.Doku.WS.dTV.XViD-FiXTv_f4n4t.srr");
        var srr = SRRFile.Load(srrPath);

        var srsFiles = srr.StoredFiles
            .Where(s => s.FileName.EndsWith(".srs", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(srsFiles);

        foreach (var stored in srsFiles)
        {
            string outputDir = Path.Combine(_tempDir, "extract_trklen");
            string? extracted = srr.ExtractStoredFile(srrPath, outputDir,
                name => name == stored.FileName);
            Assert.NotNull(extracted);

            var srs = SRSFile.Load(extracted!);

            foreach (var track in srs.Tracks)
            {
                Assert.True(track.DataLength > 0,
                    $"Track {track.TrackNumber} in {stored.FileName} has zero DataLength");
            }
        }
    }

    [Fact]
    public void Load_SrsFromSrr_TracksHave256ByteSignature()
    {
        string srrPath = Path.Combine(_testDataDir,
            "bug_detected_as_being_different3",
            "Akte.2012.08.01.German.Doku.WS.dTV.XViD-FiXTv_f4n4t.srr");
        var srr = SRRFile.Load(srrPath);

        var srsFiles = srr.StoredFiles
            .Where(s => s.FileName.EndsWith(".srs", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(srsFiles);

        foreach (var stored in srsFiles)
        {
            string outputDir = Path.Combine(_tempDir, "extract_sig");
            string? extracted = srr.ExtractStoredFile(srrPath, outputDir,
                name => name == stored.FileName);
            Assert.NotNull(extracted);

            var srs = SRSFile.Load(extracted!);

            foreach (var track in srs.Tracks)
            {
                Assert.Equal(256, track.SignatureSize);
                Assert.Equal(256, track.Signature.Length);
            }
        }
    }

    #endregion

    #region SRR Without Embedded SRS Files

    [Fact]
    public void StoreLittle_HasNoEmbeddedSrsFiles()
    {
        string srrPath = Path.Combine(_testDataDir, "store_little", "store_little.srr");
        var srr = SRRFile.Load(srrPath);

        var srsFiles = srr.StoredFiles
            .Where(s => s.FileName.EndsWith(".srs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(srsFiles);
    }

    #endregion

    #region All Embedded SRS Files in SRR

    [Fact]
    public void Load_AllSrsFromSrr_AllHaveFileDataAndTracks()
    {
        string srrPath = Path.Combine(_testDataDir,
            "bug_detected_as_being_different3",
            "Akte.2012.08.01.German.Doku.WS.dTV.XViD-FiXTv_f4n4t.srr");
        var srr = SRRFile.Load(srrPath);

        var srsFiles = srr.StoredFiles
            .Where(s => s.FileName.EndsWith(".srs", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(srsFiles);

        foreach (var stored in srsFiles)
        {
            string outputDir = Path.Combine(_tempDir, "extract_all");
            string? extracted = srr.ExtractStoredFile(srrPath, outputDir,
                name => name == stored.FileName);
            Assert.NotNull(extracted);

            var srs = SRSFile.Load(extracted!);

            Assert.NotNull(srs.FileData);
            Assert.False(string.IsNullOrEmpty(srs.FileData!.AppName),
                $"SRS {stored.FileName}: AppName is empty");
            Assert.False(string.IsNullOrEmpty(srs.FileData.FileName),
                $"SRS {stored.FileName}: FileName is empty");
            Assert.True(srs.FileData.SampleSize > 0,
                $"SRS {stored.FileName}: SampleSize is zero");
            Assert.True(srs.FileData.Crc32 != 0,
                $"SRS {stored.FileName}: Crc32 is zero");
            Assert.True(srs.Tracks.Count > 0,
                $"SRS {stored.FileName}: no tracks");
            Assert.True(Enum.IsDefined(srs.ContainerType),
                $"SRS {stored.FileName}: invalid ContainerType");
        }
    }

    #endregion
}
