namespace ReScene.Core.Cryptography;

/// <summary>
/// Specifies the hash algorithm type used for file verification.
/// </summary>
public enum HashType
{
    /// <summary>
    /// SHA-1 hash algorithm (160-bit).
    /// </summary>
    SHA1,

    /// <summary>
    /// CRC32 checksum algorithm (32-bit).
    /// </summary>
    CRC32
}
