using System.Buffers.Binary;
using System.Text;
using ReScene.RAR;
using ReScene.SRR;

namespace ReScene.Tests;

public class SRRWriterTests : TempDirTestBase
{
    // Path to RAR test data (RAR files for testing)
    private readonly string _testDataDir = Path.Combine(AppContext.BaseDirectory, "TestData");

    #region SRR Header Tests

    [Fact]
    public async Task CreateAsync_WritesCorrectSRRHeader()
    {
        string rarPath = CreateMinimalRar4File("test.rar");
        string srrPath = Path.Combine(TempDir, "output.srr");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(srrPath, [rarPath]);

        Assert.True(result.Success);
        var srr = SRRFile.Load(srrPath);
        Assert.NotNull(srr.HeaderBlock);
        Assert.Equal(SRRBlockType.Header, srr.HeaderBlock!.BlockType);
    }

    [Fact]
    public async Task CreateAsync_WritesAppName()
    {
        string rarPath = CreateMinimalRar4File("test.rar");
        string srrPath = Path.Combine(TempDir, "output.srr");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(srrPath, [rarPath],
            options: new SRRCreationOptions { AppName = "TestApp 1.0" });

        Assert.True(result.Success);
        var srr = SRRFile.Load(srrPath);
        Assert.True(srr.HeaderBlock!.HasAppName);
        Assert.Equal("TestApp 1.0", srr.HeaderBlock.AppName);
    }

    [Fact]
    public async Task CreateAsync_NoAppName_OmitsAppName()
    {
        string rarPath = CreateMinimalRar4File("test.rar");
        string srrPath = Path.Combine(TempDir, "output.srr");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(srrPath, [rarPath],
            options: new SRRCreationOptions { AppName = null });

        Assert.True(result.Success);
        var srr = SRRFile.Load(srrPath);
        Assert.False(srr.HeaderBlock!.HasAppName);
        Assert.Null(srr.HeaderBlock.AppName);
    }

    [Fact]
    public async Task CreateAsync_DefaultAppName_IsReSceneNET()
    {
        string rarPath = CreateMinimalRar4File("test.rar");
        string srrPath = Path.Combine(TempDir, "output.srr");

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
        string srrPath = Path.Combine(TempDir, "output.srr");

        List<StoredFileEntry> storedFiles =
        [
            new("release.sfv", sfvPath),
            new("release.nfo", nfoPath)
        ];

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(srrPath, [rarPath], storedFiles);

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
        string srrPath = Path.Combine(TempDir, "output.srr");

        List<StoredFileEntry> storedFiles = [new("release.sfv", sfvPath)];

        var writer = new SRRWriter();
        await writer.CreateAsync(srrPath, [rarPath], storedFiles);

        var srr = SRRFile.Load(srrPath);
        string extractDir = Path.Combine(TempDir, "extracted");
        string? extracted = srr.ExtractStoredFile(srrPath, extractDir, n => n.EndsWith(".sfv", StringComparison.Ordinal));

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

        string srrPath = Path.Combine(TempDir, "output.srr");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(srrPath, [rarPath]);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.VolumeCount);
        Assert.True(result.SRRFileSize > 0);

