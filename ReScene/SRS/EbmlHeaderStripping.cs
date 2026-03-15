namespace ReScene.SRS;

/// <summary>
/// Detects and restores MKV header stripping compression (ContentCompAlgo = 3).
/// When header stripping is used, common header bytes are removed from each frame
/// and stored once in the TrackEntry's ContentCompSettings element.
/// </summary>
public static class EbmlHeaderStripping
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
    public static byte[]? DetectStrippedHeader(ReadOnlySpan<byte> trackEntryData)
    {
        // Walk top-level children of TrackEntry looking for ContentEncodings (0x6D80)
        int pos = 0;
        while (pos < trackEntryData.Length)
        {
            var (elemId, idLen) = ReadElementId(trackEntryData[pos..]);
            if (idLen == 0) break;
            pos += idLen;

            var (dataSize, sizeLen) = EbmlVInt.ReadUnsigned(trackEntryData[pos..]);
            if (sizeLen == 0) break;
            pos += sizeLen;

            int elemDataLen = (int)Math.Min(dataSize, trackEntryData.Length - pos);

            if (elemId == IdContentEncodings)
            {
                // Found ContentEncodings - search inside it
                return SearchContentEncodings(trackEntryData.Slice(pos, elemDataLen));
            }

            pos += elemDataLen;
        }

        return null;
    }

    /// <summary>
    /// Prepends the stripped header to a frame's data, restoring the original frame content.
    /// </summary>
    /// <param name="strippedHeader">The header bytes that were stripped.</param>
    /// <param name="frameData">The frame data without the stripped header.</param>
    /// <returns>The restored frame data with the header prepended.</returns>
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
        while (pos < data.Length)
        {
            var (elemId, idLen) = ReadElementId(data[pos..]);
            if (idLen == 0) break;
            pos += idLen;

            var (dataSize, sizeLen) = EbmlVInt.ReadUnsigned(data[pos..]);
            if (sizeLen == 0) break;
            pos += sizeLen;

            int elemDataLen = (int)Math.Min(dataSize, data.Length - pos);

            if (elemId == IdContentEncoding)
            {
                var result = SearchContentEncoding(data.Slice(pos, elemDataLen));
                if (result != null) return result;
            }

            pos += elemDataLen;
        }

        return null;
    }

    private static byte[]? SearchContentEncoding(ReadOnlySpan<byte> data)
    {
        // Look for ContentCompression (0x5034) children
        int pos = 0;
        while (pos < data.Length)
        {
            var (elemId, idLen) = ReadElementId(data[pos..]);
            if (idLen == 0) break;
            pos += idLen;

            var (dataSize, sizeLen) = EbmlVInt.ReadUnsigned(data[pos..]);
            if (sizeLen == 0) break;
            pos += sizeLen;

            int elemDataLen = (int)Math.Min(dataSize, data.Length - pos);

            if (elemId == IdContentCompression)
            {
                var result = SearchContentCompression(data.Slice(pos, elemDataLen));
                if (result != null) return result;
            }

            pos += elemDataLen;
        }

        return null;
    }

    private static byte[]? SearchContentCompression(ReadOnlySpan<byte> data)
    {
        // Look for ContentCompAlgo (0x4254) = 3 and ContentCompSettings (0x4255)
        bool isHeaderStripping = false;
        byte[]? settings = null;

        int pos = 0;
        while (pos < data.Length)
        {
            var (elemId, idLen) = ReadElementId(data[pos..]);
            if (idLen == 0) break;
            pos += idLen;

            var (dataSize, sizeLen) = EbmlVInt.ReadUnsigned(data[pos..]);
            if (sizeLen == 0) break;
            pos += sizeLen;

            int elemDataLen = (int)Math.Min(dataSize, data.Length - pos);

            if (elemId == IdContentCompAlgo)
            {
                // Read the algorithm value
                long algo = ReadEbmlUIntValue(data.Slice(pos, elemDataLen));
                isHeaderStripping = algo == 3;
            }
            else if (elemId == IdContentCompSettings)
            {
                settings = data.Slice(pos, elemDataLen).ToArray();
            }

            pos += elemDataLen;
        }

        return isHeaderStripping ? settings : null;
    }

    /// <summary>
    /// Reads an EBML element ID (preserves the marker bit, unlike size VINTs).
    /// </summary>
    private static (ulong id, int length) ReadElementId(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1)
            return (0, 0);

        byte first = data[0];
        int idLen = 0;
        for (int i = 0; i < 8; i++)
        {
            if ((first & (0x80 >> i)) != 0)
            {
                idLen = i + 1;
                break;
            }
        }
        if (idLen == 0 || idLen > data.Length)
            return (0, 0);

        ulong id = first;
        for (int i = 1; i < idLen; i++)
            id = (id << 8) | data[i];

        return (id, idLen);
    }

    /// <summary>
    /// Reads an unsigned integer value from raw EBML element data (big-endian, no VINT encoding).
    /// </summary>
    private static long ReadEbmlUIntValue(ReadOnlySpan<byte> data)
    {
        long value = 0;
        for (int i = 0; i < data.Length; i++)
            value = (value << 8) | data[i];
        return value;
    }
}
