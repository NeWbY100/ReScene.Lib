namespace ReScene.Core;

/// <summary>One produced volume's positional comparison against its expected name + CRC.</summary>
public sealed record VolumeMatch(int Index, string ExpectedName, string ExpectedCrc, string ActualCrc, bool Match);
