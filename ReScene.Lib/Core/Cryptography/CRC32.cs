using Force.Crc32;

namespace Core.Cryptography;

public class CRC32
{
    public static string Calculate(string filePath)
        => Calculate(filePath, null, CancellationToken.None);

    public static string Calculate(string filePath, Action<long>? onProgress, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("File not found.", filePath);
        }

        uint hash = 0;
        byte[] buffer = new byte[1048576 * 32]; // 32MB buffer
        long totalBytesRead = 0;

        using (FileStream entryStream = File.OpenRead(filePath))
        {
            int currentBlockSize = 0;

            while ((currentBlockSize = entryStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hash = Crc32Algorithm.Append(hash, buffer, 0, currentBlockSize);
                totalBytesRead += currentBlockSize;
                onProgress?.Invoke(totalBytesRead);
            }
        }

        return hash.ToString("x8");
    }
}
