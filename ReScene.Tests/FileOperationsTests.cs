using ReScene.Core;

namespace ReScene.Tests;

/// <summary>
/// Unit tests for <see cref="FileOperations"/> — the static filesystem helpers used by the
/// reconstruction pipeline. Focus is on the path-traversal security guard
/// (<see cref="FileOperations.TryResolveRelativePath"/>), the byte-accurate copy primitives with
/// their progress counters, and the SRR-entry copy/skip/missing behaviour of
/// <see cref="FileOperations.CopySelectedEntries"/>.
/// </summary>
public class FileOperationsTests : TempDirTestBase
{
    private static readonly char Sep = Path.DirectorySeparatorChar;

    #region TryResolveRelativePath

    [Fact]
    public void TryResolveRelativePath_EmptyString_ReturnsFalseWithEmptyOut()
    {
        // Empty entries are rejected outright; out param must be cleared.
        bool ok = FileOperations.TryResolveRelativePath(TempDir, "", out string rel);
        Assert.False(ok);
        Assert.Equal(string.Empty, rel);
    }

    [Fact]
    public void TryResolveRelativePath_WhitespaceOnly_ReturnsFalseWithEmptyOut()
    {
        // Whitespace-only is treated the same as empty by IsNullOrWhiteSpace.
        bool ok = FileOperations.TryResolveRelativePath(TempDir, "   ", out string rel);
        Assert.False(ok);
        Assert.Equal(string.Empty, rel);
    }

    [Fact]
    public void TryResolveRelativePath_DotEntry_ReturnsFalse()
    {
        // A bare "." resolves to the base directory itself, which is not a valid file entry.
        bool ok = FileOperations.TryResolveRelativePath(TempDir, ".", out string rel);
        Assert.False(ok);
        Assert.Equal(string.Empty, rel);
    }

    [Fact]
    public void TryResolveRelativePath_TraversalEscapingBase_ReturnsFalseWithEmptyOut()
    {
        // The security guard: an entry whose normalized form climbs above the base directory
        // must be rejected so a malicious SRR cannot write outside the target tree.
        string entry = $"..{Sep}..{Sep}evil.exe";
        bool ok = FileOperations.TryResolveRelativePath(TempDir, entry, out string rel);
        Assert.False(ok);
        Assert.Equal(string.Empty, rel);
    }

    [Fact]
    public void TryResolveRelativePath_DotSlashPrefix_StripsAndReturnsNormalizedRelative()
    {
        // Leading "./" is stripped and the remaining path normalized to OS separators.
        bool ok = FileOperations.TryResolveRelativePath(TempDir, "./sub/file.txt", out string rel);
        Assert.True(ok);
        Assert.Equal($"sub{Sep}file.txt", rel);
    }

    [Fact]
    public void TryResolveRelativePath_LeadingSeparator_IsTrimmedAndResolvesUnderBase()
    {
        // A leading separator is trimmed (treated as base-relative), not as an absolute root,
        // so "/sub/file.txt" stays inside the base directory.
        bool ok = FileOperations.TryResolveRelativePath(TempDir, "/sub/file.txt", out string rel);
        Assert.True(ok);
        Assert.Equal($"sub{Sep}file.txt", rel);
    }

    #endregion

    #region CopyFileWithProgress

    [Fact]
    public void CopyFileWithProgress_CopiesBytesExactly_AndUpdatesCounters()
    {
        // Copy must reproduce the source bytes verbatim and advance the running
        // bytesCopied/filesCopied counters by the file length and one file.
        byte[] payload = new byte[5000];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        string source = Path.Combine(TempDir, "source.bin");
        string dest = Path.Combine(TempDir, "dest.bin");
        File.WriteAllBytes(source, payload);

        long bytesCopied = 0;
        int filesCopied = 0;
        FileCopyProgressEventArgs? last = null;

        FileOperations.CopyFileWithProgress(
            source, dest, "source.bin",
            ref bytesCopied, ref filesCopied, totalFiles: 1, totalBytes: payload.Length,
            sourceDir: TempDir, destDir: TempDir,
            ct: CancellationToken.None,
            onProgress: e => last = e);

        Assert.True(File.Exists(dest));
        Assert.Equal(payload, File.ReadAllBytes(dest));
        Assert.Equal(payload.Length, bytesCopied);
        Assert.Equal(1, filesCopied);

        // Final progress event reflects the completed counts.
        Assert.NotNull(last);
        Assert.Equal(1, last!.FilesCopied);
        Assert.Equal(payload.Length, last.BytesCopied);
        Assert.Equal("source.bin", last.FileName);
    }

