namespace ReScene.Core.Cryptography;

/// <summary>
/// Computes a file hash for a given <see cref="HashType"/>, dispatching to the
/// appropriate algorithm. Shared by the brute-force manager and the SRR reconstructor
/// so both compute hashes identically.
/// </summary>
internal static class HashCalculator
{
    /// <summary>
    /// Calculates the hash of the file at <paramref name="filePath"/> using the
    /// algorithm selected by <paramref name="hashType"/>.
    /// </summary>
    public static string Calculate(HashType hashType, string filePath) => hashType switch
    {
        HashType.SHA1 => SHA1.Calculate(filePath),
        HashType.CRC32 => CRC32.Calculate(filePath),
        _ => throw new ArgumentOutOfRangeException(nameof(hashType))
    };
}
