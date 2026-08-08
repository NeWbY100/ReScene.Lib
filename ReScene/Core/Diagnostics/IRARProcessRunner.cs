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
    /// <param name="rarExePath">Path to the rar executable.</param>
    /// <param name="inputDirectory">rar's working directory for this run.</param>
    /// <param name="outputFilePath">The output archive path.</param>
    /// <param name="arguments">The switches to pass, in order.</param>
    /// <param name="logTarget">Which log panel this process's output/status lines target.</param>
    /// <param name="onCreated">Invoked with the constructed <see cref="RARProcess"/> before it runs.</param>
    /// <param name="cancellationToken">Cancels the running process.</param>
    /// <param name="inputPaths">
    /// Forwarded verbatim to <see cref="RARProcess"/>'s own <c>inputPaths</c> parameter: the SRR's
    /// ordered file list in place of the platform input mask, or <see langword="null"/> (the
    /// default) to keep the mask.
    /// </param>
    public Task<int> RunAsync(string rarExePath, string inputDirectory, string outputFilePath,
        IEnumerable<string> arguments, LogTarget logTarget,
        Action<RARProcess>? onCreated, CancellationToken cancellationToken,
        IReadOnlyList<string>? inputPaths = null);
}
