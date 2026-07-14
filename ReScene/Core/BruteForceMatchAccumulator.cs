namespace ReScene.Core;

/// <summary>
/// Accumulates per-executable-version brute-force outcomes into the run's overall result.
/// <c>TryProcessCommandLinesAsync</c> returns on the first fully-placed match within a version, so
/// each version contributes at most one <see cref="CommittedMatch"/>; this keeps them in discovery
/// order and seeds <see cref="Combo"/> from the FIRST one found — never overwritten by a later
/// version's match, even in exploratory mode (<c>StopOnFirstMatch == false</c>), which keeps
/// searching remaining versions after the first hit. Extracted as a small, process-free unit so
/// this keep-first/accumulate decision policy is unit-testable without running rar.exe.
/// </summary>
internal sealed class BruteForceMatchAccumulator
{
    private readonly List<CommittedMatch> _matches = [];

    /// <summary>Whether any version has produced a fully-placed match so far.</summary>
    public bool Found { get; private set; }

    /// <summary>The first fully-placed match's combo (kept-first); <see langword="null"/> until the first match.</summary>
    public WinningCombo? Combo { get; private set; }

    /// <summary>Every fully-placed match recorded so far, in discovery order.</summary>
    public IReadOnlyList<CommittedMatch> Matches => _matches;

    /// <summary>
    /// Records one executable version's outcome. A version with no match — including one whose
    /// placement was incomplete, which the caller already reports as "not found" — is a no-op. A
    /// version with a match appends it and, only if this is the first match seen, seeds
    /// <see cref="Combo"/>.
    /// </summary>
    public void Record(bool foundCombination, CommittedMatch? match)
    {
        if (!foundCombination || match == null)
        {
            return;
        }

        Found = true;
        _matches.Add(match);
        Combo ??= match.Combo;
    }
}
