namespace ReScene.RAR;

/// <summary>
/// Represents parsed data from a RAR 4.x main archive header block.
/// </summary>
public class RARArchiveHeader
{
    /// <summary>
    /// Position of the header block in the stream.
    /// </summary>
    public long BlockPosition
    {
        get; set;
    }

    /// <summary>
    /// Header CRC value.
    /// </summary>
    public ushort HeaderCRC
    {
        get; set;
    }

    /// <summary>
    /// Header size in bytes.
    /// </summary>
    public ushort HeaderSize
    {
        get; set;
    }

    /// <summary>
    /// Raw flags from the header.
    /// </summary>
    internal RARArchiveFlags Flags
    {
        get; set;
    }

    /// <summary>
    /// True if header CRC validation passed.
    /// </summary>
    public bool CRCValid
    {
        get; set;
    }

    // Convenience properties for common flag checks

    /// <summary>
    /// True if this is a multi-volume archive.
    /// </summary>
    public bool IsVolume => (Flags & RARArchiveFlags.Volume) != 0;

    /// <summary>
    /// True if this is a solid archive.
    /// </summary>
    public bool IsSolid => (Flags & RARArchiveFlags.Solid) != 0;

    /// <summary>
    /// True if archive has recovery record.
    /// </summary>
    public bool HasRecoveryRecord => (Flags & RARArchiveFlags.Protected) != 0;

    /// <summary>
    /// True if archive uses new volume naming (RAR 2.9+).
    /// </summary>
    public bool HasNewVolumeNaming => (Flags & RARArchiveFlags.NewNumbering) != 0;

    /// <summary>
    /// True if this is the first volume (RAR 3.0+).
    /// </summary>
    public bool IsFirstVolume => (Flags & RARArchiveFlags.FirstVolume) != 0;

    /// <summary>
    /// True if archive headers are encrypted.
    /// </summary>
    public bool HasEncryptedHeaders => (Flags & RARArchiveFlags.Password) != 0;

    /// <summary>
    /// True if archive is locked.
    /// </summary>
    public bool IsLocked => (Flags & RARArchiveFlags.Lock) != 0;
}
