namespace ReScene.RAR.Decompression;

/// <summary>
/// RAR compression methods.
/// </summary>
public enum RARMethod
{
    /// <summary>Store (no compression)</summary>
    Store = 0x30,
    /// <summary>Fastest compression</summary>
    Fastest = 0x31,
    /// <summary>Fast compression</summary>
    Fast = 0x32,
    /// <summary>Normal compression</summary>
    Normal = 0x33,
    /// <summary>Good compression</summary>
    Good = 0x34,
    /// <summary>Best compression</summary>
    Best = 0x35
}

/// <summary>
/// RAR format versions.
/// </summary>
public enum RARVersion
{
    /// <summary>RAR 1.5</summary>
    RAR15 = 15,
    /// <summary>RAR 2.0</summary>
    RAR20 = 20,
    /// <summary>RAR 2.9/3.x</summary>
    RAR29 = 29,
    /// <summary>RAR 5.x</summary>
    RAR50 = 50
}

/// <summary>
/// Facade class for RAR decompression.
/// Automatically selects the appropriate algorithm based on RAR version and compression method.
/// Supports both LZSS and PPMd compression natively in C#.
/// </summary>
public static class RARDecompressor
{
    /// <summary>
    /// Decompresses RAR data.
    /// </summary>
    /// <param name="compressedData">The compressed data</param>
    /// <param name="uncompressedSize">Expected uncompressed size</param>
    /// <param name="method">RAR compression method</param>
    /// <param name="version">RAR format version</param>
    /// <returns>Decompressed data, or null on failure</returns>
    public static byte[]? Decompress(byte[] compressedData, int uncompressedSize, RARMethod method, RARVersion version = RARVersion.RAR29)
    {
        if (compressedData == null || compressedData.Length == 0)
            return null;

        if (uncompressedSize <= 0)
            return null;

        // Store method - no compression
        if (method == RARMethod.Store)
        {
            if (compressedData.Length < uncompressedSize)
                return null;

            byte[] result = new byte[uncompressedSize];
            Array.Copy(compressedData, result, uncompressedSize);
            return result;
        }

        // Select decompressor based on version
        return version switch
        {
            RARVersion.RAR50 => DecompressRAR50(compressedData, uncompressedSize),
            RARVersion.RAR29 => DecompressRAR29(compressedData, uncompressedSize),
            RARVersion.RAR20 => DecompressRAR20(compressedData, uncompressedSize),
            _ => DecompressRAR29(compressedData, uncompressedSize) // Default to RAR 2.9
        };
    }

    /// <summary>
    /// Decompresses RAR 2.0 data.
    /// </summary>
    private static byte[]? DecompressRAR20(byte[] compressedData, int uncompressedSize)
    {
        var unpacker = new Unpack20();
        return unpacker.Decompress(compressedData, uncompressedSize);
    }

    /// <summary>
    /// Decompresses RAR 2.9/3.x data (supports both LZSS and PPMd).
    /// </summary>
    private static byte[]? DecompressRAR29(byte[] compressedData, int uncompressedSize)
    {
        var unpacker = new Unpack29();
        return unpacker.Decompress(compressedData, uncompressedSize);
    }

    /// <summary>
    /// Decompresses RAR 5.x data.
    /// </summary>
    private static byte[]? DecompressRAR50(byte[] compressedData, int uncompressedSize)
    {
        var unpacker = new Unpack50();
        return unpacker.Decompress(compressedData, uncompressedSize);
    }

    /// <summary>
    /// Decompresses a RAR comment block.
    /// </summary>
    /// <param name="compressedData">Compressed comment data</param>
    /// <param name="uncompressedSize">Expected uncompressed size</param>
    /// <param name="method">Compression method from RAR header</param>
    /// <param name="isRAR5">True if this is a RAR5 format archive</param>
    /// <returns>Decompressed comment text, or null on failure</returns>
    public static string? DecompressComment(byte[] compressedData, int uncompressedSize, byte method, bool isRAR5 = false)
    {
        RARMethod rarMethod = (RARMethod)method;
        RARVersion version = isRAR5 ? RARVersion.RAR50 : RARVersion.RAR29;

        return DecompressComment(compressedData, uncompressedSize, rarMethod, version);
    }

    /// <summary>
    /// Decompresses a RAR comment block and returns raw bytes (for exact reconstruction).
    /// </summary>
    /// <param name="compressedData">Compressed comment data</param>
    /// <param name="uncompressedSize">Expected uncompressed size</param>
    /// <param name="method">Compression method from RAR header</param>
    /// <param name="isRAR5">True if this is a RAR5 format archive</param>
    /// <returns>Decompressed comment bytes, or null on failure</returns>
    public static byte[]? DecompressCommentBytes(byte[] compressedData, int uncompressedSize, byte method, bool isRAR5 = false)
    {
        RARMethod rarMethod = (RARMethod)method;
        RARVersion version = isRAR5 ? RARVersion.RAR50 : RARVersion.RAR29;

        return Decompress(compressedData, uncompressedSize, rarMethod, version);
    }

    /// <summary>
    /// Decompresses a RAR comment block with explicit version.
    /// </summary>
    /// <param name="compressedData">Compressed comment data</param>
    /// <param name="uncompressedSize">Expected uncompressed size</param>
    /// <param name="method">Compression method</param>
    /// <param name="version">RAR format version (from UnpVer field)</param>
    /// <returns>Decompressed comment text, or null on failure</returns>
    public static string? DecompressComment(byte[] compressedData, int uncompressedSize, RARMethod method, RARVersion version)
    {
        byte[]? decompressed = Decompress(compressedData, uncompressedSize, method, version);
        if (decompressed == null)
            return null;

        // Comments are typically stored as OEM or UTF-8 encoded text
        try
        {
            // Try UTF-8 first
            string text = System.Text.Encoding.UTF8.GetString(decompressed);
            // If it contains the replacement character, try OEM encoding
            if (text.Contains('\uFFFD'))
            {
                // Use code page 437 (OEM) as fallback
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                text = System.Text.Encoding.GetEncoding(437).GetString(decompressed);
            }
            return text.TrimEnd('\0');
        }
        catch
        {
            return null;
        }
    }
}
