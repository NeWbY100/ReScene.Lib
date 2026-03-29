namespace ReScene.SRS;

/// <summary>
/// Container format types for SRS files.
/// </summary>
/// <summary>
/// Container format types for SRS files.
/// </summary>
public enum SRSContainerType
{
    /// <summary>AVI (RIFF) container.</summary>
    AVI,
    /// <summary>Matroska (MKV/WebM) container.</summary>
    MKV,
    /// <summary>MPEG-4 Part 14 (MP4/M4V) container.</summary>
    MP4,
    /// <summary>Windows Media Video (ASF/WMV) container.</summary>
    WMV,
    /// <summary>Free Lossless Audio Codec container.</summary>
    FLAC,
    /// <summary>MPEG Audio Layer III container.</summary>
    MP3,
    /// <summary>Raw stream or MPEG-2 Transport Stream (M2TS) container.</summary>
    Stream
}

/// <summary>
/// Parsed SRSF (FileData) payload from an SRS file.
/// Every field stores its absolute byte offset for hex highlighting.
/// </summary>
public class SrsFileDataBlock
{
    /// <summary>
    /// Absolute position of the container frame in the file.
    /// </summary>
    public long BlockPosition { get; set; }

    /// <summary>
    /// Total size including container framing.
    /// </summary>
    public long BlockSize { get; set; }

    /// <summary>
    /// Offset of the container frame header.
    /// </summary>
    public long FrameOffset { get; set; }

    /// <summary>
    /// Size of the container frame header (before SRSF payload).
    /// </summary>
    public int FrameHeaderSize { get; set; }

    /// <summary>Byte offset of the flags field.</summary>
    public long FlagsOffset { get; set; }
    /// <summary>SRSF flags value.</summary>
    public ushort Flags { get; set; }

    /// <summary>Byte offset of the application name size field.</summary>
    public long AppNameSizeOffset { get; set; }
    /// <summary>Length of the application name string in bytes.</summary>
    public ushort AppNameSize { get; set; }

    /// <summary>Byte offset of the application name string.</summary>
    public long AppNameOffset { get; set; }
    /// <summary>Name of the application that created the SRS file.</summary>
    public string AppName { get; set; } = string.Empty;

    /// <summary>Byte offset of the file name size field.</summary>
    public long FileNameSizeOffset { get; set; }
    /// <summary>Length of the file name string in bytes.</summary>
    public ushort FileNameSize { get; set; }

    /// <summary>Byte offset of the file name string.</summary>
    public long FileNameOffset { get; set; }
    /// <summary>Original sample file name.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Byte offset of the sample size field.</summary>
    public long SampleSizeOffset { get; set; }
    /// <summary>Size of the original sample file in bytes.</summary>
    public ulong SampleSize { get; set; }

    /// <summary>Byte offset of the CRC32 field.</summary>
    public long Crc32Offset { get; set; }
    /// <summary>CRC32 checksum of the original sample file.</summary>
    public uint Crc32 { get; set; }
}

/// <summary>
/// Parsed SRST (TrackData) payload from an SRS file.
/// </summary>
public class SrsTrackDataBlock
{
    /// <summary>
    /// Absolute position of the container frame in the file.
    /// </summary>
    public long BlockPosition { get; set; }

    /// <summary>
    /// Total size including container framing.
    /// </summary>
    public long BlockSize { get; set; }

    /// <summary>
    /// Offset of the container frame header.
    /// </summary>
    public long FrameOffset { get; set; }

    /// <summary>
    /// Size of the container frame header (before SRST payload).
    /// </summary>
    public int FrameHeaderSize { get; set; }

    /// <summary>Byte offset of the flags field.</summary>
    public long FlagsOffset { get; set; }
    /// <summary>SRST flags value.</summary>
    public ushort Flags { get; set; }

    /// <summary>Byte offset of the track number field.</summary>
    public long TrackNumberOffset { get; set; }
    /// <summary>Size of the track number field in bytes (2 or 4).</summary>
    public int TrackNumberFieldSize { get; set; }
    /// <summary>Track number within the container.</summary>
    public uint TrackNumber { get; set; }

    /// <summary>Byte offset of the data length field.</summary>
    public long DataLengthOffset { get; set; }
    /// <summary>Size of the data length field in bytes (4 or 8).</summary>
    public int DataLengthFieldSize { get; set; }
    /// <summary>Total length of the track's stream data in the sample file.</summary>
    public ulong DataLength { get; set; }

    /// <summary>Byte offset of the match offset field.</summary>
    public long MatchOffsetOffset { get; set; }
    /// <summary>Offset in the full media file where this track's data begins.</summary>
    public ulong MatchOffset { get; set; }

    /// <summary>Byte offset of the signature size field.</summary>
    public long SignatureSizeOffset { get; set; }
    /// <summary>Length of the signature in bytes.</summary>
    public ushort SignatureSize { get; set; }

    /// <summary>Byte offset of the signature data.</summary>
    public long SignatureOffset { get; set; }
    /// <summary>First bytes of the track's stream data, used for verification during rebuild.</summary>
    public byte[] Signature { get; set; } = [];
}

/// <summary>
/// Non-SRS container element (for tree display).
/// </summary>
public class SrsContainerChunk
{
    /// <summary>
    /// Absolute position in the file.
    /// </summary>
    public long BlockPosition { get; set; }

    /// <summary>
    /// Total size of the chunk (header + payload).
    /// </summary>
    public long BlockSize { get; set; }

    /// <summary>
    /// Display label (e.g. "RIFF AVI", "LIST movi").
    /// </summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Raw chunk ID/tag (e.g. "RIFF", "LIST", GUID bytes).
    /// </summary>
    public string ChunkId { get; set; } = string.Empty;

    /// <summary>
    /// Size of the chunk header.
    /// </summary>
    public int HeaderSize { get; set; }

    /// <summary>
    /// Size of the payload (excluding header).
    /// </summary>
    public long PayloadSize { get; set; }
}
