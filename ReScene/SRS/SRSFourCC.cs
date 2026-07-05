namespace ReScene.SRS;

/// <summary>
/// FourCC string constants for SRS-specific block types used across all container formats.
/// These appear as ASCII tags in RIFF chunks, MP4 atoms, MP3/Stream framing, and string
/// comparisons during parsing.
/// </summary>
internal static class SRSFourCC
{
    /// <summary>SRS file-data block tag ("SRSF").</summary>
    public const string SRSFile = "SRSF";

    /// <summary>SRS track-data block tag ("SRST").</summary>
    public const string SRSTrack = "SRST";

    /// <summary>SRS padding block tag ("SRSP").</summary>
    public const string SRSPadding = "SRSP";
}
