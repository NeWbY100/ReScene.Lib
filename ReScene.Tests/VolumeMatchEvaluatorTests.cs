using ReScene.Core;

namespace ReScene.Tests;

public class VolumeMatchEvaluatorTests
{
    [Fact]
    public void Evaluate_AllMatch_AssignsNamesPositionally()
    {
        var produced = new[] { "f1a3ec0d", "88b361c9" };
        var expected = new[] { ("aln-re4a.rar", "f1a3ec0d"), ("aln-re4a.r00", "88b361c9") };

        VolumeMatchResult r = VolumeMatchEvaluator.Evaluate(produced, expected);

        Assert.True(r.AllMatch);
        Assert.False(r.CountMismatch);
        Assert.Null(r.FirstMismatch);
        Assert.Equal("aln-re4a.r00", r.Volumes[1].ExpectedName);
    }

    [Fact]
    public void Evaluate_NearMiss_ReportsFirstMismatch_NotAllMatch()
    {
        var produced = new[] { "f1a3ec0d", "ffffffff" };               // vol 1 ok, vol 2 wrong
        var expected = new[] { ("x.rar", "f1a3ec0d"), ("x.r00", "88b361c9") };

        VolumeMatchResult r = VolumeMatchEvaluator.Evaluate(produced, expected);

        Assert.False(r.AllMatch);
        Assert.NotNull(r.FirstMismatch);
        Assert.Equal(1, r.FirstMismatch!.Index);
        Assert.Equal("x.r00", r.FirstMismatch.ExpectedName);
    }

    [Fact]
    public void Evaluate_CrcCompareIsCaseInsensitive()
    {
        VolumeMatchResult r = VolumeMatchEvaluator.Evaluate(["AABBCCDD"], [("x.rar", "aabbccdd")]);
        Assert.True(r.AllMatch);
    }

    [Fact]
    public void Evaluate_CountMismatch_IsNotAMatch()
    {
        VolumeMatchResult r = VolumeMatchEvaluator.Evaluate(["aabbccdd"], [("x.rar", "aabbccdd"), ("x.r00", "11223344")]);
        Assert.True(r.CountMismatch);
        Assert.False(r.AllMatch);
    }
}
