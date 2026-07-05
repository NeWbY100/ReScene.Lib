namespace ReScene.SRS;

/// <summary>
/// Helper methods for reading EBML variable-length integers (VINTs).
/// </summary>
internal static class EBMLVInt
{
    /// <summary>
    /// Reads an unsigned EBML VINT from the given data.
    /// The marker bit is masked out to produce the actual value.
    /// </summary>
    /// <param name="data">
    /// Data starting at the VINT.
    /// </param>
    /// <returns>
    /// The unsigned value and the number of bytes consumed.
    /// </returns>
    public static (long value, int length) ReadUnsigned(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1)
        {
            return (0, 0);
        }

        byte first = data[0];
        int vintLen = GetVintLength(first);
        if (vintLen == 0 || vintLen > data.Length)
        {
            return (0, 0);
        }

        // Mask out the marker bit from the first byte
        long value = first & (0xFF >> vintLen);
        for (int i = 1; i < vintLen; i++)
        {
            value = (value << 8) | data[i];
        }

        return (value, vintLen);
    }

    /// <summary>
    /// Reads an EBML element ID from the given data. Unlike size/value VINTs, the
    /// marker (length-descriptor) bit is preserved in the returned value.
    /// </summary>
    /// <param name="data">
    /// Data starting at the element ID.
    /// </param>
    /// <returns>
    /// The element ID (marker bit kept) and the number of bytes consumed,
    /// or (0, 0) when the data does not contain a valid ID.
    /// </returns>
    public static (ulong id, int length) ReadId(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1)
        {
            return (0, 0);
        }

        byte first = data[0];
        int idLen = GetVintLength(first);
        if (idLen == 0 || idLen > data.Length)
        {
            return (0, 0);
        }

        ulong id = first;
        for (int i = 1; i < idLen; i++)
        {
            id = (id << 8) | data[i];
        }

        return (id, idLen);
    }

    /// <summary>
    /// Reads a signed EBML VINT from the given data.
    /// First reads as unsigned, then subtracts the bias to convert to signed.
    /// The bias for an N-byte VINT is (2^(7*N - 1) - 1).
    /// </summary>
    /// <param name="data">
    /// Data starting at the VINT.
    /// </param>
    /// <returns>
    /// The signed value and the number of bytes consumed.
    /// </returns>
    public static (long signedValue, int length) ReadSigned(ReadOnlySpan<byte> data)
    {
        (long unsignedVal, int vintLen) = ReadUnsigned(data);
        if (vintLen == 0)
        {
            return (0, 0);
        }

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
            {
                return i + 1;
            }
        }

        return 0; // Invalid: no marker bit set
    }
}
