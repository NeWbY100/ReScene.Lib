using ReScene.SRR;

namespace ReScene.Tests;

public class SRRVerifierTests : TempDirTestBase
{

    [Fact]
    public void Verify_ValidSRR_ReturnsValid()
    {
        string path = new SRRTestDataBuilder()
            .AddSRRHeader(appName: "Test")
            .AddStoredFile("hello.txt", [1, 2, 3])
            .BuildToFile(TempDir, "ok.srr");

        SRRVerifyResult result = SRRVerifier.Verify(path);

        Assert.True(result.IsValid);
        Assert.DoesNotContain(result.Issues, i => i.Severity == SRRVerifyIssueSeverity.Error);
        Assert.True(result.BlocksScanned >= 2);
    }

    // A real SRR with genuine embedded RAR headers must verify as VALID. Previously the walker
    // applied SRR ADD_SIZE framing to the embedded RAR4 file header (LONG_BLOCK + phantom packed
    // size), so it always false-errored "extends past end of file" on every real archived SRR.
    [Fact]
    public void Verify_RealSRRWithEmbeddedRar_ReturnsValid()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "store_little", "store_little.srr");

        SRRVerifyResult result = SRRVerifier.Verify(path);

        Assert.True(result.IsValid);
        Assert.DoesNotContain(result.Issues, i => i.Severity == SRRVerifyIssueSeverity.Error);
    }

    [Fact]
    public void Verify_TruncatedFile_ReportsError()
    {
        string path = new SRRTestDataBuilder()
            .AddSRRHeader()
            .AddStoredFile("hello.txt", [1, 2, 3])
            .BuildToFile(TempDir, "truncated.srr");

        long size = new FileInfo(path).Length;
        using (FileStream fs = new(path, FileMode.Open, FileAccess.Write))
        {
            fs.SetLength(size - 4);
        }

        SRRVerifyResult result = SRRVerifier.Verify(path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues,
            i => i.Severity == SRRVerifyIssueSeverity.Error
                 && (i.Message.Contains("Truncated", StringComparison.OrdinalIgnoreCase)
                     || i.Message.Contains("extends past end of file", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Verify_BadCRCSentinel_ReportsWarning()
    {
        string path = new SRRTestDataBuilder()
            .AddSRRHeader()
            .AddStoredFile("hello.txt", [1, 2, 3])
            .BuildToFile(TempDir, "badcrc.srr");

        byte[] bytes = File.ReadAllBytes(path);
        bytes[0] = 0xFF;
        bytes[1] = 0xFF;
        File.WriteAllBytes(path, bytes);

        SRRVerifyResult result = SRRVerifier.Verify(path);

        Assert.Contains(result.Issues,
            i => i.Severity == SRRVerifyIssueSeverity.Warning
                 && i.Message.Contains("CRC", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Verify_NonexistentFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(
            () => SRRVerifier.Verify(Path.Combine(TempDir, "missing.srr")));
    }

    [Fact]
    public void Verify_MissingHeader_ReportsError()
    {
        string path = new SRRTestDataBuilder()
            .AddStoredFile("hello.txt", [1, 2, 3])
            .BuildToFile(TempDir, "noheader.srr");

        SRRVerifyResult result = SRRVerifier.Verify(path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues,
            i => i.Severity == SRRVerifyIssueSeverity.Error
                 && i.Message.Contains("header", StringComparison.OrdinalIgnoreCase));
    }
}
