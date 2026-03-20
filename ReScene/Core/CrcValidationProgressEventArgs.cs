namespace ReScene.Core;

/// <summary>
/// Provides data for CRC32 validation progress events during brute-force input preparation.
/// </summary>
public class CrcValidationProgressEventArgs : EventArgs
{
    /// <summary>
    /// The name of the file currently being verified.
    /// </summary>
    public string FileName { get; init; } = "";

    /// <summary>
    /// Number of files fully verified so far.
    /// </summary>
    public int FilesVerified { get; init; }

    /// <summary>
    /// Total number of files to verify.
    /// </summary>
    public int TotalFiles { get; init; }

    /// <summary>
    /// Total bytes hashed so far (across all completed files + current file progress).
    /// </summary>
    public long BytesVerified { get; init; }

    /// <summary>
    /// Total bytes across all files to verify.
    /// </summary>
    public long TotalBytes { get; init; }
}
