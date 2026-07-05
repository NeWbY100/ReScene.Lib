namespace ReScene.RAR;

/// <summary>RAR 5.0 main-archive header flags.</summary>
[Flags]
internal enum RAR5ArchiveFlags : ulong
{
    /// <summary>No flags set.</summary>
    None = 0x0000,

    /// <summary>Multi-volume archive.</summary>
    Volume = 0x0001,

    /// <summary>Volume number field is present.</summary>
    VolumeNumber = 0x0002,

    /// <summary>Solid archive.</summary>
    Solid = 0x0004,

    /// <summary>Archive has a recovery record.</summary>
    RecoveryRecord = 0x0008,

    /// <summary>Archive headers are locked.</summary>
    Locked = 0x0010
}
