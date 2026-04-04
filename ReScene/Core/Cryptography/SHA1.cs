using System.Security.Cryptography;

namespace ReScene.Core.Cryptography;

/// <summary>
/// Computes SHA-1 hashes for files using a shared algorithm instance.
/// </summary>
public static class SHA1
{
    private static readonly HashAlgorithm _sha1Algorithm = System.Security.Cryptography.SHA1.Create();

    /// <summary>
    /// Calculates the SHA-1 hash of a file, returning the result as a lowercase hex string.
    /// </summary>
    /// <param name="filePath">The path to the file to hash.</param>
    /// <returns>The SHA-1 hash as a lowercase hex string.</returns>
    public static string Calculate(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("SHA1 file not found.", filePath);
        }

        using FileStream fileStream = File.OpenRead(filePath);
        byte[] sha1Bytes;
        lock (_sha1Algorithm)
        {
            sha1Bytes = _sha1Algorithm.ComputeHash(fileStream);
        }

        return Hashing.ByteArrayToHexViaLookup32(sha1Bytes, false);
    }
}
