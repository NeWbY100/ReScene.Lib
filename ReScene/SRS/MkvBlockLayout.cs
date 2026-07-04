namespace ReScene.SRS;

/// <summary>
/// Byte-layout constants for MKV SimpleBlock / Block elements.
/// </summary>
internal static class MkvBlockLayout
{
    /// <summary>
    /// Fixed overhead bytes between the track-number VINT and the start of frame data
    /// (or lacing header): 2-byte timecode + 1-byte flags.
    /// The full base block-header size is <c>vintLen + FixedHeaderOverhead</c>.
    /// </summary>
    public const int FixedHeaderOverhead = 3;
}
