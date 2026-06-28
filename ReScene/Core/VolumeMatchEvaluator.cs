namespace ReScene.Core;

/// <summary>One produced volume's positional comparison against its expected name + CRC.</summary>
public sealed record VolumeMatch(int Index, string ExpectedName, string ExpectedCrc, string ActualCrc, bool Match);

/// <summary>The result of comparing a produced volume set against the expected per-volume CRCs.</summary>
public sealed record VolumeMatchResult(
    bool AllMatch,
    IReadOnlyList<VolumeMatch> Volumes,
    VolumeMatch? FirstMismatch,
    bool CountMismatch);

/// <summary>
/// Pure comparison of a produced multi-volume RAR set against the expected per-volume CRCs.
/// Volumes are assigned to expected names positionally (RAR emits volumes in deterministic order);
/// CRC is the verification, not the assignment key. A full match requires equal counts and every
/// position's CRC to match (case-insensitive).
/// </summary>
public static class VolumeMatchEvaluator
{
    public static VolumeMatchResult Evaluate(
        IReadOnlyList<string> producedCrcs,
        IReadOnlyList<(string Name, string Crc)> expectedInOrder)
    {
        bool countMismatch = producedCrcs.Count != expectedInOrder.Count;
        int n = Math.Min(producedCrcs.Count, expectedInOrder.Count);
        var volumes = new List<VolumeMatch>(n);
        VolumeMatch? firstMismatch = null;

        for (int i = 0; i < n; i++)
        {
            (string name, string expectedCrc) = expectedInOrder[i];
            string actual = producedCrcs[i];
            bool match = string.Equals(actual, expectedCrc, StringComparison.OrdinalIgnoreCase);
            var vm = new VolumeMatch(i, name, expectedCrc, actual, match);
            volumes.Add(vm);
            if (!match && firstMismatch == null)
            {
                firstMismatch = vm;
            }
        }

        bool allMatch = !countMismatch && firstMismatch == null;
        return new VolumeMatchResult(allMatch, volumes, firstMismatch, countMismatch);
    }
}
