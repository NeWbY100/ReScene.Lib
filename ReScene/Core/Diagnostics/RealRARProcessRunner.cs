namespace ReScene.Core.Diagnostics;

/// <summary>
/// Default <see cref="IRARProcessRunner"/>: runs a real rar process via <see cref="RARProcess"/> —
/// exactly what <see cref="Manager"/> constructed and awaited inline at each launch site before
/// this seam was introduced. Carries the same logger <see cref="RARProcess"/> always received, so
/// its own Debug/Information/Warning/Error calls keep reaching the app's log exactly as before.
/// </summary>
internal sealed class RealRARProcessRunner(IReSceneLogger? logger = null) : IRARProcessRunner
{
    private readonly IReSceneLogger? _logger = logger;

    public Task<int> RunAsync(string rarExePath, string inputDirectory, string outputFilePath,
        IEnumerable<string> arguments, LogTarget logTarget,
        Action<RARProcess>? onCreated, CancellationToken cancellationToken,
        IReadOnlyList<string>? inputPaths = null)
    {
        RARProcess process = new(rarExePath, inputDirectory, outputFilePath, arguments, _logger, inputPaths)
        {
            LogTarget = logTarget
        };

        onCreated?.Invoke(process);

        return process.RunAsync(cancellationToken);
    }
}
