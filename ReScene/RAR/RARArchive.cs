using ReScene.RAR.Decompression;

namespace ReScene.RAR;

/// <summary>
/// One archived file as discovered by walking RAR headers across all volumes in a set.
/// </summary>
/// <param name="FileName">
/// File name as recorded in the RAR header, with backslash separators preserved.
/// </param>
/// <param name="IsStored">
/// True when stored uncompressed (method 0). False for any compressed entry.
/// </param>
/// <param name="IsSplit">
/// True when the file is split across volume boundaries (either continued from a previous
/// volume or continues into a following one).
/// </param>
/// <param name="IsSplitBefore">
/// True when the entry continues from the previous volume.
/// </param>
/// <param name="IsSplitAfter">
/// True when the entry continues into the following volume.
/// </param>
/// <param name="CompressionMethod">
/// Method index in 0–5 (0 = Store, 1 = Fastest … 5 = Best).
/// </param>
/// <param name="UnpackVersion">
/// Raw <c>UnpVer</c> byte from the file header for RAR4. RAR5 entries always report 50.
/// </param>
/// <param name="PackedSize">
/// Total packed size of the entry across all volumes.
/// </param>
/// <param name="UnpackedSize">
/// Logical (unpacked) size as reported by the file header.
/// </param>
/// <param name="IsRar5">
/// True for entries discovered in a RAR5-format archive.
/// </param>
internal sealed record RAREntry(
    string FileName,
    bool IsStored,
    bool IsSplit,
    bool IsSplitBefore,
    bool IsSplitAfter,
    byte CompressionMethod,
    byte UnpackVersion,
    long PackedSize,
    long UnpackedSize,
    bool IsRar5);

/// <summary>
/// File-level view over a set of RAR volumes. Walks the headers once at <c>Open</c>,
/// exposes the discovered entries, and provides packed/unpacked read access. Decompression,
/// where supported, runs through <see cref="RARDecompressor"/>; entries that cannot be
/// extracted (split, solid+compressed, unsupported method) surface a <c>skipReason</c>
/// from <see cref="TryReadAllBytes"/>.
/// </summary>
internal sealed class RARArchive : IDisposable
{
    private readonly string _firstVolumePath;
    private bool _disposed;

    /// <summary>
    /// True when the archive uses the RAR5 container format.
    /// </summary>
    public bool IsRar5
    {
        get;
    }

    /// <summary>
    /// True when the archive's main header has the SOLID flag set.
    /// </summary>
    public bool IsSolid
    {
        get;
    }

    /// <summary>
    /// True when the archive spans more than one volume.
    /// </summary>
    public bool IsMultiVolume => VolumePaths.Count > 1;

    /// <summary>
    /// Volume paths in archive order, in the order they were discovered or supplied.
    /// </summary>
    public IReadOnlyList<string> VolumePaths
    {
        get;
    }

    /// <summary>
    /// Files discovered inside the archive, deduplicated by name. Directories are excluded.
    /// </summary>
    public IReadOnlyList<RAREntry> Files
    {
        get;
    }

    private RARArchive(string firstVolumePath, IReadOnlyList<string> volumePaths,
        IReadOnlyList<RAREntry> files, bool isRar5, bool isSolid)
    {
        _firstVolumePath = firstVolumePath;
        VolumePaths = volumePaths;
        Files = files;
        IsRar5 = isRar5;
        IsSolid = isSolid;
    }

    /// <summary>
    /// Opens an archive starting from <paramref name="firstVolumePath"/>, auto-discovering
    /// any further volumes in the set on disk.
    /// </summary>
    public static RARArchive Open(string firstVolumePath)
    {
        if (!File.Exists(firstVolumePath))
        {
            throw new FileNotFoundException("RAR archive not found.", firstVolumePath);
        }

        var volumes = new List<string> { firstVolumePath };
        bool? isOldNaming = null;

        bool firstIsRar5 = DetectRar5(firstVolumePath);
        bool isRar5 = firstIsRar5;

        if (firstIsRar5)
        {
            isOldNaming = false;
        }

        string current = firstVolumePath;
        while (true)
        {
            if (!isOldNaming.HasValue)
            {
                isOldNaming = !ArchiveUsesNewNaming(current);
            }

            string? next = RARVolumeNaming.GetNextVolumePath(current, isOldNaming.Value);
            if (next is null || !File.Exists(next))
            {
                break;
            }

            volumes.Add(next);
            current = next;
        }

        return OpenInternal(firstVolumePath, volumes, isRar5);
    }

