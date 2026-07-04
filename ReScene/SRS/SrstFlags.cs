namespace ReScene.SRS;

/// <summary>Flags field in an SRST (SRS track data) block header.</summary>
[Flags]
internal enum SrstFlags : ushort
{
    None = 0,
    /// <summary>DataLength field is 8 bytes (64-bit) instead of 4 bytes (sample ≥ 2 GiB).</summary>
    BigFile = 0x4,
    /// <summary>TrackNumber field is 4 bytes (32-bit) instead of 2 bytes (track number ≥ 0x10000).</summary>
    BigTrackNumber = 0x8,
}
