namespace ReScene.RAR;

/// <summary>
/// Represents a logical byte range within a single RAR volume for a packed file.
/// </summary>
/// <param name="ArchivePath">
/// Full path to the RAR volume file.
/// </param>
/// <param name="LogicalStart">
/// Start byte position in the logical file (inclusive).
/// </param>
/// <param name="LogicalEnd">
/// End byte position in the logical file (inclusive).
/// </param>
/// <param name="DataOffset">
/// Byte offset within the physical RAR file where this segment's data begins.
/// </param>
internal record RARVolume(string ArchivePath, long LogicalStart, long LogicalEnd, long DataOffset);
