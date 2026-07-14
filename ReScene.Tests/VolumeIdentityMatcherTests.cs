using ReScene.Core;

namespace ReScene.Tests;

/// <summary>
/// Tests for <see cref="VolumeIdentityMatcher"/> — the pure count-and-normalized-name comparison
/// used by <see cref="SRRReconstructor"/> to require the FULL expected release volume set (not
/// merely "at least one volume produced").
/// </summary>
public class VolumeIdentityMatcherTests
{
    [Fact]
    public void Matches_ExactSameNamesAndOrder_ReturnsTrue()
        => Assert.True(VolumeIdentityMatcher.Matches(["a.rar", "b.rar"], ["a.rar", "b.rar"]));

    [Fact]
    public void Matches_CountMismatch_ReturnsFalse()
        => Assert.False(VolumeIdentityMatcher.Matches(["a.rar", "b.rar", "c.rar"], ["a.rar", "b.rar"]));

    [Fact]
    public void Matches_BothEmpty_ReturnsTrue()
        => Assert.True(VolumeIdentityMatcher.Matches([], []));

    [Fact]
    public void Matches_NameMismatchDespiteEqualCount_ReturnsFalse()
        => Assert.False(VolumeIdentityMatcher.Matches(["a.rar"], ["different.rar"]));

    [Fact]
    public void Matches_CaseInsensitive_ReturnsTrue()
        => Assert.True(VolumeIdentityMatcher.Matches(["Movie.RAR"], ["movie.rar"]));

    [Fact]
    public void Matches_DirectoryQualifiedNamesNormalizeToLastSegment_ReturnsTrue()
        => Assert.True(VolumeIdentityMatcher.Matches(["CD1\\movie.rar"], ["movie.rar"]));

    [Fact]
    public void Matches_OrderIndependent_ReturnsTrue()
        => Assert.True(VolumeIdentityMatcher.Matches(["a.rar", "b.rar"], ["b.rar", "a.rar"]));

    [Fact]
    public void Matches_DuplicateNamesBothSidesWithMatchingMultiplicity_ReturnsTrue()
        => Assert.True(VolumeIdentityMatcher.Matches(["a.rar", "a.rar", "b.rar"], ["a.rar", "b.rar", "a.rar"]));

    [Fact]
    public void Matches_DuplicateInExpectedButNotActual_ReturnsFalse()
        // Expected has "a.rar" twice; actual has it only once (replaced by an unrelated extra
        // name) — same COUNT, but the multiset differs.
        => Assert.False(VolumeIdentityMatcher.Matches(["a.rar", "a.rar"], ["a.rar", "b.rar"]));
}
