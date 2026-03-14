using System.Text;
using RARLib;

namespace SRRLib.Tests;

public class SRRFileTests : IDisposable
{
    private readonly string _testDir;

    public SRRFileTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"srrlib_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, true); } catch { }
    }

    #region Header Block Tests

    [Fact]
    public void Load_SrrHeader_ParsesBlockType()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .BuildToFile(_testDir, "header.srr");

        var srr = SRRFile.Load(path);

        Assert.NotNull(srr.HeaderBlock);
        Assert.Equal(SRRBlockType.Header, srr.HeaderBlock!.BlockType);
    }

    [Fact]
    public void Load_SrrHeaderWithAppName_ParsesAppName()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader("pyReScene 0.7")
            .BuildToFile(_testDir, "header_appname.srr");

        var srr = SRRFile.Load(path);

        Assert.NotNull(srr.HeaderBlock);
        Assert.True(srr.HeaderBlock!.HasAppName);
        Assert.Equal("pyReScene 0.7", srr.HeaderBlock.AppName);
    }

    [Fact]
    public void Load_SrrHeaderWithoutAppName_NoAppName()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .BuildToFile(_testDir, "header_no_appname.srr");

        var srr = SRRFile.Load(path);

        Assert.NotNull(srr.HeaderBlock);
        Assert.False(srr.HeaderBlock!.HasAppName);
        Assert.Null(srr.HeaderBlock.AppName);
    }

    #endregion

    #region Stored File Tests

    [Fact]
    public void Load_StoredFile_ParsesFileName()
    {
        byte[] sfvData = Encoding.UTF8.GetBytes("testfile.rar DEADBEEF\r\n");

        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddStoredFile("release.sfv", sfvData)
            .BuildToFile(_testDir, "stored.srr");

        var srr = SRRFile.Load(path);

        Assert.Single(srr.StoredFiles);
        Assert.Equal("release.sfv", srr.StoredFiles[0].FileName);
        Assert.Equal((uint)sfvData.Length, srr.StoredFiles[0].FileLength);
    }

    [Fact]
    public void Load_MultipleStoredFiles_ParsesAll()
    {
        byte[] sfvData = Encoding.UTF8.GetBytes("test.rar 12345678\r\n");
        byte[] nfoData = Encoding.UTF8.GetBytes("Release NFO content\r\n");

        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddStoredFile("release.sfv", sfvData)
            .AddStoredFile("release.nfo", nfoData)
            .BuildToFile(_testDir, "multi_stored.srr");

        var srr = SRRFile.Load(path);

        Assert.Equal(2, srr.StoredFiles.Count);
        Assert.Equal("release.sfv", srr.StoredFiles[0].FileName);
        Assert.Equal("release.nfo", srr.StoredFiles[1].FileName);
    }

    [Fact]
    public void ExtractStoredFile_ExtractsCorrectData()
    {
        byte[] sfvData = Encoding.UTF8.GetBytes("testfile.rar DEADBEEF\r\n");

        string srrPath = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddStoredFile("release.sfv", sfvData)
            .BuildToFile(_testDir, "extract.srr");

        var srr = SRRFile.Load(srrPath);
        string outputDir = Path.Combine(_testDir, "extracted");

        string? extracted = srr.ExtractStoredFile(srrPath, outputDir, name => name.EndsWith(".sfv"));

        Assert.NotNull(extracted);
        Assert.True(File.Exists(extracted));
        byte[] readBack = File.ReadAllBytes(extracted!);
        Assert.Equal(sfvData, readBack);
    }

    [Fact]
    public void ExtractStoredFile_NoMatch_ReturnsNull()
    {
        byte[] data = Encoding.UTF8.GetBytes("test");
        string srrPath = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddStoredFile("release.sfv", data)
            .BuildToFile(_testDir, "nomatch.srr");

        var srr = SRRFile.Load(srrPath);
        string outputDir = Path.Combine(_testDir, "extracted");

        string? extracted = srr.ExtractStoredFile(srrPath, outputDir, name => name.EndsWith(".nfo"));

        Assert.Null(extracted);
    }

    [Fact]
    public void ExtractStoredFile_NullSrrPath_ThrowsArgumentException()
    {
        var srr = new SRRFile();
        Assert.Throws<ArgumentException>(() => srr.ExtractStoredFile("", "output", _ => true));
    }

    [Fact]
    public void ExtractStoredFile_NullOutputDir_ThrowsArgumentException()
    {
        var srr = new SRRFile();
        Assert.Throws<ArgumentException>(() => srr.ExtractStoredFile("file.srr", "", _ => true));
    }

    [Fact]
    public void ExtractStoredFile_NullMatch_ThrowsArgumentNullException()
    {
        var srr = new SRRFile();
        Assert.Throws<ArgumentNullException>(() => srr.ExtractStoredFile("file.srr", "output", null!));
    }

    #endregion

    #region RAR File Reference and Embedded Header Tests

    [Fact]
    public void Load_RarFileBlock_ParsesRarFileName()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", headers =>
            {
                headers.AddArchiveHeader()
                       .AddFileHeader("testfile.txt")
                       .AddEndArchive();
            })
            .BuildToFile(_testDir, "rarfile.srr");

        var srr = SRRFile.Load(path);

        Assert.Single(srr.RarFiles);
        Assert.Equal("release.rar", srr.RarFiles[0].FileName);
    }

    [Fact]
    public void Load_EmbeddedRarHeaders_ExtractsFileEntries()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", headers =>
            {
                headers.AddArchiveHeader()
                       .AddFileHeader("sample.txt", packedSize: 500, unpackedSize: 1024)
                       .AddEndArchive();
            })
            .BuildToFile(_testDir, "fileentries.srr");

        var srr = SRRFile.Load(path);

        Assert.Contains("sample.txt", srr.ArchivedFiles);
    }

    [Fact]
    public void Load_EmbeddedRarHeaders_ExtractsCompressionMethod()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", headers =>
            {
                headers.AddArchiveHeader()
                       .AddFileHeader("file.dat", method: 0x35) // Best compression
                       .AddEndArchive();
            })
            .BuildToFile(_testDir, "method.srr");

        var srr = SRRFile.Load(path);

        // Method is stored as raw 0x30-0x35, decoded to 0-5 by the parser
        Assert.NotNull(srr.CompressionMethod);
        Assert.Equal(5, srr.CompressionMethod); // 0x35 - 0x30 = 5
    }

    [Fact]
    public void Load_EmbeddedRarHeaders_DetectsHostOS()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", headers =>
            {
                headers.AddArchiveHeader()
                       .AddFileHeader("file.dat", hostOS: 3) // Unix
                       .AddEndArchive();
            })
            .BuildToFile(_testDir, "hostos.srr");

        var srr = SRRFile.Load(path);

        Assert.Equal((byte)3, srr.DetectedHostOS);
        Assert.Equal("Unix", srr.DetectedHostOSName);
    }

    [Theory]
    [InlineData(0, "MS-DOS")]
    [InlineData(1, "OS/2")]
    [InlineData(2, "Windows")]
    [InlineData(3, "Unix")]
    [InlineData(4, "Mac OS")]
    [InlineData(5, "BeOS")]
    public void Load_HostOS_MapsToCorrectName(byte hostOS, string expectedName)
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", headers =>
            {
                headers.AddArchiveHeader()
                       .AddFileHeader("file.dat", hostOS: hostOS)
                       .AddEndArchive();
            })
            .BuildToFile(_testDir, $"hostos_{hostOS}.srr");

        var srr = SRRFile.Load(path);

        Assert.Equal(hostOS, srr.DetectedHostOS);
        Assert.Equal(expectedName, srr.DetectedHostOSName);
    }

    [Fact]
    public void Load_EmbeddedRarHeaders_DetectsFileAttributes()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", headers =>
            {
                headers.AddArchiveHeader()
                       .AddFileHeader("file.dat", fileAttributes: 0x000081B4) // Unix mode
                       .AddEndArchive();
            })
            .BuildToFile(_testDir, "attrs.srr");

        var srr = SRRFile.Load(path);

        Assert.Equal(0x000081B4u, srr.DetectedFileAttributes);
    }

    [Fact]
    public void Load_EmbeddedRarHeaders_DetectsUnpackVersion()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", headers =>
            {
                headers.AddArchiveHeader()
                       .AddFileHeader("file.dat", unpVer: 29)
                       .AddEndArchive();
            })
            .BuildToFile(_testDir, "unpver.srr");

        var srr = SRRFile.Load(path);

        Assert.Equal(29, srr.RARVersion);
    }

    [Fact]
    public void Load_ArchiveHeaderFlags_DetectsVolumeArchive()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", headers =>
            {
                headers.AddArchiveHeader(RARArchiveFlags.Volume | RARArchiveFlags.NewNumbering | RARArchiveFlags.FirstVolume)
                       .AddFileHeader("file.dat")
                       .AddEndArchive();
            })
            .BuildToFile(_testDir, "volume.srr");

        var srr = SRRFile.Load(path);

        Assert.True(srr.IsVolumeArchive);
        Assert.True(srr.HasNewVolumeNaming);
        Assert.True(srr.HasFirstVolumeFlag);
    }

    [Fact]
    public void Load_ArchiveHeaderFlags_DetectsSolidArchive()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", headers =>
            {
                headers.AddArchiveHeader(RARArchiveFlags.Solid)
                       .AddFileHeader("file.dat")
                       .AddEndArchive();
            })
            .BuildToFile(_testDir, "solid.srr");

        var srr = SRRFile.Load(path);

        Assert.True(srr.IsSolidArchive);
    }

    [Fact]
    public void Load_ArchiveHeaderFlags_DetectsRecoveryRecord()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", headers =>
            {
                headers.AddArchiveHeader(RARArchiveFlags.Protected)
                       .AddFileHeader("file.dat")
                       .AddEndArchive();
            })
            .BuildToFile(_testDir, "recovery.srr");

        var srr = SRRFile.Load(path);

        Assert.True(srr.HasRecoveryRecord);
    }

    #endregion

    #region CMT Block Tests

    [Fact]
    public void Load_CmtServiceBlock_ExtractsStoredComment()
    {
        string comment = "Test archive comment.";

        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", headers =>
            {
                headers.AddArchiveHeader()
                       .AddFileHeader("file.dat")
                       .AddCmtServiceBlock(comment, method: 0x30) // Stored
                       .AddEndArchive();
            })
            .BuildToFile(_testDir, "cmt_stored.srr");

        var srr = SRRFile.Load(path);

        Assert.Equal(comment, srr.ArchiveComment);
        Assert.NotNull(srr.CmtCompressedData);
    }

    [Fact]
    public void Load_CmtServiceBlock_DetectsCmtHostOS()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", headers =>
            {
                headers.AddArchiveHeader()
                       .AddFileHeader("file.dat", hostOS: 2) // File: Windows
                       .AddCmtServiceBlock("Comment", hostOS: 3) // CMT: Unix
                       .AddEndArchive();
            })
            .BuildToFile(_testDir, "cmt_hostos.srr");

        var srr = SRRFile.Load(path);

        Assert.Equal((byte)2, srr.DetectedHostOS);   // File header Host OS
        Assert.Equal((byte)3, srr.CmtHostOS);        // CMT Host OS
        Assert.Equal("Unix", srr.CmtHostOSName);
    }

    [Fact]
    public void Load_CmtServiceBlock_DetectsZeroedFileTime()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", headers =>
            {
                headers.AddArchiveHeader()
                       .AddFileHeader("file.dat")
                       .AddCmtServiceBlock("Comment", fileTimeDOS: 0)
                       .AddEndArchive();
            })
            .BuildToFile(_testDir, "cmt_zeroed_time.srr");

        var srr = SRRFile.Load(path);

        Assert.Equal(0u, srr.CmtFileTimeDOS);
        Assert.True(srr.CmtHasZeroedFileTime);
        Assert.Equal("Zeroed (no timestamp)", srr.CmtTimestampMode);
    }

    [Fact]
    public void Load_CmtServiceBlock_DetectsNonZeroFileTime()
    {
        uint dosTime = 0x5A8E3100; // Some non-zero DOS time
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", headers =>
            {
                headers.AddArchiveHeader()
                       .AddFileHeader("file.dat")
                       .AddCmtServiceBlock("Comment", fileTimeDOS: dosTime)
                       .AddEndArchive();
            })
            .BuildToFile(_testDir, "cmt_has_time.srr");

        var srr = SRRFile.Load(path);

        Assert.Equal(dosTime, srr.CmtFileTimeDOS);
        Assert.False(srr.CmtHasZeroedFileTime);
        Assert.Equal("Preserved (has timestamp)", srr.CmtTimestampMode);
    }

    [Fact]
    public void Load_CmtServiceBlock_StoresCompressionMethod()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", headers =>
            {
                headers.AddArchiveHeader()
                       .AddFileHeader("file.dat")
                       .AddCmtServiceBlock("Comment", method: 0x33) // Normal
                       .AddEndArchive();
            })
            .BuildToFile(_testDir, "cmt_method.srr");

        var srr = SRRFile.Load(path);

        Assert.Equal((byte)0x33, srr.CmtCompressionMethod);
    }

    [Fact]
    public void Load_CmtServiceBlock_DetectsCmtFileAttributes()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", headers =>
            {
                headers.AddArchiveHeader()
                       .AddFileHeader("file.dat")
                       .AddCmtServiceBlock("Comment", fileAttributes: 0x000081B4)
                       .AddEndArchive();
            })
            .BuildToFile(_testDir, "cmt_attrs.srr");

        var srr = SRRFile.Load(path);

        Assert.Equal(0x000081B4u, srr.CmtFileAttributes);
    }

    #endregion

    #region OSO Hash Tests

    [Fact]
    public void Load_OsoHashBlock_ParsesCorrectly()
    {
        byte[] osoHash = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];

        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddOsoHash("video.avi", 734003200, osoHash)
            .BuildToFile(_testDir, "osohash.srr");

        var srr = SRRFile.Load(path);

        Assert.Single(srr.OsoHashBlocks);
        Assert.Equal("video.avi", srr.OsoHashBlocks[0].FileName);
        Assert.Equal(734003200UL, srr.OsoHashBlocks[0].FileSize);
        Assert.Equal(osoHash, srr.OsoHashBlocks[0].OsoHash);
    }

    #endregion

    #region RAR Padding Tests

    [Fact]
    public void Load_RarPaddingBlock_ParsesCorrectly()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarPadding("release.r00", 512)
            .BuildToFile(_testDir, "padding.srr");

        var srr = SRRFile.Load(path);

        Assert.Single(srr.RarPaddingBlocks);
        Assert.Equal("release.r00", srr.RarPaddingBlocks[0].RarFileName);
        Assert.Equal(512u, srr.RarPaddingBlocks[0].PaddingSize);
    }

    #endregion

    #region Volume Size Detection Tests

    [Fact]
    public void Load_MultipleRarVolumes_CalculatesVolumeSize()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", headers =>
            {
                headers.AddArchiveHeader(RARArchiveFlags.Volume | RARArchiveFlags.FirstVolume)
                       .AddFileHeader("file.dat", packedSize: 5000)
                       .AddEndArchive();
            })
            .AddRarFileWithHeaders("release.r00", headers =>
            {
                headers.AddArchiveHeader(RARArchiveFlags.Volume)
                       .AddFileHeader("file.dat", packedSize: 5000, extraFlags: RARFileFlags.ExtTime | RARFileFlags.SplitBefore | RARFileFlags.SplitAfter)
                       .AddEndArchive();
            })
            .BuildToFile(_testDir, "volumes.srr");

        var srr = SRRFile.Load(path);

        Assert.Equal(2, srr.RarFiles.Count);
        Assert.Equal(2, srr.RarVolumeSizes.Count);
        Assert.NotNull(srr.VolumeSizeBytes);
    }

    #endregion

    #region Directory Entry Tests

    [Fact]
    public void Load_DirectoryEntry_TracksAsDirectory()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", headers =>
            {
                headers.AddArchiveHeader()
                       .AddFileHeader("subdir\\", isDirectory: true, packedSize: 0, unpackedSize: 0)
                       .AddFileHeader("subdir\\file.txt")
                       .AddEndArchive();
            })
            .BuildToFile(_testDir, "dirs.srr");

        var srr = SRRFile.Load(path);

        Assert.Contains("subdir", srr.ArchivedDirectories);
        // On Windows, path separator normalizes to backslash
        bool hasFile = srr.ArchivedFiles.Any(f => f.EndsWith("file.txt"));
        Assert.True(hasFile);
    }

    #endregion

    #region Multiple File Entries Tests

    [Fact]
    public void Load_MultipleFiles_TracksCrcs()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", headers =>
            {
                headers.AddArchiveHeader()
                       .AddFileHeader("file1.txt", fileCrc: 0xAABBCCDD)
                       .AddFileHeader("file2.txt", fileCrc: 0x11223344)
                       .AddEndArchive();
            })
            .BuildToFile(_testDir, "multi_files.srr");

        var srr = SRRFile.Load(path);

        Assert.Equal(2, srr.ArchivedFiles.Count);
        Assert.True(srr.ArchivedFileCrcs.ContainsKey("file1.txt"));
        Assert.True(srr.ArchivedFileCrcs.ContainsKey("file2.txt"));
        Assert.Equal("aabbccdd", srr.ArchivedFileCrcs["file1.txt"]);
        Assert.Equal("11223344", srr.ArchivedFileCrcs["file2.txt"]);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Load_EmptyStoredFile_ParsesCorrectly()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddStoredFile("empty.txt", [])
            .BuildToFile(_testDir, "empty_stored.srr");

        var srr = SRRFile.Load(path);

        Assert.Single(srr.StoredFiles);
        Assert.Equal("empty.txt", srr.StoredFiles[0].FileName);
        Assert.Equal(0u, srr.StoredFiles[0].FileLength);
    }

    [Fact]
    public void Load_NonExistentFile_ThrowsFileNotFoundException()
    {
        Assert.Throws<FileNotFoundException>(() => SRRFile.Load("nonexistent.srr"));
    }

    [Fact]
    public void Load_CaseInsensitivePaths_WorkCorrectly()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", headers =>
            {
                headers.AddArchiveHeader()
                       .AddFileHeader("FILE.TXT", fileCrc: 0x12345678)
                       .AddEndArchive();
            })
            .BuildToFile(_testDir, "case.srr");

        var srr = SRRFile.Load(path);

        // Case-insensitive lookup should work
        Assert.Contains("FILE.TXT", srr.ArchivedFiles);
        Assert.Contains("file.txt", srr.ArchivedFiles);
        Assert.True(srr.ArchivedFileCrcs.ContainsKey("file.txt"));
    }

    [Fact]
    public void DetectedHostOSName_NullHostOS_ReturnsUnknown()
    {
        var srr = new SRRFile();
        // SRRFile is public, new instance has null DetectedHostOS
        Assert.Equal("Unknown", srr.DetectedHostOSName);
    }

    [Fact]
    public void CmtHostOSName_NullCmtHostOS_ReturnsUnknown()
    {
        var srr = new SRRFile();
        Assert.Equal("Unknown", srr.CmtHostOSName);
    }

    [Fact]
    public void CmtTimestampMode_NullCmtFileTime_ReturnsUnknown()
    {
        var srr = new SRRFile();
        Assert.Equal("Unknown", srr.CmtTimestampMode);
    }

    #endregion

    #region Complete SRR Structure Tests

    [Fact]
    public void Load_CompleteSrrFile_ParsesAllBlockTypes()
    {
        byte[] sfvData = Encoding.UTF8.GetBytes("release.rar DEADBEEF\r\n");
        byte[] osoHash = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];

        string path = new SRRTestDataBuilder()
            .AddSrrHeader("TestApp 1.0")
            .AddStoredFile("release.sfv", sfvData)
            .AddOsoHash("video.avi", 734003200, osoHash)
            .AddRarFileWithHeaders("release.rar", headers =>
            {
                headers.AddArchiveHeader(RARArchiveFlags.FirstVolume)
                       .AddFileHeader("video.avi", hostOS: 2, unpVer: 29, method: 0x33, fileCrc: 0xDEADBEEF)
                       .AddCmtServiceBlock("Release comment", hostOS: 2, method: 0x30)
                       .AddEndArchive();
            })
            .BuildToFile(_testDir, "complete.srr");

        var srr = SRRFile.Load(path);

        // Header
        Assert.NotNull(srr.HeaderBlock);
        Assert.Equal("TestApp 1.0", srr.HeaderBlock!.AppName);

        // Stored files
        Assert.Single(srr.StoredFiles);
        Assert.Equal("release.sfv", srr.StoredFiles[0].FileName);

        // OSO hashes
        Assert.Single(srr.OsoHashBlocks);

        // RAR files
        Assert.Single(srr.RarFiles);
        Assert.Equal("release.rar", srr.RarFiles[0].FileName);

        // Archive metadata
        Assert.Equal(29, srr.RARVersion);
        Assert.Equal((byte)2, srr.DetectedHostOS);
        Assert.Equal("Windows", srr.DetectedHostOSName);

        // Comment
        Assert.Equal("Release comment", srr.ArchiveComment);
        Assert.Equal((byte)0x30, srr.CmtCompressionMethod);

        // Archived files
        Assert.Contains("video.avi", srr.ArchivedFiles);
        Assert.Equal("deadbeef", srr.ArchivedFileCrcs["video.avi"]);
    }

    [Fact]
    public void Load_CompressedCmtServiceBlock_StoresCompressedData()
    {
        // CMT block with method 0x33 (Normal compression) - cannot decompress synthetically,
        // but the raw compressed data and method should still be captured
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", headers =>
            {
                headers.AddArchiveHeader()
                       .AddFileHeader("file.dat")
                       .AddCmtServiceBlock("CompressedComment", method: 0x33)
                       .AddEndArchive();
            })
            .BuildToFile(_testDir, "cmt_compressed.srr");

        var srr = SRRFile.Load(path);

        // The compressed data should be captured regardless of decompression success
        Assert.NotNull(srr.CmtCompressedData);
        Assert.Equal((byte)0x33, srr.CmtCompressionMethod);
        // With method 0x33, the synthetic data isn't actually compressed,
        // so decompression via native decompressor may or may not succeed.
        // But CmtCompressedData should always be populated.
        Assert.True(srr.CmtCompressedData!.Length > 0);
    }

    [Fact]
    public void ExtractStoredFile_EmptyFile_ExtractsZeroByteFile()
    {
        string srrPath = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddStoredFile("empty.txt", [])
            .BuildToFile(_testDir, "extract_empty.srr");

        var srr = SRRFile.Load(srrPath);
        string outputDir = Path.Combine(_testDir, "extracted_empty");

        string? extracted = srr.ExtractStoredFile(srrPath, outputDir, name => name == "empty.txt");

        Assert.NotNull(extracted);
        Assert.True(File.Exists(extracted));
        Assert.Equal(0L, new FileInfo(extracted!).Length);
    }

    [Fact]
    public void ExtractStoredFile_MultipleMatches_ReturnsFirstMatch()
    {
        byte[] data1 = Encoding.UTF8.GetBytes("first sfv");
        byte[] data2 = Encoding.UTF8.GetBytes("second sfv");

        string srrPath = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddStoredFile("release.sfv", data1)
            .AddStoredFile("release2.sfv", data2)
            .BuildToFile(_testDir, "multi_match.srr");

        var srr = SRRFile.Load(srrPath);
        string outputDir = Path.Combine(_testDir, "extracted_multi");

        // Predicate matches both .sfv files; should extract the first one
        string? extracted = srr.ExtractStoredFile(srrPath, outputDir, name => name.EndsWith(".sfv"));

        Assert.NotNull(extracted);
        byte[] readBack = File.ReadAllBytes(extracted!);
        Assert.Equal(data1, readBack);
        Assert.EndsWith("release.sfv", extracted);
    }

    [Fact]
    public void ExtractStoredFile_CaseInsensitiveMatch_Works()
    {
        byte[] nfoData = Encoding.UTF8.GetBytes("Release NFO");

        string srrPath = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddStoredFile("Release.NFO", nfoData)
            .BuildToFile(_testDir, "case_extract.srr");

        var srr = SRRFile.Load(srrPath);
        string outputDir = Path.Combine(_testDir, "extracted_case");

        // Match using case-insensitive comparison in the predicate
        string? extracted = srr.ExtractStoredFile(srrPath, outputDir,
            name => name.Equals("release.nfo", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(extracted);
        byte[] readBack = File.ReadAllBytes(extracted!);
        Assert.Equal(nfoData, readBack);
    }

    [Fact]
    public void Load_MultipleRarVolumes_PreservesOrdering()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", headers =>
            {
                headers.AddArchiveHeader(RARArchiveFlags.Volume | RARArchiveFlags.FirstVolume)
                       .AddFileHeader("file.dat", packedSize: 5000)
                       .AddEndArchive();
            })
            .AddRarFileWithHeaders("release.r00", headers =>
            {
                headers.AddArchiveHeader(RARArchiveFlags.Volume)
                       .AddFileHeader("file.dat", packedSize: 5000,
                           extraFlags: RARFileFlags.ExtTime | RARFileFlags.SplitBefore | RARFileFlags.SplitAfter)
                       .AddEndArchive();
            })
            .AddRarFileWithHeaders("release.r01", headers =>
            {
                headers.AddArchiveHeader(RARArchiveFlags.Volume)
                       .AddFileHeader("file.dat", packedSize: 5000,
                           extraFlags: RARFileFlags.ExtTime | RARFileFlags.SplitBefore)
                       .AddEndArchive();
            })
            .BuildToFile(_testDir, "multi_volumes.srr");

        var srr = SRRFile.Load(path);

        Assert.Equal(3, srr.RarFiles.Count);
        Assert.Equal("release.rar", srr.RarFiles[0].FileName);
        Assert.Equal("release.r00", srr.RarFiles[1].FileName);
        Assert.Equal("release.r01", srr.RarFiles[2].FileName);
        Assert.Equal(3, srr.RarVolumeSizes.Count);
    }

    [Fact]
    public void Load_HeaderCrcMismatch_IncrementsCounter()
    {
        // Build an SRR with a valid RAR file reference, then corrupt the embedded header CRC
        byte[] srrData = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", headers =>
            {
                headers.AddArchiveHeader()
                       .AddFileHeader("file.dat")
                       .AddEndArchive();
            })
            .Build();

        // Find the archive header (0x73) after the SRR RarFile block and corrupt its CRC
        // The SRR header is 7 bytes, then the RarFile block, then RAR headers start
        // We need to find the archive header byte 0x73 and corrupt the 2 CRC bytes before it
        int archiveHeaderPos = -1;
        for (int i = 2; i < srrData.Length - 5; i++)
        {
            if (srrData[i] == 0x73 && srrData[i + 1] == 0x00 && srrData[i + 2] == 0x00)
            {
                // Check header size at offset i+3 is 13 (0x0D, 0x00)
                if (srrData[i + 3] == 0x0D && srrData[i + 4] == 0x00)
                {
                    archiveHeaderPos = i - 2; // CRC is 2 bytes before type
                    break;
                }
            }
        }
        Assert.True(archiveHeaderPos >= 0, "Could not find archive header in synthetic SRR data");

        // Corrupt the CRC bytes (flip some bits)
        srrData[archiveHeaderPos] ^= 0xFF;
        srrData[archiveHeaderPos + 1] ^= 0xFF;

        string path = Path.Combine(_testDir, "bad_crc.srr");
        File.WriteAllBytes(path, srrData);

        var srr = SRRFile.Load(path);

        Assert.True(srr.HeaderCrcMismatches >= 1);
    }

    [Fact]
    public void CmtTimestampMode_AllThreeValues_AreCorrectStrings()
    {
        // Test all three CmtTimestampMode values match expected strings

        // Unknown: no CMT block at all
        var srrNoComment = new SRRFile();
        Assert.Equal("Unknown", srrNoComment.CmtTimestampMode);

        // Zeroed: CMT block with fileTimeDOS = 0
        string pathZeroed = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", headers =>
            {
                headers.AddArchiveHeader()
                       .AddFileHeader("file.dat")
                       .AddCmtServiceBlock("Comment", fileTimeDOS: 0)
                       .AddEndArchive();
            })
            .BuildToFile(_testDir, "cmt_mode_zeroed.srr");

        var srrZeroed = SRRFile.Load(pathZeroed);
        Assert.Equal("Zeroed (no timestamp)", srrZeroed.CmtTimestampMode);

        // Preserved: CMT block with non-zero fileTimeDOS
        string pathPreserved = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", headers =>
            {
                headers.AddArchiveHeader()
                       .AddFileHeader("file.dat")
                       .AddCmtServiceBlock("Comment", fileTimeDOS: 0x5A8E3100)
                       .AddEndArchive();
            })
            .BuildToFile(_testDir, "cmt_mode_preserved.srr");

        var srrPreserved = SRRFile.Load(pathPreserved);
        Assert.Equal("Preserved (has timestamp)", srrPreserved.CmtTimestampMode);
    }

    #endregion

    #region Custom Packer Detection Tests

    [Fact]
    public void Load_NormalFileHeaders_NoCustomPackerDetected()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", h =>
            {
                h.AddArchiveHeader(RARArchiveFlags.Volume);
                h.AddFileHeader("video.avi", packedSize: 1024, unpackedSize: 1024);
                h.AddEndArchive();
            })
            .BuildToFile(_testDir, "normal.srr");

        var srr = SRRFile.Load(path);

        Assert.False(srr.HasCustomPackerHeaders);
        Assert.Equal(CustomPackerType.None, srr.CustomPackerDetected);
    }

    [Fact]
    public void Load_AllOnesUnpackedSize_DetectsCustomPacker()
    {
        // Sentinel 1: unpacked_size = 0xFFFFFFFFFFFFFFFF (RELOADED/HI2U/0x0007/0x0815 style)
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", h =>
            {
                h.AddArchiveHeader(RARArchiveFlags.Volume);
                h.AddFileHeaderWithLargeSize("video.avi",
                    packedSizeLow: 0xFFFFFFFF, packedSizeHigh: 0xFFFFFFFF,
                    unpackedSizeLow: 0xFFFFFFFF, unpackedSizeHigh: 0xFFFFFFFF);
                h.AddEndArchive();
            })
            .BuildToFile(_testDir, "reloaded_style.srr");

        var srr = SRRFile.Load(path);

        Assert.True(srr.HasCustomPackerHeaders);
        Assert.Equal(CustomPackerType.AllOnesWithLargeFlag, srr.CustomPackerDetected);
    }

    [Fact]
    public void Load_MaxUint32WithoutLargeFlag_DetectsCustomPacker()
    {
        // Sentinel 2: unpacked_size = 0xFFFFFFFF without LARGE flag (QCF style)
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", h =>
            {
                h.AddArchiveHeader(RARArchiveFlags.Volume);
                h.AddFileHeader("video.avi",
                    packedSize: 1024,
                    unpackedSize: 0xFFFFFFFF,
                    extraFlags: RARFileFlags.ExtTime); // No LARGE flag
                h.AddEndArchive();
            })
            .BuildToFile(_testDir, "qcf_style.srr");

        var srr = SRRFile.Load(path);

        Assert.True(srr.HasCustomPackerHeaders);
        Assert.Equal(CustomPackerType.MaxUint32WithoutLargeFlag, srr.CustomPackerDetected);
    }

    [Fact]
    public void Load_LargeFileWithHighUnpSizeZero_NoFalsePositive()
    {
        // Legitimate large file: UnpackedSize = 0xFFFFFFFF but LARGE flag set with HIGH_UNP = 0
        // This is a valid ~4GB file, NOT a custom packer sentinel
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", h =>
            {
                h.AddArchiveHeader(RARArchiveFlags.Volume);
                h.AddFileHeaderWithLargeSize("video.avi",
                    packedSizeLow: 0xFFFFFFFF, packedSizeHigh: 0,
                    unpackedSizeLow: 0xFFFFFFFF, unpackedSizeHigh: 0);
                h.AddEndArchive();
            })
            .BuildToFile(_testDir, "large_legit.srr");

        var srr = SRRFile.Load(path);

        // Combined UnpackedSize = 0x00000000FFFFFFFF, not 0xFFFFFFFFFFFFFFFF
        Assert.False(srr.HasCustomPackerHeaders);
        Assert.Equal(CustomPackerType.None, srr.CustomPackerDetected);
    }

    [Fact]
    public void Load_SecondFileHasSentinel_StillDetected()
    {
        // Detection should trigger even if only the second file header has the sentinel
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", h =>
            {
                h.AddArchiveHeader(RARArchiveFlags.Volume);
                h.AddFileHeader("readme.nfo", packedSize: 100, unpackedSize: 100);
                h.AddFileHeaderWithLargeSize("video.avi",
                    packedSizeLow: 0xFFFFFFFF, packedSizeHigh: 0xFFFFFFFF,
                    unpackedSizeLow: 0xFFFFFFFF, unpackedSizeHigh: 0xFFFFFFFF);
                h.AddEndArchive();
            })
            .BuildToFile(_testDir, "second_sentinel.srr");

        var srr = SRRFile.Load(path);

        Assert.True(srr.HasCustomPackerHeaders);
        Assert.Equal(CustomPackerType.AllOnesWithLargeFlag, srr.CustomPackerDetected);
    }

    [Fact]
    public void Load_DirectoryWithMaxSize_NotDetected()
    {
        // Directory entries should not trigger detection (directories often have size=0 or garbage)
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", h =>
            {
                h.AddArchiveHeader(RARArchiveFlags.Volume);
                h.AddFileHeader("subdir",
                    packedSize: 0,
                    unpackedSize: 0xFFFFFFFF,
                    extraFlags: RARFileFlags.ExtTime,
                    isDirectory: true);
                h.AddFileHeader("subdir\\video.avi", packedSize: 1024, unpackedSize: 1024);
                h.AddEndArchive();
            })
            .BuildToFile(_testDir, "dir_maxsize.srr");

        var srr = SRRFile.Load(path);

        Assert.False(srr.HasCustomPackerHeaders);
        Assert.Equal(CustomPackerType.None, srr.CustomPackerDetected);
    }

    #endregion

    #region RAR5 Embedded Header Tests

    [Fact]
    public void Load_Rar5Headers_DetectsRarVersion50()
    {
        byte[] srrData = BuildSrrWithRar5FileHeader("release.rar", "sample.txt",
            fileFlags: (ulong)RAR5FileFlags.TimePresent | (ulong)RAR5FileFlags.Crc32Present);

        string path = Path.Combine(_testDir, "rar5_version.srr");
        File.WriteAllBytes(path, srrData);

        var srr = SRRFile.Load(path);

        Assert.Equal(50, srr.RARVersion);
    }

    [Fact]
    public void Load_Rar5Headers_ExtractsArchivedFileName()
    {
        byte[] srrData = BuildSrrWithRar5FileHeader("release.rar", "video.avi",
            fileFlags: (ulong)RAR5FileFlags.TimePresent | (ulong)RAR5FileFlags.Crc32Present);

        string path = Path.Combine(_testDir, "rar5_filename.srr");
        File.WriteAllBytes(path, srrData);

        var srr = SRRFile.Load(path);

        Assert.Contains("video.avi", srr.ArchivedFiles);
    }

    [Fact]
    public void Load_Rar5Headers_ExtractsFileCrc()
    {
        byte[] srrData = BuildSrrWithRar5FileHeader("release.rar", "file.dat",
            fileFlags: (ulong)RAR5FileFlags.TimePresent | (ulong)RAR5FileFlags.Crc32Present,
            fileCrc: 0xAABBCCDD);

        string path = Path.Combine(_testDir, "rar5_crc.srr");
        File.WriteAllBytes(path, srrData);

        var srr = SRRFile.Load(path);

        Assert.True(srr.ArchivedFileCrcs.ContainsKey("file.dat"));
        Assert.Equal("aabbccdd", srr.ArchivedFileCrcs["file.dat"]);
    }

    [Fact]
    public void Load_Rar5Headers_ParsesRarFileBlockName()
    {
        byte[] srrData = BuildSrrWithRar5FileHeader("release.part01.rar", "data.bin",
            fileFlags: (ulong)RAR5FileFlags.Crc32Present);

        string path = Path.Combine(_testDir, "rar5_rarfile.srr");
        File.WriteAllBytes(path, srrData);

        var srr = SRRFile.Load(path);

        Assert.Single(srr.RarFiles);
        Assert.Equal("release.part01.rar", srr.RarFiles[0].FileName);
    }

    [Fact]
    public void Load_Rar5VolumeFlagSet_DetectsVolumeArchive()
    {
        byte[] srrData = BuildSrrWithRar5FileHeader("release.rar", "file.dat",
            archiveFlags: 0x0001,
            fileFlags: (ulong)RAR5FileFlags.Crc32Present);

        string path = Path.Combine(_testDir, "rar5_volume.srr");
        File.WriteAllBytes(path, srrData);

        var srr = SRRFile.Load(path);

        Assert.True(srr.IsVolumeArchive);
    }

    [Fact]
    public void Load_Rar5SolidFlagSet_DetectsSolidArchive()
    {
        byte[] srrData = BuildSrrWithRar5FileHeader("release.rar", "file.dat",
            archiveFlags: 0x0004,
            fileFlags: (ulong)RAR5FileFlags.Crc32Present);

        string path = Path.Combine(_testDir, "rar5_solid.srr");
        File.WriteAllBytes(path, srrData);

        var srr = SRRFile.Load(path);

        Assert.True(srr.IsSolidArchive);
    }

    [Fact]
    public void Load_Rar5SplitFlags_DetectsCorrectly()
    {
        byte[] srrData = BuildSrrWithRar5FileHeader("release.rar", "file.dat",
            fileFlags: (ulong)RAR5FileFlags.Crc32Present,
            headerFlags: (ulong)RAR5HeaderFlags.SplitAfter);

        string path = Path.Combine(_testDir, "rar5_split.srr");
        File.WriteAllBytes(path, srrData);

        var srr = SRRFile.Load(path);

        Assert.Contains("file.dat", srr.ArchivedFiles);
    }

    #endregion

    #region Timestamp Tests

    [Fact]
    public void Load_FileWithModificationTime_PopulatesTimestamp()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", headers =>
            {
                headers.AddArchiveHeader()
                       .AddFileHeader("file.txt", fileTimeDOS: 0x5A8E3100)
                       .AddEndArchive();
            })
            .BuildToFile(_testDir, "ts_mtime.srr");

        var srr = SRRFile.Load(path);

        Assert.Contains("file.txt", srr.ArchivedFiles);
        Assert.True(srr.ArchivedFileTimestamps.ContainsKey("file.txt"));
        Assert.True(srr.ArchivedFileTimestamps["file.txt"] > DateTime.MinValue);
    }

    [Fact]
    public void Load_FileWithExtendedTimesCreationAndAccess_PopulatesAllTimestamps()
    {
        byte[] srrData = BuildSrrWithExtendedTimeHeaders("release.rar", "file.txt");

        string path = Path.Combine(_testDir, "ts_ctime_atime.srr");
        File.WriteAllBytes(path, srrData);

        var srr = SRRFile.Load(path);

        Assert.Contains("file.txt", srr.ArchivedFiles);
        Assert.True(srr.ArchivedFileTimestamps.ContainsKey("file.txt"), "mtime should be populated");
        Assert.True(srr.ArchivedFileCreationTimes.ContainsKey("file.txt"), "ctime should be populated");
        Assert.True(srr.ArchivedFileAccessTimes.ContainsKey("file.txt"), "atime should be populated");
    }

    [Fact]
    public void Load_MultipleFilesWithTimestamps_TracksEachFile()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", headers =>
            {
                headers.AddArchiveHeader()
                       .AddFileHeader("file1.txt", fileTimeDOS: 0x5A8E3100)
                       .AddFileHeader("file2.txt", fileTimeDOS: 0x5B0E3100)
                       .AddEndArchive();
            })
            .BuildToFile(_testDir, "ts_multi.srr");

        var srr = SRRFile.Load(path);

        Assert.Equal(2, srr.ArchivedFiles.Count);
        Assert.True(srr.ArchivedFileTimestamps.ContainsKey("file1.txt"));
        Assert.True(srr.ArchivedFileTimestamps.ContainsKey("file2.txt"));
        Assert.NotEqual(srr.ArchivedFileTimestamps["file1.txt"], srr.ArchivedFileTimestamps["file2.txt"]);
    }

    #endregion

    #region Directory Timestamp Tests

    [Fact]
    public void Load_DirectoryEntryWithTimestamp_PopulatesDirectoryTimestamps()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", headers =>
            {
                headers.AddArchiveHeader()
                       .AddFileHeader("subdir\\", isDirectory: true, packedSize: 0, unpackedSize: 0, fileTimeDOS: 0x5A8E3100)
                       .AddFileHeader("subdir\\file.txt")
                       .AddEndArchive();
            })
            .BuildToFile(_testDir, "dir_ts.srr");

        var srr = SRRFile.Load(path);

        Assert.Contains("subdir", srr.ArchivedDirectories);
        Assert.True(srr.ArchivedDirectoryTimestamps.ContainsKey("subdir"));
        Assert.True(srr.ArchivedDirectoryTimestamps["subdir"] > DateTime.MinValue);
    }

    [Fact]
    public void Load_DirectoryWithExtendedTimes_PopulatesAllDirectoryTimestamps()
    {
        byte[] srrData = BuildSrrWithExtendedTimeHeaders("release.rar", "subdir\\", isDirectory: true);

        string path = Path.Combine(_testDir, "dir_ext_ts.srr");
        File.WriteAllBytes(path, srrData);

        var srr = SRRFile.Load(path);

        Assert.Contains("subdir", srr.ArchivedDirectories);
        Assert.True(srr.ArchivedDirectoryTimestamps.ContainsKey("subdir"), "directory mtime should be populated");
        Assert.True(srr.ArchivedDirectoryCreationTimes.ContainsKey("subdir"), "directory ctime should be populated");
        Assert.True(srr.ArchivedDirectoryAccessTimes.ContainsKey("subdir"), "directory atime should be populated");
    }

    #endregion

    #region Large File Header Tests (HIGH_PACK/HIGH_UNP)

    [Fact]
    public void Load_LargeFileHeader_TracksHighPackSize()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", headers =>
            {
                headers.AddArchiveHeader()
                       .AddFileHeaderWithLargeSize("bigfile.dat",
                           packedSizeLow: 0x10000000, packedSizeHigh: 0x00000002,
                           unpackedSizeLow: 0x20000000, unpackedSizeHigh: 0x00000003)
                       .AddEndArchive();
            })
            .BuildToFile(_testDir, "large_pack.srr");

        var srr = SRRFile.Load(path);

        Assert.NotNull(srr.DetectedHighPackSize);
        Assert.Equal(0x00000002u, srr.DetectedHighPackSize);
    }

    [Fact]
    public void Load_LargeFileHeader_TracksHighUnpSize()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", headers =>
            {
                headers.AddArchiveHeader()
                       .AddFileHeaderWithLargeSize("bigfile.dat",
                           packedSizeLow: 0x10000000, packedSizeHigh: 0x00000001,
                           unpackedSizeLow: 0x20000000, unpackedSizeHigh: 0x00000005)
                       .AddEndArchive();
            })
            .BuildToFile(_testDir, "large_unp.srr");

        var srr = SRRFile.Load(path);

        Assert.NotNull(srr.DetectedHighUnpSize);
        Assert.Equal(0x00000005u, srr.DetectedHighUnpSize);
    }

    [Fact]
    public void Load_LargeFileHeader_SetsHasLargeFilesFlag()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", headers =>
            {
                headers.AddArchiveHeader()
                       .AddFileHeaderWithLargeSize("bigfile.dat",
                           packedSizeLow: 0x10000000, packedSizeHigh: 0x00000001,
                           unpackedSizeLow: 0x20000000, unpackedSizeHigh: 0x00000001)
                       .AddEndArchive();
            })
            .BuildToFile(_testDir, "large_flag.srr");

        var srr = SRRFile.Load(path);

        Assert.True(srr.HasLargeFiles);
    }

    [Fact]
    public void Load_NormalFileHeader_NoHighSizes()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", headers =>
            {
                headers.AddArchiveHeader()
                       .AddFileHeader("small.txt", packedSize: 100, unpackedSize: 200)
                       .AddEndArchive();
            })
            .BuildToFile(_testDir, "no_large.srr");

        var srr = SRRFile.Load(path);

        Assert.Null(srr.DetectedHighPackSize);
        Assert.Null(srr.DetectedHighUnpSize);
        Assert.False(srr.HasLargeFiles == true);
    }

    #endregion

    #region Split File Flag Tests

    [Fact]
    public void Load_SplitAfterFlag_CrcOverwrittenByFinalSegment()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", headers =>
            {
                headers.AddArchiveHeader(RARArchiveFlags.Volume | RARArchiveFlags.FirstVolume)
                       .AddFileHeader("file.dat", packedSize: 5000, fileCrc: 0x11111111,
                           extraFlags: RARFileFlags.ExtTime | RARFileFlags.SplitAfter)
                       .AddEndArchive();
            })
            .AddRarFileWithHeaders("release.r00", headers =>
            {
                headers.AddArchiveHeader(RARArchiveFlags.Volume)
                       .AddFileHeader("file.dat", packedSize: 5000, fileCrc: 0x22222222,
                           extraFlags: RARFileFlags.ExtTime | RARFileFlags.SplitBefore)
                       .AddEndArchive();
            })
            .BuildToFile(_testDir, "split_crc.srr");

        var srr = SRRFile.Load(path);

        // The final CRC should come from the header without SplitAfter (the last segment)
        Assert.Equal("22222222", srr.ArchivedFileCrcs["file.dat"]);
    }

    [Fact]
    public void Load_SplitBeforeFlag_FileStillTracked()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.r00", headers =>
            {
                headers.AddArchiveHeader(RARArchiveFlags.Volume)
                       .AddFileHeader("file.dat", packedSize: 5000, fileCrc: 0xAABBCCDD,
                           extraFlags: RARFileFlags.ExtTime | RARFileFlags.SplitBefore)
                       .AddEndArchive();
            })
            .BuildToFile(_testDir, "split_before.srr");

        var srr = SRRFile.Load(path);

        Assert.Contains("file.dat", srr.ArchivedFiles);
    }

    [Fact]
    public void Load_SplitMiddleVolume_FinalCrcFromLastSegment()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", headers =>
            {
                headers.AddArchiveHeader(RARArchiveFlags.Volume | RARArchiveFlags.FirstVolume)
                       .AddFileHeader("file.dat", packedSize: 5000, fileCrc: 0x11111111,
                           extraFlags: RARFileFlags.ExtTime | RARFileFlags.SplitAfter)
                       .AddEndArchive();
            })
            .AddRarFileWithHeaders("release.r00", headers =>
            {
                headers.AddArchiveHeader(RARArchiveFlags.Volume)
                       .AddFileHeader("file.dat", packedSize: 5000, fileCrc: 0x22222222,
                           extraFlags: RARFileFlags.ExtTime | RARFileFlags.SplitBefore | RARFileFlags.SplitAfter)
                       .AddEndArchive();
            })
            .AddRarFileWithHeaders("release.r01", headers =>
            {
                headers.AddArchiveHeader(RARArchiveFlags.Volume)
                       .AddFileHeader("file.dat", packedSize: 3000, fileCrc: 0x33333333,
                           extraFlags: RARFileFlags.ExtTime | RARFileFlags.SplitBefore)
                       .AddEndArchive();
            })
            .BuildToFile(_testDir, "split_middle.srr");

        var srr = SRRFile.Load(path);

        Assert.Equal("33333333", srr.ArchivedFileCrcs["file.dat"]);
        Assert.Equal(3, srr.RarFiles.Count);
    }

    [Fact]
    public void Load_NoSplitFlags_CrcFromFirstHeader()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddRarFileWithHeaders("release.rar", headers =>
            {
                headers.AddArchiveHeader()
                       .AddFileHeader("file.dat", fileCrc: 0xDEADBEEF)
                       .AddEndArchive();
            })
            .BuildToFile(_testDir, "no_split.srr");

        var srr = SRRFile.Load(path);

        Assert.Equal("deadbeef", srr.ArchivedFileCrcs["file.dat"]);
    }

    #endregion

    #region RAR5 and Extended Time Helper Methods

    /// <summary>
    /// Builds an SRR file containing a RAR5 file block with basic file header.
    /// </summary>
    private static byte[] BuildSrrWithRar5FileHeader(
        string rarFileName, string archivedFileName,
        ulong archiveFlags = 0, ulong fileFlags = 0,
        uint fileCrc = 0xDEADBEEF, ulong headerFlags = 0)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        // SRR Header block
        w.Write((ushort)0x0000);
        w.Write((byte)0x69);
        w.Write((ushort)0x0000);
        w.Write((ushort)7);

        // SRR RarFile block
        byte[] rarNameBytes = Encoding.UTF8.GetBytes(rarFileName);
        ushort rarBlockSize = (ushort)(7 + 2 + rarNameBytes.Length);
        w.Write((ushort)0x0000);
        w.Write((byte)0x71);
        w.Write((ushort)0x0000);
        w.Write(rarBlockSize);
        w.Write((ushort)rarNameBytes.Length);
        w.Write(rarNameBytes);

        // RAR5 marker
        w.Write(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 });

        // RAR5 Main archive header
        WriteRar5Block(w, RAR5BlockType.Main, 0, mainMs =>
        {
            WriteVInt(mainMs, archiveFlags);
        });

        // RAR5 File header
        byte[] fileNameBytes = Encoding.UTF8.GetBytes(archivedFileName);

        WriteRar5Block(w, RAR5BlockType.File, headerFlags, fileMs =>
        {
            WriteVInt(fileMs, fileFlags);
            WriteVInt(fileMs, 1024);  // unpacked size
            WriteVInt(fileMs, 0x20);  // file attributes

            if ((fileFlags & (ulong)RAR5FileFlags.TimePresent) != 0)
            {
                using var bw = new BinaryWriter(fileMs, Encoding.UTF8, leaveOpen: true);
                bw.Write((uint)1700000000);
            }

            if ((fileFlags & (ulong)RAR5FileFlags.Crc32Present) != 0)
            {
                using var bw = new BinaryWriter(fileMs, Encoding.UTF8, leaveOpen: true);
                bw.Write(fileCrc);
            }

            WriteVInt(fileMs, 0x00);  // compression info
            WriteVInt(fileMs, 0x00);  // host OS
            WriteVInt(fileMs, (ulong)fileNameBytes.Length);
            fileMs.Write(fileNameBytes, 0, fileNameBytes.Length);
        });

        // RAR5 End archive header
        WriteRar5Block(w, RAR5BlockType.EndArchive, 0, endMs =>
        {
            WriteVInt(endMs, 0);
        });

        w.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// Writes a RAR5 block with proper CRC32 header.
    /// </summary>
    private static void WriteRar5Block(BinaryWriter mainWriter, RAR5BlockType blockType, ulong headerFlags, Action<MemoryStream> writeContent)
    {
        using var contentMs = new MemoryStream();
        WriteVInt(contentMs, (ulong)blockType);
        WriteVInt(contentMs, headerFlags);
        writeContent(contentMs);

        byte[] content = contentMs.ToArray();

        using var headerMs = new MemoryStream();
        WriteVInt(headerMs, (ulong)content.Length);
        headerMs.Write(content, 0, content.Length);

        byte[] headerData = headerMs.ToArray();
        uint crc32 = Force.Crc32.Crc32Algorithm.Compute(headerData);

        mainWriter.Write(crc32);
        mainWriter.Write(headerData);
    }

    /// <summary>
    /// Writes a variable-length integer (vint) to a stream.
    /// </summary>
    private static void WriteVInt(Stream stream, ulong value)
    {
        while (true)
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
            {
                b |= 0x80;
                stream.WriteByte(b);
            }
            else
            {
                stream.WriteByte(b);
                break;
            }
        }
    }

    /// <summary>
    /// Builds an SRR with a RAR4 file header that includes full extended time data
    /// (mtime, ctime, and atime with DOS-second precision).
    /// </summary>
    private static byte[] BuildSrrWithExtendedTimeHeaders(string rarFileName, string archivedFileName, bool isDirectory = false)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        // SRR Header block
        w.Write((ushort)0x0000);
        w.Write((byte)0x69);
        w.Write((ushort)0x0000);
        w.Write((ushort)7);

        // SRR RarFile block
        byte[] rarNameBytes = Encoding.UTF8.GetBytes(rarFileName);
        ushort rarBlockSize = (ushort)(7 + 2 + rarNameBytes.Length);
        w.Write((ushort)0x0000);
        w.Write((byte)0x71);
        w.Write((ushort)0x0000);
        w.Write(rarBlockSize);
        w.Write((ushort)rarNameBytes.Length);
        w.Write(rarNameBytes);

        // RAR4 Archive header (13 bytes, type 0x73)
        byte[] archiveHeader = new byte[13];
        archiveHeader[2] = 0x73;
        BitConverter.GetBytes((ushort)0).CopyTo(archiveHeader, 3);
        BitConverter.GetBytes((ushort)13).CopyTo(archiveHeader, 5);
        uint ahCrc = Force.Crc32.Crc32Algorithm.Compute(archiveHeader, 2, archiveHeader.Length - 2);
        BitConverter.GetBytes((ushort)(ahCrc & 0xFFFF)).CopyTo(archiveHeader, 0);
        w.Write(archiveHeader);

        // RAR4 File header with extended time data for mtime, ctime, atime
        byte[] nameBytes = Encoding.ASCII.GetBytes(archivedFileName);
        ushort nameSize = (ushort)nameBytes.Length;

        // Extended time flags:
        // mtime bits 15-12 = 0x8 (present, 0 extra bytes)
        // ctime bits 11-8  = 0x8 (present, 0 extra bytes)
        // atime bits 7-4   = 0x8 (present, 0 extra bytes)
        // arctime bits 3-0 = 0x0 (not present)
        ushort extFlags = 0x8880;
        // ctime and atime each need a 4-byte DOS time
        int extTimeSize = 2 + 4 + 4; // extFlags(2) + ctime_DOS(4) + atime_DOS(4)

        RARFileFlags flags = RARFileFlags.LongBlock | RARFileFlags.ExtTime;
        if (isDirectory)
        {
            flags |= RARFileFlags.Directory;
        }

        ushort headerSize = (ushort)(7 + 25 + nameSize + extTimeSize);

        byte[] header = new byte[headerSize];
        header[2] = 0x74;
        BitConverter.GetBytes((ushort)flags).CopyTo(header, 3);
        BitConverter.GetBytes(headerSize).CopyTo(header, 5);
        BitConverter.GetBytes((uint)0).CopyTo(header, 7);
        BitConverter.GetBytes((uint)0).CopyTo(header, 11);
        header[15] = 2;
        BitConverter.GetBytes((uint)0xDEADBEEF).CopyTo(header, 16);
        BitConverter.GetBytes((uint)0x5A8E3100).CopyTo(header, 20);
        header[24] = 29;
        header[25] = 0x33;
        BitConverter.GetBytes(nameSize).CopyTo(header, 26);
        BitConverter.GetBytes((uint)0x20).CopyTo(header, 28);
        nameBytes.CopyTo(header, 32);

        int extOffset = 32 + nameSize;
        BitConverter.GetBytes(extFlags).CopyTo(header, extOffset);
        BitConverter.GetBytes((uint)0x5A8E2000).CopyTo(header, extOffset + 2); // ctime DOS
        BitConverter.GetBytes((uint)0x5A8E4000).CopyTo(header, extOffset + 6); // atime DOS

        uint crc32 = Force.Crc32.Crc32Algorithm.Compute(header, 2, header.Length - 2);
        BitConverter.GetBytes((ushort)(crc32 & 0xFFFF)).CopyTo(header, 0);

        w.Write(header);

        // End archive
        byte[] endArchive = new byte[7];
        endArchive[2] = 0x7B;
        BitConverter.GetBytes((ushort)0).CopyTo(endArchive, 3);
        BitConverter.GetBytes((ushort)7).CopyTo(endArchive, 5);
        uint eaCrc = Force.Crc32.Crc32Algorithm.Compute(endArchive, 2, endArchive.Length - 2);
        BitConverter.GetBytes((ushort)(eaCrc & 0xFFFF)).CopyTo(endArchive, 0);
        w.Write(endArchive);

        w.Flush();
        return ms.ToArray();
    }

    #endregion
}
