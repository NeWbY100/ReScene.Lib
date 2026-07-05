namespace ReScene.SRS;

/// <summary>Layout constants for SRST (SRS track) blocks.</summary>
internal static class SRSTLayout
{
    /// <summary>
    /// Track numbers below this threshold fit in a 2-byte field; at or above use 4 bytes
    /// (indicated by the <see cref="SRSTFlags.BigTrackNumber"/> flag).
    /// </summary>
    public const int TrackNumberWidthThreshold = 0x10000;
}
