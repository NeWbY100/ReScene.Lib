using ReScene.SRR;

namespace ReScene.Tests;

public class SRRWriterRealDataTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _testDataDir;

    public SRRWriterRealDataTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"srrwriter_realdata_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _testDataDir = Path.Combine(AppContext.BaseDirectory, "TestData");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_testDir, true);
        }
        catch { }
        GC.SuppressFinalize(this);
    }

    #region CreateAsync — Single Volume (store_little)

    [Fact]
    public async Task CreateAsync_StoreLittle_RoundTrip()
    {
        string rarPath = Path.Combine(_testDataDir, "store_little", "store_little.rar");
        if (!File.Exists(rarPath))
        {
            return;
        }

        string srrPath = Path.Combine(_testDir, "store_little.srr");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(srrPath, [rarPath]);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.VolumeCount);

        var srr = SRRFile.Load(srrPath);
        Assert.Single(srr.RARFiles);
        Assert.Equal("store_little.rar", srr.RARFiles[0].FileName);
        Assert.Contains("little_file.txt", srr.ArchivedFiles);
    }

    [Fact]
    public async Task CreateAsync_StoreLittle_WithStoredFiles()
    {
        string rarPath = Path.Combine(_testDataDir, "store_little", "store_little.rar");
        string littleTxt = Path.Combine(_testDataDir, "txt", "little_file.txt");
        string emptyTxt = Path.Combine(_testDataDir, "txt", "empty_file.txt");
        if (!File.Exists(rarPath) || !File.Exists(littleTxt) || !File.Exists(emptyTxt))
        {
            return;
        }

        string srrPath = Path.Combine(_testDir, "store_little_stored.srr");

        var storedFiles = new Dictionary<string, string>
        {
            ["little_file.txt"] = littleTxt,
            ["empty_file.txt"] = emptyTxt
        };

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(srrPath, [rarPath], storedFiles);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.StoredFileCount);

        var srr = SRRFile.Load(srrPath);
        Assert.Equal(2, srr.StoredFiles.Count);
        Assert.Equal("little_file.txt", srr.StoredFiles[0].FileName);
        Assert.Equal("empty_file.txt", srr.StoredFiles[1].FileName);

        // Verify stored file content round-trips
        string extractDir = Path.Combine(_testDir, "extracted");
        string? extracted = srr.ExtractStoredFile(srrPath, extractDir, n => n == "little_file.txt");
        Assert.NotNull(extracted);

        byte[] originalBytes = await File.ReadAllBytesAsync(littleTxt);
        byte[] extractedBytes = await File.ReadAllBytesAsync(extracted!);
        Assert.Equal(originalBytes, extractedBytes);
    }

    #endregion

    #region CreateAsync — Multi-Volume New Style (store_rr_solid_auth)

    [Fact]
    public async Task CreateAsync_MultiVolume_NewStyle()
    {
        string baseDir = Path.Combine(_testDataDir, "store_rr_solid_auth_unicode_new");
        string[] rarPaths =
        [
            Path.Combine(baseDir, "store_rr_solid_auth.part1.rar"),
            Path.Combine(baseDir, "store_rr_solid_auth.part2.rar"),
            Path.Combine(baseDir, "store_rr_solid_auth.part3.rar")
        ];

        foreach (string path in rarPaths)
        {
            if (!File.Exists(path))
            {
                return;
            }
        }

        string srrPath = Path.Combine(_testDir, "multi_new.srr");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(srrPath, rarPaths);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(3, result.VolumeCount);
        Assert.True(result.SRRFileSize > 0);
        Assert.True(File.Exists(srrPath));

        // Verify the created SRR can be loaded and has at least the first volume
        var srr = SRRFile.Load(srrPath);
        Assert.True(srr.RARFiles.Count >= 1);
        Assert.Equal("store_rr_solid_auth.part1.rar", srr.RARFiles[0].FileName);
        Assert.True(srr.ArchivedFiles.Count > 0, "Should contain at least one archived file");
    }

    #endregion

    #region CreateAsync — Multi-Volume Old Style (store_split_folder)

    [Fact]
    public async Task CreateAsync_MultiVolume_OldStyle()
    {
        string baseDir = Path.Combine(_testDataDir, "store_split_folder_old_srrsfv_windows");
        string[] rarPaths =
        [
            Path.Combine(baseDir, "store_split_folder.rar"),
            Path.Combine(baseDir, "store_split_folder.r00"),
            Path.Combine(baseDir, "store_split_folder.r01")
        ];

        foreach (string path in rarPaths)
        {
            if (!File.Exists(path))
            {
                return;
            }
        }

        string srrPath = Path.Combine(_testDir, "multi_old.srr");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(srrPath, rarPaths);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(3, result.VolumeCount);

        var srr = SRRFile.Load(srrPath);
        Assert.Equal(3, srr.RARFiles.Count);
        Assert.Equal("store_split_folder.rar", srr.RARFiles[0].FileName);
        Assert.Equal("store_split_folder.r00", srr.RARFiles[1].FileName);
        Assert.Equal("store_split_folder.r01", srr.RARFiles[2].FileName);
    }

    #endregion

    #region CreateAsync — Empty Archive

    [Fact]
    public async Task CreateAsync_EmptyArchive()
    {
        string rarPath = Path.Combine(_testDataDir, "store_empty", "store_empty.rar");
        if (!File.Exists(rarPath))
        {
            return;
        }

        string srrPath = Path.Combine(_testDir, "empty.srr");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(srrPath, [rarPath]);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.VolumeCount);

        var srr = SRRFile.Load(srrPath);
        Assert.Single(srr.RARFiles);
        Assert.Equal("store_empty.rar", srr.RARFiles[0].FileName);
        // The RAR contains empty_file.txt (a zero-byte file), not truly empty
        Assert.Contains("empty_file.txt", srr.ArchivedFiles);
    }

    #endregion

    #region CreateAsync — Compressed RAR

    [Fact]
    public async Task CreateAsync_CompressedRar()
    {
        string rarPath = Path.Combine(_testDataDir, "best_little", "best_little.rar");
        if (!File.Exists(rarPath))
        {
            return;
        }

        string srrPath = Path.Combine(_testDir, "best_little.srr");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(srrPath, [rarPath]);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.VolumeCount);

        var srr = SRRFile.Load(srrPath);
        Assert.Single(srr.RARFiles);
        Assert.Equal("best_little.rar", srr.RARFiles[0].FileName);
        Assert.True(srr.ArchivedFiles.Count > 0, "Compressed RAR should still have archived file entries");
    }

    #endregion

    #region CreateFromSFVAsync — Real SFV Files

    [Fact]
    public async Task CreateFromSFVAsync_RealSFV_NewStyle()
    {
        string sfvPath = Path.Combine(_testDataDir, "store_rr_solid_auth_unicode_new", "store_rr_solid_auth.sfv");
        if (!File.Exists(sfvPath))
        {
            return;
        }

        // Verify all RAR volumes referenced by the SFV exist
        string sfvDir = Path.GetDirectoryName(sfvPath)!;
        string[] expectedRars =
        [
            "store_rr_solid_auth.part1.rar",
            "store_rr_solid_auth.part2.rar",
            "store_rr_solid_auth.part3.rar"
        ];
        foreach (string rar in expectedRars)
        {
            if (!File.Exists(Path.Combine(sfvDir, rar)))
            {
                return;
            }
        }

        string srrPath = Path.Combine(_testDir, "from_sfv_new.srr");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateFromSFVAsync(srrPath, sfvPath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(3, result.VolumeCount);

        var srr = SRRFile.Load(srrPath);
        // SFV should be auto-stored
        Assert.Contains(srr.StoredFiles, sf => sf.FileName == "store_rr_solid_auth.sfv");
        Assert.True(srr.RARFiles.Count >= 1, "Should have at least one RAR volume");
    }

    [Fact]
    public async Task CreateFromSFVAsync_OldStyleSFV()
    {
        string sfvPath = Path.Combine(_testDataDir, "store_split_folder_old_srrsfv_windows", "store_split_folder.sfv");
        if (!File.Exists(sfvPath))
        {
            return;
        }

        // Verify all RAR volumes referenced by the SFV exist
        string sfvDir = Path.GetDirectoryName(sfvPath)!;
        string[] expectedRars =
        [
            "store_split_folder.rar",
            "store_split_folder.r00",
            "store_split_folder.r01"
        ];
        foreach (string rar in expectedRars)
        {
            if (!File.Exists(Path.Combine(sfvDir, rar)))
            {
                return;
            }
        }

        string srrPath = Path.Combine(_testDir, "from_sfv_old.srr");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateFromSFVAsync(srrPath, sfvPath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(3, result.VolumeCount);

        var srr = SRRFile.Load(srrPath);
        Assert.Contains(srr.StoredFiles, sf => sf.FileName == "store_split_folder.sfv");
        Assert.Equal(3, srr.RARFiles.Count);

        // Volumes should be sorted: .rar first, then .r00, .r01
        Assert.Equal("store_split_folder.rar", srr.RARFiles[0].FileName);
        Assert.Equal("store_split_folder.r00", srr.RARFiles[1].FileName);
        Assert.Equal("store_split_folder.r01", srr.RARFiles[2].FileName);
    }

    #endregion

    #region CompareWithReference — SRR Structural Comparison

    [Fact]
    public async Task CompareWithReference_StoreLittle()
    {
        string baseDir = Path.Combine(_testDataDir, "store_little");
        string rarPath = Path.Combine(baseDir, "store_little.rar");
        string refSRRPath = Path.Combine(baseDir, "store_little.srr");
        if (!File.Exists(rarPath) || !File.Exists(refSRRPath))
        {
            return;
        }

        string srrPath = Path.Combine(_testDir, "compare_store_little.srr");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(srrPath, [rarPath],
            options: new SRRCreationOptions { AppName = null });

        Assert.True(result.Success, result.ErrorMessage);

        var created = SRRFile.Load(srrPath);
        var reference = SRRFile.Load(refSRRPath);

        // Same number of RAR volumes
        Assert.Equal(reference.RARFiles.Count, created.RARFiles.Count);

        // Same RAR file names
        for (int i = 0; i < reference.RARFiles.Count; i++)
        {
            Assert.Equal(reference.RARFiles[i].FileName, created.RARFiles[i].FileName);
        }

        // Same archived files
        Assert.Equal(reference.ArchivedFiles.Count, created.ArchivedFiles.Count);
        foreach (string archivedFile in reference.ArchivedFiles)
        {
            Assert.Contains(archivedFile, created.ArchivedFiles);
        }
    }

    [Fact]
    public async Task CompareWithReference_MultiVolumeNewStyle()
    {
        string baseDir = Path.Combine(_testDataDir, "store_rr_solid_auth_unicode_new");
        string[] rarPaths =
        [
            Path.Combine(baseDir, "store_rr_solid_auth.part1.rar"),
            Path.Combine(baseDir, "store_rr_solid_auth.part2.rar"),
            Path.Combine(baseDir, "store_rr_solid_auth.part3.rar")
        ];
        string refSRRPath = Path.Combine(baseDir, "store_rr_solid_auth.part1.srr");

        foreach (string path in rarPaths)
        {
            if (!File.Exists(path))
            {
                return;
            }
        }
        if (!File.Exists(refSRRPath))
        {
            return;
        }

        string srrPath = Path.Combine(_testDir, "compare_multi_new.srr");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(srrPath, rarPaths,
            options: new SRRCreationOptions { AppName = null });

        Assert.True(result.Success, result.ErrorMessage);

        var created = SRRFile.Load(srrPath);
        var reference = SRRFile.Load(refSRRPath);

        // Same number of RAR volumes
        Assert.Equal(reference.RARFiles.Count, created.RARFiles.Count);

        // Same RAR file names
        for (int i = 0; i < reference.RARFiles.Count; i++)
        {
            Assert.Equal(reference.RARFiles[i].FileName, created.RARFiles[i].FileName);
        }

        // Same archived files
        Assert.Equal(reference.ArchivedFiles.Count, created.ArchivedFiles.Count);
        foreach (string archivedFile in reference.ArchivedFiles)
        {
            Assert.Contains(archivedFile, created.ArchivedFiles);
        }
    }

    [Fact]
    public async Task CompareWithReference_OldStyleMultiVolume()
    {
        string baseDir = Path.Combine(_testDataDir, "store_split_folder_old_srrsfv_windows");
        string[] rarPaths =
        [
            Path.Combine(baseDir, "store_split_folder.rar"),
            Path.Combine(baseDir, "store_split_folder.r00"),
            Path.Combine(baseDir, "store_split_folder.r01")
        ];
        string refSRRPath = Path.Combine(baseDir, "store_split_folder.srr");

        foreach (string path in rarPaths)
        {
            if (!File.Exists(path))
            {
                return;
            }
        }
        if (!File.Exists(refSRRPath))
        {
            return;
        }

        string srrPath = Path.Combine(_testDir, "compare_multi_old.srr");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(srrPath, rarPaths,
            options: new SRRCreationOptions { AppName = null });

        Assert.True(result.Success, result.ErrorMessage);

        var created = SRRFile.Load(srrPath);
        var reference = SRRFile.Load(refSRRPath);

        // Same number of RAR volumes
        Assert.Equal(reference.RARFiles.Count, created.RARFiles.Count);

        // Same RAR file names
        for (int i = 0; i < reference.RARFiles.Count; i++)
        {
            Assert.Equal(reference.RARFiles[i].FileName, created.RARFiles[i].FileName);
        }

        // Same archived files
        Assert.Equal(reference.ArchivedFiles.Count, created.ArchivedFiles.Count);
        foreach (string archivedFile in reference.ArchivedFiles)
        {
            Assert.Contains(archivedFile, created.ArchivedFiles);
        }
    }

    #endregion
}
