using System.Buffers.Binary;
using System.IO.Hashing;

namespace ReScene;

/// <summary>
/// Shared Crc32 computation utilities.
/// </summary>
internal static class CRCUtility
{
    private const int BufferSize = 80 * 1024;

    /// <summary>
    /// Computes the Crc32 hash of an entire file.
    /// </summary>
    public static uint ComputeFileCrc32(string filePath, CancellationToken ct = default)
    {
        var crc = new Crc32();
        byte[] buffer = new byte[BufferSize];

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        int bytesRead;
        while ((bytesRead = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            crc.Append(buffer.AsSpan(0, bytesRead));
        }

        Span<byte> hash = stackalloc byte[4];
        crc.GetHashAndReset(hash);
        return BinaryPrimitives.ReadUInt32LittleEndian(hash);
    }
}
