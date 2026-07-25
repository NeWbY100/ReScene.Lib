using ReScene.Core.Diagnostics;

namespace ReScene.Tests;

/// <summary>
/// Guards the composed RAR invocation: switches first, then the output archive, then the input mask in
/// the PLATFORM's separator (".\*" on Windows, "./*" on Unix). The mask was once hardcoded to ".\*",
/// which matches nothing on Linux — backslash is an ordinary filename character there — so rar created
/// no archive and every brute-force combination read as a clean no-match.
/// </summary>
public sealed class RARProcessArgumentTests : IDisposable
{
    private readonly string _stubRar =
        Path.Combine(Path.GetTempPath(), "rarproc-" + Guid.NewGuid().ToString("N"));

    public RARProcessArgumentTests() => File.WriteAllText(_stubRar, "stub");

    public void Dispose()
    {
        try { File.Delete(_stubRar); } catch { /* best effort */ }
    }

    [Fact]
    public void Constructor_ComposesSwitchesThenOutputThenPlatformInputMask()
    {
        var process = new RARProcess(
            _stubRar,
            inputDirectory: Path.GetTempPath(),
            outputFilePath: @"out/test.rar",
            commandLineOptions: ["a", "-r", "-s-", "-m0"]);

        string platformMask = $".{Path.DirectorySeparatorChar}*";
        Assert.Equal(["a", "-r", "-s-", "-m0", @"out/test.rar", platformMask], process.CommandLineOptions);

        // The mask must use THIS platform's separator — a Windows-style ".\*" on Unix matches nothing.
        Assert.Equal(OperatingSystem.IsWindows() ? @".\*" : "./*", platformMask);
    }
}