    #endregion

    #region CopyDirectory

    [Fact]
    public void CopyDirectory_RecreatesTree_AndCopiesAllFiles()
    {
        // The whole source tree (nested subdirectory + files) must be mirrored at the
        // destination with identical content.
        string sourceDir = Path.Combine(TempDir, "src");
        string nested = Path.Combine(sourceDir, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(sourceDir, "top.txt"), "top-content");
        File.WriteAllText(Path.Combine(nested, "deep.txt"), "deep-content");

        string destDir = Path.Combine(TempDir, "dst");

        FileOperations.CopyDirectory(sourceDir, destDir, CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(destDir, "nested")));
        Assert.Equal("top-content", File.ReadAllText(Path.Combine(destDir, "top.txt")));
        Assert.Equal("deep-content", File.ReadAllText(Path.Combine(destDir, "nested", "deep.txt")));
    }

    #endregion

    #region CopySelectedEntries

    [Fact]
    public void CopySelectedEntries_MissingFile_ThrowsWithCountButStillCopiesExisting()
    {
        // One listed file exists, one is missing. The existing file must be copied before the
        // method throws, and the FileNotFoundException message must report the missing count.
        string sourceDir = Path.Combine(TempDir, "release");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "present.txt"), "here");

        string destDir = Path.Combine(TempDir, "input");

        HashSet<string> files = new(StringComparer.OrdinalIgnoreCase) { "present.txt", "absent.txt" };

        FileNotFoundException ex = Assert.Throws<FileNotFoundException>(() =>
            FileOperations.CopySelectedEntries(
                sourceDir, destDir, files, directoryPaths: new(), ct: CancellationToken.None));

        // Message reports the exact count; anchor at the start so a wrong-count regression
        // (e.g. "11 file(s)") cannot pass via substring overlap.
        Assert.StartsWith("1 file(s)", ex.Message, StringComparison.Ordinal);

        // The present file was still copied despite the later throw.
        Assert.Equal("here", File.ReadAllText(Path.Combine(destDir, "present.txt")));
        // The missing file produced nothing on disk.
        Assert.False(File.Exists(Path.Combine(destDir, "absent.txt")));
    }

    [Fact]
    public void CopySelectedEntries_TraversalEntry_IsSkippedSilently()
    {
        // A file entry that fails the path-traversal guard must be skipped without throwing
        // and without writing anything (no missing-file exception is raised for it).
        string sourceDir = Path.Combine(TempDir, "release");
        Directory.CreateDirectory(sourceDir);

        string destDir = Path.Combine(TempDir, "input");
        Directory.CreateDirectory(destDir);

        string evil = $"..{Sep}..{Sep}escape.txt";
        HashSet<string> files = new(StringComparer.OrdinalIgnoreCase) { evil };

        // No throw: the entry is skipped (not counted as a missing file).
        FileOperations.CopySelectedEntries(
            sourceDir, destDir, files, directoryPaths: new(), ct: CancellationToken.None);

        // Destination remains empty; nothing escaped the base.
        Assert.Empty(Directory.GetFiles(destDir, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void CopySelectedEntries_DirectoryPaths_CreateDirectories()
    {
        // Entries listed as directories must be materialized under the destination root.
        string sourceDir = Path.Combine(TempDir, "release");
        Directory.CreateDirectory(sourceDir);

        string destDir = Path.Combine(TempDir, "input");

        HashSet<string> dirs = new(StringComparer.OrdinalIgnoreCase) { "Subs", $"Subs{Sep}English" };

        FileOperations.CopySelectedEntries(
            sourceDir, destDir, filePaths: new(), directoryPaths: dirs, ct: CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(destDir, "Subs")));
        Assert.True(Directory.Exists(Path.Combine(destDir, "Subs", "English")));
    }

    #endregion

    #region GetAllVolumeFiles

    [Fact]
    public void GetAllVolumeFiles_NoVolumeSet_ReturnsSingleOriginal()
    {
        // A lone .rar with no continuation volumes resolves to a one-element list of itself.
        string only = Path.Combine(TempDir, "movie.rar");
        File.WriteAllText(only, "x");

        List<string> result = FileOperations.GetAllVolumeFiles(only);

        Assert.Single(result);
        Assert.Equal(only, result[0]);
    }

    #endregion
}
