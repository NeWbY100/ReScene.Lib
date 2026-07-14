namespace ReScene.Core;

/// <summary>
/// One verified brute-force match whose complete required volume set was actually placed in the
/// output directory: the winning version/argument combo, and the absolute destination paths of
/// every volume placed for it — the full release volume set when
/// <see cref="RAROptions.CompleteAllVolumes"/> is enabled, otherwise just the single first volume.
/// A <see cref="CommittedMatch"/> is only ever produced for a fully-placed set (see
/// <see cref="BruteForceRunResult.Matches"/>); an incomplete placement is not represented here.
/// </summary>
public sealed record CommittedMatch(WinningCombo Combo, IReadOnlyList<string> Files);
