namespace ReScene.Core.IO;

/// <summary>
/// Represents a single entry in a SHA-1 hash file containing a hash and filename.
/// </summary>
/// <param name="sha1">The SHA-1 hash as a 40-character hex string.</param>
/// <param name="fileName">The filename.</param>
public class SHA1FileEntry(string sha1, string fileName)
{
    /// <summary>
    /// Gets or sets the SHA-1 hash as a 40-character hex string.
    /// </summary>
    public string SHA1 { get; set; } = sha1;

    /// <summary>
    /// Gets or sets the filename.
    /// </summary>
    public string FileName { get; set; } = fileName;
}
