using System.Text;
using ReScene.SRR;

namespace ReScene.Tests;

/// <summary>
/// Non-circular oracle for <see cref="SRRWriter.CreateFromInputsAsync"/>: asserts our output is
/// byte-identical to an SRR built by the LOCAL pyrescene checkout over the same synthetic release
/// tree (app-name field normalized — pyrescene's header records "pyReScene Auto &lt;version&gt;",
/// ours defaults to "ReScene.Lib"). See
/// <c>TestData/multiset/README.md</c> for the pinned pyrescene commit and generation commands.
/// This harness originally found a real divergence (RarFile block flags — see
/// <see cref="TwoDiscTree_MatchesPyresceneGoldenBytes"/> and the README's "Fixed divergence"
/// section); <see cref="SRRWriter.WriteRARFileBlock"/> now writes
/// <see cref="SRRBlockFlags.RecoveryBlocksRemoved"/> for pyReScene parity and both golden tests
/// pass. Design: spec §6 (docs/superpowers/specs/2026-07-18-multiset-srr-creation-design.md).
/// </summary>
public class GoldenFixtureTests
{
    private static string Data(string rel) =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "multiset", rel);

    // Independent minimal splitter (spec §6): only the header block's app-name is rewritten;
    // every other byte passes through untouched. Layout: [ushort sentinel][byte type]
    // [ushort flags][ushort headerSize] then, when flags bit0 set, [ushort len][name bytes].
    //
    // This is the trust anchor for every golden byte comparison in this file (and Task 9's), so
    // it validates the input is a well-formed header block and THROWS on any mismatch rather than
    // silently rewriting it — an inconsistent headerSize/nameLen (or a wrong sentinel/type) would
    // otherwise be masked identically on both sides of the comparison, hiding a real writer bug.
    internal static byte[] NormalizeAppName(byte[] srr)
    {
        const string replacement = "NORMALIZED";

        if (srr.Length < 7)
        {
            throw new InvalidOperationException(
                $"Header block is only {srr.Length} bytes — too short for the 7-byte base header.");
        }

        ushort sentinel = BitConverter.ToUInt16(srr, 0);
        if (sentinel != 0x6969)
        {
            throw new InvalidOperationException(
                $"Unexpected header sentinel 0x{sentinel:X4} (expected 0x6969).");
        }

        if (srr[2] != 0x69)
        {
            throw new InvalidOperationException(
                $"Unexpected header block type 0x{srr[2]:X2} (expected 0x69).");
        }

        ushort flags = BitConverter.ToUInt16(srr, 3);
        if ((flags & 0x1) == 0)
        {
            return srr;
        }

        ushort headerSize = BitConverter.ToUInt16(srr, 5);
        if (srr.Length < 9)
        {
            throw new InvalidOperationException(
                $"Header claims an app name (flags=0x{flags:X4}) but is only {srr.Length} bytes — " +
                "too short to contain the name-length field.");
        }

        ushort nameLen = BitConverter.ToUInt16(srr, 7);
        int nameEnd = 9 + nameLen;
        if (headerSize != nameEnd)
        {
            throw new InvalidOperationException(
                $"Inconsistent header block: headerSize={headerSize} but 9 + nameLen({nameLen}) = {nameEnd}.");
        }

        if (nameEnd > srr.Length)
        {
            throw new InvalidOperationException(
                $"Header claims a {nameLen}-byte name ending at offset {nameEnd}, but the buffer is " +
                $"only {srr.Length} bytes.");
        }

        byte[] repl = Encoding.UTF8.GetBytes(replacement);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(srr, 0, 2);                       // sentinel
        w.Write(srr[2]);                          // type
        w.Write(flags);
        w.Write((ushort)(7 + 2 + repl.Length));   // headerSize rewritten
        w.Write((ushort)repl.Length);
        w.Write(repl);
        w.Write(srr, nameEnd, srr.Length - nameEnd);
        return ms.ToArray();
    }

    #region NormalizeAppName hand-built byte vectors

    // These are logically first in this file (xUnit does not guarantee alphabetical or
    // cross-suite run order — declaration order within a class is the only default guarantee, and
    // these Facts don't depend on it either way) so a bug in the normalizer itself can't mask a
    // real divergence between our writer's output and pyrescene's — see spec §6.

    [Fact]
    public void NormalizeAppName_NoAppNameFlag_ReturnsInputUnchanged()
    {
        byte[] input = BuildHeaderBlock(flags: 0x0000, appName: null, trailing: [0xAA, 0xBB]);

        byte[] result = NormalizeAppName(input);

        // Assert.Equal (the semantic contract), not Assert.Same: the early-return-same-reference
        // path is an implementation detail, not something callers rely on.
        Assert.Equal(input, result);
    }

    [Fact]
    public void NormalizeAppName_DifferingNameLengths_ConvergeToIdenticalBytes()
    {
        byte[] trailing = [0xAA, 0xBB, 0xCC];
        byte[] shortName = BuildHeaderBlock(flags: 0x0001, appName: "AB", trailing);
        byte[] longName = BuildHeaderBlock(flags: 0x0001, appName: "ABCDE", trailing);

        byte[] resultShort = NormalizeAppName(shortName);
        byte[] resultLong = NormalizeAppName(longName);
        byte[] expected = BuildExpectedNormalizedHeader(flags: 0x0001, trailing);

        Assert.Equal(expected, resultShort);
        Assert.Equal(expected, resultLong);
    }

    [Fact]
    public void NormalizeAppName_TruncatedHeader_NoTrailingBytes_HandlesBoundaryExactly()
    {
        // The name bytes are the very last bytes in the buffer — nothing follows the header at
        // all (a header-only SRR). Exercises the srr.Length - (9 + nameLen) == 0 boundary.
        byte[] input = BuildHeaderBlock(flags: 0x0001, appName: "X", trailing: []);

        byte[] result = NormalizeAppName(input);
        byte[] expected = BuildExpectedNormalizedHeader(flags: 0x0001, trailing: []);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void NormalizeAppName_TrailingBytesAfterName_ArePreservedVerbatim()
    {
        byte[] trailing = Encoding.ASCII.GetBytes("SUBSEQUENT-BLOCK-BYTES");
        byte[] input = BuildHeaderBlock(flags: 0x0001, appName: "MyApp 1.0", trailing);

        byte[] result = NormalizeAppName(input);
        byte[] expected = BuildExpectedNormalizedHeader(flags: 0x0001, trailing);

        Assert.Equal(expected, result);
        Assert.Equal(trailing, result[^trailing.Length..]);
    }

    // The four vectors below are the trust-anchor hardening (codex finding): NormalizeAppName
    // must THROW on a malformed/self-inconsistent header rather than silently normalize it away,
    // since a real writer bug in these exact fields would otherwise be masked identically on both
    // sides of every golden comparison in this file (and Task 9's).

    [Fact]
    public void NormalizeAppName_InconsistentHeaderSize_Throws()
    {
        // nameLen correctly says 1 byte ("X"), so a correct headerSize would be 9 + 1 = 10 — but
        // this header claims 27 (pyrescene's own real header-size value, for realism). The OLD
        // normalizer never read/validated headerSize at all, so this was silently accepted and
        // normalized away — exactly the masking codex demonstrated.
        byte[] input = BuildHeaderBlock(flags: 0x0001, appName: "X", trailing: [0xAA, 0xBB, 0xCC], headerSizeOverride: 27);

        var ex = Assert.Throws<InvalidOperationException>(() => NormalizeAppName(input));
        Assert.Contains("headerSize", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeAppName_SentinelMismatch_Throws()
    {
        byte[] input = BuildHeaderBlock(flags: 0x0000, appName: null, trailing: [], sentinel: 0x6A6A);

        var ex = Assert.Throws<InvalidOperationException>(() => NormalizeAppName(input));
        Assert.Contains("sentinel", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeAppName_TypeMismatch_Throws()
    {
        byte[] input = BuildHeaderBlock(flags: 0x0000, appName: null, trailing: [], type: 0x6A);

        var ex = Assert.Throws<InvalidOperationException>(() => NormalizeAppName(input));
        Assert.Contains("block type", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeAppName_NameLengthExceedsBuffer_Throws()
    {
        // headerSize (29) IS internally consistent with the declared nameLen (20) — 9 + 20 = 29 —
        // so this passes the headerSize-consistency check and must be caught by the bounds check
        // instead: the buffer is cut off after only 2 of the declared 20 name bytes.
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0x6969);
        w.Write((byte)0x69);
        w.Write((ushort)0x0001);
        w.Write((ushort)29);
        w.Write((ushort)20);
        w.Write(new byte[] { 0x41, 0x42 });
        byte[] input = ms.ToArray();

        var ex = Assert.Throws<InvalidOperationException>(() => NormalizeAppName(input));
        Assert.Contains("buffer", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds a synthetic header block byte array (sentinel 0x6969, type 0x69 by default) with the
    /// given flags/app-name/trailing bytes — an input fixture, NOT a call into production code.
    /// <paramref name="headerSizeOverride"/>, <paramref name="sentinel"/>, and
    /// <paramref name="type"/> let a test deliberately craft a malformed header.
    /// </summary>
    private static byte[] BuildHeaderBlock(
        ushort flags, string? appName, byte[] trailing,
        ushort? headerSizeOverride = null, ushort sentinel = 0x6969, byte type = 0x69)
    {
        byte[]? nameBytes = appName != null ? Encoding.UTF8.GetBytes(appName) : null;
        ushort headerSize = headerSizeOverride ?? (ushort)(7 + (nameBytes != null ? 2 + nameBytes.Length : 0));

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(sentinel);
        w.Write(type);
        w.Write(flags);
        w.Write(headerSize);
        if (nameBytes != null)
        {
            w.Write((ushort)nameBytes.Length);
            w.Write(nameBytes);
        }

        w.Write(trailing);
        return ms.ToArray();
    }

    /// <summary>
    /// Hand-derived expected output for a normalized header — written independently of
    /// <see cref="NormalizeAppName"/> itself so these tests can't pass by construction.
    /// </summary>
    private static byte[] BuildExpectedNormalizedHeader(ushort flags, byte[] trailing)
    {
        byte[] repl = Encoding.UTF8.GetBytes("NORMALIZED");

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0x6969);
        w.Write((byte)0x69);
        w.Write(flags);
        w.Write((ushort)(7 + 2 + repl.Length));
        w.Write((ushort)repl.Length);
        w.Write(repl);
        w.Write(trailing);
        return ms.ToArray();
    }

    #endregion

    [Fact]
    public async Task TwoDiscTree_MatchesPyresceneGoldenBytes()
    {
        string tree = Data("tree-2disc");
        string output = Path.Combine(Path.GetTempPath(), "g2-" + Guid.NewGuid().ToString("N") + ".srr");
        // Input order = spec traversal order over the tree (CD1 before CD2, ordinal).
        SRRCreationResult r = await new SRRWriter().CreateFromInputsAsync(
            output,
            [Path.Combine(tree, "CD1", "a.sfv"), Path.Combine(tree, "CD2", "b.sfv")],
            tree, storeRelativePaths: true,
            additionalFiles: BuildStoredListInTraversalOrder(tree)); // helper: nfo -> ... -> sfvs, spec §2 ordering

        Assert.Null(r.ErrorMessage);

        // This test originally caught a real divergence: golden-2disc.srr's four SrrRarFile
        // (0x71) reference blocks all carry flags 0x0001 (RECOVERY_BLOCKS_REMOVED — pyrescene
        // always sets it), while SRRWriter.WriteRARFileBlock wrote SRRBlockFlags.None. Adjudicated
        // fix (see SRRBlockFlags.RecoveryBlocksRemoved doc + TestData/multiset/README.md's "Fixed
        // divergence" section): WriteRARFileBlock now sets the flag unconditionally, matching
        // pyReScene for both this and the pre-existing single-input CreateAsync path.
        Assert.Equal(
            NormalizeAppName(File.ReadAllBytes(Data("golden-2disc.srr"))),
            NormalizeAppName(File.ReadAllBytes(output)));
    }

    [Fact]
    public async Task StorageOnlyTree_MatchesPyresceneGoldenBytes()
    {
        string tree = Data("tree-storageonly");
        string output = Path.Combine(Path.GetTempPath(), "gso-" + Guid.NewGuid().ToString("N") + ".srr");

        SRRCreationResult r = await new SRRWriter().CreateFromInputsAsync(
            output,
            inputFiles: [],
            tree, storeRelativePaths: true,
            additionalFiles: [new StoredFileEntry("release.nfo", Path.Combine(tree, "release.nfo"))]);

        Assert.Null(r.ErrorMessage);
        Assert.Equal(
            NormalizeAppName(File.ReadAllBytes(Data("golden-storageonly.srr"))),
            NormalizeAppName(File.ReadAllBytes(output)));
    }

    /// <summary>
    /// Hardcodes tree-2disc's stored-file list in the spec's category-pass order (nfo, then the
    /// excluded subtitle SFV, per the README's derivation from pyrescene's generate_srr
    /// copied_files construction) — written longhand so this lib test carries no App.Core
    /// (scanner) dependency. CreateFromInputsAsync auto-appends the two main-set SFVs
    /// (CD1/a.sfv, CD2/b.sfv) from inputFiles, in inputFiles order, after these.
    /// </summary>
    private static List<StoredFileEntry> BuildStoredListInTraversalOrder(string tree) =>
    [
        new StoredFileEntry("release.nfo", Path.Combine(tree, "release.nfo")),
        new StoredFileEntry("Subs/subs.sfv", Path.Combine(tree, "Subs", "subs.sfv")),
    ];
}
