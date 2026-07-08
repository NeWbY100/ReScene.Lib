namespace ReScene.Core;

/// <summary>Resolves the WinRAR console-binary name per OS: rar.exe on Windows, rar elsewhere.</summary>
public static class RarExecutable
{
    public static string FileName { get; } = OperatingSystem.IsWindows() ? "rar.exe" : "rar";
    public static string ResolveIn(string versionDirectory) => Path.Combine(versionDirectory, FileName);
}
