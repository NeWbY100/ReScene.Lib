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
    public void TryGetFirstFileName_RAR4Volume_CorruptedMarker_ReturnsNull()
    {
        string dir = CreateTestDir();
        try
        {
            string path = Path.Combine(dir, "corrupted-marker.rar");
            RarFixtures.WriteStoreModeRarVolume(path, "known.txt", 16);

            // Corrupt only the marker's first byte -- the archive header, file header, payload,
            // and end block that follow are all untouched and individually well-formed (valid
            // CRCs). A walker that assumes "not RAR5 => RAR4" without checking the marker itself
            // would still parse this as a normal RAR4 volume and return "known.txt".
            byte[] bytes = File.ReadAllBytes(path);
            bytes[0] = 0x00;
            File.WriteAllBytes(path, bytes);

            Assert.Null(RARFirstEntryReader.TryGetFirstFileName(path));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void TryGetFirstFileName_RAR4Volume_InvalidArchiveHeaderCRC_ReturnsNull()
    {
        string dir = CreateTestDir();
        try
        {
            string path = Path.Combine(dir, "bad-crc.rar");
            RarFixtures.WriteStoreModeRarVolume(path, "known.txt", 16);

            // Flip the archive header's last byte -- pure zero-padding no field ever reads, so
            // this changes nothing about how the header parses, only whether it matches its own
            // stored CRC. Marker(7) + archive header CRC(2)/type(1)/flags(2)/headerSize(2) = 14,
            // and the header is 13 bytes total, so byte 19 is its final (padding) byte.
            byte[] bytes = File.ReadAllBytes(path);
            bytes[19] = 0xFF;
            File.WriteAllBytes(path, bytes);

            Assert.Null(RARFirstEntryReader.TryGetFirstFileName(path));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void TryGetFirstFileName_RAR4Volume_SkipsLeadingDirectoryEntry_ReturnsFirstFileName()
    {
        string dir = CreateTestDir();
        try
        {
            string path = Path.Combine(dir, "with-dir.rar");
            RarFixtures.WriteDirectoryThenFileRarVolume(path, "subdir", "known.txt", 16);

            string? name = RARFirstEntryReader.TryGetFirstFileName(path);

            Assert.Equal("known.txt", name);
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
