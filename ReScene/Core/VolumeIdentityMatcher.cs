namespace ReScene.Core;

/// <summary>
/// Pure comparison of a produced/written set of volume names against the expected release volume
/// names, by count and normalized name — a multiset comparison (order-independent, duplicates
/// counted), so reordering or a differing directory qualification on either side doesn't cause a
/// false mismatch. Names are normalized via <see cref="Manager.LastSegment"/> (the last path
/// segment, splitting on both <c>/</c> and <c>\</c>) and compared case-insensitively.
/// </summary>
internal static class VolumeIdentityMatcher
{
    /// <summary>
    /// Returns <see langword="true"/> only when <paramref name="actualNames"/> has exactly the
    /// same normalized names, with the same multiplicity, as <paramref name="expectedNames"/>.
    /// An empty <paramref name="expectedNames"/> only matches an empty <paramref name="actualNames"/>.
    /// </summary>
    public static bool Matches(IReadOnlyList<string> expectedNames, IReadOnlyList<string> actualNames)
    {
        if (expectedNames.Count != actualNames.Count)
        {
            return false;
        }

        var remaining = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in expectedNames)
        {
            string key = Manager.LastSegment(name);
            remaining[key] = remaining.GetValueOrDefault(key) + 1;
        }

        foreach (string name in actualNames)
        {
            string key = Manager.LastSegment(name);
            if (!remaining.TryGetValue(key, out int count) || count == 0)
            {
                return false;
            }

            remaining[key] = count - 1;
        }

        return true;
    }
}
