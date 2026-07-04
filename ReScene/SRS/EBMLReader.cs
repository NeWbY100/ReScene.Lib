namespace ReScene.SRS;

/// <summary>
/// Shared EBML variable-length integer reading utilities for MKV/WebM container parsing.
/// </summary>
internal static class EBMLReader
{
    /// <summary>
    /// Reads an EBML element ID from the stream. The marker bit is preserved in the value.
    /// </summary>
    public static bool TryReadId(Stream stream, out ulong value, out int length)
    {
        value = 0;
        length = 0;
        int first = stream.ReadByte();
        if (first < 0)
        {
            return false;
        }

        int mask = 0x80;
        length = 1;
        while (length <= 8 && (first & mask) == 0)
        {
            mask >>= 1;
            length++;
        }

        if (length > 8)
        {
            return false;
        }

        // For element IDs, keep the marker bit
        value = (ulong)first;
        for (int i = 1; i < length; i++)
        {
            int b = stream.ReadByte();
            if (b < 0)
            {
                return false;
            }

            value = (value << 8) | (uint)b;
        }

        return true;
    }

    /// <summary>
    /// Reads an EBML size value from the stream. The marker bit is masked out.
    /// Also used for VINT values (track numbers, etc.).
    /// </summary>
    public static bool TryReadSize(Stream stream, out ulong value, out int length)
    {
        value = 0;
        length = 0;
        int first = stream.ReadByte();
        if (first < 0)
        {
            return false;
        }

        int mask = 0x80;
        length = 1;
        while (length <= 8 && (first & mask) == 0)
        {
            mask >>= 1;
            length++;
        }

        if (length > 8)
        {
            return false;
        }

        // For sizes, mask out the marker bit
        value = (ulong)(first & (mask - 1));
        for (int i = 1; i < length; i++)
        {
            int b = stream.ReadByte();
            if (b < 0)
            {
                return false;
            }

            value = (value << 8) | (uint)b;
        }

        return true;
    }
}