        var srr = SRRFile.Load(srrPath);
        Assert.Single(srr.RARFiles);
    }

    [Fact]
    public async Task CreateAsync_Rar4_PreservesArchivedFileNames()
    {
        string rarPath = CreateMinimalRar4File("test.rar", "testfile.txt");
        string srrPath = Path.Combine(TempDir, "output.srr");

        var writer = new SRRWriter();
        await writer.CreateAsync(srrPath, [rarPath]);

        var srr = SRRFile.Load(srrPath);
        Assert.Contains("testfile.txt", srr.ArchivedFiles);
    }

    [Fact]
    public async Task CreateAsync_Rar4_PreservesRarFileName()
    {
        string rarPath = CreateMinimalRar4File("release.rar");
        string srrPath = Path.Combine(TempDir, "output.srr");

        var writer = new SRRWriter();
        await writer.CreateAsync(srrPath, [rarPath]);

        var srr = SRRFile.Load(srrPath);
        Assert.Single(srr.RARFiles);
        Assert.Equal("release.rar", srr.RARFiles[0].FileName);
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
        string srrPath = Path.Combine(TempDir, "output.srr");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(srrPath, [rar1, rar2]);

        Assert.True(result.Success);
        Assert.Equal(2, result.VolumeCount);

        var srr = SRRFile.Load(srrPath);
        Assert.Equal(2, srr.RARFiles.Count);
        Assert.Equal("release.rar", srr.RARFiles[0].FileName);
        Assert.Equal("release.r00", srr.RARFiles[1].FileName);
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public async Task CreateAsync_EmptyVolumeList_Fails()
    {
        string srrPath = Path.Combine(TempDir, "output.srr");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(srrPath, []);

        Assert.False(result.Success);
        Assert.Contains("at least one", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_MissingRarFile_Fails()
    {
        string srrPath = Path.Combine(TempDir, "output.srr");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(srrPath, ["/nonexistent/file.rar"]);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task CreateAsync_MissingStoredFile_Fails()
    {
        string rarPath = CreateMinimalRar4File("test.rar");
        string srrPath = Path.Combine(TempDir, "output.srr");

        List<StoredFileEntry> storedFiles = [new("test.sfv", "/nonexistent/test.sfv")];

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(srrPath, [rarPath], storedFiles);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task CreateAsync_Cancellation_StopsAndCleansUp()
    {
        string rarPath = CreateMinimalRar4File("test.rar");
        string srrPath = Path.Combine(TempDir, "output.srr");

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(srrPath, [rarPath], ct: cts.Token);

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
        string srrPath = Path.Combine(TempDir, "output.srr");

        var progressMessages = new List<string>();
        var writer = new SRRWriter();
        writer.Progress += (_, args) => progressMessages.Add(args.Message);

        await writer.CreateAsync(srrPath, [rarPath]);

        Assert.NotEmpty(progressMessages);
        Assert.Contains(progressMessages, m => m.Contains("test.rar", StringComparison.Ordinal));
    }

    #endregion

    #region SFV Parsing Tests

    [Fact]
    public async Task CreateFromSFVAsync_FindsRarVolumes()
    {
        // Create RAR files and an SFV referencing them
        string rar1 = CreateMinimalRar4File("release.rar");
        string sfvContent = $"release.rar DEADBEEF\r\n";
        string sfvPath = CreateTextFile("release.sfv", sfvContent);
        string srrPath = Path.Combine(TempDir, "output.srr");

        var writer = new SRRWriter();
        List<StoredFileEntry> storedFiles =
        [
            new(Path.GetFileName(sfvPath), sfvPath)
        ];
        SRRCreationResult result = await writer.CreateFromSFVAsync(srrPath, sfvPath, storedFiles);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.VolumeCount);

        var srr = SRRFile.Load(srrPath);
        Assert.Contains(srr.StoredFiles, sf => sf.FileName == "release.sfv");
    }

    [Fact]
    public async Task CreateFromSFVAsync_MissingSFV_Fails()
    {
        string srrPath = Path.Combine(TempDir, "output.srr");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateFromSFVAsync(srrPath, "/nonexistent/release.sfv");

        Assert.False(result.Success);
        Assert.Contains("not found", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateFromSFVAsync_NoRarInSFV_Fails()
    {
        string sfvContent = "; Only comments\n; No files\n";
        string sfvPath = CreateTextFile("empty.sfv", sfvContent);
        string srrPath = Path.Combine(TempDir, "output.srr");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateFromSFVAsync(srrPath, sfvPath);

        Assert.False(result.Success);
        Assert.Contains("No RAR volumes", result.ErrorMessage!, StringComparison.Ordinal);
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

        files.Sort(RARVolumeNameComparer.Instance);

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

        files.Sort(RARVolumeNameComparer.Instance);

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
            hostOS: 2, method: 0x33, fileCRC: 0xAABBCCDD, unpVer: 29);
        string srrPath = Path.Combine(TempDir, "roundtrip.srr");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(srrPath, [rarPath],
            options: new SRRCreationOptions { AppName = "RoundTripTest" });

        Assert.True(result.Success, result.ErrorMessage);

        var srr = SRRFile.Load(srrPath);
        Assert.Equal("RoundTripTest", srr.HeaderBlock!.AppName);
        Assert.Single(srr.RARFiles);
        Assert.Equal("test.rar", srr.RARFiles[0].FileName);
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
        string srrPath = Path.Combine(TempDir, "roundtrip.srr");

        List<StoredFileEntry> storedFiles =
        [
            new("release.sfv", sfvPath),
            new("release.nfo", nfoPath)
        ];

        var writer = new SRRWriter();
        await writer.CreateAsync(srrPath, [rarPath], storedFiles);

        var srr = SRRFile.Load(srrPath);
        Assert.Equal(2, srr.StoredFiles.Count);

        // Extract and verify content
        string extractDir = Path.Combine(TempDir, "extract_verify");
        string? extractedSFV = srr.ExtractStoredFile(srrPath, extractDir, n => n.EndsWith(".sfv", StringComparison.Ordinal));
        Assert.NotNull(extractedSFV);
        Assert.Equal(sfvContent, File.ReadAllText(extractedSFV!));

        string? extractedNfo = srr.ExtractStoredFile(srrPath, extractDir, n => n.EndsWith(".nfo", StringComparison.Ordinal));
        Assert.NotNull(extractedNfo);
        Assert.Equal(nfoContent, File.ReadAllText(extractedNfo!));
    }

    [Fact]
    public async Task RoundTrip_SRRFileSize_IsReasonable()
    {
        // SRR should be much smaller than the original RAR (headers only, no file data)
        string rarPath = CreateMinimalRar4File("test.rar");
        string srrPath = Path.Combine(TempDir, "size_check.srr");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(srrPath, [rarPath]);

        Assert.True(result.Success);
        Assert.True(result.SRRFileSize > 0);
        Assert.True(result.SRRFileSize < new FileInfo(rarPath).Length + 200,
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
            if (!File.Exists(rarPath))
            {
                Assert.Fail($"Test file not found: {rarPath}");
            }

            string srrPath = Path.Combine(TempDir, $"{testFile}.srr");

            var writer = new SRRWriter();
            SRRCreationResult result = await writer.CreateAsync(srrPath, [rarPath]);

            Assert.True(result.Success, $"Failed for {testFile}: {result.ErrorMessage}");
            Assert.Equal(1, result.VolumeCount);

            var srr = SRRFile.Load(srrPath);
            Assert.Single(srr.RARFiles);
            Assert.Equal(testFile, srr.RARFiles[0].FileName);
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
        uint fileCRC = 0xDEADBEEF,
        byte unpVer = 29,
        RARArchiveFlags archiveFlags = RARArchiveFlags.None,
        RARFileFlags fileFlags = RARFileFlags.LongBlock | RARFileFlags.ExtTime)
    {
        string path = Path.Combine(TempDir, fileName);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(fs);

        // RAR4 marker (7 bytes)
        writer.Write(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 });

        // Archive header (13 bytes)
        WriteRar4ArchiveHeader(writer, archiveFlags);

        // File header with fake data
        byte[] fakeData = "This is fake packed data for testing."u8.ToArray();
        WriteRar4FileHeader(writer, archivedFileName, (uint)fakeData.Length, (uint)fakeData.Length,
            hostOS, fileCRC, unpVer, method, fileFlags);
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
        uint packedSize, uint unpackedSize, byte hostOS, uint fileCRC, byte unpVer, byte method,
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
        BitConverter.GetBytes(fileCRC).CopyTo(header, 16);
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
        string path = Path.Combine(TempDir, fileName);
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
        string srrPath = Path.Combine(TempDir, "output.srr");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(srrPath, [rarPath],
            options: new SRRCreationOptions { AllowCompressed = false });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task CreateAsync_AllowCompressedFalse_CompressedRar_AddsWarning()
    {
        // method 0x33 = Normal compression, AllowCompressed=false should add warning
        string rarPath = CreateMinimalRar4File("compressed.rar", "file.txt", method: 0x33);
        string srrPath = Path.Combine(TempDir, "output.srr");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(srrPath, [rarPath],
            options: new SRRCreationOptions { AllowCompressed = false });

        // SRR is still created, but with a warning
        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotEmpty(result.Warnings);
        Assert.Contains(result.Warnings, w => w.Contains("Compressed file detected", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("file.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateAsync_AllowCompressedTrue_CompressedRar_NoWarning()
    {
        // With AllowCompressed=true (default), compressed files should produce no warnings
        string rarPath = CreateMinimalRar4File("compressed.rar", "file.txt", method: 0x33);
        string srrPath = Path.Combine(TempDir, "output.srr");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(srrPath, [rarPath],
            options: new SRRCreationOptions { AllowCompressed = true });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task CreateAsync_AllowCompressedFalse_WithRealCompressedRar_AddsWarning()
    {
        string rarPath = Path.Combine(_testDataDir, "test_wrar40_m3.rar");
        if (!File.Exists(rarPath))
        {
            Assert.Fail($"Test file not found: {rarPath}");
        }

        string srrPath = Path.Combine(TempDir, "output.srr");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(srrPath, [rarPath],
            options: new SRRCreationOptions { AllowCompressed = false });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotEmpty(result.Warnings);
        Assert.Contains(result.Warnings, w => w.Contains("Compressed file detected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateAsync_AllowCompressedFalse_WithRealStoreRar_NoWarning()
    {
        string rarPath = Path.Combine(_testDataDir, "test_wrar40_m0.rar");
        if (!File.Exists(rarPath))
        {
            Assert.Fail($"Test file not found: {rarPath}");
        }

        string srrPath = Path.Combine(TempDir, "output.srr");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(srrPath, [rarPath],
            options: new SRRCreationOptions { AllowCompressed = false });

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
        string srrPath = Path.Combine(TempDir, "output.srr");

        // Use a path with directory component as the stored file name
        List<StoredFileEntry> storedFiles =
        [
            new("subdir/release.sfv", sfvPath),
            new("another/dir/release.nfo", CreateTextFile("release.nfo", "NFO content\r\n"))
        ];

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(srrPath, [rarPath], storedFiles,
            options: new SRRCreationOptions());

        Assert.True(result.Success, result.ErrorMessage);
        var srr = SRRFile.Load(srrPath);
        Assert.Equal(2, srr.StoredFiles.Count);
        Assert.Equal("subdir/release.sfv", srr.StoredFiles[0].FileName);
        Assert.Equal("another/dir/release.nfo", srr.StoredFiles[1].FileName);
    }


    #endregion

    #region ComputeOSOHashes Option Tests

    [Fact]
    public async Task CreateAsync_ComputeOSOHashesFalse_NoOSOBlocks()
    {
        string rarPath = CreateMinimalRar4File("test.rar");
        string srrPath = Path.Combine(TempDir, "output.srr");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(srrPath, [rarPath],
            options: new SRRCreationOptions { ComputeOSOHashes = false });

        Assert.True(result.Success, result.ErrorMessage);
        var srr = SRRFile.Load(srrPath);
        Assert.Empty(srr.OSOHashBlocks);
    }

    [Fact]
    public async Task CreateAsync_ComputeOSOHashesTrue_SubThresholdFile_EmitsNoOSOBlock()
    {
        // OSO hashing IS implemented (see CreateAsync_ComputeOSOHashes... over a >=64 KiB fixture).
        // A minimal RAR file is below OSOHashCalculator.MinFileSize (64 KiB), so no OSO block is
        // emitted even with ComputeOSOHashes=true — creation still succeeds.
        string rarPath = CreateMinimalRar4File("test.rar");
        string srrPath = Path.Combine(TempDir, "output.srr");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(srrPath, [rarPath],
            options: new SRRCreationOptions { ComputeOSOHashes = true });

        Assert.True(result.Success, result.ErrorMessage);
        var srr = SRRFile.Load(srrPath);
        // Sub-threshold input → no OSO hash block.
        Assert.Empty(srr.OSOHashBlocks);
    }

    #endregion

    #region OSO Hash Write Path Characterization Tests

    /// <summary>
    /// Pins <c>WriteOSOHashBlock</c>'s 7+8+8+2 framing: when
    /// <see cref="SRRCreationOptions.ComputeOSOHashes"/> is enabled and the RAR contains a
    /// stored file of at least 64 KiB, the resulting SRR must contain exactly one OSO block
    /// whose file-size field, 8-byte hash, and filename all match the independent oracle.
    /// </summary>
    [Fact]
    public async Task CreateAsync_ComputeOSOHashes_WithLargeStoredFile_EmitsOsoBlockWithExactFraming()
    {
        const int ChunkSize = 64 * 1024; // 65536 bytes — OSO minimum file size
        const string ArchivedFileName = "clip.bin";

        // Deterministic content so the oracle and the SUT agree on the expected hash bytes.
        byte[] content = BuildOsoContentPattern(ChunkSize, seed: 0x1234);
        string rarPath = CreateLargeStoredRar4("clip.rar", ArchivedFileName, content);
        string srrPath = Path.Combine(TempDir, "oso_framing.srr");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(srrPath, [rarPath],
            options: new SRRCreationOptions { ComputeOSOHashes = true });

        Assert.True(result.Success, result.ErrorMessage);

        var srr = SRRFile.Load(srrPath);
        SRROsoHashBlock oso = Assert.Single(srr.OSOHashBlocks);

        // Pin the three payload fields written by WriteOSOHashBlock:
        // file size (8 bytes), hash (8 bytes), and the file name.
        Assert.Equal(ArchivedFileName, oso.FileName);
        Assert.Equal((ulong)ChunkSize, oso.FileSize);
        Assert.Equal(8, oso.OSOHash.Length);
        Assert.Equal(OsoHashOracle(content, ChunkSize), oso.OSOHash.ToArray());
    }

    /// <summary>
    /// Independent OSO hash oracle — deliberately does NOT call <c>OSOHashCalculator</c>.
    /// Recomputes per OSO spec: fileSize + LE-qword sum of first and last 64 KiB windows.
    /// </summary>
    private static byte[] OsoHashOracle(byte[] content, int chunkSize)
    {
        ulong hash = (ulong)content.Length;
        ReadOnlySpan<byte> head = content.AsSpan(0, chunkSize);
        ReadOnlySpan<byte> tail = content.AsSpan(content.Length - chunkSize, chunkSize);
        for (int i = 0; i < chunkSize; i += 8)
        {
            hash += BinaryPrimitives.ReadUInt64LittleEndian(head.Slice(i, 8));
            hash += BinaryPrimitives.ReadUInt64LittleEndian(tail.Slice(i, 8));
        }

        byte[] result = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(result, hash);
        return result;
    }

    /// <summary>
    /// Deterministic LCG byte pattern. Mirrors <c>OSOHashCalculatorTests.BuildPattern</c>
    /// exactly so the same fixture content produces the same expected hash value.
    /// </summary>
    private static byte[] BuildOsoContentPattern(int length, uint seed)
    {
        byte[] buffer = new byte[length];
        uint state = seed == 0 ? 1u : seed;
        for (int i = 0; i < length; i++)
        {
            state = (state * 1664525u) + 1013904223u;
            buffer[i] = (byte)(state >> 24);
        }

        return buffer;
    }

    /// <summary>
    /// Writes a minimal RAR4 archive with one stored (method 0x30) file entry whose packed
    /// data is <paramref name="content"/>. <c>OSOHashCalculator</c> only hashes stored entries
    /// of at least 64 KiB, so the caller must supply at least 65 536 bytes.
    /// </summary>
    private string CreateLargeStoredRar4(string rarFileName, string archivedFileName, byte[] content)
    {
        string path = Path.Combine(TempDir, rarFileName);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        bw.Write(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 }); // RAR4 marker
        WriteRar4ArchiveHeader(bw, RARArchiveFlags.None);
        WriteRar4FileHeader(bw, archivedFileName, (uint)content.Length, (uint)content.Length,
            hostOS: 2, fileCRC: 0, unpVer: 29, method: 0x30 /* Store */,
            flags: RARFileFlags.LongBlock);
        bw.Write(content);
        WriteRar4EndArchive(bw);

        return path;
    }

    #endregion

    #region RAR5 Volume Tests

    [Fact]
    public async Task CreateAsync_WithRar5File_ExtractsHeaders()
    {
        string rarPath = Path.Combine(_testDataDir, "test_rar5_m3.rar");
        if (!File.Exists(rarPath))
        {
            Assert.Fail($"Test file not found: {rarPath}");
        }

        string srrPath = Path.Combine(TempDir, "rar5_output.srr");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(srrPath, [rarPath]);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.VolumeCount);
        Assert.True(result.SRRFileSize > 0);

        var srr = SRRFile.Load(srrPath);
        Assert.Single(srr.RARFiles);
        Assert.Equal("test_rar5_m3.rar", srr.RARFiles[0].FileName);
        Assert.True(srr.ArchivedFiles.Count > 0, "Should have extracted archived file names from RAR5");
    }

    [Fact]
    public async Task CreateAsync_WithRar5M5File_ExtractsHeaders()
    {
        string rarPath = Path.Combine(_testDataDir, "test_rar5_m5.rar");
        if (!File.Exists(rarPath))
        {
            Assert.Fail($"Test file not found: {rarPath}");
        }

        string srrPath = Path.Combine(TempDir, "rar5_m5_output.srr");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(srrPath, [rarPath]);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.VolumeCount);

        var srr = SRRFile.Load(srrPath);
        Assert.Single(srr.RARFiles);
        Assert.Equal("test_rar5_m5.rar", srr.RARFiles[0].FileName);
        Assert.True(srr.ArchivedFiles.Count > 0, "Should have extracted archived file names from RAR5 m5");
    }

    [Fact]
    public async Task CreateAsync_WithRar5File_SetsRarVersion50()
    {
        string rarPath = Path.Combine(_testDataDir, "test_rar5_m3.rar");
        if (!File.Exists(rarPath))
        {
            Assert.Fail($"Test file not found: {rarPath}");
        }

        string srrPath = Path.Combine(TempDir, "rar5_version.srr");

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
        string srrPath = Path.Combine(TempDir, "output.srr");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(srrPath, [rarPath],
            options: new SRRCreationOptions { AppName = "" });

        Assert.True(result.Success, result.ErrorMessage);
        var srr = SRRFile.Load(srrPath);
        // Empty string is non-null so WriteSRRHeader sets AppNamePresent flag,
        // but the parser returns null for a 0-length name
        Assert.True(srr.HeaderBlock!.HasAppName);
        Assert.Null(srr.HeaderBlock.AppName);
    }

    [Fact]
    public async Task CreateAsync_NullAppName_OmitsAppNameFromHeader()
    {
        string rarPath = CreateMinimalRar4File("test.rar");
        string srrPath = Path.Combine(TempDir, "output.srr");

        var writer = new SRRWriter();
        SRRCreationResult result = await writer.CreateAsync(srrPath, [rarPath],
            options: new SRRCreationOptions { AppName = null });

        Assert.True(result.Success, result.ErrorMessage);
        var srr = SRRFile.Load(srrPath);
        Assert.False(srr.HeaderBlock!.HasAppName);
        Assert.Null(srr.HeaderBlock.AppName);
    }

    #endregion

    #region Large Entry Handling (HIGH_PACK_SIZE)

    private static void WriteRar4LargeFileHeader(BinaryWriter writer, string fileName,
        uint packedSizeLow, uint packedSizeHigh)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(fileName);
        ushort nameSize = (ushort)nameBytes.Length;
        RARFileFlags flags = RARFileFlags.LongBlock | RARFileFlags.Large;
        ushort headerSize = (ushort)(7 + 25 + 8 + nameSize);

        byte[] header = new byte[headerSize];
        header[2] = 0x74;
        BitConverter.GetBytes((ushort)flags).CopyTo(header, 3);
        BitConverter.GetBytes(headerSize).CopyTo(header, 5);
        BitConverter.GetBytes(packedSizeLow).CopyTo(header, 7);
        BitConverter.GetBytes(1024u).CopyTo(header, 11);
        header[15] = 2;
        BitConverter.GetBytes(0xDEADBEEFu).CopyTo(header, 16);
        BitConverter.GetBytes(0x5A8E3100u).CopyTo(header, 20);
        header[24] = 29;
        header[25] = 0x30; // store
        BitConverter.GetBytes(nameSize).CopyTo(header, 26);
        BitConverter.GetBytes(0x00000020u).CopyTo(header, 28);
        BitConverter.GetBytes(packedSizeHigh).CopyTo(header, 32); // HIGH_PACK_SIZE
        BitConverter.GetBytes(0u).CopyTo(header, 36);             // HIGH_UNP_SIZE
        nameBytes.CopyTo(header, 40);

        uint crc32 = Force.Crc32.Crc32Algorithm.Compute(header, 2, header.Length - 2);
        BitConverter.GetBytes((ushort)(crc32 & 0xFFFF)).CopyTo(header, 0);
        writer.Write(header);
    }

    [Fact]
    public async Task CreateAsync_Rar4LargeEntry_SkipsFull64BitSize_DoesNotMisparseTrailingHeaders()
    {
        // A LARGE (>= 4 GiB) packed entry: HIGH_PACK_SIZE=1, ADD_SIZE=0 => 4 GiB of packed data.
        // The writer must skip the full 64-bit size before parsing the next header. Reading only
        // the 32-bit ADD_SIZE (0) makes it land on the following header and misparse the packed-data
        // region as extra archived files, silently producing an incorrect SRR.
        string rarPath = Path.Combine(TempDir, "large.rar");
        using (var fs = new FileStream(rarPath, FileMode.Create, FileAccess.Write))
        using (var writer = new BinaryWriter(fs))
        {
            writer.Write(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 });
            WriteRar4ArchiveHeader(writer, RARArchiveFlags.None);
            WriteRar4LargeFileHeader(writer, "big.bin", packedSizeLow: 0, packedSizeHigh: 1);
            // "second.bin" sits 4 GiB downstream in a real archive; here it is not preceded by data.
            WriteRar4FileHeader(writer, "second.bin", 0, 0, 2, 0xCAFEBABE, 29, 0x30, RARFileFlags.LongBlock);
            WriteRar4EndArchive(writer);
        }

        string srrPath = Path.Combine(TempDir, "large.srr");
        var srrWriter = new SRRWriter();
        SRRCreationResult result = await srrWriter.CreateAsync(srrPath, [rarPath]);

        Assert.True(result.Success, result.ErrorMessage);

        var srr = SRRFile.Load(srrPath);
        Assert.Contains("big.bin", srr.ArchivedFiles);
        Assert.DoesNotContain("second.bin", srr.ArchivedFiles);
    }

    #endregion
}
