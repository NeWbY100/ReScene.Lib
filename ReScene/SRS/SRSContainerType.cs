namespace ReScene.SRS;

/// <summary>
/// Container format types for SRS files.
/// </summary>
public enum SRSContainerType
{
    /// <summary>
    /// AVI (RIFF) container.
    /// </summary>
    AVI,

    /// <summary>
    /// Matroska (MKV/WebM) container.
    /// </summary>
    MKV,

    /// <summary>
    /// MPEG-4 Part 14 (MP4/M4V) container.
    /// </summary>
    MP4,

    /// <summary>
    /// Windows Media Video (ASF/WMV) container.
    /// </summary>
    WMV,

    /// <summary>
    /// Free Lossless Audio Codec container.
    /// </summary>
    FLAC,

    /// <summary>
    /// MPEG Audio Layer III container.
    /// </summary>
    MP3,

    /// <summary>
    /// Raw stream or MPEG-2 Transport Stream (M2TS) container.
    /// </summary>
    Stream
}
