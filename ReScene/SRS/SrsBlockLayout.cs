namespace ReScene.SRS;

/// <summary>Layout constants for the generic SRS block header (4-byte FourCC tag + 4-byte LE size field).</summary>
internal static class SrsBlockLayout
{
    /// <summary>
    /// Size of the SRS block header common to Stream, MP3, and RIFF-SRS framing:
    /// 4-byte ASCII FourCC tag + 4-byte little-endian total-size field.
    /// </summary>
    public const int HeaderSize = 8;
}
