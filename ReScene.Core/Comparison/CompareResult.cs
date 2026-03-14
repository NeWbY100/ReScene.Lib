namespace ReScene.Core.Comparison;

public class CompareResult
{
    public List<PropertyDifference> ArchiveDifferences { get; set; } = [];
    public List<FileDifference> FileDifferences { get; set; } = [];
    public List<FileDifference> StoredFileDifferences { get; set; } = [];
    public int TotalDifferences => ArchiveDifferences.Count + FileDifferences.Count + StoredFileDifferences.Count;
}
