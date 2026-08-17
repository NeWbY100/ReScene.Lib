using ReScene.SRR;

namespace ReScene.Tests;

/// <summary>
/// Tests for <see cref="SRRFile.ExtractStoredFiles"/> — the bulk, path-preserving extraction
/// API. Unlike <see cref="SRRFile.ExtractStoredFile"/> (single file, flattened to its base
/// name), the bulk API recreates each stored file's relative directory structure, and it
/// validates EVERY stored name and data range before writing anything: a hostile name
/// (rooted, or containing "." / ".." segments) fails the whole call via
/// <see cref="SrrNameException"/> with the output directory left untouched, instead of
/// silently sanitizing the name the way the old CLI extraction did.
/// </summary>
public class SRRFileBulkExtractTests : TempDirTestBase
{
    private readonly string _outDir;

    /// <summary>
    /// <see cref="_outDir"/> as the extractor itself reports it.
    /// </summary>
    /// <remarks>
    /// <see cref="SRRFile.ExtractStoredFiles"/> resolves its output root through the filesystem
    /// once and builds every returned path from THAT — deliberately, so a link inside the output
    /// directory cannot redirect a lexically-clean name. On macOS the temp directory sits behind
    /// exactly such a link: <c>Path.GetTempPath()</c> hands back <c>/var/folders/…</c> while
    /// <c>/var</c> is a symlink to <c>/private/var</c>, so the returned paths are rooted at
    /// <c>/private/var/folders/…</c>. Comparing raw <see cref="Path.Combine(string, string)"/>
    /// results against them therefore failed on macOS only, while passing on Windows and Linux
    /// where no such link exists. Expectations are built from the resolved root for that reason.
    /// The unresolved <see cref="_outDir"/> is still what gets PASSED IN, which is what a real
    /// caller does.
    /// </remarks>
    private readonly string _resolvedOutDir;

    public SRRFileBulkExtractTests()
    {
        _outDir = Path.Combine(TempDir, "out");
        Directory.CreateDirectory(_outDir);
        _resolvedOutDir = SrrNameCanonicalizer.GetFinalPath(_outDir);
    }

    private static readonly byte[] NfoBytes = [0x4E, 0x46, 0x4F, 0x21];
    private static readonly byte[] SrsBytes = [0x53, 0x52, 0x53, 0x21, 0x00, 0x01];

    private string BuildSrr(Action<SRRTestDataBuilder> populate)
    {
        SRRTestDataBuilder builder = new SRRTestDataBuilder().AddSRRHeader("ReScene.Tests");
        populate(builder);
        return builder.BuildToFile(TempDir, "bulk.srr");
    }

    [Fact]
    public void ExtractStoredFiles_PreservesRelativeDirectoryStructure()
    {
        string srrPath = BuildSrr(b => b
            .AddStoredFile("release.nfo", NfoBytes)
            .AddStoredFile("Sample/clip.srs", SrsBytes));

        IReadOnlyList<string> written = SRRFile.Load(srrPath).ExtractStoredFiles(srrPath, _outDir);

        string expectedNfo = Path.Combine(_resolvedOutDir, "release.nfo");
        string expectedSrs = Path.Combine(_resolvedOutDir, "Sample", "clip.srs");
        Assert.Equal([expectedNfo, expectedSrs], written);
        Assert.Equal(NfoBytes, File.ReadAllBytes(expectedNfo));
        Assert.Equal(SrsBytes, File.ReadAllBytes(expectedSrs));
    }

    [Fact]
    public void ExtractStoredFiles_BackslashSeparatedName_ExtractsUnderSameStructure()
    {
        string srrPath = BuildSrr(b => b.AddStoredFile(@"Subs\idx.nfo", NfoBytes));

        IReadOnlyList<string> written = SRRFile.Load(srrPath).ExtractStoredFiles(srrPath, _outDir);

        Assert.Equal([Path.Combine(_resolvedOutDir, "Subs", "idx.nfo")], written);
    }

    [Fact]
    public void ExtractStoredFiles_OutputDirectoryReachedThroughALink_ReturnsPathsRootedAtTheResolvedDirectory()
    {
        // Reproduces on EVERY platform what previously only macOS hit: the output directory handed
        // in is reached through a link, so the paths returned are rooted at the link's TARGET
        // rather than at the spelling the caller passed. On macOS that arises with nobody arranging
        // it — Path.GetTempPath() returns /var/folders/…, and /var is a symlink to /private/var —
        // which is why two assertions built with a plain Path.Combine of the passed-in directory
        // passed on Windows and Linux and failed on macOS alone, red-lighting CI.
        string real = Path.Combine(TempDir, "real-out");
        Directory.CreateDirectory(real);

        string linked = Path.Combine(TempDir, "linked-out");
        CreateLink(linked, real);

        string srrPath = BuildSrr(b => b.AddStoredFile("Sample/clip.srs", SrsBytes));

        IReadOnlyList<string> written = SRRFile.Load(srrPath).ExtractStoredFiles(srrPath, linked);

        string expected = Path.Combine(SrrNameCanonicalizer.GetFinalPath(real), "Sample", "clip.srs");
        Assert.Equal([expected], written);
        Assert.True(File.Exists(expected), "the extracted file should exist at the resolved path.");
    }

