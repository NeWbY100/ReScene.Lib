using ReScene.SRR;

namespace ReScene.Tests;

public class SRRFileRealDataTests : IDisposable
{
    private static readonly string TestDataDir = Path.Combine(AppContext.BaseDirectory, "TestData");
    private readonly string _tempDir;

    public SRRFileRealDataTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"srrlib_realdata_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private static string TestFile(params string[] parts)
    {
        string[] allParts = [TestDataDir, .. parts];
        return Path.Combine(allParts);
    }

    #region store_little

    [Fact]
    public void Load_StoreLittle_HasHeader()
    {
        var srr = SRRFile.Load(TestFile("store_little", "store_little.srr"));

        Assert.NotNull(srr.HeaderBlock);
        Assert.Equal(SRRBlockType.Header, srr.HeaderBlock!.BlockType);
    }

    [Fact]
    public void Load_StoreLittle_HasAppName()
    {
        var srr = SRRFile.Load(TestFile("store_little", "store_little.srr"));

        Assert.True(srr.HeaderBlock!.HasAppName);
        Assert.Equal("ReScene .NET 1.2", srr.HeaderBlock.AppName);
    }

    [Fact]
    public void Load_StoreLittle_HasOneRarFile()
    {
        var srr = SRRFile.Load(TestFile("store_little", "store_little.srr"));

        Assert.Single(srr.RarFiles);
        Assert.Equal("store_little.rar", srr.RarFiles[0].FileName);
    }

    [Fact]
    public void Load_StoreLittle_HasOneArchivedFile()
    {
        var srr = SRRFile.Load(TestFile("store_little", "store_little.srr"));

        Assert.Single(srr.ArchivedFiles);
        Assert.Contains("little_file.txt", srr.ArchivedFiles);
    }

    [Fact]
    public void Load_StoreLittle_HasNoStoredFiles()
    {
        var srr = SRRFile.Load(TestFile("store_little", "store_little.srr"));

        Assert.Empty(srr.StoredFiles);
    }

    [Fact]
    public void Load_StoreLittle_IsNotVolumeArchive()
    {
        var srr = SRRFile.Load(TestFile("store_little", "store_little.srr"));

        Assert.Equal(false, srr.IsVolumeArchive);
    }

    [Fact]
    public void Load_StoreLittle_IsNotSolid()
    {
        var srr = SRRFile.Load(TestFile("store_little", "store_little.srr"));

        Assert.Equal(false, srr.IsSolidArchive);
    }

    [Fact]
    public void Load_StoreLittle_HasNoRecoveryRecord()
    {
        var srr = SRRFile.Load(TestFile("store_little", "store_little.srr"));

        Assert.Equal(false, srr.HasRecoveryRecord);
    }

    [Fact]
    public void Load_StoreLittle_CompressionMethodIsStore()
    {
        var srr = SRRFile.Load(TestFile("store_little", "store_little.srr"));

        Assert.Equal(0, srr.CompressionMethod);
    }

    [Fact]
    public void Load_StoreLittle_RarVersionIs20()
    {
        var srr = SRRFile.Load(TestFile("store_little", "store_little.srr"));

        Assert.Equal(20, srr.RARVersion);
    }

    [Fact]
    public void Load_StoreLittle_HasNoComment()
    {
        var srr = SRRFile.Load(TestFile("store_little", "store_little.srr"));

        Assert.Null(srr.ArchiveComment);
    }

    #endregion

    #region store_little — stored file with path

    [Fact]
    public void Load_StoreLittleWithPath_HasOneStoredFile()
    {
        var srr = SRRFile.Load(TestFile("store_little", "store_little_srrfile_with_path.srr"));

        Assert.Single(srr.StoredFiles);
    }

    [Fact]
    public void Load_StoreLittleWithPath_StoredFileHasForwardSlashPath()
    {
        var srr = SRRFile.Load(TestFile("store_little", "store_little_srrfile_with_path.srr"));

        Assert.Equal("store_little/store_little.srr", srr.StoredFiles[0].FileName);
    }

    [Fact]
    public void Load_StoreLittleWithPath_StoredFileHasCorrectLength()
    {
        var srr = SRRFile.Load(TestFile("store_little", "store_little_srrfile_with_path.srr"));

        Assert.Equal(124u, srr.StoredFiles[0].FileLength);
    }

    [Fact]
    public void Load_StoreLittleWithPathBackslash_StoredFileHasBackslashPath()
    {
        var srr = SRRFile.Load(TestFile("store_little", "store_little_srrfile_with_path_backslash.srr"));

        Assert.Single(srr.StoredFiles);
        Assert.Equal("store_little\\store_little.srr", srr.StoredFiles[0].FileName);
    }

    [Fact]
    public void Load_StoreLittleWithPathBackslash_HasDifferentAppName()
    {
        var srr = SRRFile.Load(TestFile("store_little", "store_little_srrfile_with_path_backslash.srr"));

        Assert.Equal("wxHexEditor v0.1", srr.HeaderBlock!.AppName);
    }

