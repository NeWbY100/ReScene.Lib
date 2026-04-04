using System.Buffers.Binary;
using ReScene.RAR;

namespace ReScene.SRR;

/// <summary>
/// Computes OpenSubtitles (ISDb/OSO) hashes for files inside RAR archives.
/// Algorithm: fileSize + 64-bit checksum of the first and last 64 KiB.
/// </summary>
internal static class OsoHashCalculator
{
    private const int HashChunkSize = 64 * 1024; // 64 KiB
    private const int MinFileSize = HashChunkSize; // Files smaller than 64 KiB can't be hashed

    /// <summary>
    /// Computes OSO hashes for all archived files in the given RAR volumes.
    /// Returns a list of (fileName, fileSize, hash) tuples.
    /// </summary>
    /// <param name="rarVolumePaths">The paths to the RAR volume files.</param>
    /// <returns>A list of tuples containing file name, file size, and OSO hash bytes.</returns>
    public static List<(string FileName, ulong FileSize, byte[] Hash)> ComputeHashes(
        IReadOnlyList<string> rarVolumePaths)
    {
        var results = new List<(string FileName, ulong FileSize, byte[] Hash)>();
        if (rarVolumePaths.Count == 0)
        {
            return results;
        }

        string firstVolume = rarVolumePaths[0];

        // Find all archived files by parsing RAR headers
        List<string> fileNames = FindArchivedFiles(rarVolumePaths);

        foreach (string fileName in fileNames)
        {
            try
            {
                using var stream = new RarStream(firstVolume, fileName);

                if (stream.Length < MinFileSize)
                {
                    continue;
                }

                byte[] hash = ComputeHash(stream);
                results.Add((fileName, (ulong)stream.Length, hash));
            }
            catch
            {
                // Skip files that can't be read (compressed, corrupt, etc.)
            }
        }

        return results;
    }

    /// <summary>
    /// Computes the OSO hash for a seekable stream.
    /// </summary>
    private static byte[] ComputeHash(Stream stream)
    {
        ulong hash = (ulong)stream.Length;

        // Read first 64 KiB
        byte[] bufferBegin = new byte[HashChunkSize];
        stream.Position = 0;
        stream.ReadExactly(bufferBegin, 0, HashChunkSize);

        // Read last 64 KiB
        byte[] bufferEnd = new byte[HashChunkSize];
        stream.Seek(-HashChunkSize, SeekOrigin.End);
        stream.ReadExactly(bufferEnd, 0, HashChunkSize);

        // Sum all 8-byte little-endian chunks
        for (int i = 0; i < HashChunkSize; i += 8)
        {
            hash += BinaryPrimitives.ReadUInt64LittleEndian(bufferBegin.AsSpan(i, 8));
            hash += BinaryPrimitives.ReadUInt64LittleEndian(bufferEnd.AsSpan(i, 8));
        }

        // Return as 8 bytes little-endian
        byte[] result = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(result, hash);
        return result;
    }

    /// <summary>
    /// Finds all archived file names across all RAR volumes.
    /// </summary>
    private static List<string> FindArchivedFiles(IReadOnlyList<string> volumePaths)
    {
        var fileNames = new List<string>();

        foreach (string volumePath in volumePaths)
        {
            try
            {
                using var fs = new FileStream(volumePath, FileMode.Open, FileAccess.Read, FileShare.Read);

                if (IsRar5(fs))
                {
                    FindFilesRar5(fs, fileNames);
                }
                else
                {
                    FindFilesRar4(fs, fileNames);
                }
            }
            catch
            {
                // Continue with next volume
            }
        }

        return fileNames;
    }

    private static void FindFilesRar4(FileStream fs, List<string> fileNames)
    {
        fs.Position = 0;
        var reader = new RARHeaderReader(fs);

        while (reader.CanReadBaseHeader)
        {
            RARBlockReadResult? block = reader.ReadBlock(parseContents: true);
            if (block is null)
            {
                break;
            }

            if (block.FileHeader is { } fh && !fh.IsDirectory && !fh.IsSplitBefore
                && !fileNames.Contains(fh.FileName))
            {
                fileNames.Add(fh.FileName);
            }

            // Skip past block header and data (same pattern as RarStream)
            long target = block.BlockPosition + block.HeaderSize;
            if (block.BlockType is RAR4BlockType.FileHeader or RAR4BlockType.Service)
            {
                target += block.AddSize;
            }
            else if ((block.Flags & (ushort)RARFileFlags.LongBlock) != 0)
            {
                target += block.AddSize;
            }

            fs.Position = Math.Min(target, fs.Length);
        }
    }

    private static void FindFilesRar5(FileStream fs, List<string> fileNames)
    {
        fs.Position = 8;
        var reader = new RAR5HeaderReader(fs);

        while (reader.CanReadBaseHeader)
        {
            RAR5BlockReadResult? block = reader.ReadBlock();
            if (block is null)
            {
                break;
            }

            if (block.FileInfo is { } fi && !fi.IsDirectory && !fi.IsSplitBefore
                && !fileNames.Contains(fi.FileName))
            {
                fileNames.Add(fi.FileName);
            }

            reader.SkipBlock(block);
        }
    }

    private static bool IsRar5(FileStream fs)
    {
        if (fs.Length < 8)
        {
            return false;
        }

        long pos = fs.Position;
        Span<byte> marker = stackalloc byte[8];
        int read = fs.Read(marker);
        fs.Position = pos;

        if (read < 8)
        {
            return false;
        }

        ReadOnlySpan<byte> rar5Marker = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];
        return marker.SequenceEqual(rar5Marker);
    }
}
