using ReScene.Core.IO;

namespace ReScene.Tests;

public class SHA1FileTests : TempDirTestBase
{
    private string WriteTemp(string name, string content)
    {
        string path = Path.Combine(TempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ReadFile_BlankLineBetweenEntries_IsSkipped()
    {
        // Regression: ReadFile had no blank-line skip (unlike SFVFile), so a valid
        // .sha1 with a blank separator line threw InvalidDataException. Real SHA-1
        // listing tools frequently emit blank separator lines.
        string a = new('a', 40);
        string b = new('b', 40);
        string content = $"{a} *first.rar\r\n\r\n{b} *second.rar\r\n";
        string path = WriteTemp("blank.sha1", content);

        SHA1File result = SHA1File.ReadFile(path);

        Assert.Equal(2, result.Entries.Count);
        Assert.Equal("first.rar", result.Entries[0].FileName);
        Assert.Equal("second.rar", result.Entries[1].FileName);
    }

    [Fact]
    public void ReadFile_WhitespaceOnlyLine_IsSkipped()
    {
        string a = new('a', 40);
        string content = $"{a} *only.rar\r\n   \r\n";
        string path = WriteTemp("ws.sha1", content);

        SHA1File result = SHA1File.ReadFile(path);

        Assert.Single(result.Entries);
        Assert.Equal("only.rar", result.Entries[0].FileName);
    }

    [Fact]
    public void ReadFile_CommentLines_AreSkipped()
    {
        string a = new('a', 40);
        string content = $"; comment\r\n# comment\r\n: comment\r\n{a} *file.rar\r\n";
        string path = WriteTemp("comments.sha1", content);

        SHA1File result = SHA1File.ReadFile(path);

        Assert.Single(result.Entries);
        Assert.Equal("file.rar", result.Entries[0].FileName);
    }

    [Fact]
    public void ReadFile_ShortHash_ThrowsInvalidData()
    {
        string content = $"{new string('a', 39)} *bad.rar\r\n";
        string path = WriteTemp("short.sha1", content);

        Assert.Throws<InvalidDataException>(() => SHA1File.ReadFile(path));
    }

    [Fact]
    public void WriteThenRead_RoundTripsEntriesSortedByFileName()
    {
        var file = new SHA1File();
        file.Entries.Add(new SHA1FileEntry(new string('c', 40), "zeta.rar"));
        file.Entries.Add(new SHA1FileEntry(new string('d', 40), "alpha.rar"));
        string path = Path.Combine(TempDir, "rt.sha1");

        file.WriteFile(path);
        SHA1File reloaded = SHA1File.ReadFile(path);

        Assert.Equal(2, reloaded.Entries.Count);
        // WriteFile orders entries by file name.
        Assert.Equal("alpha.rar", reloaded.Entries[0].FileName);
        Assert.Equal("zeta.rar", reloaded.Entries[1].FileName);
        Assert.Equal(new string('d', 40), reloaded.Entries[0].SHA1);
    }
}
