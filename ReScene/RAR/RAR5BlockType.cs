namespace ReScene.RAR;

/// <summary>
/// RAR 5.0+ block header types from unrar headers.hpp
/// </summary>
internal enum RAR5BlockType : byte
{
    /// <summary>
    /// RAR 5.0 signature (HEAD_MARK).
    /// </summary>
    Marker = 0x00,

    /// <summary>
    /// Main archive header (HEAD_MAIN).
    /// </summary>
    Main = 0x01,

    /// <summary>
    /// File header (HEAD_FILE).
    /// </summary>
    File = 0x02,

    /// <summary>
    /// Service header (HEAD_SERVICE).
    /// </summary>
    Service = 0x03,

    /// <summary>
    /// Encryption header (HEAD_CRYPT).
    /// </summary>
    Crypt = 0x04,

    /// <summary>
    /// End of archive marker (HEAD_ENDARC).
    /// </summary>
    EndArchive = 0x05,

    /// <summary>
    /// Unknown block type (HEAD_UNKNOWN).
    /// </summary>
    Unknown = 0xFF
}
