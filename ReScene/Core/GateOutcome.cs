namespace ReScene.Core;

/// <summary>What a candidate's first-volume gate decided.</summary>
internal enum GateOutcome
{
    /// <summary>The first volume matched; proceed to the win path.</summary>
    Match,

    /// <summary>No match (or an unusable result); move to the next candidate.</summary>
    NextCandidate
}
