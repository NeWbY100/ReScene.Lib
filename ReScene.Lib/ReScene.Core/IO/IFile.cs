namespace ReScene.Core.IO;

/// <summary>
/// Represents a file with a file path.
/// </summary>
public interface IFile
{
    /// <summary>
    /// Gets the full path to the file.
    /// </summary>
    public string FilePath { get; }
}