    /// <summary>
    /// Opens an archive over the explicit volume list. The first entry is treated as the
    /// archive's first volume (used for downstream <see cref="RARStream"/> reads).
    /// </summary>
    public static RARArchive Open(IReadOnlyList<string> volumePaths)
    {
        if (volumePaths.Count == 0)
        {
            throw new ArgumentException("At least one volume path is required.", nameof(volumePaths));
        }

        string first = volumePaths[0];
        if (!File.Exists(first))
        {
            throw new FileNotFoundException("RAR archive not found.", first);
        }

        bool isRar5 = DetectRar5(first);
        return OpenInternal(first, volumePaths, isRar5);
    }

    private static RARArchive OpenInternal(string firstVolumePath, IReadOnlyList<string> volumePaths, bool isRar5)
    {
        var entries = new List<RAREntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool isSolid = false;

        foreach (string volumePath in volumePaths)
        {
            try
            {
                using var fs = new FileStream(volumePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (isRar5)
                {
                    WalkRar5(fs, entries, seen, ref isSolid);
                }
                else
                {
                    WalkRar4(fs, entries, seen, ref isSolid);
                }
            }
            catch
            {
                // Skip volumes whose headers fail to parse; remaining volumes still contribute.
            }
        }

        return new RARArchive(firstVolumePath, volumePaths, entries, isRar5, isSolid);
    }

    /// <summary>
    /// Opens a stream over the entry's raw packed bytes. Stored entries hand back the
    /// original file content; compressed entries hand back the compressed bitstream.
    /// Caller owns the returned stream.
    /// </summary>
    public Stream OpenPackedStream(RAREntry entry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(entry);
        return new RARStream(_firstVolumePath, entry.FileName);
    }

    /// <summary>
    /// Reads the full content of an entry, decompressing where supported. Returns the
    /// unpacked bytes, or <see langword="null"/> with a populated <paramref name="skipReason"/>
    /// when the entry cannot be extracted in stream mode (split, solid+compressed,
    /// unsupported method, exceeds <paramref name="maxUnpackedBytes"/>, or decompression failed).
    /// </summary>
    public byte[]? TryReadAllBytes(RAREntry entry, long maxUnpackedBytes, out string? skipReason)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.IsSplit)
        {
            skipReason = "file spans multiple volumes — extraction unsupported";
            return null;
        }

        if (!entry.IsStored && IsSolid)
        {
            skipReason = "compressed inside solid archive — extraction unsupported";
            return null;
        }

        if (entry.PackedSize <= 0)
        {
            skipReason = "entry has no data";
            return null;
        }

        if (entry.UnpackedSize <= 0 || entry.UnpackedSize > maxUnpackedBytes)
        {
            skipReason = $"unpacked size {entry.UnpackedSize} bytes exceeds {maxUnpackedBytes}-byte cap";
            return null;
        }

        try
        {
            using Stream stream = OpenPackedStream(entry);

            byte[] packed = new byte[entry.PackedSize];
            stream.ReadExactly(packed, 0, packed.Length);

            if (entry.IsStored)
            {
                skipReason = null;
                return packed;
            }

            RARVersion version = entry.IsRar5
                ? RARVersion.RAR50
                : entry.UnpackVersion <= 20 ? RARVersion.RAR20 : RARVersion.RAR29;
            var method = (RARMethod)(0x30 + entry.CompressionMethod);

            byte[]? unpacked = RARDecompressor.Decompress(packed, (int)entry.UnpackedSize, method, version);
            if (unpacked is null)
            {
                skipReason = "decompression failed";
                return null;
            }

            skipReason = null;
            return unpacked;
        }
        catch (Exception ex)
        {
            skipReason = ex.Message;
            return null;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _disposed = true;
    }

