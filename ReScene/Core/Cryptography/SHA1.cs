namespace ReScene.Core.Cryptography;

/// <summary>
/// Computes SHA-1 hashes for files.
/// </summary>
public static class SHA1
{
    /// <summary>
    /// Calculates the SHA-1 hash of a file, returning the result as a lowercase hex string.
    /// </summary>
    /// <param name="filePath">
    /// The path to the file to hash.
    /// </param>
    /// <returns>
    /// The SHA-1 hash as a lowercase hex string.
    /// </returns>
    public static string Calculate(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("SHA1 file not found.", filePath);
        }

        // Stateless, lock-free, fully concurrent — unlike a shared HashAlgorithm instance,
        // which is not thread-safe and would serialize all SHA-1 hashing process-wide.
        using FileStream fileStream = File.OpenRead(filePath);
        byte[] sha1Bytes = System.Security.Cryptography.SHA1.HashData(fileStream);

        return Hashing.ByteArrayToHexViaLookup32(sha1Bytes, false);
    }
}
