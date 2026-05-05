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

        SRRFile srr = SRRFile.Load(path);
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

        SRRFile srr = SRRFile.Load(path);
        var renamed = srr.StoredFiles.Single(b => b.FileName == "b.bin");
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
}
