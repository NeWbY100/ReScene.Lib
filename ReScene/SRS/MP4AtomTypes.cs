namespace ReScene.SRS;

/// <summary>
/// Layout constants for the ISO BMFF / MP4 atom framing used by the MP4
/// container handler, profiler, rebuilder, and SRS file parser.
/// These model the box header described in ISO 14496-12 §4.2.
/// </summary>
internal static class MP4AtomTypes
{
    /// <summary>4-byte big-endian size field + 4-byte ASCII type field.</summary>
    public const int AtomHeaderSize = 8;

    /// <summary>8-byte base header + 8-byte unsigned 64-bit extended size field.</summary>
    public const int AtomExtendedHeaderSize = 16;

    /// <summary>When size32 == 1, a 64-bit extended size field immediately follows the type.</summary>
    public const int ExtendedSizeSentinel = 1;

    /// <summary>When size32 == 0, the atom extends to the end of the enclosing boundary.</summary>
    public const int ToEndSentinel = 0;

    /// <summary>
    /// Byte offset of the track ID field inside a version-0 tkhd box payload.
    /// Layout: version(1) + flags(3) + creationTime(4) + modificationTime(4) = 12 bytes before trackID.
    /// </summary>
    public const int TkhdTrackIdOffsetV0 = 12;

    /// <summary>
    /// Byte offset of the track ID field inside a version-1 tkhd box payload.
    /// Layout: version(1) + flags(3) + creationTime(8) + modificationTime(8) = 20 bytes before trackID.
    /// </summary>
    public const int TkhdTrackIdOffsetV1 = 20;

    /// <summary>Width of the track ID field in bytes (stored as a 32-bit big-endian integer).</summary>
    public const int TkhdTrackIdFieldSize = 4;

    /// <summary>FourCC type string for the file-type compatibility box ("ftyp").</summary>
    public const string Ftyp = "ftyp";
}
