namespace ReScene.Core.Comparison;

/// <summary>
/// Specifies the type of difference found between two compared items.
/// </summary>
public enum DifferenceType
{
    /// <summary>
    /// No difference detected.
    /// </summary>
    None,

    /// <summary>
    /// The item exists only in the right file.
    /// </summary>
    Added,

    /// <summary>
    /// The item exists only in the left file.
    /// </summary>
    Removed,

    /// <summary>
    /// The item exists in both files but has different property values.
    /// </summary>
    Modified
}
