namespace ReScene.Core.Comparison;

/// <summary>
/// Aggregates all differences found when comparing two files (SRR, SRS, or RAR).
/// </summary>
public class CompareResult
{
    /// <summary>
    /// Differences in archive-level properties (format, compression, flags, etc.).
    /// </summary>
    public IReadOnlyList<PropertyDifference> ArchiveDifferences => _archiveDifferences;

    internal List<PropertyDifference> _archiveDifferences { get; } = [];

    /// <summary>
    /// Differences in archived file entries (added, removed, or modified files).
    /// </summary>
    public IReadOnlyList<FileDifference> FileDifferences => _fileDifferences;

    internal List<FileDifference> _fileDifferences { get; } = [];

    /// <summary>
    /// Differences in stored (non-RAR) files embedded in SRR archives.
    /// </summary>
    public IReadOnlyList<FileDifference> StoredFileDifferences => _storedFileDifferences;

    internal List<FileDifference> _storedFileDifferences { get; } = [];

    /// <summary>
    /// Gets the total number of differences across all categories.
    /// </summary>
    public int TotalDifferences => ArchiveDifferences.Count + FileDifferences.Count + StoredFileDifferences.Count;
}
