namespace ReScene.RAR;

/// <summary>
/// RAR 5.0 main archive header info.
/// </summary>
public class RAR5ArchiveInfo
{
    /// <summary>
    /// Archive flags.
    /// </summary>
    public ulong ArchiveFlags
    {
        get; set;
    }

    /// <summary>
    /// Volume number (if present).
    /// </summary>
    public ulong? VolumeNumber
    {
        get; set;
    }

    /// <summary>
    /// True if this is a multi-volume archive.
    /// </summary>
    public bool IsVolume => ((RAR5ArchiveFlags)ArchiveFlags).HasFlag(RAR5ArchiveFlags.Volume);

    /// <summary>
    /// True if volume number field is present.
    /// </summary>
    public bool HasVolumeNumber => ((RAR5ArchiveFlags)ArchiveFlags).HasFlag(RAR5ArchiveFlags.VolumeNumber);

    /// <summary>
    /// True if this is a solid archive.
    /// </summary>
    public bool IsSolid => ((RAR5ArchiveFlags)ArchiveFlags).HasFlag(RAR5ArchiveFlags.Solid);

    /// <summary>
    /// True if archive has recovery record.
    /// </summary>
    public bool HasRecoveryRecord => ((RAR5ArchiveFlags)ArchiveFlags).HasFlag(RAR5ArchiveFlags.RecoveryRecord);

    /// <summary>
    /// True if archive headers are locked.
    /// </summary>
    public bool IsLocked => ((RAR5ArchiveFlags)ArchiveFlags).HasFlag(RAR5ArchiveFlags.Locked);
}
