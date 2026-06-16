namespace ReScene.SRS;

/// <summary>
/// Detects and restores MKV header stripping compression (ContentCompAlgo = 3).
/// When header stripping is used, common header bytes are removed from each frame
/// and stored once in the TrackEntry's ContentCompSettings element.
/// </summary>
internal static class EBMLHeaderStripping
{
    // EBML element IDs for header stripping detection within a TrackEntry
    private const ulong IdContentEncodings = 0x6D80;
    private const ulong IdContentEncoding = 0x6240;
    private const ulong IdContentCompression = 0x5034;
    private const ulong IdContentCompAlgo = 0x4254;
    private const ulong IdContentCompSettings = 0x4255;

    /// <summary>
    /// Detects if track entry data uses header stripping compression (ContentCompAlgo = 3).
    /// Returns the stripped header bytes if found, null otherwise.
    /// </summary>
    /// <param name="trackEntryData">
    /// The raw data of a TrackEntry element (children only, not including the TrackEntry element header itself).
    /// </param>
    /// <returns>
    /// The stripped header bytes, or <see langword="null"/> if header stripping is not used.
    /// </returns>
    public static byte[]? DetectStrippedHeader(ReadOnlySpan<byte> trackEntryData)
    {
        // Walk top-level children of TrackEntry looking for ContentEncodings (0x6D80)
        int pos = 0;
        while (TryNextChild(trackEntryData, ref pos, out ulong elemId, out ReadOnlySpan<byte> child))
        {
            if (elemId == IdContentEncodings)
            {
                // Found ContentEncodings - search inside it
                return SearchContentEncodings(child);
            }
        }

        return null;
    }

    /// <summary>
    /// Prepends the stripped header to a frame's data, restoring the original frame content.
    /// </summary>
    /// <param name="strippedHeader">
    /// The header bytes that were stripped.
    /// </param>
    /// <param name="frameData">
    /// The frame data without the stripped header.
    /// </param>
    /// <returns>
    /// The restored frame data with the header prepended.
    /// </returns>
    public static byte[] RestoreFrame(byte[] strippedHeader, ReadOnlySpan<byte> frameData)
    {
        byte[] result = new byte[strippedHeader.Length + frameData.Length];
        strippedHeader.CopyTo(result, 0);
        frameData.CopyTo(result.AsSpan(strippedHeader.Length));
        return result;
    }

    private static byte[]? SearchContentEncodings(ReadOnlySpan<byte> data)
    {
        // Look for ContentEncoding (0x6240) children
        int pos = 0;
        while (TryNextChild(data, ref pos, out ulong elemId, out ReadOnlySpan<byte> child))
        {
            if (elemId == IdContentEncoding)
            {
                byte[]? result = SearchContentEncoding(child);
                if (result != null)
                {
                    return result;
                }
            }
        }

        return null;
    }

    private static byte[]? SearchContentEncoding(ReadOnlySpan<byte> data)
    {
        // Look for ContentCompression (0x5034) children
        int pos = 0;
        while (TryNextChild(data, ref pos, out ulong elemId, out ReadOnlySpan<byte> child))
        {
            if (elemId == IdContentCompression)
            {
                byte[]? result = SearchContentCompression(child);
                if (result != null)
                {
                    return result;
                }
            }
        }

        return null;
    }

    private static byte[]? SearchContentCompression(ReadOnlySpan<byte> data)
    {
        // Look for ContentCompAlgo (0x4254) = 3 and ContentCompSettings (0x4255)
        bool isHeaderStripping = false;
        byte[]? settings = null;

        int pos = 0;
        while (TryNextChild(data, ref pos, out ulong elemId, out ReadOnlySpan<byte> child))
        {
            if (elemId == IdContentCompAlgo)
            {
                // Read the algorithm value
                long algo = ReadEBMLUIntValue(child);
                isHeaderStripping = algo == 3;
            }
            else if (elemId == IdContentCompSettings)
            {
                settings = child.ToArray();
            }
        }

        return isHeaderStripping ? settings : null;
    }

    /// <summary>
    /// Reads the next child element (ID + size VINT + bounded data) starting at
    /// <paramref name="pos"/>, advancing <paramref name="pos"/> past the child's data.
    /// Returns <see langword="false"/> when no further valid child can be read.
    /// </summary>
    private static bool TryNextChild(ReadOnlySpan<byte> data, ref int pos, out ulong id, out ReadOnlySpan<byte> child)
    {
        id = 0;
        child = default;

        if (pos >= data.Length)
        {
            return false;
        }

        (ulong elemId, int idLen) = EBMLVInt.ReadId(data[pos..]);
        if (idLen == 0)
        {
            return false;
        }

        pos += idLen;

        (long dataSize, int sizeLen) = EBMLVInt.ReadUnsigned(data[pos..]);
        if (sizeLen == 0)
        {
            return false;
        }

        pos += sizeLen;

        int elemDataLen = (int)Math.Min(dataSize, data.Length - pos);
        id = elemId;
        child = data.Slice(pos, elemDataLen);
        pos += elemDataLen;
        return true;
    }

    /// <summary>
    /// Reads an unsigned integer value from raw EBML element data (big-endian, no VINT encoding).
    /// </summary>
    private static long ReadEBMLUIntValue(ReadOnlySpan<byte> data)
    {
        long value = 0;
        for (int i = 0; i < data.Length; i++)
        {
            value = (value << 8) | data[i];
        }

        return value;
    }
}
