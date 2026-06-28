using System.Text;
using ReScene.Core.IO;

namespace ReScene.Tests;

public class SFVFileTests
{
    private static readonly string TestDataDir = Path.Combine(AppContext.BaseDirectory, "TestData");

    private static string TestFile(params string[] parts)
    {
        string[] allParts = [TestDataDir, .. parts];
        return Path.Combine(allParts);
    }

    #region store_split_folder.sfv

    [Fact]
    public void ReadFile_StoreSplitFolderSFV_HasThreeEntries()
    {
        var sfv = SFVFile.ReadFile(TestFile("store_split_folder_old_srrsfv_windows", "store_split_folder.sfv"));

        Assert.Equal(3, sfv.Entries.Count);
    }

    [Fact]
    public void ReadFile_StoreSplitFolderSFV_EntriesHaveCorrectFileNames()
    {
        var sfv = SFVFile.ReadFile(TestFile("store_split_folder_old_srrsfv_windows", "store_split_folder.sfv"));

        Assert.Equal("store_split_folder.r00", sfv.Entries[0].FileName);
        Assert.Equal("store_split_folder.r01", sfv.Entries[1].FileName);
        Assert.Equal("store_split_folder.rar", sfv.Entries[2].FileName);
    }

    [Fact]
    public void ReadFile_StoreSplitFolderSFV_EntriesHaveEightCharCRC()
    {
        var sfv = SFVFile.ReadFile(TestFile("store_split_folder_old_srrsfv_windows", "store_split_folder.sfv"));

        Assert.All(sfv.Entries, entry => Assert.Equal(8, entry.CRC.Length));
    }

    #endregion

    #region checksum.sfv

    [Fact]
    public void ReadFile_ChecksumSFV_ThrowsOnFilenameWithSpaces()
    {
        // checksum.sfv contains a Greek filename with spaces that the parser
        // cannot handle (it splits on space to find the CRC)
        string path = TestFile("txt", "checksum.sfv");

        Assert.Throws<InvalidDataException>(() => SFVFile.ReadFile(path));
    }

    [Fact]
    public void ReadFile_ChecksumAndCopy_BothThrowSameException()
    {
        // Both files have identical content with space-containing filenames
        string originalPath = TestFile("txt", "checksum.sfv");
        string copyPath = TestFile("txt", "checksum_copy.sfv");

        Assert.Throws<InvalidDataException>(() => SFVFile.ReadFile(originalPath));
        Assert.Throws<InvalidDataException>(() => SFVFile.ReadFile(copyPath));
    }

    #endregion

    #region store_rr_solid_auth.sfv

    [Fact]
    public void ReadFile_StoreRrSolidAuth_HasFourEntries()
    {
        var sfv = SFVFile.ReadFile(TestFile("store_rr_solid_auth_unicode_new", "store_rr_solid_auth.sfv"));

        Assert.Equal(4, sfv.Entries.Count);
    }

    #endregion

    #region Error Handling

    [Fact]
    public void ReadFile_NonExistentFile_Throws()
    {
        string bogusPath = Path.Combine(TestDataDir, "nonexistent.sfv");

        Assert.ThrowsAny<IOException>(() => SFVFile.ReadFile(bogusPath));
    }

    #endregion

    #region CRC Format

    [Fact]
    public void ReadFile_EntriesHaveLowercaseCRC()
    {
        var sfv = SFVFile.ReadFile(TestFile("store_split_folder_old_srrsfv_windows", "store_split_folder.sfv"));

        Assert.All(sfv.Entries, entry => Assert.Equal(entry.CRC, entry.CRC.ToLower()));
    }

    #endregion

    #region ParseBytes

    [Fact]
    public void ParseBytes_ParsesNameCrcPairs()
    {
        byte[] data = Encoding.ASCII.GetBytes("; comment\r\naln-re4a.r00 88b361c9\r\naln-re4a.rar f1a3ec0d\r\n");
        SFVFile sfv = SFVFile.ParseBytes(data, tolerant: true);

        Assert.Equal(2, sfv.Entries.Count);
        Assert.Equal("aln-re4a.r00", sfv.Entries[0].FileName);
        Assert.Equal("88b361c9", sfv.Entries[0].CRC);
    }

    #endregion

    #region ParseLines

    [Fact]
    public void ParseLines_Tolerant_SkipsMalformedInsteadOfThrowing()
    {
        string[] lines = ["good.r00 deadbeef", "this line is broken", "good.r01 cafebabe"];
        SFVFile sfv = SFVFile.ParseLines(lines, tolerant: true);

        Assert.Equal(2, sfv.Entries.Count);
        Assert.Equal("good.r01", sfv.Entries[1].FileName);
    }

    [Fact]
    public void ParseLines_Strict_ThrowsOnMalformed()
    {
        string[] lines = ["good.r00 deadbeef", "broken"];
        Assert.Throws<InvalidDataException>(() => SFVFile.ParseLines(lines, tolerant: false));
    }

    #endregion
}
