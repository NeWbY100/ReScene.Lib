namespace SRRLib;

/// <summary>
/// Container format types for SRS files.
/// </summary>
public enum SRSContainerType
{
    AVI,
    MKV,
    MP4,
    WMV,
    FLAC,
    MP3,
    Stream
}

/// <summary>
/// Parsed SRSF (FileData) payload from an SRS file.
/// Every field stores its absolute byte offset for hex highlighting.
/// </summary>
public class SrsFileDataBlock
{
    /// <summary>Absolute position of the container frame in the file.</summary>
    public long BlockPosition { get; set; }

    /// <summary>Total size including container framing.</summary>
    public long BlockSize { get; set; }

    /// <summary>Offset of the container frame header.</summary>
    public long FrameOffset { get; set; }

    /// <summary>Size of the container frame header (before SRSF payload).</summary>
    public int FrameHeaderSize { get; set; }

    public long FlagsOffset { get; set; }
    public ushort Flags { get; set; }

    public long AppNameSizeOffset { get; set; }
    public ushort AppNameSize { get; set; }

    public long AppNameOffset { get; set; }
    public string AppName { get; set; } = string.Empty;

    public long FileNameSizeOffset { get; set; }
    public ushort FileNameSize { get; set; }

    public long FileNameOffset { get; set; }
    public string FileName { get; set; } = string.Empty;

    public long SampleSizeOffset { get; set; }
    public ulong SampleSize { get; set; }

    public long Crc32Offset { get; set; }
    public uint Crc32 { get; set; }
}

/// <summary>
/// Parsed SRST (TrackData) payload from an SRS file.
/// </summary>
public class SrsTrackDataBlock
{
    /// <summary>Absolute position of the container frame in the file.</summary>
    public long BlockPosition { get; set; }

    /// <summary>Total size including container framing.</summary>
    public long BlockSize { get; set; }

    /// <summary>Offset of the container frame header.</summary>
    public long FrameOffset { get; set; }

    /// <summary>Size of the container frame header (before SRST payload).</summary>
    public int FrameHeaderSize { get; set; }

    public long FlagsOffset { get; set; }
    public ushort Flags { get; set; }

    public long TrackNumberOffset { get; set; }
    public int TrackNumberFieldSize { get; set; }
    public uint TrackNumber { get; set; }

    public long DataLengthOffset { get; set; }
    public int DataLengthFieldSize { get; set; }
    public ulong DataLength { get; set; }

    public long MatchOffsetOffset { get; set; }
    public ulong MatchOffset { get; set; }

    public long SignatureSizeOffset { get; set; }
    public ushort SignatureSize { get; set; }

    public long SignatureOffset { get; set; }
    public byte[] Signature { get; set; } = [];
}

/// <summary>
/// Non-SRS container element (for tree display).
/// </summary>
public class SrsContainerChunk
{
    /// <summary>Absolute position in the file.</summary>
    public long BlockPosition { get; set; }

    /// <summary>Total size of the chunk (header + payload).</summary>
    public long BlockSize { get; set; }

    /// <summary>Display label (e.g. "RIFF AVI", "LIST movi").</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Raw chunk ID/tag (e.g. "RIFF", "LIST", GUID bytes).</summary>
    public string ChunkId { get; set; } = string.Empty;

    /// <summary>Size of the chunk header.</summary>
    public int HeaderSize { get; set; }

    /// <summary>Size of the payload (excluding header).</summary>
    public long PayloadSize { get; set; }
}