    [Fact]
    public void ExtractStoredFiles_HostileName_ThrowsAndWritesNothing()
    {
        // The benign entry comes FIRST in block order, so writing anything before the hostile
        // name is discovered would leave release.nfo behind — the validate-all-first contract
        // is what keeps the output directory untouched.
        string srrPath = BuildSrr(b => b
            .AddStoredFile("release.nfo", NfoBytes)
            .AddStoredFile("../evil.txt", SrsBytes));

        SRRFile srr = SRRFile.Load(srrPath);

        Assert.Throws<SrrNameException>(() => srr.ExtractStoredFiles(srrPath, _outDir));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_outDir));
    }

    [Fact]
    public void ExtractStoredFiles_RootedName_ThrowsAndWritesNothing()
    {
        string srrPath = BuildSrr(b => b
            .AddStoredFile("release.nfo", NfoBytes)
            .AddStoredFile("C:/evil.txt", SrsBytes));

        SRRFile srr = SRRFile.Load(srrPath);

        Assert.Throws<SrrNameException>(() => srr.ExtractStoredFiles(srrPath, _outDir));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_outDir));
    }

    [Fact]
    public void ExtractStoredFiles_TruncatedStoredData_ThrowsAndWritesNothing()
    {
        string srrPath = BuildSrr(b => b.AddStoredFile("release.nfo", NfoBytes));

        // Load first, then truncate the tail so the stored block's declared range now runs past
        // the physical end — the pre-write bounds validation must refuse the whole call.
        SRRFile srr = SRRFile.Load(srrPath);
        using (FileStream fs = new(srrPath, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.SetLength(fs.Length - 2);
        }

        Assert.Throws<InvalidDataException>(() => srr.ExtractStoredFiles(srrPath, _outDir));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_outDir));
    }

    [Fact]
    public void ExtractStoredFiles_NoStoredFiles_ReturnsEmpty()
    {
        string srrPath = BuildSrr(_ => { });

        Assert.Empty(SRRFile.Load(srrPath).ExtractStoredFiles(srrPath, _outDir));
    }

    [Fact]
    public void ExtractStoredFiles_DuplicateNamesAfterNormalization_ThrowsAndWritesNothing()
    {
        // "Subs\a.nfo" and "SUBS/A.NFO" are one path after separator normalization on a
        // case-insensitive filesystem — extracting both would silently overwrite the first.
        // The contract refuses the collision uniformly on every host (not just where the
        // filesystem happens to collide), like the rest of the portable name grammar.
        string srrPath = BuildSrr(b => b
            .AddStoredFile(@"Subs\a.nfo", NfoBytes)
            .AddStoredFile("SUBS/A.NFO", SrsBytes));

        SRRFile srr = SRRFile.Load(srrPath);

        Assert.Throws<SrrNameException>(() => srr.ExtractStoredFiles(srrPath, _outDir));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_outDir));
    }

    [Fact]
    public void ExtractStoredFiles_FileAndDirectoryPrefixConflict_ThrowsAndWritesNothing()
    {
        // "a" as a file and "a/b.nfo" needing "a" as a directory cannot both materialize;
        // without the preflight this would fail midway with a partial extraction behind it.
        string srrPath = BuildSrr(b => b
            .AddStoredFile("a", NfoBytes)
            .AddStoredFile("a/b.nfo", SrsBytes));

        SRRFile srr = SRRFile.Load(srrPath);

        Assert.Throws<SrrNameException>(() => srr.ExtractStoredFiles(srrPath, _outDir));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_outDir));
    }

    [Fact]
    public void ExtractStoredFiles_LinkedSubdirectoryEscapingOutput_ThrowsAndWritesNothing()
    {
        // Lexically "Sample/clip.srs" is contained, but a pre-existing "Sample" link inside
        // the output directory redirects it elsewhere — the OS-final-path containment check
        // (the same machinery ResolveSfvEntry uses) must catch what lexical validation cannot.
        string srrPath = BuildSrr(b => b.AddStoredFile("Sample/clip.srs", SrsBytes));
        string outside = Path.Combine(TempDir, "outside");
        Directory.CreateDirectory(outside);
        CreateLink(Path.Combine(_outDir, "Sample"), outside);

        SRRFile srr = SRRFile.Load(srrPath);

        Assert.Throws<SrrNameException>(() => srr.ExtractStoredFiles(srrPath, _outDir));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public void ExtractStoredFiles_AncestorConflictNotAdjacentInSortOrder_ThrowsAndWritesNothing()
    {
        // '-' (0x2D) sorts before '/' (0x2F), so ordinal sorting puts "a-b" BETWEEN "a" and
        // "a/b.nfo" — an adjacent-pair check never compares the conflicting pair. The ancestor
        // check must catch it anyway, before anything is written.
        string srrPath = BuildSrr(b => b
            .AddStoredFile("a", NfoBytes)
            .AddStoredFile("a-b", NfoBytes)
            .AddStoredFile("a/b.nfo", SrsBytes));

        SRRFile srr = SRRFile.Load(srrPath);

        Assert.Throws<SrrNameException>(() => srr.ExtractStoredFiles(srrPath, _outDir));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_outDir));
    }

    [Fact]
    public void ExtractStoredFiles_DistinctNamesResolvingToSamePath_ThrowsAndWritesNothing()
    {
        // "Alias" links to "Real" INSIDE the output directory, so both names pass containment —
        // but "Alias/a.nfo" and "Real/a.nfo" are one physical file. Logical-name deduplication
        // cannot see that; the resolved-path deduplication must.
        string real = Path.Combine(_outDir, "Real");
        Directory.CreateDirectory(real);
        CreateLink(Path.Combine(_outDir, "Alias"), real);
        string srrPath = BuildSrr(b => b
            .AddStoredFile("Real/a.nfo", NfoBytes)
            .AddStoredFile("Alias/a.nfo", SrsBytes));

        SRRFile srr = SRRFile.Load(srrPath);

        Assert.Throws<SrrNameException>(() => srr.ExtractStoredFiles(srrPath, _outDir));
        Assert.Empty(Directory.EnumerateFileSystemEntries(real));
    }

    [Fact]
    public void ExtractStoredFiles_DanglingDirectoryLinkEscaping_ThrowsAndWritesNothing()
    {
        // The link's TARGET does not exist, so Directory.Exists/File.Exists (which follow the
        // link) both say "nothing there" — but the link ENTRY exists, and creating
        // "Sample/clip.srs" through it would materialize the target directory outside the
        // output root. The walk must resolve the dangling link instead of literal-appending it.
        string outsideTarget = Path.Combine(TempDir, "outside-not-yet");
        CreateLink(Path.Combine(_outDir, "Sample"), outsideTarget);
        string srrPath = BuildSrr(b => b.AddStoredFile("Sample/clip.srs", SrsBytes));

        SRRFile srr = SRRFile.Load(srrPath);

        Assert.Throws<SrrNameException>(() => srr.ExtractStoredFiles(srrPath, _outDir));
        Assert.False(Directory.Exists(outsideTarget));
    }

    [Fact]
    public void ExtractStoredFiles_DanglingFileSymlinkEscaping_ThrowsAndWritesNothing()
    {
        // POSIX-only: an unprivileged dangling FILE symlink ("escape.nfo" -> ../outside/new.nfo).
        // FileMode.Create would follow it and create the target file outside the output root.
        // (Windows file symlinks need privilege; the dangling-directory variant above covers the
        // same walk hole there via an unprivileged junction.)
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string outside = Path.Combine(TempDir, "outside");
        Directory.CreateDirectory(outside);
        string escapeTarget = Path.Combine(outside, "new.nfo");
        File.CreateSymbolicLink(Path.Combine(_outDir, "escape.nfo"), escapeTarget);
        string srrPath = BuildSrr(b => b.AddStoredFile("escape.nfo", NfoBytes));

        SRRFile srr = SRRFile.Load(srrPath);

        Assert.Throws<SrrNameException>(() => srr.ExtractStoredFiles(srrPath, _outDir));
        Assert.False(File.Exists(escapeTarget));
    }

    [Fact]
    public void ExtractStoredFiles_PreCancelledToken_ThrowsBeforeWritingAnything()
    {
        string srrPath = BuildSrr(b => b.AddStoredFile("release.nfo", NfoBytes));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        SRRFile srr = SRRFile.Load(srrPath);

        Assert.Throws<OperationCanceledException>(() => srr.ExtractStoredFiles(srrPath, _outDir, cts.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_outDir));
    }

    // Windows: NTFS junctions need no privilege (unlike symlinks). POSIX: symlink creation
    // also needs no privilege. Same pattern as SrrNameCanonicalizerTests.CreateLink.
    private static void CreateLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
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
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }
    }
}
