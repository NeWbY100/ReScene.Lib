namespace ReScene.SRS;

/// <summary>
/// Parsed SRSF (FileData) payload from an SRS file.
/// Every field stores its absolute byte offset for hex highlighting.
/// </summary>
public class SRSFileDataBlock
{
    /// <summary>
    /// Absolute position of the container frame in the file.
    /// </summary>
    public long BlockPosition
    {
        get; set;
    }

    /// <summary>
    /// Total size including container framing.
    /// </summary>
    public long BlockSize
    {
        get; set;
    }

    /// <summary>
    /// Offset of the container frame header.
    /// </summary>
    public long FrameOffset
    {
        get; set;
    }

    /// <summary>
    /// Size of the container frame header (before SRSF payload).
    /// </summary>
    public int FrameHeaderSize
    {
        get; set;
    }

    /// <summary>
    /// Byte offset of the flags field.
    /// </summary>
    public long FlagsOffset
    {
        get; set;
    }

    /// <summary>
    /// SRSF flags value.
    /// </summary>
    public ushort Flags
    {
        get; set;
    }

    /// <summary>
    /// Byte offset of the application name size field.
    /// </summary>
    public long AppNameSizeOffset
    {
        get; set;
    }

    /// <summary>
    /// Length of the application name string in bytes.
    /// </summary>
    public ushort AppNameSize
    {
        get; set;
    }

    /// <summary>
    /// Byte offset of the application name string.
    /// </summary>
    public long AppNameOffset
    {
        get; set;
    }

    /// <summary>
    /// Name of the application that created the SRS file.
    /// </summary>
    public string AppName { get; set; } = string.Empty;

    /// <summary>
    /// Byte offset of the file name size field.
    /// </summary>
    public long FileNameSizeOffset
    {
        get; set;
    }

    /// <summary>
    /// Length of the file name string in bytes.
    /// </summary>
    public ushort FileNameSize
    {
        get; set;
    }

    /// <summary>
    /// Byte offset of the file name string.
    /// </summary>
    public long FileNameOffset
    {
        get; set;
    }

    /// <summary>
    /// Original sample file name.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Byte offset of the sample size field.
    /// </summary>
    public long SampleSizeOffset
    {
        get; set;
    }

    /// <summary>
    /// Size of the original sample file in bytes.
    /// </summary>
    public ulong SampleSize
    {
        get; set;
    }

    /// <summary>
    /// Byte offset of the CRC32 field.
    /// </summary>
    public long CRC32Offset
    {
        get; set;
    }

    /// <summary>
    /// CRC32 checksum of the original sample file.
    /// </summary>
    public uint CRC32
    {
        get; set;
    }
}
