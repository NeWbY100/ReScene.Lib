using ReScene.SRR;

namespace ReScene.Tests;

/// <summary>
/// Tests for <see cref="SRREditor.AddStoredFiles"/> and <see cref="SRREditor.RemoveStoredFiles"/>.
/// (Rename/Move are covered by <c>SRREditorTests</c>.)
/// </summary>
public class SRREditorAddRemoveTests : TempDirTestBase
{
    // Raw little-endian CRC sentinel bytes that begin each block type's base header.
    private static readonly byte[] RarFileSentinel = [0x71, 0x71];   // SRRBlockType.RARFile (0x7171)
    private static readonly byte[] OsoHashSentinel = [0x6B, 0x6B];   // SRRBlockType.OSOHash (0x6B6B)

    /// <summary>Writes a payload file under TempDir and returns its full path.</summary>
    private string WriteFile(string name, byte[] data)
    {
        string path = Path.Combine(TempDir, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    /// <summary>Locates the first byte offset of <paramref name="needle"/> in <paramref name="haystack"/>, or -1.</summary>
    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    // Adding a stored file to an SRR that already has a stored file plus a RAR block:
    // the existing stored file stays first, the new one is appended after it but
    // still ahead of the RARFile block (proves insertion position, not just presence).
    [Fact]
    public void AddStoredFiles_InsertsAfterExistingStoredFile_AndBeforeRarBlock()
    {
        string srrPath = new SRRTestDataBuilder()
            .AddSRRHeader()
            .AddStoredFile("existing.nfo", [1, 2, 3])
            .AddRarFileWithHeaders("rls.rar", rar => rar.AddArchiveHeader().AddFileHeader("movie.mkv"))
            .BuildToFile(TempDir, "add_before_rar.srr");

        string newFile = WriteFile("added.nfo", [9, 9, 9, 9]);

        SRREditor.AddStoredFiles(srrPath, [("added.nfo", newFile)]);

        SRRFile srr = SRRFile.Load(srrPath);

        // Order is [existing, new] in the parsed stored-file list.
        Assert.Equal(["existing.nfo", "added.nfo"], srr.StoredFiles.Select(b => b.FileName).ToArray());

        // The new stored block must physically precede the RARFile block in the byte stream.
        byte[] bytes = File.ReadAllBytes(srrPath);
        SRRStoredFileBlock added = srr.StoredFiles.Single(b => b.FileName == "added.nfo");
        int rarOffset = IndexOf(bytes, RarFileSentinel);
        Assert.True(rarOffset >= 0, "RARFile block sentinel not found");
        // The stored block's base header starts NameLength(2)+name(var)+AddSize(4)+base(7) before its data.
        long addedBlockStart = added.DataOffset - "added.nfo".Length - 2 - 4 - 7;
        Assert.True(addedBlockStart < rarOffset, "New stored block must come before the RARFile block");
    }

    // Adding to a header-only SRR: the new stored file lands immediately after the header
    // (there is no stored-file run to follow), and is the sole stored block on reload.
    [Fact]
    public void AddStoredFiles_HeaderOnly_AppendsAfterHeader()
    {
        string srrPath = new SRRTestDataBuilder()
            .AddSRRHeader()
            .BuildToFile(TempDir, "add_header_only.srr");

        long headerLen = new FileInfo(srrPath).Length; // header-only file length == header block size
        string newFile = WriteFile("first.nfo", [7, 7]);

        SRREditor.AddStoredFiles(srrPath, [("first.nfo", newFile)]);

        SRRFile srr = SRRFile.Load(srrPath);
        Assert.NotNull(srr.HeaderBlock);
        SRRStoredFileBlock only = Assert.Single(srr.StoredFiles);
        Assert.Equal("first.nfo", only.FileName);
        Assert.Equal(2u, only.FileLength);

        // The stored block must begin exactly at the end of the original header block — pin the
        // offset precisely so padding/duplication regressions can't pass a loose lower bound.
        long blockStart = only.DataOffset - "first.nfo".Length - 2 - 4 - 7;
        Assert.Equal(headerLen, blockStart);
    }

    // Two files added in a single call must preserve their argument order in the SRR.
    [Fact]
    public void AddStoredFiles_TwoFilesInOneCall_PreservesArgumentOrder()
    {
        string srrPath = new SRRTestDataBuilder()
            .AddSRRHeader()
            .AddStoredFile("base.nfo", [0])
            .BuildToFile(TempDir, "add_two.srr");

        string fileA = WriteFile("alpha.nfo", [10]);
        string fileB = WriteFile("beta.nfo", [20]);

        SRREditor.AddStoredFiles(srrPath, [("alpha.nfo", fileA), ("beta.nfo", fileB)]);

        SRRFile srr = SRRFile.Load(srrPath);
        Assert.Equal(["base.nfo", "alpha.nfo", "beta.nfo"], srr.StoredFiles.Select(b => b.FileName).ToArray());
    }

    // Removal is case-insensitive (OrdinalIgnoreCase): removing "B.NFO" deletes the
    // lowercase "b.nfo" entry and leaves the other two untouched.
    [Fact]
    public void RemoveStoredFiles_MatchesCaseInsensitively_RemovesOnlyTarget()
    {
        string srrPath = new SRRTestDataBuilder()
            .AddSRRHeader()
            .AddStoredFile("a.nfo", [1])
            .AddStoredFile("b.nfo", [2])
            .AddStoredFile("c.nfo", [3])
            .BuildToFile(TempDir, "remove_ci.srr");

        SRREditor.RemoveStoredFiles(srrPath, ["B.NFO"]);

        SRRFile srr = SRRFile.Load(srrPath);
        Assert.Equal(["a.nfo", "c.nfo"], srr.StoredFiles.Select(b => b.FileName).ToArray());
    }

    // Removing a name that is not present is a silent no-op: the file is left
    // byte-for-byte identical and no exception is thrown.
    [Fact]
    public void RemoveStoredFiles_NonexistentName_LeavesFileByteIdentical()
    {
        string srrPath = new SRRTestDataBuilder()
            .AddSRRHeader()
            .AddStoredFile("a.nfo", [1, 2])
            .AddStoredFile("b.nfo", [3, 4])
            .BuildToFile(TempDir, "remove_missing.srr");

        byte[] before = File.ReadAllBytes(srrPath);

        SRREditor.RemoveStoredFiles(srrPath, ["does-not-exist.nfo"]);

        byte[] after = File.ReadAllBytes(srrPath);
        Assert.Equal(before, after);
    }

    // Removing the only stored file empties StoredFiles while keeping the header block intact.
    [Fact]
    public void RemoveStoredFiles_RemovesOnlyStoredFile_HeaderRemains()
    {
        string srrPath = new SRRTestDataBuilder()
            .AddSRRHeader("ReScene .NET")
            .AddStoredFile("solo.nfo", [5, 6, 7])
            .BuildToFile(TempDir, "remove_solo.srr");

        SRREditor.RemoveStoredFiles(srrPath, ["solo.nfo"]);

        SRRFile srr = SRRFile.Load(srrPath);
        Assert.Empty(srr.StoredFiles);
        Assert.NotNull(srr.HeaderBlock);
        Assert.Equal("ReScene .NET", srr.HeaderBlock!.AppName);
    }

    // Byte preservation across an Add: a non-StoredFile block (OSO hash) must be
    // byte-identical before and after, even though a stored file was inserted ahead of it.
    [Fact]
    public void AddStoredFiles_PreservesNonStoredBlockBytes()
    {
        byte[] osoHash = [0xAA, 0xBB, 0xCC, 0xDD, 0x11, 0x22, 0x33, 0x44];
        string srrPath = new SRRTestDataBuilder()
            .AddSRRHeader()
            .AddStoredFile("keep.nfo", [1])
            .AddOSOHash("movie.avi", 123456UL, osoHash)
            .BuildToFile(TempDir, "preserve_add.srr");

        byte[] before = File.ReadAllBytes(srrPath);
        int osoStart = IndexOf(before, OsoHashSentinel);
        Assert.True(osoStart >= 0, "OSO hash block not found in original");
        byte[] osoBlockOriginal = before[osoStart..]; // OSO is the last block, so this captures it fully

        string newFile = WriteFile("inserted.nfo", [42]);
        SRREditor.AddStoredFiles(srrPath, [("inserted.nfo", newFile)]);

        byte[] after = File.ReadAllBytes(srrPath);
        int osoStartAfter = IndexOf(after, OsoHashSentinel);
        Assert.True(osoStartAfter >= 0, "OSO hash block not found after add");
        byte[] osoBlockAfter = after[osoStartAfter..];
        Assert.Equal(osoBlockOriginal, osoBlockAfter);
    }

    // Byte preservation across a Remove: removing one stored file must not alter the
    // bytes of an unrelated OSO hash block (the tail of the file is identical).
    [Fact]
    public void RemoveStoredFiles_PreservesNonStoredBlockBytes()
    {
        byte[] osoHash = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        string srrPath = new SRRTestDataBuilder()
            .AddSRRHeader()
            .AddStoredFile("drop.nfo", [9, 9, 9])
            .AddStoredFile("keep.nfo", [8])
            .AddOSOHash("clip.mp4", 7UL, osoHash)
            .BuildToFile(TempDir, "preserve_remove.srr");

        byte[] before = File.ReadAllBytes(srrPath);
        int osoStart = IndexOf(before, OsoHashSentinel);
        Assert.True(osoStart >= 0, "OSO hash block not found in original");
        byte[] osoBlockOriginal = before[osoStart..]; // OSO is the last block

        SRREditor.RemoveStoredFiles(srrPath, ["drop.nfo"]);

        byte[] after = File.ReadAllBytes(srrPath);
        int osoStartAfter = IndexOf(after, OsoHashSentinel);
        Assert.True(osoStartAfter >= 0, "OSO hash block not found after remove");
        byte[] osoBlockAfter = after[osoStartAfter..];
        Assert.Equal(osoBlockOriginal, osoBlockAfter);

        // Sanity: the removal actually happened.
        SRRFile srr = SRRFile.Load(srrPath);
        Assert.Equal(["keep.nfo"], srr.StoredFiles.Select(b => b.FileName).ToArray());
    }
}
