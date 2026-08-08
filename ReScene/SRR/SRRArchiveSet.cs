namespace ReScene.SRR;

/// <summary>
/// One RAR archive set within an SRR: a single multi-volume series (e.g. a disc's
/// <c>.rar</c>+<c>.r00</c>…) and the files it archives, with the header-derived metadata captured
/// from this set's own first headers. Distinct from the flat <see cref="SRRFile"/> properties,
/// which remain the union across all sets.
/// </summary>
public sealed class SRRArchiveSet
{
    /// <summary>The set key (directory + volume base name), e.g. "DVD1/aln-re4a".</summary>
    public required string Key { get; init; }

    /// <summary>The set's directory relative to the release root ("" for root-level volumes).</summary>
    public required string Directory { get; init; }

    /// <summary>Volume file names in SRR order, with directory prefix (e.g. "DVD1\aln-re4a.rar").</summary>
    public IList<string> VolumeNames => _volumeNames;

    internal List<string> _volumeNames { get; } = [];

    /// <summary>Content files this set archives (normalized relative paths).</summary>
    public HashSet<string> ArchivedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Content files this set archives, in the order their headers first appeared in the
    /// SRR. A file's continuation headers (split across volumes) do not repeat its entry.</summary>
    public IReadOnlyList<string> ArchivedFilesInOrder => _archivedFilesInOrder;

    internal List<string> _archivedFilesInOrder { get; } = [];

    /// <summary>CRC32 values (as 8-digit hex strings) for each file this set archives, keyed by normalized path. Equals the flat <see cref="SRRFile.ArchivedFileCrcs"/> value for the same file.</summary>
    public Dictionary<string, string> ArchivedFileCrcs { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>File modification times for each file this set archives, keyed by normalized path. Equals the flat <see cref="SRRFile.ArchivedFileTimestamps"/> value for the same file.</summary>
    public Dictionary<string, DateTime> ArchivedFileTimestamps { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>File creation times for each file this set archives, keyed by normalized path. Equals the flat <see cref="SRRFile.ArchivedFileCreationTimes"/> value for the same file.</summary>
    public Dictionary<string, DateTime> ArchivedFileCreationTimes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>File access times for each file this set archives, keyed by normalized path. Equals the flat <see cref="SRRFile.ArchivedFileAccessTimes"/> value for the same file.</summary>
    public Dictionary<string, DateTime> ArchivedFileAccessTimes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Directories this set archives (normalized relative paths). Per-set membership avoids the flat, release-wide last-write-wins contamination when two sets archive a same-named directory.</summary>
    public HashSet<string> ArchivedDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Directory modification times for each directory this set archives, keyed by normalized path.</summary>
    public Dictionary<string, DateTime> ArchivedDirectoryTimestamps { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Directory creation times for each directory this set archives, keyed by normalized path.</summary>
    public Dictionary<string, DateTime> ArchivedDirectoryCreationTimes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Directory access times for each directory this set archives, keyed by normalized path.</summary>
    public Dictionary<string, DateTime> ArchivedDirectoryAccessTimes { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Header-derived metadata, from this set's first headers.
    public int? CompressionMethod { get; set; }
    public int? DictionarySize { get; set; }
    public int? RARVersion { get; set; }
    public bool? IsSolid { get; set; }
    public bool? HasRecoveryRecord { get; set; }
    public byte? DetectedHostOS { get; set; }
    public uint? DetectedFileAttributes { get; set; }
    public bool? HasLargeFiles { get; set; }
    public uint? DetectedHighPackSize { get; set; }
    public uint? DetectedHighUnpSize { get; set; }
}
