namespace ReScene.RAR;

/// <summary>
/// One archived file as discovered by walking RAR headers across all volumes in a set.
/// </summary>
/// <param name="FileName">
/// File name as recorded in the RAR header, with backslash separators preserved.
/// </param>
/// <param name="IsStored">
/// True when stored uncompressed (method 0). False for any compressed entry.
/// </param>
/// <param name="IsSplit">
/// True when the file is split across volume boundaries (either continued from a previous
/// volume or continues into a following one).
/// </param>
/// <param name="IsSplitBefore">
/// True when the entry continues from the previous volume.
/// </param>
/// <param name="IsSplitAfter">
/// True when the entry continues into the following volume.
/// </param>
/// <param name="CompressionMethod">
/// Method index in 0–5 (0 = Store, 1 = Fastest … 5 = Best).
/// </param>
/// <param name="UnpackVersion">
/// Raw <c>UnpVer</c> byte from the file header for RAR4. RAR5 entries always report 50.
/// </param>
/// <param name="PackedSize">
/// Total packed size of the entry across all volumes.
/// </param>
/// <param name="UnpackedSize">
/// Logical (unpacked) size as reported by the file header.
/// </param>
/// <param name="IsRar5">
/// True for entries discovered in a RAR5-format archive.
/// </param>
/// <param name="ExpectedCrc">
/// Unpacked-data CRC32 from the file header, used to validate decompression output.
/// <see langword="null"/> when the header does not carry a CRC (e.g. a RAR5 entry
/// without the CRC32 flag).
/// </param>
internal sealed record RAREntry(
    string FileName,
    bool IsStored,
    bool IsSplit,
    bool IsSplitBefore,
    bool IsSplitAfter,
    byte CompressionMethod,
    byte UnpackVersion,
    long PackedSize,
    long UnpackedSize,
    bool IsRar5,
    uint? ExpectedCrc = null);
