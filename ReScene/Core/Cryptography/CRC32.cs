using Force.Crc32;

namespace ReScene.Core.Cryptography;

/// <summary>
/// Computes CRC32 checksums for files.
/// </summary>
public class CRC32
{
    /// <summary>
    /// Calculates the CRC32 hash of a file, returning the result as a lowercase hex string.
    /// </summary>
    /// <param name="filePath">The path to the file to hash.</param>
    /// <returns>The CRC32 hash as a lowercase 8-character hex string.</returns>
    public static string Calculate(string filePath)
        => Calculate(filePath, null, CancellationToken.None);

    /// <summary>
    /// Calculates the CRC32 hash of a file with progress reporting and cancellation support.
    /// </summary>
    /// <param name="filePath">The path to the file to hash.</param>
    /// <param name="onProgress">Optional callback invoked with total bytes read so far.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The CRC32 hash as a lowercase 8-character hex string.</returns>
    public static string Calculate(string filePath, Action<long>? onProgress, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("File not found.", filePath);
        }

        uint hash = 0;
        byte[] buffer = new byte[32 * 1024 * 1024];
        long totalBytesRead = 0;

        using FileStream entryStream = File.OpenRead(filePath);
        int currentBlockSize = 0;

        while ((currentBlockSize = entryStream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash = Crc32Algorithm.Append(hash, buffer, 0, currentBlockSize);
            totalBytesRead += currentBlockSize;
            onProgress?.Invoke(totalBytesRead);
        }

        return hash.ToString("x8");
    }
}
