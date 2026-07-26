namespace ReScene.Core;

/// <summary>
/// Resolves the WinRAR console executable for a version directory: <c>rar.exe</c> on Windows; on Unix,
/// the version's <c>run-rar</c> launcher when the pack ships one (it runs the binary against the pack's
/// bundled runtime in <c>../_libs</c> via the bundled dynamic linker, so 2002-era builds work on any
/// x86_64 host with nothing installed — same arguments, exit code passed straight through), falling
/// back to the bare <c>rar</c> binary otherwise.
/// </summary>
public static class RarExecutable
{
    /// <summary>The console binary's plain name: rar.exe on Windows, rar elsewhere.</summary>
    public static string FileName { get; } = OperatingSystem.IsWindows() ? "rar.exe" : "rar";

    /// <summary>The optional per-version launcher script Unix packs bundle alongside the binary.</summary>
    internal const string UnixWrapperName = "run-rar";

    public static string ResolveIn(string versionDirectory)
        => ResolveIn(versionDirectory, preferUnixWrapper: !OperatingSystem.IsWindows());

    // Test seam: both arms are exercisable on any OS. The wrapper preference is Unix-only in
    // production — a stray run-rar file on Windows must never shadow rar.exe.
    internal static string ResolveIn(string versionDirectory, bool preferUnixWrapper)
    {
        if (preferUnixWrapper)
        {
            string wrapper = Path.Combine(versionDirectory, UnixWrapperName);
            if (File.Exists(wrapper))
            {
                return wrapper;
            }
        }

        return Path.Combine(versionDirectory, FileName);
    }
}
