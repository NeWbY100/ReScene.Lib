namespace ReScene.SRS;

/// <summary>
/// Shared EBML element-ID constants and container classification for MKV/WebM parsing.
/// </summary>
internal static class EBMLIds
{
    public const ulong EBML = 0x1A45DFA3;
    public const ulong Segment = 0x18538067;
    public const ulong SeekHead = 0x114D9B74;
    public const ulong Info = 0x1549A966;
    public const ulong Cluster = 0x1F43B675;
    public const ulong Tracks = 0x1654AE6B;
    public const ulong TrackEntry = 0xAE;
    public const ulong TrackNumber = 0xD7;
    public const ulong ContentEncodings = 0x6D80;
    public const ulong ContentEncoding = 0x6240;
    public const ulong ContentCompression = 0x5034;
    public const ulong ContentCompAlgo = 0x4254;
    public const ulong ContentCompSettings = 0x4255;
    public const ulong BlockGroup = 0xA0;
    public const ulong Block = 0xA1;
    public const ulong SimpleBlock = 0xA3;
    public const ulong Attachments = 0x1941A469;
    public const ulong AttachedFile = 0x61A7;
    public const ulong Cues = 0x1C53BB6B;
    public const ulong Chapters = 0x1043A770;
    public const ulong Tags = 0x1254C367;
    public const ulong ReSampleContainer = 0x1F697576;
    public const ulong ResampleFile = 0x6A75;  // SRSF
    public const ulong ResampleTrack = 0x6B75;  // SRST

    /// <summary>
    /// Container element IDs that are stepped into (they hold child elements, not leaf data).
    /// </summary>
    public static bool IsContainer(ulong id) => id is
        Segment or
        Cluster or
        Tracks or
        TrackEntry or
        ContentEncodings or
        ContentEncoding or
        ContentCompression or
        BlockGroup or
        Attachments or
        AttachedFile;
}

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

        return result.ToArray();
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
