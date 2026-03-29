namespace ReScene.Core.Comparison;

/// <summary>
/// Represents a single property value difference between the left and right files.
/// </summary>
public class PropertyDifference
{
    /// <summary>
    /// Gets or sets the name of the property that differs.
    /// </summary>
    public string PropertyName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the property value from the left file.
    /// </summary>
    public string LeftValue { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the property value from the right file.
    /// </summary>
    public string RightValue { get; set; } = string.Empty;
}
