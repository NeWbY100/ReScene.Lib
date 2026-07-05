namespace ReScene.RAR;

/// <summary>
/// RAR 5.0 file/service block flags.
/// </summary>
[Flags]
internal enum RAR5FileFlags : ulong
{
    /// <summary>
    /// Entry is a directory.
    /// </summary>
    Directory = 0x0001,

    /// <summary>
    /// Time field is present.
    /// </summary>
    TimePresent = 0x0002,

    /// <summary>
    /// CRC32 field is present.
    /// </summary>
    CRC32Present = 0x0004,

    /// <summary>
    /// Unpacked size is unknown.
    /// </summary>
    UnknownSize = 0x0008
}
