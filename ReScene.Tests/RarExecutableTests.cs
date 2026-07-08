using ReScene.Core;

namespace ReScene.Tests;

public class RarExecutableTests
{
    [Fact]
    public void FileName_IsRarExe_OnWindows_AndRar_Elsewhere()
    {
        var expected = OperatingSystem.IsWindows() ? "rar.exe" : "rar";
        Assert.Equal(expected, RarExecutable.FileName);
    }

    [Fact]
    public void ResolveIn_CombinesVersionDirWithPlatformBinary()
    {
        var dir = Path.Combine("some", "ver");
        Assert.Equal(Path.Combine(dir, RarExecutable.FileName), RarExecutable.ResolveIn(dir));
    }
}
