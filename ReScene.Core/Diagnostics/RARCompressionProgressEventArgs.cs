using ReScene.Core.IO;

namespace ReScene.Core.Diagnostics;

public class RARCompressionProgressEventArgs(RARProcess process, long operationSize, long operationProgressed, DateTime startDateTime, string filePath) : OperationProgressEventArgs(operationSize, operationProgressed, startDateTime)
{
    public RARProcess Process { get; private set; } = process;

    /// <summary>
    /// Gets the file path currently being compressed.
    /// </summary>
    public string FilePath { get; private set; } = filePath;
}
