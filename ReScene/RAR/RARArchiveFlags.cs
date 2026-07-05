namespace ReScene.RAR;

/// <summary>
/// RAR 4.x main archive header flags (MHD_*) from unrar headers.hpp
/// </summary>
[Flags]
internal enum RARArchiveFlags : ushort
{
    /// <summary>
    /// No flags set.
    /// </summary>
    None = 0x0000,

    /// <summary>
    /// Multi-volume archive (MHD_VOLUME).
    /// </summary>
    Volume = 0x0001,

    /// <summary>
    /// Archive comment present (MHD_COMMENT).
    /// </summary>
    Comment = 0x0002,

    /// <summary>
    /// Archive is locked (MHD_LOCK).
    /// </summary>
    Lock = 0x0004,

    /// <summary>
    /// Solid archive (MHD_SOLID).
    /// </summary>
    Solid = 0x0008,

    /// <summary>
    /// New volume naming scheme, RAR 2.9+ (MHD_NEWNUMBERING).
    /// </summary>
    NewNumbering = 0x0010,

    /// <summary>
    /// Authenticity info present (MHD_AV).
    /// </summary>
    AuthInfo = 0x0020,

    /// <summary>
    /// Has recovery record (MHD_PROTECT).
    /// </summary>
    Protected = 0x0040,

    /// <summary>
    /// Encrypted headers (MHD_PASSWORD).
    /// </summary>
    Password = 0x0080,

    /// <summary>
    /// First volume, RAR 3.0+ (MHD_FIRSTVOLUME).
    /// </summary>
    FirstVolume = 0x0100
}
