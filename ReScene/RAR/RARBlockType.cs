namespace ReScene.RAR;

/// <summary>
/// RAR 4.x (RAR 1.5-4.x) block header types from unrar headers.hpp
/// </summary>
public enum RAR4BlockType : byte
{
    /// <summary>RAR signature block (HEAD3_MARK).</summary>
    Marker = 0x72,
    /// <summary>Main archive header (HEAD3_MAIN).</summary>
    ArchiveHeader = 0x73,
    /// <summary>File header (HEAD3_FILE).</summary>
    FileHeader = 0x74,
    /// <summary>Old-style comment block (HEAD3_CMT).</summary>
    Comment = 0x75,
    /// <summary>Old-style authenticity verification (HEAD3_AV).</summary>
    AuthInfo = 0x76,
    /// <summary>Old-style subblock (HEAD3_OLDSERVICE).</summary>
    OldService = 0x77,
    /// <summary>Recovery record (HEAD3_PROTECT).</summary>
    Protect = 0x78,
    /// <summary>Digital signature (HEAD3_SIGN).</summary>
    Sign = 0x79,
    /// <summary>Service header/subheader (HEAD3_SERVICE).</summary>
    Service = 0x7A,
    /// <summary>End of archive marker (HEAD3_ENDARC).</summary>
    EndArchive = 0x7B
}

/// <summary>
/// RAR 5.0+ block header types from unrar headers.hpp
/// </summary>
public enum RAR5BlockType : byte
{
    /// <summary>RAR 5.0 signature (HEAD_MARK).</summary>
    Marker = 0x00,
    /// <summary>Main archive header (HEAD_MAIN).</summary>
    Main = 0x01,
    /// <summary>File header (HEAD_FILE).</summary>
    File = 0x02,
    /// <summary>Service header (HEAD_SERVICE).</summary>
    Service = 0x03,
    /// <summary>Encryption header (HEAD_CRYPT).</summary>
    Crypt = 0x04,
    /// <summary>End of archive marker (HEAD_ENDARC).</summary>
    EndArchive = 0x05,
    /// <summary>Unknown block type (HEAD_UNKNOWN).</summary>
    Unknown = 0xFF
}
