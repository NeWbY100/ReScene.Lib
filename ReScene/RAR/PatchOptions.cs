namespace ReScene.RAR;

/// <summary>
/// Options for patching RAR files. Supports exact values detected from SRR headers.
/// </summary>
internal class PatchOptions
{
    // ===== File Header Options =====

    /// <summary>
    /// Target Host OS value for file headers (0=MS-DOS, 1=OS/2, 2=Windows, 3=Unix, 4=Mac OS, 5=BeOS).
    /// </summary>
    public byte? FileHostOS
    {
        get; set;
    }

    /// <summary>
    /// Target file attributes for file headers.
    /// </summary>
    public uint? FileAttributes
    {
        get; set;
    }

    // ===== Service Block (CMT) Options =====

    /// <summary>
    /// If true, also patch service blocks (like CMT).
    /// </summary>
    public bool PatchServiceBlocks { get; set; } = true;

    /// <summary>
    /// Target Host OS value for service blocks (CMT). If null, uses FileHostOS.
    /// </summary>
    public byte? ServiceBlockHostOS
    {
        get; set;
    }

    /// <summary>
    /// Target file attributes for service blocks (CMT). If null, uses FileAttributes.
    /// </summary>
    public uint? ServiceBlockAttributes
    {
        get; set;
    }

    /// <summary>
    /// Target file time (DOS format) for service blocks. If null, time is not patched.
    /// </summary>
    public uint? ServiceBlockFileTime
    {
        get; set;
    }

    // ===== LARGE Flag Options =====

    /// <summary>
    /// If true, add LARGE flag + HIGH fields. If false, remove them. If null, no change.
    /// </summary>
    public bool? SetLargeFlag
    {
        get; set;
    }

    /// <summary>
    /// HIGH_PACK_SIZE value to insert when adding LARGE (typically 0).
    /// </summary>
    public uint HighPackSize
    {
        get; set;
    }

    /// <summary>
    /// HIGH_UNP_SIZE value to insert when adding LARGE.
    /// </summary>
    public uint HighUnpSize
    {
        get; set;
    }

    // ===== Per-file Modification Time Overrides =====

    /// <summary>
    /// Per-file target modification time, keyed by file name (case-insensitive).
    /// When a file header's <c>FILE_NAME</c> matches a key, the patcher rewrites the
    /// 4-byte DOS <c>FTIME</c> field and (if the <c>LHD_EXTTIME</c> flag is set and
    /// the mtime entry is present in the EXT_TIME extension) the corresponding
    /// sub-second remainder bytes — preserving the existing precision (byte count).
    /// Files not present in this dictionary are left alone. Use this to bypass
    /// file-system / WinRAR precision quirks that prevent the source file's mtime
    /// from being faithfully captured into the produced archive.
    /// </summary>
    public Dictionary<string, DateTime>? FileModifiedTimes
    {
        get; set;
    }

    // ===== Computed Properties =====

    /// <summary>
    /// Gets the Host OS to use for file headers. Returns null if no patching needed.
    /// </summary>
    /// <returns>
    /// The Host OS byte, or <see langword="null"/> if no patching needed.
    /// </returns>
    public byte? GetFileHostOS() => FileHostOS;

    /// <summary>
    /// Gets the Host OS to use for service blocks. Falls back to FileHostOS if not set.
    /// </summary>
    /// <returns>
    /// The Host OS byte, or <see langword="null"/> if no patching needed.
    /// </returns>
    public byte? GetServiceBlockHostOS() => ServiceBlockHostOS ?? FileHostOS;

    /// <summary>
    /// Gets the file attributes to use for file headers.
    /// </summary>
    /// <returns>
    /// The file attributes value, or <see langword="null"/> if not set.
    /// </returns>
    public uint? GetFileAttributes() => FileAttributes;

    /// <summary>
    /// Gets the file attributes to use for service blocks. Falls back to FileAttributes if not set.
    /// </summary>
    /// <returns>
    /// The service block attributes value, or <see langword="null"/> if not set.
    /// </returns>
    public uint? GetServiceBlockAttributes() => ServiceBlockAttributes ?? FileAttributes;
}
