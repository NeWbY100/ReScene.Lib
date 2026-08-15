using ReScene.Core;

namespace ReScene.Tests;

public class RarExecutableTests
{
    [Fact]
    public void FileName_IsRarExe_OnWindows_AndRar_Elsewhere()
    {
        string expected = OperatingSystem.IsWindows() ? "rar.exe" : "rar";
        Assert.Equal(expected, RarExecutable.FileName);
    }

    [Fact]
    public void ResolveIn_CombinesVersionDirWithPlatformBinary()
    {
        string dir = Path.Combine("some", "ver");
        Assert.Equal(Path.Combine(dir, RarExecutable.FileName), RarExecutable.ResolveIn(dir));
    }

    [Fact]
    public void ResolveIn_UnixArm_PrefersRunRarWrapper_WhenPresent()
    {
        // Unix packs bundle a per-version run-rar launcher that runs the binary against the pack's
        // bundled runtime (2002-era builds need C++ runtimes no current distro ships); when it exists,
        // it IS the correct executable to invoke.
        string dir = Path.Combine(Path.GetTempPath(), "rarexe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // No wrapper: falls back to the plain binary name.
            Assert.Equal(Path.Combine(dir, RarExecutable.FileName), RarExecutable.ResolveIn(dir, preferUnixWrapper: true));

            File.WriteAllText(Path.Combine(dir, "run-rar"), "#!/bin/sh\n");
            Assert.Equal(Path.Combine(dir, "run-rar"), RarExecutable.ResolveIn(dir, preferUnixWrapper: true));
        }
        finally
        {
            try
            { Directory.Delete(dir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public void ResolveIn_WindowsArm_IgnoresRunRarWrapper()
    {
        // A stray run-rar file on Windows must never shadow rar.exe.
        string dir = Path.Combine(Path.GetTempPath(), "rarexe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "run-rar"), "#!/bin/sh\n");
            Assert.Equal(Path.Combine(dir, RarExecutable.FileName), RarExecutable.ResolveIn(dir, preferUnixWrapper: false));
        }
        finally
        {
            try
            { Directory.Delete(dir, recursive: true); }
            catch { }
        }
    }
}
