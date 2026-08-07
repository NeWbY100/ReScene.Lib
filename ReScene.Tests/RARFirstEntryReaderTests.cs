using ReScene.RAR;

namespace ReScene.Tests;

public class RARFirstEntryReaderTests
{
    private static string CreateTestDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RARFirstEntryReaderTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void TryGetFirstFileName_RAR4Volume_ReturnsFirstFileName()
    {
        string dir = CreateTestDir();
        try
        {
            string path = Path.Combine(dir, "known.rar");
            RarFixtures.WriteStoreModeRarVolume(path, "known.txt", 16);

            string? name = RARFirstEntryReader.TryGetFirstFileName(path);

            Assert.Equal("known.txt", name);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void TryGetFirstFileName_RAR4Volume_NameWithForwardSlash_NormalizesToBackslash()
    {
        string dir = CreateTestDir();
        try
        {
            string path = Path.Combine(dir, "known.rar");
            RarFixtures.WriteStoreModeRarVolume(path, "sub/known.txt", 16);

            string? name = RARFirstEntryReader.TryGetFirstFileName(path);

            Assert.Equal(@"sub\known.txt", name);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void TryGetFirstFileName_RAR5Volume_ReturnsFirstFileName()
    {
        // Real fixture (also used by RARStreamTests/RARArchiveOpenTests): a single RAR5 volume
        // whose only entry is "testfile.txt".
        string path = TestData.Path("test_rar5_m3.rar");
        if (!File.Exists(path))
        {
            Assert.Fail($"Test file not found: {path}");
        }

        string? name = RARFirstEntryReader.TryGetFirstFileName(path);

        Assert.Equal("testfile.txt", name);
    }

    [Fact]
    public void TryGetFirstFileName_NonRARBytes_ReturnsNull()
    {
        string dir = CreateTestDir();
        try
        {
            string path = Path.Combine(dir, "notarar.bin");
            byte[] bytes = new byte[40];
            Array.Fill(bytes, (byte)0xFF);
            File.WriteAllBytes(path, bytes);

            Assert.Null(RARFirstEntryReader.TryGetFirstFileName(path));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void TryGetFirstFileName_MissingPath_ReturnsNull()
    {
        string path = Path.Combine(Path.GetTempPath(), "RARFirstEntryReaderTest_missing_" + Guid.NewGuid().ToString("N") + ".rar");

        Assert.Null(RARFirstEntryReader.TryGetFirstFileName(path));
    }

    [Fact]
    public void TryGetFirstFileName_EmptyFile_ReturnsNull()
    {
        string dir = CreateTestDir();
        try
        {
            string path = Path.Combine(dir, "empty.rar");
            File.WriteAllBytes(path, []);

            Assert.Null(RARFirstEntryReader.TryGetFirstFileName(path));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
