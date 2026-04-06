namespace ReScene.SRS;

/// <summary>
/// Scans streams for byte signature patterns using a sliding window algorithm.
/// </summary>
internal static class SignatureScanner
{
    private const int DefaultBufferSize = 64 * 1024;

    /// <summary>
    /// Searches for a byte signature in a region of the stream using a sliding window.
    /// </summary>
    /// <param name="stream">The stream to search.</param>
    /// <param name="signature">The byte pattern to find.</param>
    /// <param name="regionStart">Start offset of the search region.</param>
    /// <param name="regionEnd">End offset of the search region.</param>
    /// <param name="onProgress">Optional callback reporting (bytesScanned, totalBytes, percent).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The offset where the signature was found, or -1 if not found.</returns>
    public static long Scan(
        Stream stream,
        byte[] signature,
        long regionStart,
        long regionEnd,
        Action<long, long, int>? onProgress = null,
        CancellationToken ct = default)
    {
        if (regionEnd - regionStart < signature.Length)
        {
            return -1;
        }

        int bufSize = Math.Max(DefaultBufferSize, signature.Length * 2);
        byte[] buffer = new byte[bufSize];
        long position = regionStart;
        int carry = 0;
        long totalRegion = regionEnd - regionStart;
        int lastPercent = -1;

        while (position < regionEnd)
        {
            ct.ThrowIfCancellationRequested();

            stream.Position = position - carry;
            int toRead = (int)Math.Min(bufSize, regionEnd - position + carry);
            int bytesRead = StreamUtilities.ReadFully(stream, buffer, 0, toRead);
            if (bytesRead < signature.Length)
            {
                break;
            }

            // Report scan progress
            long scanned = position - regionStart;
            if (totalRegion > 0 && onProgress is not null)
            {
                int percent = (int)(scanned * 100 / totalRegion);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    onProgress(scanned, totalRegion, percent);
                }
            }

            // Search within the buffer
            int searchLimit = bytesRead - signature.Length;
            for (int i = 0; i <= searchLimit; i++)
            {
                if (buffer.AsSpan(i, signature.Length).SequenceEqual(signature))
                {
                    return position - carry + i;
                }
            }

            // Advance, keeping overlap for boundary matches
            position = position - carry + bytesRead;
            carry = signature.Length - 1;
            if (carry > bytesRead)
            {
                carry = bytesRead;
            }
        }

        return -1;
    }

    /// <summary>
    /// Checks whether the signature matches at the given exact offset.
    /// </summary>
    public static bool MatchesAt(Stream stream, byte[] signature, long offset)
    {
        if (signature.Length == 0)
        {
            return true;
        }

        if (offset < 0 || offset + signature.Length > stream.Length)
        {
            return false;
        }

        stream.Position = offset;
        byte[] buffer = new byte[signature.Length];
        int read = StreamUtilities.ReadFully(stream, buffer, 0, buffer.Length);
        return read == signature.Length && buffer.AsSpan().SequenceEqual(signature);
    }
}
