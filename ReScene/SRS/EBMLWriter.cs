namespace ReScene.SRS;

/// <summary>
/// Shared EBML element/VINT writing utilities for MKV/WebM container output.
/// </summary>
internal static class EBMLWriter
{
    /// <summary>
    /// Encodes <paramref name="value"/> as an EBML variable-length unsigned integer
    /// (size descriptor), using the shortest representation.
    /// </summary>
    public static byte[] MakeEBMLUInt(long value)
    {
        if (value < 0x7F)
        {
            return [(byte)(0x80 | value)];
        }

        if (value < 0x3FFF)
        {
            return [(byte)(0x40 | (value >> 8)), (byte)(value & 0xFF)];
        }

        if (value < 0x1FFFFF)
        {
            return [(byte)(0x20 | (value >> 16)), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF)];
        }

        if (value < 0x0FFFFFFF)
        {
            return [(byte)(0x10 | (value >> 24)), (byte)((value >> 16) & 0xFF), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF)];
        }

        // 5+ bytes
        var result = new List<byte>();
        int width = 5;
        long max = 0x07FFFFFFFF;
        while (value > max && width < 8)
        {
            width++;
            max = (max << 8) | 0xFF;
        }

        byte marker = (byte)(1 << (8 - width));
        result.Add((byte)(marker | (byte)(value >> ((width - 1) * 8))));
        for (int i = width - 2; i >= 0; i--)
        {
            result.Add((byte)((value >> (i * 8)) & 0xFF));
        }

        return [.. result];
    }

    /// <summary>
    /// Encodes an EBML element ID as big-endian bytes (the marker bit is preserved).
    /// </summary>
    public static byte[] MakeEBMLId(ulong id)
    {
        if (id < 0x100)
        {
            return [(byte)id];
        }

        if (id < 0x10000)
        {
            return [(byte)(id >> 8), (byte)(id & 0xFF)];
        }

        if (id < 0x1000000)
        {
            return [(byte)(id >> 16), (byte)((id >> 8) & 0xFF), (byte)(id & 0xFF)];
        }

        return [(byte)(id >> 24), (byte)((id >> 16) & 0xFF), (byte)((id >> 8) & 0xFF), (byte)(id & 0xFF)];
    }

    /// <summary>
    /// Builds a complete EBML element: ID + size VINT + <paramref name="data"/>.
    /// </summary>
    public static byte[] BuildEBMLElement(ulong id, byte[] data)
    {
        byte[] idBytes = MakeEBMLId(id);
        byte[] sizeBytes = MakeEBMLUInt(data.Length);
        byte[] result = new byte[idBytes.Length + sizeBytes.Length + data.Length];
        idBytes.CopyTo(result, 0);
        sizeBytes.CopyTo(result, idBytes.Length);
        data.CopyTo(result, idBytes.Length + sizeBytes.Length);
        return result;
    }

    /// <summary>
    /// Builds just an EBML element header: ID + size VINT (no data).
    /// </summary>
    public static byte[] BuildEBMLElementHeader(ulong id, long dataSize)
    {
        byte[] idBytes = MakeEBMLId(id);
        byte[] sizeBytes = MakeEBMLUInt(dataSize);
        byte[] result = new byte[idBytes.Length + sizeBytes.Length];
        idBytes.CopyTo(result, 0);
        sizeBytes.CopyTo(result, idBytes.Length);
        return result;
    }
}
