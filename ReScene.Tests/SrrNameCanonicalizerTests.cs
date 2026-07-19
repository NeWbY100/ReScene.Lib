using ReScene.SRR;

namespace ReScene.Tests;

public class SrrNameCanonicalizerTests : IDisposable
{
    private readonly string _root;

    public SrrNameCanonicalizerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "canon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "CD1"));
        File.WriteAllText(Path.Combine(_root, "CD1", "a.sfv"), "x");
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void CanonicalizeRelative_ProducesForwardSlashNames()
    {
        string rootFinal = SrrNameCanonicalizer.GetFinalPath(_root);
        string name = SrrNameCanonicalizer.CanonicalizeRelative(
            rootFinal, Path.Combine(_root, "CD1", "a.sfv"));
        Assert.Equal("CD1/a.sfv", name);
    }

    [Fact]
    public void CanonicalizeRelative_OutsideRoot_Throws()
    {
        string rootFinal = SrrNameCanonicalizer.GetFinalPath(Path.Combine(_root, "CD1"));
        string outside = Path.Combine(_root, "b.txt");
        File.WriteAllText(outside, "x");
        Assert.Throws<SrrNameException>(() =>
            SrrNameCanonicalizer.CanonicalizeRelative(rootFinal, outside));
    }

    [Fact]
    public void CanonicalizeRelative_AncestorLink_ResolvedBeforeContainment()
    {
        // spec §1a rev 4: a link INSIDE the root pointing OUTSIDE it is rejected even though
        // the lexical path looks inside — final paths on both sides.
        string target = Path.Combine(Path.GetTempPath(), "canon-tgt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "x.bin"), "x");
        string link = Path.Combine(_root, "J");
        // NTFS JUNCTIONS need no privilege (unlike symlinks) — runs unconditionally on
        // Windows (codex r2b f1 / r4 f1; xUnit 2.9.3 has no Assert.Skip, none needed).
        CreateJunction(link, target);
        try
        {
            string rootFinal = SrrNameCanonicalizer.GetFinalPath(_root);
            Assert.Throws<SrrNameException>(() =>
                SrrNameCanonicalizer.CanonicalizeRelative(rootFinal, Path.Combine(link, "x.bin")));
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(target, recursive: true);
        }
    }

    [Theory]
    [InlineData("..\\evil.rar")]
    [InlineData("C:\\abs\\evil.rar")]
    [InlineData("sub/../../evil.rar")]
    public void ResolveSfvEntry_EscapingEntry_Throws(string entry)
    {
        Assert.Throws<SrrNameException>(() =>
            SrrNameCanonicalizer.ResolveSfvEntry(Path.Combine(_root, "CD1"), entry));
    }

    [Theory]
    [InlineData("CD1\\a.sfv", "CD1/a.sfv")]
    public void CanonicalizeLogicalName_NormalizesBackslashes(string input, string expected) =>
        Assert.Equal(expected, SrrNameCanonicalizer.CanonicalizeLogicalName(input));

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/../b.nfo")]
    [InlineData("C:/abs/x.nfo")]
    [InlineData("a//b.nfo")]
    public void CanonicalizeLogicalName_Degenerate_Throws(string bad) =>
        Assert.Throws<SrrNameException>(() => SrrNameCanonicalizer.CanonicalizeLogicalName(bad));

    private static void CreateJunction(string link, string target)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        proc.WaitForExit();
        Assert.Equal(0, proc.ExitCode); // junction creation must succeed — never skipped
    }

    [Fact]
    public void ResolveSfvEntry_BothSeparatorKinds_ResolveIdentically()
    {
        string p1 = SrrNameCanonicalizer.ResolveSfvEntry(_root, "CD1\\a.sfv");
        string p2 = SrrNameCanonicalizer.ResolveSfvEntry(_root, "CD1/a.sfv");
        Assert.Equal(p1, p2);
        Assert.True(File.Exists(p1));
    }
}
