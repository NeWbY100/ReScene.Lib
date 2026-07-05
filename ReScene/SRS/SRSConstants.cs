namespace ReScene.SRS;

/// <summary>Cross-format SRS constants.</summary>
internal static class SRSConstants
{
    /// <summary>
    /// Sample sizes at or above this threshold (2 GiB) require the <see cref="SRSTFlags.BigFile"/> flag,
    /// switching the SRST DataLength field from 4 bytes to 8 bytes.
    /// </summary>
    public const long BigFileSizeThreshold = 0x80000000L;
}
