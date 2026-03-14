namespace Core;

/// <summary>
/// Provides data for file copy progress events during brute-force input directory preparation.
/// </summary>
public class FileCopyProgressEventArgs : EventArgs
{
    /// <summary>The name of the file currently being copied.</summary>
    public string FileName { get; init; } = "";

    /// <summary>Number of files copied so far.</summary>
    public int FilesCopied { get; init; }

    /// <summary>Total number of files to copy.</summary>
    public int TotalFiles { get; init; }

    /// <summary>Total bytes copied so far.</summary>
    public long BytesCopied { get; init; }

    /// <summary>Total bytes to copy.</summary>
    public long TotalBytes { get; init; }

    /// <summary>Source directory path.</summary>
    public string SourceDirectory { get; init; } = "";

    /// <summary>Destination directory path.</summary>
    public string DestinationDirectory { get; init; } = "";
}
