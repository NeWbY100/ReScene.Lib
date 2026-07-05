namespace ReScene.SRS;

/// <summary>Tag-format constants shared across MP3/ID3 parsing and container detection.</summary>
internal static class MP3Constants
{
    /// <summary>ID3v2 tag marker "ID3".</summary>
    public const string Id3v2Magic = "ID3";

    /// <summary>Size of the "TAG" marker that opens an ID3v1 tag (3 bytes).</summary>
    public const int Id3v1MagicSize = 3;

    /// <summary>Size of the "LYRICSBEGIN" marker that opens a Lyrics3 tag body (11 bytes).</summary>
    public const int Lyrics3BeginMagicSize = 11;

    /// <summary>First byte of an MP3 frame sync word (0xFF).</summary>
    public const byte SyncByte0 = 0xFF;

    /// <summary>Mask applied to the second byte to check the upper 3 sync bits (0xE0).</summary>
    public const byte SyncMask1 = 0xE0;
}
