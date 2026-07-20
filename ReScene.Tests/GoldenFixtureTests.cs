using System.Text;
using ReScene.SRR;

namespace ReScene.Tests;

/// <summary>
/// Non-circular oracle for <see cref="SRRWriter.CreateFromInputsAsync"/>: asserts our output is
/// byte-identical to an SRR built by the LOCAL pyrescene checkout over the same synthetic release
/// tree (app-name field normalized — pyrescene's header records "pyReScene Auto &lt;version&gt;",
/// ours defaults to "ReScene.NET"). See
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
    internal static byte[] NormalizeAppName(byte[] srr)
    {
        const string replacement = "NORMALIZED";
        ushort flags = BitConverter.ToUInt16(srr, 3);
        if ((flags & 0x1) == 0)
        {
            return srr;
        }

        ushort nameLen = BitConverter.ToUInt16(srr, 7);
        byte[] repl = Encoding.UTF8.GetBytes(replacement);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(srr, 0, 2);                       // sentinel
        w.Write(srr[2]);                          // type
        w.Write(flags);
        w.Write((ushort)(7 + 2 + repl.Length));   // headerSize rewritten
        w.Write((ushort)repl.Length);
        w.Write(repl);
        w.Write(srr, 9 + nameLen, srr.Length - (9 + nameLen));
        return ms.ToArray();
    }

    #region NormalizeAppName hand-built byte vectors

    // These run FIRST (alphabetically before the golden-comparison tests, but more importantly
    // logically first in this file) so a symmetric bug in the normalizer itself can't mask a real
    // divergence between our writer's output and pyrescene's — see spec §6.

    [Fact]
    public void NormalizeAppName_NoAppNameFlag_ReturnsInputUnchanged()
    {
        byte[] input = BuildHeaderBlock(flags: 0x0000, appName: null, trailing: [0xAA, 0xBB]);

        byte[] result = NormalizeAppName(input);

        Assert.Same(input, result);
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

    /// <summary>
    /// Builds a synthetic header block byte array (sentinel 0x6969, type 0x69) with the given
    /// flags/app-name/trailing bytes — an input fixture, NOT a call into production code.
    /// </summary>
    private static byte[] BuildHeaderBlock(ushort flags, string? appName, byte[] trailing)
    {
        byte[]? nameBytes = appName != null ? Encoding.UTF8.GetBytes(appName) : null;
        ushort headerSize = (ushort)(7 + (nameBytes != null ? 2 + nameBytes.Length : 0));

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0x6969);
        w.Write((byte)0x69);
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
