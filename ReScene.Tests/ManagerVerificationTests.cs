using ReScene.Core;

namespace ReScene.Tests;

public class ManagerVerificationTests
{
    [Fact]
    public void Hashes_MatchIsCaseInsensitive()
    {
        // Regression: computed hashes are lowercase hex, but .sfv/.sha1 files may carry uppercase
        // hex. The Hashes set must compare case-insensitively (like its sibling ExpectedVolumeCrcs),
        // otherwise every byte-correct rebuild verified against an uppercase expected hash is
        // reported as a non-match.
        var opts = new BruteForceOptions("w", "r", "o");
        opts.Hashes.Add("ABCDEF0123456789");

        Assert.Contains("abcdef0123456789", opts.Hashes);
    }

    [Fact]
    public void BuildExpectedInOrder_MapsVolumeNamesToCrcsByBaseFilename()
    {
        var opts = new BruteForceOptions("w", "r", "o")
        {
            RAROptions = new RAROptions { OriginalRARFileNames = ["DVD1\\aln-re4a.rar", "DVD1\\aln-re4a.r00"] }
        };
        opts.ExpectedVolumeCrcs["aln-re4a.rar"] = "f1a3ec0d";
        opts.ExpectedVolumeCrcs["aln-re4a.r00"] = "88b361c9";

        IReadOnlyList<(string Name, string Crc)> expected = Manager.BuildExpectedInOrder(opts);

        Assert.Equal(2, expected.Count);
        Assert.Equal(("aln-re4a.rar", "f1a3ec0d"), expected[0]);
        Assert.Equal(("aln-re4a.r00", "88b361c9"), expected[1]);
    }

    [Fact]
    public void BuildExpectedInOrder_MissingCrc_OmitsTheVolume()
    {
        var opts = new BruteForceOptions("w", "r", "o")
        {
            RAROptions = new RAROptions { OriginalRARFileNames = ["x.rar", "x.r00"] }
        };
        opts.ExpectedVolumeCrcs["x.rar"] = "aabbccdd"; // x.r00 missing

        IReadOnlyList<(string Name, string Crc)> expected = Manager.BuildExpectedInOrder(opts);
        Assert.Single(expected); // only the covered volume; caller treats partial coverage as not-verifiable
    }

    [Fact]
    public void BuildExpectedInOrder_NoExpectedCrcs_ReturnsEmpty()
    {
        var opts = new BruteForceOptions("w", "r", "o")
        {
            RAROptions = new RAROptions { OriginalRARFileNames = ["x.rar", "x.r00"] }
        };

        IReadOnlyList<(string Name, string Crc)> expected = Manager.BuildExpectedInOrder(opts);
        Assert.Empty(expected);
    }

    [Fact]
    public void BuildExpectedInOrder_MatchesByBaseFilenameIgnoringCase()
    {
        var opts = new BruteForceOptions("w", "r", "o")
        {
            RAROptions = new RAROptions { OriginalRARFileNames = ["Sub\\ALN-RE4A.RAR"] }
        };
        opts.ExpectedVolumeCrcs["aln-re4a.rar"] = "f1a3ec0d";

        IReadOnlyList<(string Name, string Crc)> expected = Manager.BuildExpectedInOrder(opts);

        Assert.Single(expected);
        Assert.Equal(("ALN-RE4A.RAR", "f1a3ec0d"), expected[0]);
    }

    [Fact]
    public void BuildExpectedInOrder_DirQualifiedKey_DisambiguatesSameBasenameSets()
    {
        // #9: two sets share the basename "x.rar" under different directories. When the expected
        // CRCs are keyed by their directory-qualified path, this set's volume ("CD2\\x.rar") must
        // resolve to CD2's CRC — not collide onto CD1's. A bare-basename lookup can't tell them
        // apart (and, with only dir-qualified keys present, finds neither → empty).
        var opts = new BruteForceOptions("w", "r", "o")
        {
            RAROptions = new RAROptions { OriginalRARFileNames = ["CD2\\x.rar"] }
        };
        opts.ExpectedVolumeCrcs["CD1/x.rar"] = "aaaaaaaa";
        opts.ExpectedVolumeCrcs["CD2/x.rar"] = "bbbbbbbb";

        IReadOnlyList<(string Name, string Crc)> expected = Manager.BuildExpectedInOrder(opts);

        Assert.Single(expected);
        Assert.Equal(("x.rar", "bbbbbbbb"), expected[0]); // CD2's CRC, and Name is the bare basename
    }

    [Fact]
    public void BuildExpectedInOrder_FlatSfvBasename_StillMatchesViaFallback()
    {
        // #9 fallback: the common flat-SFV case keys expected CRCs by bare basename ("x.r00"),
        // while the SRR-internal volume name carries a directory ("DVD1\\x.r00"). The dir-qualified
        // lookup misses, so the legacy basename fallback must still match — the map is never empty
        // where a basename lookup would have succeeded.
        var opts = new BruteForceOptions("w", "r", "o")
        {
            RAROptions = new RAROptions { OriginalRARFileNames = ["DVD1\\x.r00"] }
        };
        opts.ExpectedVolumeCrcs["x.r00"] = "cafebabe";

        IReadOnlyList<(string Name, string Crc)> expected = Manager.BuildExpectedInOrder(opts);

        Assert.Single(expected);
        Assert.Equal(("x.r00", "cafebabe"), expected[0]);
    }

    [Theory]
    [InlineData("DVD1\\x.rar", "x.rar")] // #10: backslash-separated SRR-internal name (the Linux bug)
    [InlineData("DVD1/x.rar", "x.rar")]
    [InlineData("A\\B/c.rar", "c.rar")]  // mixed separators
    [InlineData("x.rar", "x.rar")]       // no separator
    public void LastSegment_SplitsOnBothSeparators_RegardlessOfPlatform(string input, string expected)
    {
        // RenameMatchedOutput derives each volume's output name from its SRR-internal original name
        // via LastSegment (not Path.GetFileName, which leaves '\\' embedded on non-Windows). Splitting
        // on both '/' and '\\' unconditionally makes the rename correct on any platform: a
        // "DVD1\\x.rar" original yields "x.rar", never the literal-backslash "DVD1\\x.rar".
        Assert.Equal(expected, Manager.LastSegment(input));
    }
}
