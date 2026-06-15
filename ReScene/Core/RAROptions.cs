using ReScene.Core.Diagnostics;
using ReScene.SRR;

namespace ReScene.Core;

/// <summary>
/// Configuration options for RAR archive creation and brute-force operations.
/// Contains all parameters needed to reconstruct a RAR archive from SRR metadata.
/// </summary>
public class RAROptions
{
    /// <summary>
    /// Gets or sets a value indicating whether to set the archive attribute on files before compressing.
    /// </summary>
    public TriState SetFileArchiveAttribute
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets a value indicating whether to set the not content indexed attribute on files before compressing.
    /// </summary>
    public TriState SetFileNotContentIndexedAttribute
    {
        get; set;
    }

    /// <summary>
    /// Gets the command line arguments.
    /// </summary>
    public IReadOnlyList<RARCommandLineArgument[]> CommandLineArguments { get; init; } = [];

    /// <summary>
    /// Gets the RAR versions to try.
    /// </summary>
    public IReadOnlyList<VersionRange> RARVersions { get; init; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether to delete the RAR files if checksum does not match.
    /// </summary>
    public bool DeleteRARFiles
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets a value indicating whether to delete duplicate CRC files.
    /// </summary>
    public bool DeleteDuplicateCRCFiles { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to stop brute-forcing on the first successful match.
    /// </summary>
    public bool StopOnFirstMatch { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to complete all RAR volumes when a match is found.
    /// </summary>
    public bool CompleteAllVolumes
    {
        get; set;
    }

    /// <summary>
    /// Gets expected CRC32 values for archived files (relative paths).
    /// </summary>
    public Dictionary<string, string> ArchiveFileCrcs { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the file paths to include when an SRR file list is present.
    /// </summary>
    public HashSet<string> ArchiveFilePaths { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the directory paths to include when an SRR file list is present.
    /// </summary>
    public HashSet<string> ArchiveDirectoryPaths { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets directory modified times (mtime) keyed by relative path.
    /// </summary>
    public Dictionary<string, DateTime> DirectoryTimestamps { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets directory creation times (ctime) keyed by relative path.
    /// </summary>
    public Dictionary<string, DateTime> DirectoryCreationTimes { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets directory access times (atime) keyed by relative path.
    /// </summary>
    public Dictionary<string, DateTime> DirectoryAccessTimes { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets file modified times (mtime) keyed by relative path.
    /// </summary>
    public Dictionary<string, DateTime> FileTimestamps { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets file creation times (ctime) keyed by relative path.
    /// </summary>
    public Dictionary<string, DateTime> FileCreationTimes { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets file access times (atime) keyed by relative path.
    /// </summary>
    public Dictionary<string, DateTime> FileAccessTimes { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets a value indicating whether the SRR file list restricts inputs.
    /// </summary>
    public bool HasArchiveFileList => ArchiveFilePaths.Count > 0 || ArchiveDirectoryPaths.Count > 0;

    /// <summary>
    /// Gets or sets the archive comment to include when creating RAR files.
    /// </summary>
    public string? ArchiveComment
    {
        get; set;
    }

    /// <summary>
    /// Gets the raw archive comment bytes for exact reconstruction.
    /// </summary>
    public ReadOnlyMemory<byte>? ArchiveCommentBytes
    {
        get; init;
    }

    /// <summary>
    /// Gets the raw CMT block compressed data from the SRR.
    /// </summary>
    public ReadOnlyMemory<byte>? CmtCompressedData
    {
        get; init;
    }

    /// <summary>
    /// Gets or sets the CMT block compression method from the SRR.
    /// 0x30 = Store, 0x31-0x35 = Compressed (Fastest to Best).
    /// </summary>
    public byte? CmtCompressionMethod
    {
        get; set;
    }

    /// <summary>
    /// Gets a value indicating whether Phase 1 (comment brute-force) can be used.
    /// </summary>
    public bool CanUseCommentPhase => CmtCompressedData is { Length: > 0 };

    /// <summary>
    /// Gets or sets whether to enable Host OS patching.
    /// </summary>
    public bool EnableHostOSPatching
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the detected Host OS from file headers in the SRR.
    /// </summary>
    public byte? DetectedFileHostOS
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the detected file attributes from file headers in the SRR.
    /// </summary>
    public uint? DetectedFileAttributes
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the detected Host OS from CMT service block in the SRR.
    /// </summary>
    public byte? DetectedCmtHostOS
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the detected file time (DOS format) from CMT service block in the SRR.
    /// </summary>
    public uint? DetectedCmtFileTime
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the detected file attributes from CMT service block in the SRR.
    /// </summary>
    public uint? DetectedCmtFileAttributes
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets whether to use old volume naming scheme (.rar, .r00, .r01) instead of new (.part01.rar).
    /// </summary>
    public bool UseOldVolumeNaming
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets whether to rename matched output files to the original RAR filenames in
    /// <see cref="OriginalRarFileNames"/>.
    /// </summary>
    public bool RenameToOriginalNames
    {
        get; set;
    }

    /// <summary>
    /// Gets the original RAR volume filenames, in volume order. Typically sourced from
    /// the SRR file, or from the verification SFV when no SRR is available.
    /// </summary>
    public IReadOnlyList<string> OriginalRarFileNames { get; init; } = [];

    /// <summary>
    /// Gets or sets whether the LARGE flag was detected in SRR file headers.
    /// </summary>
    public bool? DetectedLargeFlag
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the HIGH_PACK_SIZE value from SRR.
    /// </summary>
    public uint? DetectedHighPackSize
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the HIGH_UNP_SIZE value from SRR.
    /// </summary>
    public uint? DetectedHighUnpSize
    {
        get; set;
    }

    /// <summary>
    /// Returns true if LARGE flag patching is needed.
    /// </summary>
    public bool NeedsLargePatching => DetectedLargeFlag.HasValue;

    /// <summary>
    /// Gets or sets the type of custom RAR packer detected from SRR headers.
    /// </summary>
    public CustomPackerType CustomPackerDetected
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the path to the SRR file for direct reconstruction.
    /// </summary>
    public string? SRRFilePath
    {
        get; set;
    }

    /// <summary>
    /// Returns true if any patching is needed.
    /// </summary>
    public bool NeedsPatching => NeedsHostOSPatching || NeedsAttributePatching || NeedsLargePatching || NeedsMtimePatching;

    /// <summary>
    /// Returns true if per-file modification-time patching is needed (the SRR provided
    /// mtimes that should be stamped into the produced RAR headers regardless of what
    /// WinRAR actually captured from the file system).
    /// </summary>
    public bool NeedsMtimePatching => EnableHostOSPatching && FileTimestamps.Count > 0;

    /// <summary>
    /// Returns true if Host OS patching is needed.
    /// </summary>
    public bool NeedsHostOSPatching
    {
        get
        {
            if (!EnableHostOSPatching || !DetectedFileHostOS.HasValue)
            {
                return false;
            }

            bool isCurrentWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;
            byte currentHostOS = isCurrentWindows ? (byte)2 : (byte)3;
            return DetectedFileHostOS.Value != currentHostOS;
        }
    }

    /// <summary>
    /// Returns true if file attribute patching is needed.
    /// </summary>
    public bool NeedsAttributePatching => EnableHostOSPatching && DetectedFileAttributes.HasValue;
}
