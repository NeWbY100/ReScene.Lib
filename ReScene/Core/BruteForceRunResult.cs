using ReScene.Core.Diagnostics;

namespace ReScene.Core;

/// <summary>The version + command-line argument combination that reproduced a set, byte-exact.</summary>
public sealed record WinningCombo(int Version, IReadOnlyList<RARCommandLineArgument> Args);

/// <summary>The outcome of a brute-force run: success plus the winning combo (for seeding the next set).</summary>
public sealed record BruteForceRunResult(bool Success, WinningCombo? Combo);
