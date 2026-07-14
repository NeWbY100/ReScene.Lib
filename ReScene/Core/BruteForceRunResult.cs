namespace ReScene.Core;

/// <summary>The outcome of a brute-force run: success plus the winning combo (for seeding the next set).</summary>
public sealed record BruteForceRunResult(bool Success, WinningCombo? Combo)
{
    /// <summary>
    /// Every fully-placed verified match, in discovery order. Exploratory mode
    /// (<see cref="RAROptions.StopOnFirstMatch"/> disabled) continues across executable versions
    /// and contributes at most one match per version, so this holds at most one entry per version
    /// tried; <see cref="Matches"/>[0] is the first one found (first-kept) and its
    /// <see cref="CommittedMatch.Combo"/> mirrors <see cref="Combo"/>. Empty on failure and on the
    /// custom-packer path (see <see cref="CustomPackerFiles"/> instead).
    /// </summary>
    public IReadOnlyList<CommittedMatch> Matches { get; init; } = [];

    /// <summary>
    /// The full set of volume paths written by the direct SRR (custom-packer) reconstruction path,
    /// populated when <see cref="Combo"/> is <see langword="null"/> and the run succeeded; empty
    /// otherwise (including on failure).
    /// </summary>
    public IReadOnlyList<string> CustomPackerFiles { get; init; } = [];
}
