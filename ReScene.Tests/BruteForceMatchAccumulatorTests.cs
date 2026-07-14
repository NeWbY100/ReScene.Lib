using ReScene.Core;
using ReScene.Core.Diagnostics;

namespace ReScene.Tests;

/// <summary>
/// Tests for <see cref="BruteForceMatchAccumulator"/> — the pure, process-free unit that decides
/// how per-executable-version brute-force outcomes fold into the run's overall result. Exercises
/// the exact contract the outer loop in <c>BruteForceRARVersionAsync</c> relies on: keep-first
/// combo selection, accumulation of one match per version in discovery order, and no-ops for
/// versions that didn't produce a fully-placed match.
/// </summary>
public class BruteForceMatchAccumulatorTests
{
    private static CommittedMatch MakeMatch(int version, params string[] files)
        => new(new WinningCombo(version, [new RARCommandLineArgument("-m0", 0)]), files);

    [Fact]
    public void Record_TwoMatchingVersions_KeepsBothInDiscoveryOrder_FirstVersionSeedsCombo()
    {
        var accumulator = new BruteForceMatchAccumulator();
        CommittedMatch first = MakeMatch(350, "a.rar");
        CommittedMatch second = MakeMatch(560, "b.rar");

        accumulator.Record(true, first);
        accumulator.Record(true, second);

        Assert.True(accumulator.Found);
        Assert.Equal(2, accumulator.Matches.Count);
        Assert.Same(first, accumulator.Matches[0]);
        Assert.Same(second, accumulator.Matches[1]);
        // First-kept: the run's seed combo is the FIRST match found, never overwritten by a later
        // version's match (the historical bug always kept the LAST one).
        Assert.Same(first.Combo, accumulator.Combo);
    }

    [Fact]
    public void Record_SingleMatch_MatchesCountIsOne()
    {
        var accumulator = new BruteForceMatchAccumulator();
        CommittedMatch match = MakeMatch(400, "a.rar");

        accumulator.Record(true, match);

        Assert.True(accumulator.Found);
        Assert.Single(accumulator.Matches);
        Assert.Same(match.Combo, accumulator.Combo);
    }

    [Fact]
    public void Record_NoMatchesEver_FoundFalseAndMatchesEmpty()
    {
        var accumulator = new BruteForceMatchAccumulator();

        accumulator.Record(false, null);
        accumulator.Record(false, null);

        Assert.False(accumulator.Found);
        Assert.Empty(accumulator.Matches);
        Assert.Null(accumulator.Combo);
    }

    [Fact]
    public void Record_FoundFalseWithNonNullMatch_IsIgnored()
    {
        // Defensive: TryProcessCommandLinesAsync never actually returns (false, someMatch), but the
        // accumulator must not misbehave if it did — foundCombination is authoritative.
        var accumulator = new BruteForceMatchAccumulator();
        accumulator.Record(false, MakeMatch(400, "a.rar"));

        Assert.False(accumulator.Found);
        Assert.Empty(accumulator.Matches);
        Assert.Null(accumulator.Combo);
    }

    [Fact]
    public void Record_FoundTrueWithNullMatch_IsIgnored()
    {
        // An incomplete placement is reported as "not found" by the caller, but guard the
        // accumulator itself against a null match slipping through as a "found" state change.
        var accumulator = new BruteForceMatchAccumulator();
        accumulator.Record(true, null);

        Assert.False(accumulator.Found);
        Assert.Empty(accumulator.Matches);
        Assert.Null(accumulator.Combo);
    }
}
