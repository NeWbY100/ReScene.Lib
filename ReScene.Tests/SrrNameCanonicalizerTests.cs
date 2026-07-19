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
        CreateLink(link, target);
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

    [Fact]
    public void CanonicalizeRelative_RepeatedTrailingSeparators_StillContained()
    {
        // codex Important #3: a root string with extra trailing separators must not break
        // containment for otherwise-valid children.
        string rootFinal = SrrNameCanonicalizer.GetFinalPath(_root);
        string rootWithExtraSeparators = rootFinal + new string(Path.DirectorySeparatorChar, 3);
        string name = SrrNameCanonicalizer.CanonicalizeRelative(
            rootWithExtraSeparators, Path.Combine(_root, "CD1", "a.sfv"));
        Assert.Equal("CD1/a.sfv", name);
    }

    [Fact]
    public void CanonicalizeRelative_FilesystemRoot_ProducesRelativeName()
    {
        // codex Important #3: the filesystem root ("C:\" / "/") must not become a doubled
        // separator ("C:\\" / "//") that rejects every real child.
        string driveRoot = SrrNameCanonicalizer.GetFinalPath(Path.GetPathRoot(_root)!);
        string name = SrrNameCanonicalizer.CanonicalizeRelative(
            driveRoot, Path.Combine(_root, "CD1", "a.sfv"));
        Assert.EndsWith("CD1/a.sfv", name, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", name, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalizeRelative_CaseDistinctSiblings_OnlyExactCaseContained()
    {
        // codex Important #3: Windows filesystems are case-insensitive by design
        // (OrdinalIgnoreCase is correct there); only case-sensitive POSIX filesystems can
        // distinguish "Root" from "root", so this assertion only applies off Windows.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string upper = Path.Combine(Path.GetTempPath(), "Canon-Case-" + Guid.NewGuid().ToString("N"));
        string lower = upper.ToLowerInvariant();
        Directory.CreateDirectory(upper);
        Directory.CreateDirectory(lower);
        try
        {
            File.WriteAllText(Path.Combine(upper, "x.bin"), "x");
            string rootFinal = SrrNameCanonicalizer.GetFinalPath(lower);
            Assert.Throws<SrrNameException>(() =>
                SrrNameCanonicalizer.CanonicalizeRelative(rootFinal, Path.Combine(upper, "x.bin")));
        }
        finally
        {
            Directory.Delete(upper, recursive: true);
            Directory.Delete(lower, recursive: true);
        }
    }

    [Fact]
    public void GetFinalPath_LongPath_Succeeds()
    {
        // codex Important #4: a valid result longer than the original fixed 1024-char buffer
        // must not be rejected.
        string deep = _root;
        while (deep.Length < 1100)
        {
            deep = Path.Combine(deep, "seg1234567890");
        }

        Directory.CreateDirectory(deep);
        string finalPath = SrrNameCanonicalizer.GetFinalPath(deep);
        Assert.True(finalPath.Length > 1024);
    }

    [Fact]
    public void ToExtendedLengthPath_ResolvesLinkBeforeParentSegment()
    {
        // Regression test for the long-path-open fallback's path-construction helper (closes the
        // residual flagged after the codex containment fix, review round 2): proves it resolves
        // a link's target BEFORE applying a following ".." rather than lexically canceling
        // "link" and ".." the way Path.GetFullPath would — the exact codex Critical #1 pattern,
        // now also closed here. This is a helper-level unit test on ToExtendedLengthPath rather
        // than an end-to-end >MAX_PATH fixture, because whether a >MAX_PATH path actually forces
        // the CreateFileW fallback branch (rather than succeeding directly) varies by the host's
        // Windows long-path policy; testing the fallback's path construction directly is
        // deterministic regardless of that policy.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string outside = Path.Combine(Path.GetTempPath(), "canon-ext-outside-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(outside, "dir");
        Directory.CreateDirectory(target);
        string link = Path.Combine(_root, "J");
        CreateLink(link, target);
        try
        {
            string constructed = SrrNameCanonicalizer.ToExtendedLengthPath(Path.Combine(link, "..", "secret.bin"));
            string expectedTarget = Path.Combine(SrrNameCanonicalizer.GetFinalPath(outside), "secret.bin");

            Assert.Equal(@"\\?\" + expectedTarget, constructed);
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void GetFinalPath_NonExistentPath_ThrowsWithErrorCode()
    {
        // codex Important #4: the captured Win32 error must surface in the exception message.
        // This is specific to the Windows CreateFileW branch; the POSIX fallback has different,
        // pre-existing missing-path semantics that are out of scope here.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string missing = Path.Combine(_root, "does-not-exist-" + Guid.NewGuid().ToString("N"));
        SrrNameException ex = Assert.Throws<SrrNameException>(() => SrrNameCanonicalizer.GetFinalPath(missing));
        Assert.Contains("error", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("..\\evil.rar")]
    [InlineData("C:\\abs\\evil.rar")]
    [InlineData("sub/../../evil.rar")]
    [InlineData("/abs/evil.rar")]
    [InlineData("C:relative.rar")]
    [InlineData("\\\\server\\share\\evil.rar")]
    [InlineData("\\\\?\\C:\\evil.rar")]
    public void ResolveSfvEntry_EscapingEntry_Throws(string entry)
    {
        Assert.Throws<SrrNameException>(() =>
            SrrNameCanonicalizer.ResolveSfvEntry(Path.Combine(_root, "CD1"), entry));
    }

    [Fact]
    public void ResolveSfvEntry_ThroughLink_Throws()
    {
        // codex Critical #2: an SFV entry that traverses a link inside the SFV directory
        // pointing outside it must be rejected via final-path containment, not just lexically.
        string target = Path.Combine(Path.GetTempPath(), "canon-sfv-tgt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "x.bin"), "x");
        string link = Path.Combine(_root, "CD1", "J");
        CreateLink(link, target);
        try
        {
            Assert.Throws<SrrNameException>(() =>
                SrrNameCanonicalizer.ResolveSfvEntry(Path.Combine(_root, "CD1"), "J/x.bin"));
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(target, recursive: true);
        }
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
    [InlineData("/abs/x.nfo")]
    [InlineData("C:relative.nfo")]
    [InlineData("\\\\server\\share\\x.nfo")]
    [InlineData("\\\\?\\C:\\x.nfo")]
    public void CanonicalizeLogicalName_Degenerate_Throws(string bad) =>
        Assert.Throws<SrrNameException>(() => SrrNameCanonicalizer.CanonicalizeLogicalName(bad));

    // Windows: NTFS junctions need no privilege (unlike symlinks). POSIX: symlink creation also
    // needs no privilege. Runs unconditionally on both hosts — no skip path exists (codex r2b
    // f1 / r4 f1 / r7 f7; xUnit 2.9.3 has no Assert.Skip, none needed).
    private static void CreateLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            CreateJunction(link, target);
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }
    }

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
