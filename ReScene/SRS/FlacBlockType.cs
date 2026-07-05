namespace ReScene.SRS;

/// <summary>Standard FLAC metadata block types (type nibble, 0-6).</summary>
internal enum FlacBlockType
{
    Streaminfo = 0,
    Padding = 1,
    Application = 2,
    Seektable = 3,
    VorbisComment = 4,
    Cuesheet = 5,
    Picture = 6,
}
