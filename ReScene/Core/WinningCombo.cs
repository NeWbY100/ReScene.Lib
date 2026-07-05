using ReScene.Core.Diagnostics;

namespace ReScene.Core;

/// <summary>The version + command-line argument combination that reproduced a set, byte-exact.</summary>
public sealed record WinningCombo(int Version, IReadOnlyList<RARCommandLineArgument> Args);
