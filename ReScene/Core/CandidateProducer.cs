namespace ReScene.Core;

/// <summary>
/// One candidate's launched rar process, and the four ways the engine is allowed to observe it.
/// Exists so the producer-observation invariant — no finalization, deletion, or next-candidate
/// launch while a launched task is unobserved — is expressed as named operations rather than as
/// repeated inline await patterns over two loose locals.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ObserveQuietlyAsync"/> and <see cref="JoinForWinAsync"/> are NOT interchangeable.
/// The quiet observer swallows faults and is for cleanup, mismatch and catch paths, where the run
/// is already abandoning this candidate. The winning join awaits plainly so a fault propagates into
/// the caller's generic catch and becomes an error row: a quiet observer there would silently
/// accept a producer that faulted after the volume-2 trigger and finalize a broken candidate as a
/// match.
/// </para>
/// <para>
/// Diagnostics arrive as callbacks rather than a logger reference, so this type stays free of the
/// engine's logging concerns while the messages keep firing from the same places.
/// </para>
/// <para>
/// An instance is created for every candidate but stays empty on the standard early-termination
/// path, where <c>RARCompressDirectoryAsync</c> observes its own producer internally and
/// <see cref="ProcessTask"/> remains null. <see cref="ObserveQuietlyAsync"/> and
/// <see cref="JoinForWinAsync"/> are no-ops on an empty handle;
/// <see cref="AwaitLaunchOrSecondVolumeAsync"/> is not — it is only meaningful once a launch has
/// assigned <see cref="ProcessTask"/>, and is called from that path alone.
/// </para>
/// <para>
/// Disposal releases only <see cref="Cts"/>. The candidate loop's own <c>finally</c> disposes the
/// handle it owns exactly once; disposing earlier would make a later
/// <see cref="ObserveQuietlyAsync"/> throw <see cref="ObjectDisposedException"/> from its pre-await
/// cancel, which sits outside that method's try. A handle constructed to WRAP a cancellation source
/// somebody else owns (see the early-termination helper) is deliberately not disposed — it does not
/// own what it wraps.
/// </para>
/// </remarks>
internal sealed class CandidateProducer : IDisposable
{
    /// <summary>The launched task, or null when this candidate used the early-termination path.</summary>
    public Task<int>? ProcessTask
    {
        get; set;
    }

    /// <summary>The linked source cancelling <see cref="ProcessTask"/>, or null.</summary>
    public CancellationTokenSource? Cts
    {
        get; set;
    }

    /// <summary>
    /// Whether an assembly attempt may retry on an incomplete snapshot. MUST be read BEFORE the
    /// attempt: reading it afterwards observes a producer that may have finished during the attempt
    /// and silently loses the retry, which is the real race the check exists to catch.
    /// </summary>
    public bool RetryEligible => ProcessTask is { IsCompleted: false };

    /// <summary>
    /// Waits for the launch to resolve one way or the other: either the process finished, or the
    /// second volume appeared (meaning the first is complete). Cancels the monitor if the process
    /// won, then surfaces a faulted LAUNCH — <c>Task.WhenAny</c> does not observe faults, so
    /// without this rethrow a process that could not start (e.g. a *nix rar binary without the
    /// execute bit) would fall through to the ordinary "archive not created" no-match path instead
    /// of being recorded as a failed combination.
    /// </summary>
    public async Task AwaitLaunchOrSecondVolumeAsync(Task monitorTask, CancellationTokenSource monitorCts)
    {
        await Task.WhenAny(ProcessTask!, monitorTask).ConfigureAwait(false);

        if (!monitorTask.IsCompleted)
        {
            monitorCts.Cancel();
        }

        if (ProcessTask!.IsFaulted)
        {
            await ProcessTask.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The CLEANUP-path observer: no PRODUCER outcome escapes it — cancellation and faults both
    /// return null. Use only where a producer fault must not abort the run. Returns the producer's
    /// real exit code, or null if it was cancelled, faulted, or never launched.
    /// <para>
    /// It is not exception-free in the absolute sense: the pre-await <see cref="Cts"/> cancel sits
    /// outside the try (so cancelling an already-disposed source throws
    /// <see cref="ObjectDisposedException"/> — see the disposal contract above), and an exception
    /// thrown by <paramref name="onFaultObserved"/> propagates. Neither is a producer outcome.
    /// </para>
    /// </summary>
    /// <param name="cancelFirst">Whether to cancel the producer before awaiting it.</param>
    /// <param name="onFaultObserved">Receives a faulted producer's message, for cleanup-noise logging.</param>
    public async Task<int?> ObserveQuietlyAsync(bool cancelFirst, Action<string>? onFaultObserved = null)
    {
        if (ProcessTask is null)
        {
            return null;
        }

        if (cancelFirst)
        {
            Cts?.Cancel();
        }

        try
        {
            return await ProcessTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            onFaultObserved?.Invoke(ex.Message);
            return null;
        }
    }

    /// <summary>
    /// The WINNING-path join: lets a still-running producer finish creating every volume before the
    /// set is verified, then awaits it PLAINLY so a fault propagates to the caller's generic catch.
    /// The await is unconditional whenever a producer exists — never gated on
    /// <see cref="Task.IsCompleted"/>. A producer that faulted between the launch check and here is
    /// already completed, and gating on that would skip observing it entirely, letting a candidate
    /// whose producer crashed mid-volume-set be finalized as a match.
    /// </summary>
    /// <param name="onStillRunning">Invoked only when the producer has not finished yet.</param>
    public async Task JoinForWinAsync(Action? onStillRunning = null)
    {
        if (ProcessTask is null)
        {
            return;
        }

        if (!ProcessTask.IsCompleted)
        {
            onStillRunning?.Invoke();
        }

        await ProcessTask.ConfigureAwait(false);
    }

    public void Dispose() => Cts?.Dispose();
}
