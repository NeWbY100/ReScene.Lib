namespace ReScene.SRS;

/// <summary>Structural constants for the FLAC container format.</summary>
internal static class FlacConstants
{
    /// <summary>FLAC stream marker "fLaC".</summary>
    public const string Marker = "fLaC";

    /// <summary>Size of the "fLaC" stream marker in bytes.</summary>
    public const int MarkerSize = 4;

    /// <summary>
    /// Size of each FLAC metadata block header in bytes
    /// (isLast+type byte + 3-byte big-endian payload size).
    /// </summary>
    public const int BlockHeaderSize = 4;

    /// <summary>Width of the big-endian payload-size field within a block header.</summary>
    public const int BlockSizeFieldWidth = 3;

    /// <summary>Bit flag in the type byte indicating the last metadata block.</summary>
    public const byte LastBlockFlag = 0x80;

    /// <summary>Mask to extract the block type from the combined type byte.</summary>
    public const byte BlockTypeMask = 0x7F;

    /// <summary>Highest defined standard FLAC metadata block type (PICTURE = 6).</summary>
    public const int MaxStandardType = 6;

    /// <summary>
    /// Upper bound for the SRS-block skip guard during rebuild. Mirrors pyrescene's
    /// <c>srs_flac_blocks &lt;= 3</c> check in <c>flac_rebuild_sample</c>: a bounded counter that stops
    /// the injected SRSF/SRST/fingerprint blocks near the stream start from being copied into the
    /// rebuilt FLAC. The comparison is inclusive, so it tolerates one block beyond the three canonical
    /// SRS blocks — kept exactly as pyrescene defines it for byte-for-byte parity. In practice this
    /// edge is unreachable: the SRS sentinel types 0x73-0x75 cannot collide with a real FLAC metadata
    /// block (valid types are 0-6, plus 127 reserved).
    /// </summary>
    public const int MaxSRSBlockCount = 3;
}
