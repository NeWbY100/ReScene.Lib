namespace ReScene.RAR;

/// <summary>
/// RAR 4.x end archive flags (EARC_*) from unrar headers.hpp
/// </summary>
[Flags]
internal enum RAREndArchiveFlags : ushort
{
    /// <summary>
    /// No flags set.
    /// </summary>
    None = 0x0000,

    /// <summary>
    /// Not the last volume (EARC_NEXT_VOLUME).
    /// </summary>
    NextVolume = 0x0001,

    /// <summary>
    /// Data CRC present (EARC_DATACRC).
    /// </summary>
    DataCRC = 0x0002,

    /// <summary>
    /// Reserved space present (EARC_REVSPACE).
    /// </summary>
    RevSpace = 0x0004,

    /// <summary>
    /// Volume number present (EARC_VOLNUMBER).
    /// </summary>
    VolNumber = 0x0008
}