    #endregion

    #region store_empty

    [Fact]
    public void Load_StoreEmpty_HasOneRarFile()
    {
        var srr = SRRFile.Load(TestFile("store_empty", "store_empty.srr"));

        Assert.Single(srr.RarFiles);
        Assert.Equal("store_empty.rar", srr.RarFiles[0].FileName);
    }

    [Fact]
    public void Load_StoreEmpty_HasOneArchivedFile()
    {
        var srr = SRRFile.Load(TestFile("store_empty", "store_empty.srr"));

        Assert.Single(srr.ArchivedFiles);
        Assert.Contains("empty_file.txt", srr.ArchivedFiles);
    }

    [Fact]
    public void Load_StoreEmpty_HasNoStoredFiles()
    {
        var srr = SRRFile.Load(TestFile("store_empty", "store_empty.srr"));

        Assert.Empty(srr.StoredFiles);
    }

    [Fact]
    public void Load_AddedEmptyFile_HasStoredFileWithZeroLength()
    {
        var srr = SRRFile.Load(TestFile("store_empty", "added_empty_file.srr"));

        Assert.Single(srr.StoredFiles);
        Assert.Equal("empty_file.txt", srr.StoredFiles[0].FileName);
        Assert.Equal(0u, srr.StoredFiles[0].FileLength);
    }

    #endregion

    #region store_rr_solid_auth_unicode_new

    [Fact]
    public void Load_StoreRrSolidAuth_HasOneRarFile()
    {
        var srr = SRRFile.Load(TestFile("store_rr_solid_auth_unicode_new", "store_rr_solid_auth.part1.srr"));

        Assert.Single(srr.RarFiles);
        Assert.Equal("store_rr_solid_auth.part1.rar", srr.RarFiles[0].FileName);
    }

    [Fact]
    public void Load_StoreRrSolidAuth_HasThreeArchivedFiles()
    {
        var srr = SRRFile.Load(TestFile("store_rr_solid_auth_unicode_new", "store_rr_solid_auth.part1.srr"));

        Assert.Equal(3, srr.ArchivedFiles.Count);
        Assert.Contains("empty_file.txt", srr.ArchivedFiles);
        Assert.Contains("little_file.txt", srr.ArchivedFiles);
        Assert.Contains("users_manual4.00.txt", srr.ArchivedFiles);
    }

    [Fact]
    public void Load_StoreRrSolidAuth_IsVolumeArchive()
    {
        var srr = SRRFile.Load(TestFile("store_rr_solid_auth_unicode_new", "store_rr_solid_auth.part1.srr"));

        Assert.Equal(true, srr.IsVolumeArchive);
    }

    [Fact]
    public void Load_StoreRrSolidAuth_HasRecoveryRecord()
    {
        var srr = SRRFile.Load(TestFile("store_rr_solid_auth_unicode_new", "store_rr_solid_auth.part1.srr"));

        Assert.Equal(true, srr.HasRecoveryRecord);
    }

    [Fact]
    public void Load_StoreRrSolidAuth_HasNewVolumeNaming()
    {
        var srr = SRRFile.Load(TestFile("store_rr_solid_auth_unicode_new", "store_rr_solid_auth.part1.srr"));

        Assert.Equal(true, srr.HasNewVolumeNaming);
    }

    [Fact]
    public void Load_StoreRrSolidAuth_HasNoStoredFiles()
    {
        var srr = SRRFile.Load(TestFile("store_rr_solid_auth_unicode_new", "store_rr_solid_auth.part1.srr"));

        Assert.Empty(srr.StoredFiles);
    }

    #endregion

    #region store_split_folder_old_srrsfv_windows

    [Fact]
    public void Load_StoreSplitFolder_HasThreeRarFiles()
    {
        var srr = SRRFile.Load(TestFile("store_split_folder_old_srrsfv_windows", "store_split_folder.srr"));

        Assert.Equal(3, srr.RarFiles.Count);
        Assert.Equal("store_split_folder.rar", srr.RarFiles[0].FileName);
        Assert.Equal("store_split_folder.r00", srr.RarFiles[1].FileName);
        Assert.Equal("store_split_folder.r01", srr.RarFiles[2].FileName);
    }

    [Fact]
    public void Load_StoreSplitFolder_IsVolumeArchive()
    {
        var srr = SRRFile.Load(TestFile("store_split_folder_old_srrsfv_windows", "store_split_folder.srr"));

        Assert.Equal(true, srr.IsVolumeArchive);
    }

    [Fact]
    public void Load_StoreSplitFolder_UsesOldVolumeNaming()
    {
        var srr = SRRFile.Load(TestFile("store_split_folder_old_srrsfv_windows", "store_split_folder.srr"));

        Assert.Equal(false, srr.HasNewVolumeNaming);
    }

