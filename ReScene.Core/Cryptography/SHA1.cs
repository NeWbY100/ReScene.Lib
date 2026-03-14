using System.Security.Cryptography;

namespace ReScene.Core.Cryptography;

public static class SHA1
{
    private static readonly HashAlgorithm SHA1Algorithm = System.Security.Cryptography.SHA1.Create() ?? throw new InvalidProgramException("Could not create a SHA1 hash algorithm instance.");

    public static string Calculate(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("SHA1 file not found.", filePath);
        }

        using FileStream fileStream = File.OpenRead(filePath);
        byte[] sha1Bytes;
        lock (SHA1Algorithm)
        {
            sha1Bytes = SHA1Algorithm.ComputeHash(fileStream);
        }
        return Hashing.ByteArrayToHexViaLookup32(sha1Bytes, false);
    }
}
