using System.Buffers.Binary;
using ReScene.RAR;

namespace ReScene.SRR;

/// <summary>
/// Computes OpenSubtitles (ISDb/OSO) hashes for files inside RAR archives.
/// Algorithm: fileSize + 64-bit checksum of the first and last 64 KiB.
/// </summary>
internal static class OSOHashCalculator
{
    private const int HashChunkSize = 64 * 1024;
    private const int MinFileSize = HashChunkSize;

    /// <summary>
    /// Computes OSO hashes for all stored files large enough to hash in the given RAR volumes.
    /// Compressed and split entries are skipped — OSO hashes are computed against the original
    /// file bytes, which only flow through unchanged for stored entries.
    /// </summary>
    /// <param name="rarVolumePaths">
    /// The paths to the RAR volume files.
    /// </param>
    /// <returns>
    /// A list of tuples containing file name, file size, and OSO hash bytes.
    /// </returns>
    public static List<(string FileName, ulong FileSize, byte[] Hash)> ComputeHashes(
        IReadOnlyList<string> rarVolumePaths)
    {
        var results = new List<(string FileName, ulong FileSize, byte[] Hash)>();
        if (rarVolumePaths.Count == 0)
        {
            return results;
        }

        using var archive = RARArchive.Open(rarVolumePaths);

        foreach (RAREntry entry in archive.Files)
        {
            if (entry.IsSplitBefore || !entry.IsStored)
            {
                continue;
            }

            try
            {
                using Stream stream = archive.OpenPackedStream(entry);

                if (stream.Length < MinFileSize)
                {
                    continue;
                }

                byte[] hash = ComputeHash(stream);
                results.Add((entry.FileName, (ulong)stream.Length, hash));
            }
            catch
            {
                // Skip files that can't be read
            }
        }

        return results;
    }

    private static byte[] ComputeHash(Stream stream)
    {
        ulong hash = (ulong)stream.Length;

        byte[] bufferBegin = new byte[HashChunkSize];
        stream.Position = 0;
        stream.ReadExactly(bufferBegin, 0, HashChunkSize);

        byte[] bufferEnd = new byte[HashChunkSize];
        stream.Seek(-HashChunkSize, SeekOrigin.End);
        stream.ReadExactly(bufferEnd, 0, HashChunkSize);

        for (int i = 0; i < HashChunkSize; i += 8)
        {
            hash += BinaryPrimitives.ReadUInt64LittleEndian(bufferBegin.AsSpan(i, 8));
            hash += BinaryPrimitives.ReadUInt64LittleEndian(bufferEnd.AsSpan(i, 8));
        }

        byte[] result = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(result, hash);
        return result;
    }
}
