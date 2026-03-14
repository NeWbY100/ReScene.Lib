using System.Text;
using ReScene.RAR;

namespace ReScene.SRR.Tests;

public class SRRWriterTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _testDataDir;

    public SRRWriterTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"srrwriter_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);

        // Path to RAR test data (RAR files for testing)
        _testDataDir = Path.Combine(AppContext.BaseDirectory, "TestData");
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, true); } catch { }
    }

    #region SRR Header Tests

    [Fact]
    public async Task CreateAsync_WritesCorrectSrrHeader()
    {
        string rarPath = CreateMinimalRar4File("test.rar");
        string srrPath = Path.Combine(_testDir, "output.srr");

        var writer = new SRRWriter();
        var result = await writer.CreateAsync(srrPath, [rarPath]);

        Assert.True(result.Success);
        var srr = SRRFile.Load(srrPath);
        Assert.NotNull(srr.HeaderBlock);
        Assert.Equal(SRRBlockType.Header, srr.HeaderBlock!.BlockType);
    }

    [Fact]
    public async Task CreateAsync_WritesAppName()
    {
        string rarPath = CreateMinimalRar4File("test.rar");
        string srrPath = Path.Combine(_testDir, "output.srr");

        var writer = new SRRWriter();
        var result = await writer.CreateAsync(srrPath, [rarPath],
            options: new SrrCreationOptions { AppName = "TestApp 1.0" });

        Assert.True(result.Success);
        var srr = SRRFile.Load(srrPath);
        Assert.True(srr.HeaderBlock!.HasAppName);
        Assert.Equal("TestApp 1.0", srr.HeaderBlock.AppName);
    }

    [Fact]
    public async Task CreateAsync_NoAppName_OmitsAppName()
    {
        string rarPath = CreateMinimalRar4File("test.rar");
        string srrPath = Path.Combine(_testDir, "output.srr");

        var writer = new SRRWriter();
        var result = await writer.CreateAsync(srrPath, [rarPath],
            options: new SrrCreationOptions { AppName = null });

        Assert.True(result.Success);
        var srr = SRRFile.Load(srrPath);
        Assert.False(srr.HeaderBlock!.HasAppName);
        Assert.Null(srr.HeaderBlock.AppName);
    }

    [Fact]
    public async Task CreateAsync_DefaultAppName_IsReSceneNET()
    {
        string rarPath = CreateMinimalRar4File("test.rar");
        string srrPath = Path.Combine(_testDir, "output.srr");

        var writer = new SRRWriter();
        await writer.CreateAsync(srrPath, [rarPath]);

        var srr = SRRFile.Load(srrPath);
        Assert.Equal("ReScene.NET", srr.HeaderBlock!.AppName);
    }

    #endregion

    #region Stored File Tests

    [Fact]
    public async Task CreateAsync_WithStoredFiles_EmbedsFiles()
    {
        string rarPath = CreateMinimalRar4File("test.rar");
        string sfvPath = CreateTextFile("release.sfv", "test.rar DEADBEEF\r\n");
        string nfoPath = CreateTextFile("release.nfo", "Release info\r\n");
        string srrPath = Path.Combine(_testDir, "output.srr");

        var storedFiles = new Dictionary<string, string>
        {
            ["release.sfv"] = sfvPath,
            ["release.nfo"] = nfoPath
        };

        var writer = new SRRWriter();
        var result = await writer.CreateAsync(srrPath, [rarPath], storedFiles);

        Assert.True(result.Success);
        Assert.Equal(2, result.StoredFileCount);

        var srr = SRRFile.Load(srrPath);
        Assert.Equal(2, srr.StoredFiles.Count);
        Assert.Equal("release.sfv", srr.StoredFiles[0].FileName);
        Assert.Equal("release.nfo", srr.StoredFiles[1].FileName);
    }

    [Fact]
    public async Task CreateAsync_StoredFileContent_IsPreserved()
    {
        string rarPath = CreateMinimalRar4File("test.rar");
        string content = "test.rar DEADBEEF\r\n";
        string sfvPath = CreateTextFile("release.sfv", content);
        string srrPath = Path.Combine(_testDir, "output.srr");

        var storedFiles = new Dictionary<string, string> { ["release.sfv"] = sfvPath };

        var writer = new SRRWriter();
        await writer.CreateAsync(srrPath, [rarPath], storedFiles);

        var srr = SRRFile.Load(srrPath);
        string extractDir = Path.Combine(_testDir, "extracted");
        string? extracted = srr.ExtractStoredFile(srrPath, extractDir, n => n.EndsWith(".sfv"));

        Assert.NotNull(extracted);
        string readBack = File.ReadAllText(extracted!);
        Assert.Equal(content, readBack);
    }

    #endregion

    #region RAR4 Header Extraction Tests

    [Fact]
    public async Task CreateAsync_WithRealRar4File_ExtractsHeaders()
    {
        // Use a real RAR test file if available
        string rarPath = Path.Combine(_testDataDir, "test_wrar40_m3.rar");
        if (!File.Exists(rarPath))
        {
            // Fall back to synthetic RAR
            rarPath = CreateMinimalRar4File("test.rar");
        }

        string srrPath = Path.Combine(_testDir, "output.srr");

        var writer = new SRRWriter();
        var result = await writer.CreateAsync(srrPath, [rarPath]);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.VolumeCount);
        Assert.True(result.SrrFileSize > 0);

        var srr = SRRFile.Load(srrPath);
        Assert.Single(srr.RarFiles);
    }

    [Fact]
    public async Task CreateAsync_Rar4_PreservesArchivedFileNames()
    {
        string rarPath = CreateMinimalRar4File("test.rar", "testfile.txt");
        string srrPath = Path.Combine(_testDir, "output.srr");

        var writer = new SRRWriter();
        await writer.CreateAsync(srrPath, [rarPath]);

        var srr = SRRFile.Load(srrPath);
        Assert.Contains("testfile.txt", srr.ArchivedFiles);
    }

    [Fact]
    public async Task CreateAsync_Rar4_PreservesRarFileName()
    {
        string rarPath = CreateMinimalRar4File("release.rar");
        string srrPath = Path.Combine(_testDir, "output.srr");

        var writer = new SRRWriter();
        await writer.CreateAsync(srrPath, [rarPath]);

        var srr = SRRFile.Load(srrPath);
        Assert.Single(srr.RarFiles);
        Assert.Equal("release.rar", srr.RarFiles[0].FileName);
    }

    #endregion

    #region Multi-Volume Tests

    [Fact]
    public async Task CreateAsync_MultipleVolumes_ProcessesAll()
    {
        string rar1 = CreateMinimalRar4File("release.rar", "file.dat",
            archiveFlags: RARArchiveFlags.Volume | RARArchiveFlags.FirstVolume | RARArchiveFlags.NewNumbering);
        string rar2 = CreateMinimalRar4File("release.r00", "file.dat",
            archiveFlags: RARArchiveFlags.Volume | RARArchiveFlags.NewNumbering,
            fileFlags: RARFileFlags.LongBlock | RARFileFlags.ExtTime | RARFileFlags.SplitBefore | RARFileFlags.SplitAfter);
        string srrPath = Path.Combine(_testDir, "output.srr");

        var writer = new SRRWriter();
        var result = await writer.CreateAsync(srrPath, [rar1, rar2]);

        Assert.True(result.Success);
        Assert.Equal(2, result.VolumeCount);

        var srr = SRRFile.Load(srrPath);
        Assert.Equal(2, srr.RarFiles.Count);
        Assert.Equal("release.rar", srr.RarFiles[0].FileName);
        Assert.Equal("release.r00", srr.RarFiles[1].FileName);
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public async Task CreateAsync_EmptyVolumeList_Fails()
    {
        string srrPath = Path.Combine(_testDir, "output.srr");

        var writer = new SRRWriter();
        var result = await writer.CreateAsync(srrPath, []);

        Assert.False(result.Success);
        Assert.Contains("at least one", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_MissingRarFile_Fails()
    {
        string srrPath = Path.Combine(_testDir, "output.srr");

        var writer = new SRRWriter();
        var result = await writer.CreateAsync(srrPath, ["/nonexistent/file.rar"]);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task CreateAsync_MissingStoredFile_Fails()
    {
        string rarPath = CreateMinimalRar4File("test.rar");
        string srrPath = Path.Combine(_testDir, "output.srr");

        var storedFiles = new Dictionary<string, string> { ["test.sfv"] = "/nonexistent/test.sfv" };

        var writer = new SRRWriter();
        var result = await writer.CreateAsync(srrPath, [rarPath], storedFiles);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task CreateAsync_Cancellation_StopsAndCleansUp()
    {
        string rarPath = CreateMinimalRar4File("test.rar");
        string srrPath = Path.Combine(_testDir, "output.srr");

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        var writer = new SRRWriter();
        var result = await writer.CreateAsync(srrPath, [rarPath], ct: cts.Token);

        Assert.False(result.Success);
        Assert.Contains("cancel", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(srrPath));
    }

    #endregion

    #region Progress Reporting Tests

    [Fact]
    public async Task CreateAsync_ReportsProgress()
    {
        string rarPath = CreateMinimalRar4File("test.rar");
        string srrPath = Path.Combine(_testDir, "output.srr");

        var progressMessages = new List<string>();
        var writer = new SRRWriter();
        writer.Progress += (_, args) => progressMessages.Add(args.Message);

        await writer.CreateAsync(srrPath, [rarPath]);

        Assert.NotEmpty(progressMessages);
        Assert.Contains(progressMessages, m => m.Contains("test.rar"));
    }

    #endregion

    #region SFV Parsing Tests

    [Fact]
    public async Task CreateFromSfvAsync_FindsRarVolumes()
    {
        // Create RAR files and an SFV referencing them
        string rar1 = CreateMinimalRar4File("release.rar");
        string sfvContent = $"release.rar DEADBEEF\r\n";
        string sfvPath = CreateTextFile("release.sfv", sfvContent);
        string srrPath = Path.Combine(_testDir, "output.srr");

        var writer = new SRRWriter();
        var result = await writer.CreateFromSfvAsync(srrPath, sfvPath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.VolumeCount);

        var srr = SRRFile.Load(srrPath);
        // SFV itself should be stored
        Assert.Contains(srr.StoredFiles, sf => sf.FileName == "release.sfv");
    }

    [Fact]
    public async Task CreateFromSfvAsync_MissingSfv_Fails()
    {
        string srrPath = Path.Combine(_testDir, "output.srr");

        var writer = new SRRWriter();
        var result = await writer.CreateFromSfvAsync(srrPath, "/nonexistent/release.sfv");

        Assert.False(result.Success);
        Assert.Contains("not found", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateFromSfvAsync_NoRarInSfv_Fails()
    {
        string sfvContent = "; Only comments\n; No files\n";
        string sfvPath = CreateTextFile("empty.sfv", sfvContent);
        string srrPath = Path.Combine(_testDir, "output.srr");

        var writer = new SRRWriter();
        var result = await writer.CreateFromSfvAsync(srrPath, sfvPath);

        Assert.False(result.Success);
        Assert.Contains("No RAR volumes", result.ErrorMessage!);
    }

    #endregion

    #region Volume Name Sorting Tests

    [Fact]
    public void CompareRarVolumeNames_OldStyle_SortsCorrectly()
    {
        var files = new List<string>
        {
            "release.r02", "release.rar", "release.r00", "release.r01"
        };

        files.Sort(SRRWriter.CompareRarVolumeNames);

        Assert.Equal("release.rar", files[0]);
        Assert.Equal("release.r00", files[1]);
        Assert.Equal("release.r01", files[2]);
        Assert.Equal("release.r02", files[3]);
    }

    [Fact]
    public void CompareRarVolumeNames_NewStyle_SortsCorrectly()
    {
        var files = new List<string>
        {
            "release.part03.rar", "release.part01.rar", "release.part02.rar"
        };

        files.Sort(SRRWriter.CompareRarVolumeNames);

        Assert.Equal("release.part01.rar", files[0]);
        Assert.Equal("release.part02.rar", files[1]);
        Assert.Equal("release.part03.rar", files[2]);
    }

    #endregion

    #region Round-Trip Tests

    [Fact]
    public async Task RoundTrip_Rar4_HeadersPreserved()
    {
        // Create a RAR4 file with specific metadata, create SRR, read back, verify
        string rarPath = CreateMinimalRar4File("test.rar", "sample.txt",
            hostOS: 2, method: 0x33, fileCrc: 0xAABBCCDD, unpVer: 29);
        string srrPath = Path.Combine(_testDir, "roundtrip.srr");

        var writer = new SRRWriter();
        var result = await writer.CreateAsync(srrPath, [rarPath],
            options: new SrrCreationOptions { AppName = "RoundTripTest" });

        Assert.True(result.Success, result.ErrorMessage);

        var srr = SRRFile.Load(srrPath);
        Assert.Equal("RoundTripTest", srr.HeaderBlock!.AppName);
        Assert.Single(srr.RarFiles);
        Assert.Equal("test.rar", srr.RarFiles[0].FileName);
        Assert.Contains("sample.txt", srr.ArchivedFiles);
        Assert.Equal(29, srr.RARVersion);
        Assert.Equal((byte)2, srr.DetectedHostOS);
    }

    [Fact]
    public async Task RoundTrip_WithStoredFiles_AllPreserved()
    {
        string rarPath = CreateMinimalRar4File("test.rar");
        string sfvContent = "test.rar DEADBEEF\r\n";
        string nfoContent = "Release NFO\r\n";
        string sfvPath = CreateTextFile("release.sfv", sfvContent);
        string nfoPath = CreateTextFile("release.nfo", nfoContent);
        string srrPath = Path.Combine(_testDir, "roundtrip.srr");

        var storedFiles = new Dictionary<string, string>
        {
            ["release.sfv"] = sfvPath,
            ["release.nfo"] = nfoPath
        };

        var writer = new SRRWriter();
        await writer.CreateAsync(srrPath, [rarPath], storedFiles);

        var srr = SRRFile.Load(srrPath);
        Assert.Equal(2, srr.StoredFiles.Count);

        // Extract and verify content
        string extractDir = Path.Combine(_testDir, "extract_verify");
        string? extractedSfv = srr.ExtractStoredFile(srrPath, extractDir, n => n.EndsWith(".sfv"));
        Assert.NotNull(extractedSfv);
        Assert.Equal(sfvContent, File.ReadAllText(extractedSfv!));

        string? extractedNfo = srr.ExtractStoredFile(srrPath, extractDir, n => n.EndsWith(".nfo"));
        Assert.NotNull(extractedNfo);
        Assert.Equal(nfoContent, File.ReadAllText(extractedNfo!));
    }

    [Fact]
    public async Task RoundTrip_SrrFileSize_IsReasonable()
    {
        // SRR should be much smaller than the original RAR (headers only, no file data)
        string rarPath = CreateMinimalRar4File("test.rar");
        string srrPath = Path.Combine(_testDir, "size_check.srr");

        var writer = new SRRWriter();
        var result = await writer.CreateAsync(srrPath, [rarPath]);

        Assert.True(result.Success);
        Assert.True(result.SrrFileSize > 0);
        Assert.True(result.SrrFileSize < new FileInfo(rarPath).Length + 200,
            "SRR file should be comparable in size to headers-only data");
    }

    [Fact]
    public async Task RoundTrip_WithRealRarFiles_Succeeds()
    {
        // Test with actual RAR test files from the test data directory
        string[] testFiles = ["test_wrar40_m3.rar", "test_wrar40_m0.rar", "test_wrar35_m3.rar"];

        foreach (string testFile in testFiles)
        {
            string rarPath = Path.Combine(_testDataDir, testFile);
            if (!File.Exists(rarPath)) continue;

            string srrPath = Path.Combine(_testDir, $"{testFile}.srr");

            var writer = new SRRWriter();
            var result = await writer.CreateAsync(srrPath, [rarPath]);

            Assert.True(result.Success, $"Failed for {testFile}: {result.ErrorMessage}");
            Assert.Equal(1, result.VolumeCount);

            var srr = SRRFile.Load(srrPath);
            Assert.Single(srr.RarFiles);
            Assert.Equal(testFile, srr.RarFiles[0].FileName);
            Assert.True(srr.ArchivedFiles.Count > 0, $"No archived files found in SRR from {testFile}");
        }
    }

    #endregion

    #region Test Helpers

    /// <summary>
    /// Creates a minimal synthetic RAR4 file with marker, archive header, file header with data, and end block.
    /// </summary>
    private string CreateMinimalRar4File(
        string fileName,
        string archivedFileName = "testfile.txt",
        byte hostOS = 2,
        byte method = 0x33,
        uint fileCrc = 0xDEADBEEF,
        byte unpVer = 29,
        RARArchiveFlags archiveFlags = RARArchiveFlags.None,
        RARFileFlags fileFlags = RARFileFlags.LongBlock | RARFileFlags.ExtTime)
    {
        string path = Path.Combine(_testDir, fileName);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(fs);

        // RAR4 marker (7 bytes)
        writer.Write(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 });

        // Archive header (13 bytes)
        WriteRar4ArchiveHeader(writer, archiveFlags);

        // File header with fake data
        byte[] fakeData = "This is fake packed data for testing."u8.ToArray();
        WriteRar4FileHeader(writer, archivedFileName, (uint)fakeData.Length, (uint)fakeData.Length,
            hostOS, fileCrc, unpVer, method, fileFlags);
        writer.Write(fakeData); // packed data

        // End of archive
        WriteRar4EndArchive(writer);

        return path;
    }

    private static void WriteRar4ArchiveHeader(BinaryWriter writer, RARArchiveFlags flags)
    {
        ushort headerSize = 13;
        byte[] header = new byte[headerSize];
        header[2] = 0x73;
        BitConverter.GetBytes((ushort)flags).CopyTo(header, 3);
        BitConverter.GetBytes(headerSize).CopyTo(header, 5);

        uint crc32 = Force.Crc32.Crc32Algorithm.Compute(header, 2, header.Length - 2);
        ushort crc = (ushort)(crc32 & 0xFFFF);
        BitConverter.GetBytes(crc).CopyTo(header, 0);

        writer.Write(header);
    }

    private static void WriteRar4FileHeader(BinaryWriter writer, string fileName,
        uint packedSize, uint unpackedSize, byte hostOS, uint fileCrc, byte unpVer, byte method,
        RARFileFlags flags)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(fileName);
        ushort nameSize = (ushort)nameBytes.Length;

        int extTimeSize = (flags & RARFileFlags.ExtTime) != 0 ? 2 : 0;
        ushort headerSize = (ushort)(7 + 25 + nameSize + extTimeSize);

        byte[] header = new byte[headerSize];
        header[2] = 0x74;
        BitConverter.GetBytes((ushort)flags).CopyTo(header, 3);
        BitConverter.GetBytes(headerSize).CopyTo(header, 5);
        BitConverter.GetBytes(packedSize).CopyTo(header, 7);
        BitConverter.GetBytes(unpackedSize).CopyTo(header, 11);
        header[15] = hostOS;
        BitConverter.GetBytes(fileCrc).CopyTo(header, 16);
        BitConverter.GetBytes((uint)0x5A8E3100).CopyTo(header, 20); // DOS time
        header[24] = unpVer;
        header[25] = method;
        BitConverter.GetBytes(nameSize).CopyTo(header, 26);
        BitConverter.GetBytes((uint)0x00000020).CopyTo(header, 28);
        nameBytes.CopyTo(header, 32);

        if ((flags & RARFileFlags.ExtTime) != 0)
        {
            int extTimeOffset = 32 + nameSize;
            BitConverter.GetBytes((ushort)0x8000).CopyTo(header, extTimeOffset);
        }

        uint crc32 = Force.Crc32.Crc32Algorithm.Compute(header, 2, header.Length - 2);
        ushort crc = (ushort)(crc32 & 0xFFFF);
        BitConverter.GetBytes(crc).CopyTo(header, 0);

        writer.Write(header);
    }

    private static void WriteRar4EndArchive(BinaryWriter writer)
    {
        ushort headerSize = 7;
        byte[] header = new byte[headerSize];
        header[2] = 0x7B;
        BitConverter.GetBytes((ushort)0).CopyTo(header, 3);
        BitConverter.GetBytes(headerSize).CopyTo(header, 5);

        uint crc32 = Force.Crc32.Crc32Algorithm.Compute(header, 2, header.Length - 2);
        ushort crc = (ushort)(crc32 & 0xFFFF);
        BitConverter.GetBytes(crc).CopyTo(header, 0);

        writer.Write(header);
    }

    private string CreateTextFile(string fileName, string content)
    {
        string path = Path.Combine(_testDir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    #endregion

    #region AllowCompressed Option Tests

    [Fact]
    public async Task CreateAsync_AllowCompressedFalse_StoreMethodRar_Succeeds()
    {
        // method 0x30 = Store, should succeed even with AllowCompressed=false
        string rarPath = CreateMinimalRar4File("store.rar", "file.txt", method: 0x30);
        string srrPath = Path.Combine(_testDir, "output.srr");

        var writer = new SRRWriter();
        var result = await writer.CreateAsync(srrPath, [rarPath],
            options: new SrrCreationOptions { AllowCompressed = false });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task CreateAsync_AllowCompressedFalse_CompressedRar_AddsWarning()
    {
        // method 0x33 = Normal compression, AllowCompressed=false should add warning
        string rarPath = CreateMinimalRar4File("compressed.rar", "file.txt", method: 0x33);
        string srrPath = Path.Combine(_testDir, "output.srr");

        var writer = new SRRWriter();
        var result = await writer.CreateAsync(srrPath, [rarPath],
            options: new SrrCreationOptions { AllowCompressed = false });

        // SRR is still created, but with a warning
        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotEmpty(result.Warnings);
        Assert.Contains(result.Warnings, w => w.Contains("Compressed file detected"));
        Assert.Contains(result.Warnings, w => w.Contains("file.txt"));
    }

    [Fact]
    public async Task CreateAsync_AllowCompressedTrue_CompressedRar_NoWarning()
    {
        // With AllowCompressed=true (default), compressed files should produce no warnings
        string rarPath = CreateMinimalRar4File("compressed.rar", "file.txt", method: 0x33);
        string srrPath = Path.Combine(_testDir, "output.srr");

        var writer = new SRRWriter();
        var result = await writer.CreateAsync(srrPath, [rarPath],
            options: new SrrCreationOptions { AllowCompressed = true });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task CreateAsync_AllowCompressedFalse_WithRealCompressedRar_AddsWarning()
    {
        string rarPath = Path.Combine(_testDataDir, "test_wrar40_m3.rar");
        if (!File.Exists(rarPath)) return; // Skip if test data not available

        string srrPath = Path.Combine(_testDir, "output.srr");

        var writer = new SRRWriter();
        var result = await writer.CreateAsync(srrPath, [rarPath],
            options: new SrrCreationOptions { AllowCompressed = false });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotEmpty(result.Warnings);
        Assert.Contains(result.Warnings, w => w.Contains("Compressed file detected"));
    }

    [Fact]
    public async Task CreateAsync_AllowCompressedFalse_WithRealStoreRar_NoWarning()
    {
        string rarPath = Path.Combine(_testDataDir, "test_wrar40_m0.rar");
        if (!File.Exists(rarPath)) return; // Skip if test data not available

        string srrPath = Path.Combine(_testDir, "output.srr");

        var writer = new SRRWriter();
        var result = await writer.CreateAsync(srrPath, [rarPath],
            options: new SrrCreationOptions { AllowCompressed = false });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.Warnings);
    }

    #endregion

    #region StorePaths Option Tests

    [Fact]
    public async Task CreateAsync_StorePathsTrue_PreservesDirectoryInStoredFileName()
    {
        string rarPath = CreateMinimalRar4File("test.rar");
        string sfvPath = CreateTextFile("release.sfv", "test.rar DEADBEEF\r\n");
        string srrPath = Path.Combine(_testDir, "output.srr");

        // Use a path with directory component as the stored file name
        var storedFiles = new Dictionary<string, string>
        {
            ["subdir/release.sfv"] = sfvPath,
            ["another/dir/release.nfo"] = CreateTextFile("release.nfo", "NFO content\r\n")
        };

        var writer = new SRRWriter();
        var result = await writer.CreateAsync(srrPath, [rarPath], storedFiles,
            options: new SrrCreationOptions { StorePaths = true });

        Assert.True(result.Success, result.ErrorMessage);
        var srr = SRRFile.Load(srrPath);
        Assert.Equal(2, srr.StoredFiles.Count);
        Assert.Equal("subdir/release.sfv", srr.StoredFiles[0].FileName);
        Assert.Equal("another/dir/release.nfo", srr.StoredFiles[1].FileName);
    }

    [Fact]
    public async Task CreateAsync_StorePathsFalse_UsesFileNameOnly()
    {
        string rarPath = CreateMinimalRar4File("test.rar");
        string sfvPath = CreateTextFile("release.sfv", "test.rar DEADBEEF\r\n");
        string nfoPath = CreateTextFile("release.nfo", "NFO content\r\n");
        string srrPath = Path.Combine(_testDir, "output.srr");

        // Use paths with directory components
        var storedFiles = new Dictionary<string, string>
        {
            ["subdir/release.sfv"] = sfvPath,
            ["another/dir/release.nfo"] = nfoPath
        };

        var writer = new SRRWriter();
        var result = await writer.CreateAsync(srrPath, [rarPath], storedFiles,
            options: new SrrCreationOptions { StorePaths = false });

        Assert.True(result.Success, result.ErrorMessage);
        var srr = SRRFile.Load(srrPath);
        Assert.Equal(2, srr.StoredFiles.Count);
        // StorePaths=false should strip directory and use just the filename
        Assert.Equal("release.sfv", srr.StoredFiles[0].FileName);
        Assert.Equal("release.nfo", srr.StoredFiles[1].FileName);
    }

    [Fact]
    public async Task CreateAsync_StorePathsFalse_FlatFileNameUnchanged()
    {
        string rarPath = CreateMinimalRar4File("test.rar");
        string sfvPath = CreateTextFile("release.sfv", "test.rar DEADBEEF\r\n");
        string srrPath = Path.Combine(_testDir, "output.srr");

        // Name without directory path
        var storedFiles = new Dictionary<string, string>
        {
            ["release.sfv"] = sfvPath
        };

        var writer = new SRRWriter();
        var result = await writer.CreateAsync(srrPath, [rarPath], storedFiles,
            options: new SrrCreationOptions { StorePaths = false });

        Assert.True(result.Success, result.ErrorMessage);
        var srr = SRRFile.Load(srrPath);
        Assert.Single(srr.StoredFiles);
        Assert.Equal("release.sfv", srr.StoredFiles[0].FileName);
    }

    #endregion

    #region ComputeOsoHashes Option Tests

    [Fact]
    public async Task CreateAsync_ComputeOsoHashesFalse_NoOsoBlocks()
    {
        string rarPath = CreateMinimalRar4File("test.rar");
        string srrPath = Path.Combine(_testDir, "output.srr");

        var writer = new SRRWriter();
        var result = await writer.CreateAsync(srrPath, [rarPath],
            options: new SrrCreationOptions { ComputeOsoHashes = false });

        Assert.True(result.Success, result.ErrorMessage);
        var srr = SRRFile.Load(srrPath);
        Assert.Empty(srr.OsoHashBlocks);
    }

    [Fact]
    public async Task CreateAsync_ComputeOsoHashesTrue_NoOsoBlocksYet()
    {
        // OSO hash computation is a placeholder in SRRWriter (requires file data access).
        // With ComputeOsoHashes=true, creation should still succeed (no OSO blocks emitted yet).
        string rarPath = CreateMinimalRar4File("test.rar");
        string srrPath = Path.Combine(_testDir, "output.srr");

        var writer = new SRRWriter();
        var result = await writer.CreateAsync(srrPath, [rarPath],
            options: new SrrCreationOptions { ComputeOsoHashes = true });

        Assert.True(result.Success, result.ErrorMessage);
        var srr = SRRFile.Load(srrPath);
        // OSO hash blocks are not yet implemented, so none should be present
        Assert.Empty(srr.OsoHashBlocks);
    }

    #endregion

    #region RAR5 Volume Tests

    [Fact]
    public async Task CreateAsync_WithRar5File_ExtractsHeaders()
    {
        string rarPath = Path.Combine(_testDataDir, "test_rar5_m3.rar");
        if (!File.Exists(rarPath)) return; // Skip if test data not available

        string srrPath = Path.Combine(_testDir, "rar5_output.srr");

        var writer = new SRRWriter();
        var result = await writer.CreateAsync(srrPath, [rarPath]);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.VolumeCount);
        Assert.True(result.SrrFileSize > 0);

        var srr = SRRFile.Load(srrPath);
        Assert.Single(srr.RarFiles);
        Assert.Equal("test_rar5_m3.rar", srr.RarFiles[0].FileName);
        Assert.True(srr.ArchivedFiles.Count > 0, "Should have extracted archived file names from RAR5");
    }

    [Fact]
    public async Task CreateAsync_WithRar5M5File_ExtractsHeaders()
    {
        string rarPath = Path.Combine(_testDataDir, "test_rar5_m5.rar");
        if (!File.Exists(rarPath)) return; // Skip if test data not available

        string srrPath = Path.Combine(_testDir, "rar5_m5_output.srr");

        var writer = new SRRWriter();
        var result = await writer.CreateAsync(srrPath, [rarPath]);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.VolumeCount);

        var srr = SRRFile.Load(srrPath);
        Assert.Single(srr.RarFiles);
        Assert.Equal("test_rar5_m5.rar", srr.RarFiles[0].FileName);
        Assert.True(srr.ArchivedFiles.Count > 0, "Should have extracted archived file names from RAR5 m5");
    }

    [Fact]
    public async Task CreateAsync_WithRar5File_SetsRarVersion50()
    {
        string rarPath = Path.Combine(_testDataDir, "test_rar5_m3.rar");
        if (!File.Exists(rarPath)) return; // Skip if test data not available

        string srrPath = Path.Combine(_testDir, "rar5_version.srr");

        var writer = new SRRWriter();
        await writer.CreateAsync(srrPath, [rarPath]);

        var srr = SRRFile.Load(srrPath);
        Assert.Equal(50, srr.RARVersion);
    }

    #endregion

    #region Empty AppName Tests

    [Fact]
    public async Task CreateAsync_EmptyAppName_WritesHeaderWithAppNameFlag()
    {
        string rarPath = CreateMinimalRar4File("test.rar");
        string srrPath = Path.Combine(_testDir, "output.srr");

        var writer = new SRRWriter();
        var result = await writer.CreateAsync(srrPath, [rarPath],
            options: new SrrCreationOptions { AppName = "" });

        Assert.True(result.Success, result.ErrorMessage);
        var srr = SRRFile.Load(srrPath);
        // Empty string is non-null so WriteSrrHeader sets AppNamePresent flag,
        // but the parser returns null for a 0-length name
        Assert.True(srr.HeaderBlock!.HasAppName);
        Assert.Null(srr.HeaderBlock.AppName);
    }

    [Fact]
    public async Task CreateAsync_NullAppName_OmitsAppNameFromHeader()
    {
        string rarPath = CreateMinimalRar4File("test.rar");
        string srrPath = Path.Combine(_testDir, "output.srr");

        var writer = new SRRWriter();
        var result = await writer.CreateAsync(srrPath, [rarPath],
            options: new SrrCreationOptions { AppName = null });

        Assert.True(result.Success, result.ErrorMessage);
        var srr = SRRFile.Load(srrPath);
        Assert.False(srr.HeaderBlock!.HasAppName);
        Assert.Null(srr.HeaderBlock.AppName);
    }

    #endregion
}
