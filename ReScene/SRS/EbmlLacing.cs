namespace ReScene.SRS;

/// <summary>
/// Lacing mode for MKV Block/SimpleBlock elements.
/// Values correspond to the 2-bit lacing field in the block flags byte (bits 1-2).
/// </summary>
public enum EbmlLaceType : byte
{
    /// <summary>
    /// No lacing - single frame per block.
    /// </summary>
    None = 0,

    /// <summary>
    /// Xiph lacing - 0xFF-terminated sizes.
    /// </summary>
    Xiph = 2,

    /// <summary>
    /// Fixed-size lacing - all frames are equal size.
    /// </summary>
    Fixed = 4,

    /// <summary>
    /// EBML lacing - delta-encoded sizes using EBML VINTs.
    /// </summary>
    Ebml = 6
}

/// <summary>
/// Parses lacing headers from MKV Block/SimpleBlock elements to determine
/// individual frame sizes within a laced block.
/// </summary>
public static class EbmlLacing
{
    /// <summary>
    /// Parses the lacing information from block data to get individual frame sizes.
    /// </summary>
    /// <param name="data">
    /// Block data starting at the lacing header (after track number, timecode, and flags byte).
    /// For <see cref="EbmlLaceType.None"/>, this parameter is unused.
    /// </param>
    /// <param name="laceType">The lacing type extracted from the block flags byte.</param>
    /// <param name="totalDataLength">
    /// Total length of the frame data area (block data size minus the block header: track VINT + 2 timecode + 1 flags).
    /// </param>
    /// <returns>Array of frame sizes and the number of bytes consumed by the lacing header.</returns>
    public static (int[] frameSizes, int bytesConsumed) GetFrameLengths(
        ReadOnlySpan<byte> data, EbmlLaceType laceType, int totalDataLength)
    {
        int bytesConsumed = 0;
        int frameCount = 1;

        if (laceType != EbmlLaceType.None)
        {
            if (data.Length < 1)
                return ([totalDataLength], 0);

            frameCount = data[0] + 1;
            bytesConsumed = 1;
        }

        int[] frameSizes = new int[frameCount];

        switch (laceType)
        {
            case EbmlLaceType.None:
                frameSizes[0] = totalDataLength;
                break;

            case EbmlLaceType.Fixed:
                int fixedSize = totalDataLength / frameCount;
                for (int i = 0; i < frameCount; i++)
                    frameSizes[i] = fixedSize;
                break;

            case EbmlLaceType.Xiph:
                for (int i = 0; i < frameCount; i++)
                {
                    if (i < frameCount - 1)
                    {
                        // Read 0xFF bytes, summing them, until a non-0xFF byte
                        int size = 0;
                        while (bytesConsumed < data.Length)
                        {
                            byte b = data[bytesConsumed];
                            bytesConsumed++;
                            size += b;
                            if (b != 0xFF)
                                break;
                        }
                        frameSizes[i] = size;
                    }
                    else
                    {
                        // Last frame: remaining bytes after lacing header and previous frames
                        int usedByFrames = 0;
                        for (int j = 0; j < i; j++)
                            usedByFrames += frameSizes[j];
                        frameSizes[i] = totalDataLength - bytesConsumed - usedByFrames;
                    }
                }
                break;

            case EbmlLaceType.Ebml:
                for (int i = 0; i < frameCount; i++)
                {
                    if (i == 0)
                    {
                        // First frame: read as unsigned EBML VINT
                        var (value, vintLen) = EbmlVInt.ReadUnsigned(data[bytesConsumed..]);
                        frameSizes[0] = (int)value;
                        bytesConsumed += vintLen;
                    }
                    else if (i < frameCount - 1)
                    {
                        // Subsequent frames (not last): read signed EBML VINT delta
                        var (delta, vintLen) = EbmlVInt.ReadSigned(data[bytesConsumed..]);
                        frameSizes[i] = frameSizes[i - 1] + (int)delta;
                        bytesConsumed += vintLen;
                    }
                    else
                    {
                        // Last frame: remaining bytes
                        int usedByFrames = 0;
                        for (int j = 0; j < i; j++)
                            usedByFrames += frameSizes[j];
                        frameSizes[i] = totalDataLength - bytesConsumed - usedByFrames;
                    }
                }
                break;
        }

        return (frameSizes, bytesConsumed);
    }
}

/// <summary>
/// Helper methods for reading EBML variable-length integers (VINTs).
/// </summary>
public static class EbmlVInt
{
    /// <summary>
    /// Reads an unsigned EBML VINT from the given data.
    /// The marker bit is masked out to produce the actual value.
    /// </summary>
    /// <param name="data">Data starting at the VINT.</param>
    /// <returns>The unsigned value and the number of bytes consumed.</returns>
    public static (long value, int length) ReadUnsigned(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1)
            return (0, 0);

        byte first = data[0];
        int vintLen = GetVintLength(first);
        if (vintLen == 0 || vintLen > data.Length)
            return (0, 0);

        // Mask out the marker bit from the first byte
        long value = first & (0xFF >> vintLen);
        for (int i = 1; i < vintLen; i++)
            value = (value << 8) | data[i];

        return (value, vintLen);
    }

    /// <summary>
    /// Reads a signed EBML VINT from the given data.
    /// First reads as unsigned, then subtracts the bias to convert to signed.
    /// The bias for an N-byte VINT is (2^(7*N - 1) - 1).
    /// </summary>
    /// <param name="data">Data starting at the VINT.</param>
    /// <returns>The signed value and the number of bytes consumed.</returns>
    public static (long signedValue, int length) ReadSigned(ReadOnlySpan<byte> data)
    {
        var (unsignedVal, vintLen) = ReadUnsigned(data);
        if (vintLen == 0)
            return (0, 0);

        // Bias: (2^(7*N - 1) - 1)
        // For 1-byte VINT: 2^6 - 1 = 63
        // For 2-byte VINT: 2^13 - 1 = 8191
        // For 3-byte VINT: 2^20 - 1 = 1048575
        // For 4-byte VINT: 2^27 - 1 = 134217727
        long bias = (1L << (7 * vintLen - 1)) - 1;
        long signedVal = unsignedVal - bias;

        return (signedVal, vintLen);
    }

    /// <summary>
    /// Determines the length (in bytes) of a VINT based on its first byte (length descriptor).
    /// </summary>
    private static int GetVintLength(byte firstByte)
    {
        for (int i = 0; i < 8; i++)
        {
            if ((firstByte & (0x80 >> i)) != 0)
                return i + 1;
        }
        return 0; // Invalid: no marker bit set
    }
}
