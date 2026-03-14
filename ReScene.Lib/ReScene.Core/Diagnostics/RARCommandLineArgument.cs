namespace ReScene.Core.Diagnostics;

/// <summary>
/// Represents a RAR command-line argument with version constraints specifying which RAR versions support it.
/// </summary>
/// <param name="argument">The command-line argument string (e.g., "-m5", "-md4096k").</param>
/// <param name="minimumVersion">The minimum RAR version that supports this argument.</param>
/// <param name="archiveVersion">The archive format version required for this argument, if any.</param>
public class RARCommandLineArgument(string argument, int minimumVersion, RARArchiveVersion? archiveVersion = null)
{
    /// <summary>
    /// Gets or sets the command-line argument string.
    /// </summary>
    public string Argument { get; set; } = argument;

    /// <summary>
    /// Gets or sets the minimum RAR version that supports this argument.
    /// </summary>
    public int MinimumVersion { get; set; } = minimumVersion;

    /// <summary>
    /// Gets or sets the maximum RAR version that supports this argument, or <c>null</c> if supported indefinitely.
    /// </summary>
    public int? MaximumVersion { get; set; }

    /// <summary>
    /// Gets or sets the archive format version required for this argument, or <c>null</c> if format-agnostic.
    /// </summary>
    public RARArchiveVersion? ArchiveVersion { get; set; } = archiveVersion;

    public RARCommandLineArgument(string argument, int minimumVersion, int maximumVersion, RARArchiveVersion? archiveVersion = null)
        : this(argument, minimumVersion, archiveVersion)
    {
        MaximumVersion = maximumVersion;
    }
}
