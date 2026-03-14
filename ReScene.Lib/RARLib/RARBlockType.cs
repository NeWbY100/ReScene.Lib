namespace RARLib;

/// <summary>
/// RAR 4.x (RAR 1.5-4.x) block header types from unrar headers.hpp
/// </summary>
public enum RAR4BlockType : byte
{
    Marker = 0x72,          // HEAD3_MARK - RAR signature block
    ArchiveHeader = 0x73,   // HEAD3_MAIN - Main archive header
    FileHeader = 0x74,      // HEAD3_FILE - File header
    Comment = 0x75,         // HEAD3_CMT - Comment block (old style)
    AuthInfo = 0x76,        // HEAD3_AV - Authenticity verification (old)
    OldService = 0x77,      // HEAD3_OLDSERVICE - Old-style subblock
    Protect = 0x78,         // HEAD3_PROTECT - Recovery record
    Sign = 0x79,            // HEAD3_SIGN - Digital signature
    Service = 0x7A,         // HEAD3_SERVICE - Service header (subheader)
    EndArchive = 0x7B       // HEAD3_ENDARC - End of archive
}

/// <summary>
/// RAR 5.0+ block header types from unrar headers.hpp
/// </summary>
public enum RAR5BlockType : byte
{
    Marker = 0x00,          // HEAD_MARK - RAR 5.0 signature
    Main = 0x01,            // HEAD_MAIN - Main archive header
    File = 0x02,            // HEAD_FILE - File header
    Service = 0x03,         // HEAD_SERVICE - Service header
    Crypt = 0x04,           // HEAD_CRYPT - Encryption header
    EndArchive = 0x05,      // HEAD_ENDARC - End of archive
    Unknown = 0xFF          // HEAD_UNKNOWN
}
