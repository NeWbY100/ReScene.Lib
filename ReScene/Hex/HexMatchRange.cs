namespace ReScene.Hex;

/// <summary>
/// One contiguous byte range to highlight inside a hex view control.
/// </summary>
public readonly record struct HexMatchRange(long Offset, int Length);
