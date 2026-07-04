namespace ReScene.SRS;

/// <summary>
/// Constants for RIFF container framing. RIFF uses a 4-byte ASCII FourCC tag followed by
/// a 4-byte little-endian chunk size, so the chunk header is always 8 bytes.
/// </summary>
internal static class RiffFourCC
{
    /// <summary>RIFF container FourCC ("RIFF").</summary>
    public const string Riff = "RIFF";

    /// <summary>
    /// Size of a RIFF chunk header in bytes: 4-byte ASCII FourCC tag + 4-byte LE size field.
    /// </summary>
    public const int ChunkHeaderSize = 8;

    /// <summary>Byte offset of the LE32 size field within a RIFF chunk header (after the 4-byte FourCC).</summary>
    public const int SizeOffset = 4;
}
