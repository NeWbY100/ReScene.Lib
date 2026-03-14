namespace ReScene.Core;

/// <summary>
/// Represents a range of RAR version numbers with inclusive start and exclusive end.
/// Used to filter which RAR versions to test during brute-force operations.
/// </summary>
public class VersionRange
{
    /// <summary>
    /// The inclusive start value.
    /// </summary>
    public int Start { get; set; }

    /// <summary>
    /// The exclusive end value.
    /// </summary>
    public int End { get; set; }

    public VersionRange()
    {
    }

    public VersionRange(int start, int end)
    {
        Start = start;
        End = end;
    }

    /// <summary>
    /// Checks whether the given value is in range of the start and end.
    /// </summary>
    /// <param name="version"></param>
    /// <returns>True if in range; otherwise, false.</returns>
    public bool InRange(int version)
    {
        return version >= Start && version < End;
    }
}