    private static bool DetectRar5(string volumePath)
    {
        using var fs = new FileStream(volumePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return RARUtils.IsRar5Marker(fs);
    }

    private static bool ArchiveUsesNewNaming(string volumePath)
    {
        using var fs = new FileStream(volumePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (RARUtils.IsRar5Marker(fs))
        {
            return true;
        }

        fs.Position = 0;
        var reader = new RARHeaderReader(fs);
        while (reader.CanReadBaseHeader)
        {
            RARBlockReadResult? block = reader.ReadBlock(parseContents: true);
            if (block is null)
            {
                break;
            }

            if (block.ArchiveHeader is { } ah)
            {
                return ah.HasNewVolumeNaming;
            }

            long target = block.BlockPosition + block.HeaderSize;
            if (block.BlockType is RAR4BlockType.FileHeader or RAR4BlockType.Service)
            {
                target += block.AddSize;
            }
            else if ((block.Flags & (ushort)RARFileFlags.LongBlock) != 0)
            {
                target += block.AddSize;
            }

            fs.Position = Math.Min(target, fs.Length);
        }

        return true;
    }

    private static void WalkRar4(FileStream fs, List<RAREntry> entries, HashSet<string> seen, ref bool isSolid)
    {
        fs.Position = 0;
        var reader = new RARHeaderReader(fs);

        while (reader.CanReadBaseHeader)
        {
            RARBlockReadResult? block = reader.ReadBlock(parseContents: true);
            if (block is null)
            {
                break;
            }

            if (block.ArchiveHeader is { } ah)
            {
                isSolid |= ah.IsSolid;
            }

            if (block.FileHeader is { } fh && !fh.IsDirectory && seen.Add(fh.FileName))
            {
                bool isSplit = fh.IsSplitBefore || fh.IsSplitAfter;
                entries.Add(new RAREntry(
                    FileName: fh.FileName,
                    IsStored: fh.CompressionMethod == 0,
                    IsSplit: isSplit,
                    IsSplitBefore: fh.IsSplitBefore,
                    IsSplitAfter: fh.IsSplitAfter,
                    CompressionMethod: fh.CompressionMethod,
                    UnpackVersion: fh.UnpackVersion,
                    PackedSize: (long)fh.PackedSize,
                    UnpackedSize: (long)fh.UnpackedSize,
                    IsRar5: false));
            }

            long target = block.BlockPosition + block.HeaderSize;
            if (block.BlockType is RAR4BlockType.FileHeader or RAR4BlockType.Service)
            {
                target += block.AddSize;
            }
            else if ((block.Flags & (ushort)RARFileFlags.LongBlock) != 0)
            {
                target += block.AddSize;
            }

            fs.Position = Math.Min(target, fs.Length);
        }
    }

    private static void WalkRar5(FileStream fs, List<RAREntry> entries, HashSet<string> seen, ref bool isSolid)
    {
        fs.Position = 8;
        var reader = new RAR5HeaderReader(fs);

        while (reader.CanReadBaseHeader)
        {
            RAR5BlockReadResult? block = reader.ReadBlock();
            if (block is null)
            {
                break;
            }

            if (block.ArchiveInfo is { } ai)
            {
                isSolid |= ai.IsSolid;
            }

            if (block.FileInfo is { } fi && !fi.IsDirectory && seen.Add(fi.FileName))
            {
                bool isSplit = fi.IsSplitBefore || fi.IsSplitAfter;
                entries.Add(new RAREntry(
                    FileName: fi.FileName,
                    IsStored: fi.IsStored,
                    IsSplit: isSplit,
                    IsSplitBefore: fi.IsSplitBefore,
                    IsSplitAfter: fi.IsSplitAfter,
                    CompressionMethod: (byte)fi.CompressionMethod,
                    UnpackVersion: 50,
                    PackedSize: (long)block.DataSize,
                    UnpackedSize: (long)fi.UnpackedSize,
                    IsRar5: true));
            }

            reader.SkipBlock(block);
        }
    }
}
