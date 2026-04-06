namespace ReScene;

/// <summary>
/// Shared stream I/O helper methods used across SRR, SRS, and RAR modules.
/// </summary>
internal static class StreamUtilities
{
    private const int DefaultBufferSize = 80 * 1024;

    /// <summary>
    /// Copies exactly <paramref name="count"/> bytes from <paramref name="source"/>
    /// to <paramref name="destination"/>.
    /// </summary>
    public static void CopyBytes(Stream source, Stream destination, long count)
    {
        byte[] buffer = new byte[Math.Min(DefaultBufferSize, count)];
        long remaining = count;

        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = source.Read(buffer, 0, toRead);
            if (read == 0)
            {
                break;
            }

            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    /// <summary>
    /// Copies exactly <paramref name="bytes"/> bytes (ulong overload).
    /// </summary>
    public static void CopyBytes(Stream source, Stream destination, ulong bytes)
        => CopyBytes(source, destination, (long)bytes);

    /// <summary>
    /// Copies exactly <paramref name="bytes"/> bytes (uint overload).
    /// </summary>
    public static void CopyBytes(Stream source, Stream destination, uint bytes)
        => CopyBytes(source, destination, (long)bytes);

    /// <summary>
    /// Skips <paramref name="bytes"/> bytes in the stream by seeking forward.
    /// </summary>
    public static void SkipBytes(Stream stream, ulong bytes)
        => stream.Seek((long)bytes, SeekOrigin.Current);

    /// <summary>
    /// Reads exactly <paramref name="count"/> bytes from the BinaryReader.
    /// Throws <see cref="EndOfStreamException"/> if fewer bytes are available.
    /// </summary>
    public static byte[] ReadExactly(BinaryReader reader, int count)
    {
        byte[] data = reader.ReadBytes(count);
        if (data.Length < count)
        {
            throw new EndOfStreamException(
                $"Expected {count} bytes but got {data.Length}.");
        }

        return data;
    }

    /// <summary>
    /// Reads up to <paramref name="count"/> bytes from the stream, retrying until
    /// the requested count is reached or the stream ends.
    /// </summary>
    public static int ReadFully(Stream stream, byte[] buffer, int offset, int count)
    {
        int totalRead = 0;

        while (totalRead < count)
        {
            int read = stream.Read(buffer, offset + totalRead, count - totalRead);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead;
    }

    /// <summary>
    /// Copies exactly <paramref name="count"/> bytes from <paramref name="source"/>
    /// to <paramref name="destination"/>. Throws <see cref="EndOfStreamException"/>
    /// if the source stream ends before all bytes are copied.
    /// </summary>
    public static void CopyBytesStrict(Stream source, Stream destination, long count, string? errorMessage = null)
    {
        byte[] buffer = new byte[Math.Min(DefaultBufferSize, count)];
        long remaining = count;

        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = source.Read(buffer, 0, toRead);
            if (read <= 0)
            {
                throw new EndOfStreamException(
                    errorMessage ?? "Unexpected end of stream while copying data.");
            }

            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    /// <summary>
    /// Attempts to delete a file. Exceptions are suppressed (best-effort cleanup).
    /// </summary>
    public static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }
}
