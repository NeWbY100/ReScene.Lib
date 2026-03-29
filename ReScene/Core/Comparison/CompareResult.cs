namespace ReScene.Core.Comparison;

/// <summary>
/// Aggregates all differences found when comparing two files (SRR, SRS, or RAR).
/// </summary>
public class CompareResult
{
    /// <summary>
    /// Differences in archive-level properties (format, compression, flags, etc.).
    /// </summary>
    public List<PropertyDifference> ArchiveDifferences { get; set; } = [];

    /// <summary>
    /// Differences in archived file entries (added, removed, or modified files).
    /// </summary>
    public List<FileDifference> FileDifferences { get; set; } = [];

    /// <summary>
    /// Differences in stored (non-RAR) files embedded in SRR archives.
    /// </summary>
    public List<FileDifference> StoredFileDifferences { get; set; } = [];

    /// <summary>
    /// Gets the total number of differences across all categories.
    /// </summary>
    public int TotalDifferences => ArchiveDifferences.Count + FileDifferences.Count + StoredFileDifferences.Count;
}
