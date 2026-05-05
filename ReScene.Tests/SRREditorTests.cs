using ReScene.SRR;

namespace ReScene.Tests;

public class SRREditorTests : IDisposable
{
    private readonly string _testDir;

    public SRREditorTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"srreditor_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_testDir, true);
        }
        catch
        {
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void RenameStoredFile_RewritesOnlyTargetBlock()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddStoredFile("old.nfo", [1, 2, 3])
            .AddStoredFile("keep.nfo", [4, 5])
            .BuildToFile(_testDir, "rename.srr");

        SRREditor.RenameStoredFile(path, "old.nfo", "new.nfo");

        var srr = SRRFile.Load(path);
        Assert.Contains(srr.StoredFiles, b => b.FileName == "new.nfo");
        Assert.Contains(srr.StoredFiles, b => b.FileName == "keep.nfo");
        Assert.DoesNotContain(srr.StoredFiles, b => b.FileName == "old.nfo");
    }

    [Fact]
    public void RenameStoredFile_PreservesPayload()
    {
        byte[] payload = [10, 20, 30, 40, 50];
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddStoredFile("a.bin", payload)
            .BuildToFile(_testDir, "rename2.srr");

        SRREditor.RenameStoredFile(path, "a.bin", "b.bin");

        var srr = SRRFile.Load(path);
        SrrStoredFileBlock renamed = srr.StoredFiles.Single(b => b.FileName == "b.bin");
        Assert.Equal(payload.Length, (int)renamed.FileLength);

        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(renamed.DataOffset, SeekOrigin.Begin);
        byte[] actual = new byte[renamed.FileLength];
        int read = fs.Read(actual, 0, actual.Length);
        Assert.Equal(actual.Length, read);
        Assert.Equal(payload, actual);
    }

    [Fact]
    public void RenameStoredFile_MissingName_Throws()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddStoredFile("a.nfo", [0])
            .BuildToFile(_testDir, "rename3.srr");

        Assert.Throws<InvalidOperationException>(
            () => SRREditor.RenameStoredFile(path, "missing.nfo", "x.nfo"));
    }

    [Fact]
    public void RenameStoredFile_NonexistentFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(
            () => SRREditor.RenameStoredFile(
                Path.Combine(_testDir, "missing.srr"), "a.nfo", "b.nfo"));
    }

    [Fact]
    public void MoveStoredFile_MovesUpByOne()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddStoredFile("a.nfo", [1])
            .AddStoredFile("b.nfo", [2])
            .AddStoredFile("c.nfo", [3])
            .BuildToFile(_testDir, "reorder.srr");

        SRREditor.MoveStoredFile(path, "b.nfo", offset: -1);

        var names = SRRFile.Load(path).StoredFiles.Select(b => b.FileName).ToList();
        Assert.Equal(new[] { "b.nfo", "a.nfo", "c.nfo" }, names);
    }

    [Fact]
    public void MoveStoredFile_MovesDownByOne()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddStoredFile("a.nfo", [1])
            .AddStoredFile("b.nfo", [2])
            .AddStoredFile("c.nfo", [3])
            .BuildToFile(_testDir, "reorder2.srr");

        SRREditor.MoveStoredFile(path, "b.nfo", offset: 1);

        var names = SRRFile.Load(path).StoredFiles.Select(b => b.FileName).ToList();
        Assert.Equal(new[] { "a.nfo", "c.nfo", "b.nfo" }, names);
    }

    [Fact]
    public void MoveStoredFile_AtEdge_NoOp()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddStoredFile("a.nfo", [1])
            .AddStoredFile("b.nfo", [2])
            .BuildToFile(_testDir, "reorder3.srr");

        SRREditor.MoveStoredFile(path, "a.nfo", offset: -1);

        var names = SRRFile.Load(path).StoredFiles.Select(b => b.FileName).ToList();
        Assert.Equal(new[] { "a.nfo", "b.nfo" }, names);
    }

    [Fact]
    public void MoveStoredFile_MissingName_Throws()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddStoredFile("a.nfo", [1])
            .BuildToFile(_testDir, "reorder4.srr");

        Assert.Throws<InvalidOperationException>(
            () => SRREditor.MoveStoredFile(path, "missing.nfo", offset: 1));
    }
}