    [Fact]
    public void Load_StoreSplitFolder_HasOneStoredSfv()
    {
        var srr = SRRFile.Load(TestFile("store_split_folder_old_srrsfv_windows", "store_split_folder.srr"));

        Assert.Single(srr.StoredFiles);
        Assert.Equal("store_split_folder.sfv", srr.StoredFiles[0].FileName);
        Assert.Equal(372u, srr.StoredFiles[0].FileLength);
    }

    [Fact]
    public void Load_StoreSplitFolder_HasFourArchivedFiles()
    {
        var srr = SRRFile.Load(TestFile("store_split_folder_old_srrsfv_windows", "store_split_folder.srr"));

        Assert.Equal(4, srr.ArchivedFiles.Count);
    }

    [Fact]
    public void Load_StoreSplitFolder_ArchivedFilesHaveTxtSubdirectory()
    {
        var srr = SRRFile.Load(TestFile("store_split_folder_old_srrsfv_windows", "store_split_folder.srr"));

        Assert.Contains(srr.ArchivedFiles, f => f.StartsWith("txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Load_StoreSplitFolder_ContainsSpecificArchivedFiles()
    {
        var srr = SRRFile.Load(TestFile("store_split_folder_old_srrsfv_windows", "store_split_folder.srr"));

        Assert.Contains("txt\\empty_file.txt", srr.ArchivedFiles);
        Assert.Contains("txt\\little_file.txt", srr.ArchivedFiles);
        Assert.Contains("txt\\users_manual4.00.txt", srr.ArchivedFiles);
    }

    [Fact]
    public void Load_StoreSplitFolder_ContainsGreekFilename()
    {
        var srr = SRRFile.Load(TestFile("store_split_folder_old_srrsfv_windows", "store_split_folder.srr"));

        Assert.Contains(srr.ArchivedFiles, f => f.Contains("\u039A\u03B5\u03AF\u03BC\u03B5\u03BD\u03BF"));
    }

    [Fact]
    public void Load_StoreSplitFolder_HasNoRecoveryRecord()
    {
        var srr = SRRFile.Load(TestFile("store_split_folder_old_srrsfv_windows", "store_split_folder.srr"));

        Assert.Equal(false, srr.HasRecoveryRecord);
    }

    [Fact]
    public void Load_WinRar280_HeaderOnly()
    {
        var srr = SRRFile.Load(TestFile("store_split_folder_old_srrsfv_windows", "winrar2.80.srr"));

        Assert.NotNull(srr.HeaderBlock);
        Assert.Empty(srr.RarFiles);
        Assert.Empty(srr.StoredFiles);
        Assert.Empty(srr.ArchivedFiles);
    }

    #endregion

    #region store_utf8_comment

    [Fact]
    public void Load_StoreUtf8Comment_HasArchiveComment()
    {
        var srr = SRRFile.Load(TestFile("store_utf8_comment", "store_utf8_comment.srr"));

        Assert.NotNull(srr.ArchiveComment);
        Assert.Equal("Test comment.", srr.ArchiveComment);
    }

    [Fact]
    public void Load_StoreUtf8Comment_HasCommentBytes()
    {
        var srr = SRRFile.Load(TestFile("store_utf8_comment", "store_utf8_comment.srr"));

        Assert.NotNull(srr.ArchiveCommentBytes);
        Assert.Equal(13, srr.ArchiveCommentBytes!.Length);
    }

    [Fact]
    public void Load_StoreUtf8Comment_HasOneRarFile()
    {
        var srr = SRRFile.Load(TestFile("store_utf8_comment", "store_utf8_comment.srr"));

        Assert.Single(srr.RarFiles);
        Assert.Equal("store_utf8_comment.rar", srr.RarFiles[0].FileName);
    }

    [Fact]
    public void Load_StoreUtf8Comment_HasThreeArchivedFiles()
    {
        var srr = SRRFile.Load(TestFile("store_utf8_comment", "store_utf8_comment.srr"));

        Assert.Equal(3, srr.ArchivedFiles.Count);
        Assert.Contains("little_file.txt", srr.ArchivedFiles);
        Assert.Contains("empty_file.txt", srr.ArchivedFiles);
    }

    [Fact]
    public void Load_Utf8FilenameAdded_HasGreekStoredFilename()
    {
        var srr = SRRFile.Load(TestFile("store_utf8_comment", "utf8_filename_added.srr"));

        Assert.Single(srr.StoredFiles);
        Assert.Contains("\u039A\u03B5\u03AF\u03BC\u03B5\u03BD\u03BF", srr.StoredFiles[0].FileName);
    }

    [Fact]
    public void Load_Utf8FilenameAdded_StoredFileHasCorrectLength()
    {
        var srr = SRRFile.Load(TestFile("store_utf8_comment", "utf8_filename_added.srr"));

        Assert.Equal(65u, srr.StoredFiles[0].FileLength);
    }

    [Fact]
    public void Load_Utf8FilenameAdded_AlsoHasComment()
    {
        var srr = SRRFile.Load(TestFile("store_utf8_comment", "utf8_filename_added.srr"));

        Assert.Equal("Test comment.", srr.ArchiveComment);
    }

    #endregion

    #region no_files_stored

    [Theory]
    [InlineData("Burial.Ground.The.Nights.of.Terror.1981.DVDRip.XviD-spawny.srr", 37)]
    [InlineData("Hofmanns.Potion.2002.DVDRip.XviD-belos.srr", 49)]
    [InlineData("Zombi.Holocaust.1980.DVDRip.XviD-spawny.srr", 37)]
    public void Load_NoFilesStored_HasExpectedRarFileCount(string fileName, int expectedCount)
    {
        var srr = SRRFile.Load(TestFile("no_files_stored", fileName));

        Assert.Equal(expectedCount, srr.RarFiles.Count);
    }

    [Theory]
    [InlineData("Burial.Ground.The.Nights.of.Terror.1981.DVDRip.XviD-spawny.srr")]
    [InlineData("Hofmanns.Potion.2002.DVDRip.XviD-belos.srr")]
    [InlineData("Zombi.Holocaust.1980.DVDRip.XviD-spawny.srr")]
    public void Load_NoFilesStored_HasZeroStoredFiles(string fileName)
    {
        var srr = SRRFile.Load(TestFile("no_files_stored", fileName));

        Assert.Empty(srr.StoredFiles);
    }

    [Theory]
    [InlineData("Burial.Ground.The.Nights.of.Terror.1981.DVDRip.XviD-spawny.srr",
        "Burial.Ground.The.Nights.of.Terror.DVDRip.XviD-spawny.avi")]
    [InlineData("Hofmanns.Potion.2002.DVDRip.XviD-belos.srr",
        "Hofmann_s_Potion_xvid-belos.avi")]
    [InlineData("Zombi.Holocaust.1980.DVDRip.XviD-spawny.srr",
        "Zombi.Holocaust.DVDRip.XviD-spawny.avi")]
    public void Load_NoFilesStored_HasExpectedArchivedFile(string fileName, string archivedFile)
    {
        var srr = SRRFile.Load(TestFile("no_files_stored", fileName));

        Assert.Single(srr.ArchivedFiles);
        Assert.Contains(archivedFile, srr.ArchivedFiles);
    }

    [Theory]
    [InlineData("Burial.Ground.The.Nights.of.Terror.1981.DVDRip.XviD-spawny.srr")]
    [InlineData("Hofmanns.Potion.2002.DVDRip.XviD-belos.srr")]
    [InlineData("Zombi.Holocaust.1980.DVDRip.XviD-spawny.srr")]
    public void Load_NoFilesStored_IsVolumeArchive(string fileName)
    {
        var srr = SRRFile.Load(TestFile("no_files_stored", fileName));

        Assert.Equal(true, srr.IsVolumeArchive);
        Assert.Equal(true, srr.HasNewVolumeNaming);
    }

    [Fact]
    public void Load_NoFilesStored_BurialGround_HasCorrectAppName()
    {
        var srr = SRRFile.Load(TestFile("no_files_stored",
            "Burial.Ground.The.Nights.of.Terror.1981.DVDRip.XviD-spawny.srr"));

        Assert.Equal("ReScene .NET Beta 7", srr.HeaderBlock!.AppName);
    }

    [Fact]
    public void Load_NoFilesStored_Hofmanns_HasCorrectAppName()
    {
        var srr = SRRFile.Load(TestFile("no_files_stored",
            "Hofmanns.Potion.2002.DVDRip.XviD-belos.srr"));

        Assert.Equal("ReScene .NET Beta 10", srr.HeaderBlock!.AppName);
    }

    #endregion

    #region incomplete_srr

    [Fact]
    public void Load_IncompleteSrr_LoadsWithoutThrowing()
    {
        var srr = SRRFile.Load(TestFile("incomplete_srr",
            "Shark.Week.2012.Shark.Fight.HDTV.x264-KILLERS.srr"));

        Assert.NotNull(srr.HeaderBlock);
    }

    [Fact]
    public void Load_IncompleteSrr_ParsesAvailableRarFiles()
    {
        var srr = SRRFile.Load(TestFile("incomplete_srr",
            "Shark.Week.2012.Shark.Fight.HDTV.x264-KILLERS.srr"));

        Assert.Equal(2, srr.RarFiles.Count);
        Assert.Equal("shark.week.2012.shark.fight-killers.r00", srr.RarFiles[0].FileName);
        Assert.Equal("shark.week.2012.shark.fight-killers.r01", srr.RarFiles[1].FileName);
    }

    [Fact]
    public void Load_IncompleteSrr_ParsesAvailableStoredFiles()
    {
        var srr = SRRFile.Load(TestFile("incomplete_srr",
            "Shark.Week.2012.Shark.Fight.HDTV.x264-KILLERS.srr"));

        Assert.Equal(2, srr.StoredFiles.Count);
        Assert.Equal("shark.week.2012.shark.fight-killers.nfo", srr.StoredFiles[0].FileName);
        Assert.Equal("shark.week.2012.shark.fight-killers.sfv", srr.StoredFiles[1].FileName);
    }

    [Fact]
    public void Load_IncompleteSrr_ParsesAvailableArchivedFiles()
    {
        var srr = SRRFile.Load(TestFile("incomplete_srr",
            "Shark.Week.2012.Shark.Fight.HDTV.x264-KILLERS.srr"));

        Assert.Single(srr.ArchivedFiles);
        Assert.Contains("shark.week.2012.shark.fight-killers.mp4", srr.ArchivedFiles);
    }

    [Fact]
    public void Load_IncompleteSrr_HasCorrectAppName()
    {
        var srr = SRRFile.Load(TestFile("incomplete_srr",
            "Shark.Week.2012.Shark.Fight.HDTV.x264-KILLERS.srr"));

        Assert.Equal("ReScene .NET Beta 11", srr.HeaderBlock!.AppName);
    }

    #endregion

    #region hash_capitals

    [Fact]
    public void Load_HashCapitals_AllLower_LoadsSuccessfully()
    {
        var srr = SRRFile.Load(TestFile("hash_capitals",
            "Parlamentet.S06E02.SWEDiSH-SQC_alllower.srr"));

        Assert.NotNull(srr.HeaderBlock);
        Assert.Equal(25, srr.RarFiles.Count);
    }

    [Fact]
    public void Load_HashCapitals_Capitals_LoadsSuccessfully()
    {
        var srr = SRRFile.Load(TestFile("hash_capitals",
            "Parlamentet.S06E02.SWEDiSH-SQC_capitals.srr"));

        Assert.NotNull(srr.HeaderBlock);
        Assert.Equal(25, srr.RarFiles.Count);
    }

    [Fact]
    public void Load_HashCapitals_BothHaveSameRarFileCount()
    {
        var srrLower = SRRFile.Load(TestFile("hash_capitals",
            "Parlamentet.S06E02.SWEDiSH-SQC_alllower.srr"));
        var srrCaps = SRRFile.Load(TestFile("hash_capitals",
            "Parlamentet.S06E02.SWEDiSH-SQC_capitals.srr"));

        Assert.Equal(srrLower.RarFiles.Count, srrCaps.RarFiles.Count);
    }

    [Fact]
    public void Load_HashCapitals_BothHaveSameArchivedFile()
    {
        var srrLower = SRRFile.Load(TestFile("hash_capitals",
            "Parlamentet.S06E02.SWEDiSH-SQC_alllower.srr"));
        var srrCaps = SRRFile.Load(TestFile("hash_capitals",
            "Parlamentet.S06E02.SWEDiSH-SQC_capitals.srr"));

        Assert.Single(srrLower.ArchivedFiles);
        Assert.Single(srrCaps.ArchivedFiles);
        Assert.Contains("parlamentet.s06e02-sqc.mpg", srrLower.ArchivedFiles);
        Assert.Contains("parlamentet.s06e02-sqc.mpg", srrCaps.ArchivedFiles);
    }

    [Fact]
    public void Load_HashCapitals_BothHaveSameStoredFileCount()
    {
        var srrLower = SRRFile.Load(TestFile("hash_capitals",
            "Parlamentet.S06E02.SWEDiSH-SQC_alllower.srr"));
        var srrCaps = SRRFile.Load(TestFile("hash_capitals",
            "Parlamentet.S06E02.SWEDiSH-SQC_capitals.srr"));

        Assert.Equal(2, srrLower.StoredFiles.Count);
        Assert.Equal(2, srrCaps.StoredFiles.Count);
    }

    [Fact]
    public void Load_HashCapitals_BothHaveSameCrcMismatchCount()
    {
        var srrLower = SRRFile.Load(TestFile("hash_capitals",
            "Parlamentet.S06E02.SWEDiSH-SQC_alllower.srr"));
        var srrCaps = SRRFile.Load(TestFile("hash_capitals",
            "Parlamentet.S06E02.SWEDiSH-SQC_capitals.srr"));

        Assert.Equal(srrLower.HeaderCrcMismatches, srrCaps.HeaderCrcMismatches);
        Assert.Equal(25, srrLower.HeaderCrcMismatches);
    }

    [Fact]
    public void Load_HashCapitals_Capitals_HasMixedCaseRarFileNames()
    {
        var srr = SRRFile.Load(TestFile("hash_capitals",
            "Parlamentet.S06E02.SWEDiSH-SQC_capitals.srr"));

        bool hasLowercase = srr.RarFiles.Any(rf => rf.FileName.StartsWith("parlamentet", StringComparison.Ordinal));
        bool hasUppercase = srr.RarFiles.Any(rf => rf.FileName.StartsWith("Parlamentet", StringComparison.Ordinal));

        Assert.True(hasLowercase);
        Assert.True(hasUppercase);
    }

    [Fact]
    public void Load_HashCapitals_AllLower_HasAllLowercaseRarFileNames()
    {
        var srr = SRRFile.Load(TestFile("hash_capitals",
            "Parlamentet.S06E02.SWEDiSH-SQC_alllower.srr"));

        Assert.All(srr.RarFiles, rf =>
            Assert.StartsWith("parlamentet", rf.FileName));
    }

    [Fact]
    public void Load_HashCapitals_StoredFilesHaveExpectedContent()
    {
        var srr = SRRFile.Load(TestFile("hash_capitals",
            "Parlamentet.S06E02.SWEDiSH-SQC_alllower.srr"));

        Assert.Contains(srr.StoredFiles, sf => sf.FileName == "parlamentet.s06e02-sqc.nfo");
        Assert.Contains(srr.StoredFiles, sf => sf.FileName == "parlamentet.s06e02-sqc.sfv");
    }

    [Fact]
    public void Load_HashCapitals_StoredFileSizesMatch()
    {
        var srrLower = SRRFile.Load(TestFile("hash_capitals",
            "Parlamentet.S06E02.SWEDiSH-SQC_alllower.srr"));
        var srrCaps = SRRFile.Load(TestFile("hash_capitals",
            "Parlamentet.S06E02.SWEDiSH-SQC_capitals.srr"));

        var lowerNfo = srrLower.StoredFiles.First(sf => sf.FileName.EndsWith(".nfo", StringComparison.OrdinalIgnoreCase));
        var capsNfo = srrCaps.StoredFiles.First(sf => sf.FileName.EndsWith(".nfo", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(lowerNfo.FileLength, capsNfo.FileLength);
        Assert.Equal(8249u, lowerNfo.FileLength);
    }

    #endregion

    #region cleanup_script

    [Fact]
    public void Load_CleanupScript_007ViewToAKill_LoadsSuccessfully()
    {
        var srr = SRRFile.Load(TestFile("cleanup_script",
            "007.A.View.To.A.Kill.1985.UE.iNTERNAL.DVDRip.XviD-iNCiTE.fine_2cd.srr"));

        Assert.NotNull(srr.HeaderBlock);
    }

    [Fact]
    public void Load_CleanupScript_007ViewToAKill_Has98RarFiles()
    {
        var srr = SRRFile.Load(TestFile("cleanup_script",
            "007.A.View.To.A.Kill.1985.UE.iNTERNAL.DVDRip.XviD-iNCiTE.fine_2cd.srr"));

        Assert.Equal(98, srr.RarFiles.Count);
    }

    [Fact]
    public void Load_CleanupScript_007ViewToAKill_HasFourStoredFiles()
    {
        var srr = SRRFile.Load(TestFile("cleanup_script",
            "007.A.View.To.A.Kill.1985.UE.iNTERNAL.DVDRip.XviD-iNCiTE.fine_2cd.srr"));

        Assert.Equal(4, srr.StoredFiles.Count);
    }

    [Fact]
    public void Load_CleanupScript_007ViewToAKill_HasTwoArchivedAviFiles()
    {
        var srr = SRRFile.Load(TestFile("cleanup_script",
            "007.A.View.To.A.Kill.1985.UE.iNTERNAL.DVDRip.XviD-iNCiTE.fine_2cd.srr"));

        Assert.Equal(2, srr.ArchivedFiles.Count);
        Assert.Contains("incite-avtak.ue.xvid.cd1.avi", srr.ArchivedFiles);
        Assert.Contains("incite-avtak.ue.xvid.cd2.avi", srr.ArchivedFiles);
    }

    [Fact]
    public void Load_CleanupScript_007QuantumOfSolace_HasSubdirectoryRarFile()
    {
        var srr = SRRFile.Load(TestFile("cleanup_script",
            "007.Quantum.Of.Solace.DVDRip.XViD-PUKKA.cleanup_script.srr"));

        Assert.Single(srr.RarFiles);
        Assert.Equal("CD1/p-qos-cd1.rar", srr.RarFiles[0].FileName);
    }

    [Fact]
    public void Load_CleanupScript_007QuantumOfSolace_HasStoredFilesWithSubdirectories()
    {
        var srr = SRRFile.Load(TestFile("cleanup_script",
            "007.Quantum.Of.Solace.DVDRip.XViD-PUKKA.cleanup_script.srr"));

        Assert.Equal(3, srr.StoredFiles.Count);
        Assert.Contains(srr.StoredFiles, sf => sf.FileName == "p-qos.nfo");
        Assert.Contains(srr.StoredFiles, sf => sf.FileName == "Sample/p-qos-sample.srs");
        Assert.Contains(srr.StoredFiles, sf => sf.FileName == "CD1/p-qos-cd1.sfv");
    }

    [Fact]
    public void Load_CleanupScript_007QuantumOfSolace_HasRecoveryRecord()
    {
        var srr = SRRFile.Load(TestFile("cleanup_script",
            "007.Quantum.Of.Solace.DVDRip.XViD-PUKKA.cleanup_script.srr"));

        Assert.Equal(true, srr.HasRecoveryRecord);
    }

    [Fact]
    public void Load_CleanupScript_007QuantumOfSolace_HasCleanupAppName()
    {
        var srr = SRRFile.Load(TestFile("cleanup_script",
            "007.Quantum.Of.Solace.DVDRip.XViD-PUKKA.cleanup_script.srr"));

        Assert.Equal("ReScene Database Cleanup Script 1.0", srr.HeaderBlock!.AppName);
    }

    [Fact]
    public void Load_CleanupScript_Fixed_HasMoreStoredFiles()
    {
        var srr = SRRFile.Load(TestFile("cleanup_script", "fixed",
            "007.Quantum.Of.Solace.DVDRip.XViD-PUKKA.cleanup_script.srr"));

        Assert.Equal(4, srr.StoredFiles.Count);
        Assert.Contains(srr.StoredFiles, sf => sf.FileName == "CD2/p-qos-cd2.sfv");
    }

    #endregion

    #region best_little

    [Fact]
    public void Load_BestLittle_AddedEmptyFile_HasStoredFileWithZeroLength()
    {
        var srr = SRRFile.Load(TestFile("best_little", "added_empty_file.srr"));

        Assert.Single(srr.StoredFiles);
        Assert.Equal("empty_file.txt", srr.StoredFiles[0].FileName);
        Assert.Equal(0u, srr.StoredFiles[0].FileLength);
    }

    #endregion

    #region bug_detected_as_being_different3

    [Theory]
    [InlineData("Akte.2012.08.01.German.Doku.WS.dTV.XViD-FiXTv_f4n4t.srr")]
    [InlineData("Akte.2012.08.01.German.Doku.WS.dTV.XViD-FiXTv_nzbsauto.srr")]
    [InlineData("The.Closer.S04E10.Zeitbomben.German.WS.DVDRip.XviD-EXPiRED_f4n4t.srr")]
    [InlineData("The.Closer.S04E10.Zeitbomben.German.WS.DVDRip.XviD-EXPiRED_nzbsauto.srr")]
    public void Load_BugDetectedAsDifferent_LoadsSuccessfully(string fileName)
    {
        var srr = SRRFile.Load(TestFile("bug_detected_as_being_different3", fileName));

        Assert.NotNull(srr.HeaderBlock);
        Assert.True(srr.RarFiles.Count > 0);
    }

    [Fact]
    public void Load_BugDetectedAsDifferent_SameRelease_HasSameArchivedFiles()
    {
        var srrF4n4t = SRRFile.Load(TestFile("bug_detected_as_being_different3",
            "Akte.2012.08.01.German.Doku.WS.dTV.XViD-FiXTv_f4n4t.srr"));
        var srrNzbsauto = SRRFile.Load(TestFile("bug_detected_as_being_different3",
            "Akte.2012.08.01.German.Doku.WS.dTV.XViD-FiXTv_nzbsauto.srr"));

        Assert.Equal(srrF4n4t.ArchivedFiles.Count, srrNzbsauto.ArchivedFiles.Count);
    }

    #endregion

    #region Cross-directory consistency tests

    [Theory]
    [InlineData("store_little/store_little.srr")]
    [InlineData("store_empty/store_empty.srr")]
    [InlineData("store_utf8_comment/store_utf8_comment.srr")]
    [InlineData("store_split_folder_old_srrsfv_windows/store_split_folder.srr")]
    [InlineData("store_rr_solid_auth_unicode_new/store_rr_solid_auth.part1.srr")]
    [InlineData("no_files_stored/Burial.Ground.The.Nights.of.Terror.1981.DVDRip.XviD-spawny.srr")]
    [InlineData("incomplete_srr/Shark.Week.2012.Shark.Fight.HDTV.x264-KILLERS.srr")]
    [InlineData("hash_capitals/Parlamentet.S06E02.SWEDiSH-SQC_alllower.srr")]
    public void Load_AllSrrFiles_HaveNonNullHeader(string relativePath)
    {
        var srr = SRRFile.Load(TestFile(relativePath.Split('/')));

        Assert.NotNull(srr.HeaderBlock);
        Assert.Equal(SRRBlockType.Header, srr.HeaderBlock!.BlockType);
    }

    [Theory]
    [InlineData("store_little/store_little.srr")]
    [InlineData("store_empty/store_empty.srr")]
    [InlineData("store_utf8_comment/store_utf8_comment.srr")]
    [InlineData("store_split_folder_old_srrsfv_windows/store_split_folder.srr")]
    [InlineData("store_rr_solid_auth_unicode_new/store_rr_solid_auth.part1.srr")]
    [InlineData("no_files_stored/Burial.Ground.The.Nights.of.Terror.1981.DVDRip.XviD-spawny.srr")]
    [InlineData("incomplete_srr/Shark.Week.2012.Shark.Fight.HDTV.x264-KILLERS.srr")]
    [InlineData("hash_capitals/Parlamentet.S06E02.SWEDiSH-SQC_alllower.srr")]
    public void Load_AllSrrFiles_HaveAppName(string relativePath)
    {
        var srr = SRRFile.Load(TestFile(relativePath.Split('/')));

        Assert.True(srr.HeaderBlock!.HasAppName);
        Assert.False(string.IsNullOrEmpty(srr.HeaderBlock.AppName));
    }

    #endregion

    #region ExtractStoredFile tests

    [Fact]
    public void ExtractStoredFile_WithPath_ExtractsFileWithCorrectSize()
    {
        string srrPath = TestFile("store_little", "store_little_srrfile_with_path.srr");
        var srr = SRRFile.Load(srrPath);

        string? extractedPath = srr.ExtractStoredFile(srrPath, _tempDir,
            name => name.EndsWith(".srr", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(extractedPath);
        Assert.True(File.Exists(extractedPath));
        var info = new FileInfo(extractedPath!);
        Assert.Equal(124, info.Length);
    }

    [Fact]
    public void ExtractStoredFile_WithPath_ExtractsToFlatFilename()
    {
        string srrPath = TestFile("store_little", "store_little_srrfile_with_path.srr");
        var srr = SRRFile.Load(srrPath);

        string? extractedPath = srr.ExtractStoredFile(srrPath, _tempDir,
            name => name.EndsWith(".srr", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(extractedPath);
        Assert.Equal("store_little.srr", Path.GetFileName(extractedPath));
    }

    [Fact]
    public void ExtractStoredFile_GreekFilename_ExtractsSuccessfully()
    {
        string srrPath = TestFile("store_utf8_comment", "utf8_filename_added.srr");
        var srr = SRRFile.Load(srrPath);

        string? extractedPath = srr.ExtractStoredFile(srrPath, _tempDir,
            name => name.Contains("\u039A\u03B5\u03AF\u03BC\u03B5\u03BD\u03BF"));

        Assert.NotNull(extractedPath);
        Assert.True(File.Exists(extractedPath));
        var info = new FileInfo(extractedPath!);
        Assert.Equal(65, info.Length);
    }

    [Fact]
    public void ExtractStoredFile_NonExistentFile_ReturnsNull()
    {
        string srrPath = TestFile("store_little", "store_little.srr");
        var srr = SRRFile.Load(srrPath);

        string? extractedPath = srr.ExtractStoredFile(srrPath, _tempDir,
            name => name == "does_not_exist.txt");

        Assert.Null(extractedPath);
    }

    [Fact]
    public void ExtractStoredFile_EmptyStoredFile_CreatesZeroLengthFile()
    {
        string srrPath = TestFile("store_empty", "added_empty_file.srr");
        var srr = SRRFile.Load(srrPath);

        string? extractedPath = srr.ExtractStoredFile(srrPath, _tempDir,
            name => name == "empty_file.txt");

        Assert.NotNull(extractedPath);
        Assert.True(File.Exists(extractedPath));
        var info = new FileInfo(extractedPath!);
        Assert.Equal(0, info.Length);
    }

    [Fact]
    public void ExtractStoredFile_SfvFromSplitFolder_ExtractsCorrectContent()
    {
        string srrPath = TestFile("store_split_folder_old_srrsfv_windows", "store_split_folder.srr");
        var srr = SRRFile.Load(srrPath);

        string? extractedPath = srr.ExtractStoredFile(srrPath, _tempDir,
            name => name.EndsWith(".sfv", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(extractedPath);
        Assert.True(File.Exists(extractedPath));
        var info = new FileInfo(extractedPath!);
        Assert.Equal(372, info.Length);
    }

    [Fact]
    public void ExtractStoredFile_ExtractedSrrCanBeReloaded()
    {
        string srrPath = TestFile("store_little", "store_little_srrfile_with_path.srr");
        var srr = SRRFile.Load(srrPath);

        string? extractedPath = srr.ExtractStoredFile(srrPath, _tempDir,
            name => name.EndsWith(".srr", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(extractedPath);

        var reloaded = SRRFile.Load(extractedPath!);
        Assert.NotNull(reloaded.HeaderBlock);
        Assert.Single(reloaded.RarFiles);
        Assert.Equal("store_little.rar", reloaded.RarFiles[0].FileName);
    }

    [Fact]
    public void ExtractStoredFile_NfoFromCleanupScript_ExtractsCorrectSize()
    {
        string srrPath = TestFile("cleanup_script",
            "007.A.View.To.A.Kill.1985.UE.iNTERNAL.DVDRip.XviD-iNCiTE.fine_2cd.srr");
        var srr = SRRFile.Load(srrPath);

        string? extractedPath = srr.ExtractStoredFile(srrPath, _tempDir,
            name => name.EndsWith(".nfo", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(extractedPath);
        var info = new FileInfo(extractedPath!);
        Assert.Equal(12800, info.Length);
    }

    #endregion

    #region FileNotFoundException

    [Fact]
    public void Load_NonExistentFile_ThrowsFileNotFoundException()
    {
        string fakePath = TestFile("store_little", "does_not_exist.srr");

        Assert.Throws<FileNotFoundException>(() => SRRFile.Load(fakePath));
    }

    #endregion
}
