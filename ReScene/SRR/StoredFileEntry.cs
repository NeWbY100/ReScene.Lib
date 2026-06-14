namespace ReScene.SRR;

/// <summary>
/// A file to embed in an SRR, pairing the name stored inside the SRR with the source file on disk.
/// Stored files are passed as an ordered list so the on-disk order of stored-file blocks is
/// deterministic (a dictionary's enumeration order is not contractual).
/// </summary>
/// <param name="StoredName">
/// The name stored inside the SRR — release-relative, forward-slash separated.
/// </param>
/// <param name="FullPath">
/// Absolute path to the source file on disk.
/// </param>
public sealed record StoredFileEntry(string StoredName, string FullPath);
