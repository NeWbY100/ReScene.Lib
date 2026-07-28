namespace ReScene.Core.Diagnostics;

/// <summary>
/// Seam over rar execution so the candidate loop is testable without a rar binary. The real
/// implementation (<see cref="RealRARProcessRunner"/>) wraps <see cref="RARProcess"/> exactly as
/// <see cref="Manager"/> did inline at each launch site before this seam was introduced.
/// </summary>
internal interface IRARProcessRunner
{
    /// <summary>
    /// Runs one rar process. <paramref name="onCreated"/> is invoked with the constructed
    /// <see cref="RARProcess"/> before it starts running, so the caller can open its streaming log
    /// and subscribe to its events — the same setup <see cref="Manager"/> performed inline at each
    /// launch site before the process started.
    /// </summary>
    public Task<int> RunAsync(string rarExePath, string inputDirectory, string outputFilePath,
        IEnumerable<string> arguments, LogTarget logTarget,
        Action<RARProcess>? onCreated, CancellationToken cancellationToken);
}
