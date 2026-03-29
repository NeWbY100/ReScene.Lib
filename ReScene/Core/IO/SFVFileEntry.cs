namespace ReScene.Core.IO;

/// <summary>
/// Represents a single entry in an SFV file containing a filename and its CRC32 checksum.
/// </summary>
/// <param name="fileName">The filename.</param>
/// <param name="crc">The CRC32 checksum as a lowercase hex string.</param>
public class SFVFileEntry(string fileName, string crc)
{
    /// <summary>
    /// Gets or sets the filename.
    /// </summary>
    public string FileName { get; set; } = fileName;

    /// <summary>
    /// Gets or sets the CRC32 checksum as a lowercase hex string.
    /// </summary>
    public string CRC { get; set; } = crc;
}
