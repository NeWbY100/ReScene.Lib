namespace ReScene.Core.Comparison;

/// <summary>
/// Represents a difference in a single file entry between two compared archives.
/// </summary>
public class FileDifference
{
    /// <summary>
    /// Gets or sets the name of the file that differs.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the type of difference (added, removed, or modified).
    /// </summary>
    public DifferenceType Type { get; set; } = DifferenceType.None;

    /// <summary>
    /// Gets or sets the property-level differences within this file entry.
    /// </summary>
    public List<PropertyDifference> PropertyDifferences { get; set; } = [];
}
