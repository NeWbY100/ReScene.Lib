namespace ReScene.Core.Comparison;

public class FileDifference
{
    public string FileName { get; set; } = string.Empty;
    public DifferenceType Type { get; set; } = DifferenceType.None;
    public List<PropertyDifference> PropertyDifferences { get; set; } = [];
}
