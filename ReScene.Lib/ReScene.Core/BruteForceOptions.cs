using ReScene.Core.Cryptography;

namespace ReScene.Core;

/// <summary>
/// Configuration options for the brute-force RAR reconstruction operation.
/// Specifies the RAR installations directory, release files, output location, and expected hashes.
/// </summary>
/// <param name="rarInstallationsDirectoryPath">The directory containing extracted RAR installation versions.</param>
/// <param name="releaseDirectoryPath">The directory containing the release files to be archived.</param>
/// <param name="outputDirectoryPath">The directory to save generated RAR files.</param>
public class BruteForceOptions(string rarInstallationsDirectoryPath, string releaseDirectoryPath, string outputDirectoryPath)
{
    /// <summary>
    /// Gets or sets the directory to which the RAR installation files have been extracted to.
    /// </summary>
    public string RARInstallationsDirectoryPath { get; set; } = rarInstallationsDirectoryPath;

    /// <summary>
    /// Gets or sets the release directory which contains the files to RAR.
    /// </summary>
    public string ReleaseDirectoryPath { get; set; } = releaseDirectoryPath;

    /// <summary>
    /// Gets or sets the output directory to save the temp RAR files.
    /// </summary>
    public string OutputDirectoryPath { get; set; } = outputDirectoryPath;

    /// <summary>
    /// Gets or sets the hashes which contain the expected hash of the RAR file(s).
    /// </summary>
    public HashSet<string> Hashes { get; set; } = [];

    /// <summary>
    /// Gets or sets the type of the hash in the <see cref="Hashes"/> set.
    /// </summary>
    public HashType HashType { get; set; }

    /// <summary>
    /// Gets or sets the RAR options.
    /// </summary>
    public RAROptions RAROptions { get; set; } = new();
}
