namespace ReScene.RAR;

/// <summary>
/// RAR 5.0 file header info.
/// </summary>
public class RAR5FileInfo
{
    /// <summary>
    /// File flags.
    /// </summary>
    public ulong FileFlags
    {
        get; set;
    }

    /// <summary>
    /// Unpacked size.
    /// </summary>
    public ulong UnpackedSize
    {
        get; set;
    }

    /// <summary>
    /// File attributes.
    /// </summary>
    public ulong Attributes
    {
        get; set;
    }

    /// <summary>
    /// Modification time (Unix timestamp).
    /// </summary>
    public uint? ModificationTime
    {
        get; set;
    }

    /// <summary>
    /// File CRC32.
    /// </summary>
    public uint? FileCRC
    {
        get; set;
    }

    /// <summary>
    /// Compression info (version, solid, method, dict size).
    /// </summary>
    public ulong CompressionInfo
    {
        get; set;
    }

    /// <summary>
    /// Host OS.
    /// </summary>
    public ulong HostOS
    {
        get; set;
    }

    /// <summary>
    /// File name.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// True if this is a directory.
    /// </summary>
    public bool IsDirectory => (FileFlags & (ulong)RAR5FileFlags.Directory) != 0;

    /// <summary>
    /// True if data is stored uncompressed.
    /// </summary>
    public bool IsStored => CompressionMethod == 0;

    /// <summary>
    /// Compression method (0-5).
    /// </summary>
    public int CompressionMethod => (int)((CompressionInfo >> RAR5Format.CompInfoMethodShift) & RAR5Format.CompInfoMethodMask);

    /// <summary>
    /// Dictionary size as power of 2 (bits 10-13 of CompInfo for RAR5).
    /// </summary>
    public int DictSizePower => (int)((CompressionInfo >> RAR5Format.CompInfoDictShift) & RAR5Format.CompInfoDictMask);

    /// <summary>
    /// Dictionary size in KB (base 128KB shifted by DictSizePower).
    /// </summary>
    public int DictionarySizeKB => RAR5Format.CompInfoDictBaseKB << DictSizePower;

    /// <summary>
    /// True if file continues from previous volume.
    /// </summary>
    public bool IsSplitBefore
    {
        get; set;
    }

    /// <summary>
    /// True if file continues in next volume.
    /// </summary>
    public bool IsSplitAfter
    {
        get; set;
    }
}
