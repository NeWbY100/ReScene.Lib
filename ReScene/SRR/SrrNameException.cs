namespace ReScene.SRR;

/// <summary>A stored/volume logical-name violation (spec §1a): source outside the release
/// root, an SFV entry escaping its directory, or a logical-name collision.</summary>
public sealed class SrrNameException(string message) : Exception(message);
