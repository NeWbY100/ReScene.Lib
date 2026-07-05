namespace ReScene.Core;

/// <summary>The outcome of a brute-force run: success plus the winning combo (for seeding the next set).</summary>
public sealed record BruteForceRunResult(bool Success, WinningCombo? Combo);
