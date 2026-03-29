using ReScene.Core.IO;

namespace ReScene.Core.Diagnostics;

/// <summary>
/// Provides data for RAR compression progress events, including the process and file being compressed.
/// </summary>
public class RARCompressionProgressEventArgs(RARProcess process, long operationSize, long operationProgressed, DateTime startDateTime, string filePath) : OperationProgressEventArgs(operationSize, operationProgressed, startDateTime)
{
    /// <summary>
    /// Gets the RAR process performing the compression.
    /// </summary>
    public RARProcess Process { get; private set; } = process;

    /// <summary>
    /// Gets the file path currently being compressed.
    /// </summary>
    public string FilePath { get; private set; } = filePath;
}
