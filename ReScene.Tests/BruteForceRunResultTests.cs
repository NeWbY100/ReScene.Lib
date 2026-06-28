using ReScene.Core;
using ReScene.Core.Diagnostics;

namespace ReScene.Tests;

public class BruteForceRunResultTests
{
    [Fact]
    public void WinningCombo_CarriesVersionAndArgs()
    {
        var args = new[] { new RARCommandLineArgument("-m0", 300) };
        var combo = new WinningCombo(351, args);
        var result = new BruteForceRunResult(true, combo);

        Assert.True(result.Success);
        Assert.Equal(351, result.Combo!.Version);
        Assert.Single(result.Combo.Args);
    }

    [Fact]
    public void Options_ExpectedVolumeCrcs_IsCaseInsensitive()
    {
        var opts = new BruteForceOptions("a", "b", "c");
        opts.ExpectedVolumeCrcs["aln-re4a.rar"] = "f1a3ec0d";
        Assert.True(opts.ExpectedVolumeCrcs.ContainsKey("ALN-RE4A.RAR"));
    }
}
