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
            RAROptions = new RAROptions { OriginalRarFileNames = ["DVD1\\aln-re4a.rar", "DVD1\\aln-re4a.r00"] }
        };
        opts.ExpectedVolumeCrcs["aln-re4a.rar"] = "f1a3ec0d";
        opts.ExpectedVolumeCrcs["aln-re4a.r00"] = "88b361c9";

        var expected = Manager.BuildExpectedInOrder(opts);

        Assert.Equal(2, expected.Count);
        Assert.Equal(("aln-re4a.rar", "f1a3ec0d"), expected[0]);
        Assert.Equal(("aln-re4a.r00", "88b361c9"), expected[1]);
    }

    [Fact]
    public void BuildExpectedInOrder_MissingCrc_OmitsTheVolume()
    {
        var opts = new BruteForceOptions("w", "r", "o")
        {
            RAROptions = new RAROptions { OriginalRarFileNames = ["x.rar", "x.r00"] }
        };
        opts.ExpectedVolumeCrcs["x.rar"] = "aabbccdd"; // x.r00 missing

        var expected = Manager.BuildExpectedInOrder(opts);
        Assert.Single(expected); // only the covered volume; caller treats partial coverage as not-verifiable
    }

    [Fact]
    public void BuildExpectedInOrder_NoExpectedCrcs_ReturnsEmpty()
    {
        var opts = new BruteForceOptions("w", "r", "o")
        {
            RAROptions = new RAROptions { OriginalRarFileNames = ["x.rar", "x.r00"] }
        };

        var expected = Manager.BuildExpectedInOrder(opts);
        Assert.Empty(expected);
    }

    [Fact]
    public void BuildExpectedInOrder_MatchesByBaseFilenameIgnoringCase()
    {
        var opts = new BruteForceOptions("w", "r", "o")
        {
            RAROptions = new RAROptions { OriginalRarFileNames = ["Sub\\ALN-RE4A.RAR"] }
        };
        opts.ExpectedVolumeCrcs["aln-re4a.rar"] = "f1a3ec0d";

        var expected = Manager.BuildExpectedInOrder(opts);

        Assert.Single(expected);
        Assert.Equal(("ALN-RE4A.RAR", "f1a3ec0d"), expected[0]);
    }
}
