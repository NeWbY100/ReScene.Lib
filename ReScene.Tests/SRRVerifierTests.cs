using ReScene.SRR;

namespace ReScene.Tests;

public class SRRVerifierTests : IDisposable
{
    private readonly string _testDir;

    public SRRVerifierTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"srrverify_{Guid.NewGuid():N}");
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
    public void Verify_ValidSrr_ReturnsValid()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader(appName: "Test")
            .AddStoredFile("hello.txt", [1, 2, 3])
            .BuildToFile(_testDir, "ok.srr");

        SrrVerifyResult result = SRRVerifier.Verify(path);

        Assert.True(result.IsValid);
        Assert.DoesNotContain(result.Issues, i => i.Severity == SrrVerifyIssueSeverity.Error);
        Assert.True(result.BlocksScanned >= 2);
    }

    [Fact]
    public void Verify_TruncatedFile_ReportsError()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddStoredFile("hello.txt", [1, 2, 3])
            .BuildToFile(_testDir, "truncated.srr");

        long size = new FileInfo(path).Length;
        using (FileStream fs = new(path, FileMode.Open, FileAccess.Write))
        {
            fs.SetLength(size - 4);
        }

        SrrVerifyResult result = SRRVerifier.Verify(path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues,
            i => i.Severity == SrrVerifyIssueSeverity.Error
                 && (i.Message.Contains("Truncated", StringComparison.OrdinalIgnoreCase)
                     || i.Message.Contains("extends past end of file", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Verify_BadCrcSentinel_ReportsWarning()
    {
        string path = new SRRTestDataBuilder()
            .AddSrrHeader()
            .AddStoredFile("hello.txt", [1, 2, 3])
            .BuildToFile(_testDir, "badcrc.srr");

        byte[] bytes = File.ReadAllBytes(path);
        bytes[0] = 0xFF;
        bytes[1] = 0xFF;
        File.WriteAllBytes(path, bytes);

        SrrVerifyResult result = SRRVerifier.Verify(path);

        Assert.Contains(result.Issues,
            i => i.Severity == SrrVerifyIssueSeverity.Warning
                 && i.Message.Contains("CRC", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Verify_NonexistentFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(
            () => SRRVerifier.Verify(Path.Combine(_testDir, "missing.srr")));
    }

    [Fact]
    public void Verify_MissingHeader_ReportsError()
    {
        string path = new SRRTestDataBuilder()
            .AddStoredFile("hello.txt", [1, 2, 3])
            .BuildToFile(_testDir, "noheader.srr");

        SrrVerifyResult result = SRRVerifier.Verify(path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues,
            i => i.Severity == SrrVerifyIssueSeverity.Error
                 && i.Message.Contains("header", StringComparison.OrdinalIgnoreCase));
    }
}
