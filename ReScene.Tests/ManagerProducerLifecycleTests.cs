using System.Diagnostics;
using ReScene.Core;
using ReScene.Core.Cryptography;
using ReScene.Core.IO;
using ReScene.RAR;

namespace ReScene.Tests;

/// <summary>
/// Tests for the producer-observation invariant: no finalization, deletion, or
/// next-candidate launch may happen while a launched rar process's task is unobserved. Drives
/// <see cref="Manager.BruteForceRARVersionAsync"/> through <see cref="FakeRunner"/> so each
/// scenario can hold a launch open indefinitely and assert Manager genuinely blocks on it — a real
/// rar.exe can't be held open on demand, so these paths were previously untested end-to-end.
/// </summary>
/// <remarks>
/// None of these drive SRR-guided assembly (that's the reconstructor side, covered elsewhere) —
/// they exercise the plain legacy candidate loop: find-created-file, hash, compare, rename/delete.
/// That loop never parses RAR structure, so "carrier volumes" here are arbitrary bytes at the
/// right path/naming convention, not real RAR4 archives.
/// </remarks>
public class ManagerProducerLifecycleTests : TempDirTestBase
{
    private static readonly byte[] CarrierBytes = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];
    private static readonly byte[] TriggerBytes = [0x00];

    private AssemblyTestHost NewHost() => new(TempDir);

    /// <summary>
    /// The CRC32 <see cref="HashCalculator"/> would report for <see cref="CarrierBytes"/>, computed
    /// via a disposable scratch file — the exact production code path, not a re-derivation of the
    /// algorithm.
    /// </summary>
    private string CarrierCrc()
    {
        string scratch = Path.Combine(TempDir, $"scratch-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(scratch, CarrierBytes);
        return HashCalculator.Calculate(HashType.CRC32, scratch);
    }

    /// <summary>Old-style second-volume path (".r00") for <paramref name="firstVolumePath"/> — one
    /// of the candidates <see cref="RARVolumeNaming"/>'s early-termination/CAV monitor probes for.</summary>
    private static string SecondVolumePath(string firstVolumePath)
        => Path.Combine(Path.GetDirectoryName(firstVolumePath)!, Path.GetFileNameWithoutExtension(firstVolumePath) + ".r00");

    /// <summary>Polls <paramref name="condition"/> until true, failing with a clear message if it
    /// never becomes true within <paramref name="timeout"/> (default 5s) — used for test-setup
    /// synchronization (e.g. "wait until the candidate has launched"), not the invariant itself.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string because, TimeSpan? timeout = null)
    {
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(5);
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > limit)
            {
                throw new TimeoutException($"Timed out waiting for: {because}");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Awaits <paramref name="task"/>, failing fast with a clear message instead of hanging
    /// the test indefinitely if the invariant under test regresses.</summary>
    private static async Task WithTimeoutAsync(Task task, string because, TimeSpan? timeout = null)
    {
        Task winner = await Task.WhenAny(task, Task.Delay(timeout ?? TimeSpan.FromSeconds(5)));
        if (winner != task)
        {
            throw new TimeoutException($"Timed out waiting for: {because}");
        }

        await task;
    }

    private static async Task<T> WithTimeoutAsync<T>(Task<T> task, string because, TimeSpan? timeout = null)
    {
        Task winner = await Task.WhenAny(task, Task.Delay(timeout ?? TimeSpan.FromSeconds(5)));
        if (winner != task)
        {
            throw new TimeoutException($"Timed out waiting for: {because}");
        }

        return await task;
    }

    /// <summary>
    /// Polls <paramref name="stillPending"/> for ~250ms, asserting it stays true throughout —
    /// proves the invariant actually BLOCKS progression rather than merely "hasn't raced yet".
    /// </summary>
    private static async Task AssertBlockedAsync(Func<bool> stillPending, string because)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromMilliseconds(250))
        {
            Assert.True(stillPending(), because);
            await Task.Delay(25);
        }
    }

    /// <summary>
    /// Subscribes to <paramref name="manager"/>'s progress events into a plain list — safe to read
    /// only AFTER the run's task has been awaited to completion (no concurrent writer remains at
    /// that point; awaiting establishes the happens-before edge). Asserting on
    /// <c>CombinationFailed</c> here is the real contract; log-message checks are secondary.
    /// </summary>
    private static List<BruteForceProgressEventArgs> CollectProgressEvents(Manager manager)
    {
        List<BruteForceProgressEventArgs> events = [];
        manager.BruteForceProgress += (_, e) => events.Add(e);
        return events;
    }

    [Fact]
    public async Task HarnessSmoke_LegacyRun_SingleFakeVersion_Completes()
    {
        // Proves the options/harness plumbing works — a legacy (no-SRR) run, one version, one
        // (empty-args) combination, one launch that immediately writes a matching carrier and
        // completes — BEFORE any lifecycle test relies on this harness.
        using AssemblyTestHost host = NewHost();
        BruteForceOptions options = host.Options(fixture: null, completeAllVolumes: false);
        options.Hashes.Add(CarrierCrc());

        host.Runner.OnLaunch = launch =>
        {
            File.WriteAllBytes(launch.OutputFilePath, CarrierBytes);
            launch.Exit.TrySetResult(0);
        };

        BruteForceRunResult result = await WithTimeoutAsync(
            host.Manager.BruteForceRARVersionAsync(options), "the harness smoke run to finish");

        Assert.True(result.Success);
        Assert.Single(host.Runner.Launches);
    }

    [Fact]
    public async Task NonCavSuccess_DoesNotComplete_UntilProducerTaskObserved()
    {
        // Standard (non-CAV) path: exercises RARCompressDirectoryAsync's own monitor-triggered
        // branch. OnLaunch writes a matching carrier volume 1 AND a volume 2 (the early-termination
        // trigger) but holds Exit — Manager must cancel and then BLOCK on real observation, never
        // complete (or report any completion status) while that producer task is unobserved.
        using AssemblyTestHost host = NewHost();
        BruteForceOptions options = host.Options(fixture: null, completeAllVolumes: false);
        options.Hashes.Add(CarrierCrc());

        FakeRunner.Launch? launch = null;
        host.Runner.OnLaunch = l =>
        {
            launch = l;
            File.WriteAllBytes(l.OutputFilePath, CarrierBytes);
            File.WriteAllBytes(SecondVolumePath(l.OutputFilePath), TriggerBytes);
            // Exit is deliberately left unresolved.
        };

        bool completed = false;
        host.Manager.BruteForceStatusChanged += (_, e) => completed |= e.NewStatus == OperationStatus.Completed;

        Task<BruteForceRunResult> runTask = host.Manager.BruteForceRARVersionAsync(options);

        await WaitUntilAsync(() => launch is not null, "the candidate to launch");
        await WithTimeoutAsync(launch!.CancellationRequested.Task, "Manager to cancel the early-terminated rar after detecting volume 2");

        await AssertBlockedAsync(() => !runTask.IsCompleted && !completed,
            "the run must not complete while the producer task is unobserved");

        launch.Exit.TrySetResult(1);

        BruteForceRunResult result = await WithTimeoutAsync(runTask, "the run to finish");
        Assert.True(result.Success);
        Assert.True(completed);
    }

    [Fact]
    public async Task QuickMismatch_ObservesProducer_BeforeNextCandidateLaunch()
    {
        // CAV path, two versions. Launch 1 writes a non-matching carrier + volume 2 (the CAV
        // completion trigger) and holds Exit. The mismatch branch must cancel and BLOCK on real
        // observation before candidate 2 may launch.
        using AssemblyTestHost host = NewHost();
        host.AddSecondVersion();
        BruteForceOptions options = host.Options(fixture: null, completeAllVolumes: true);
        options.Hashes.Add("00000000"); // deliberately wrong — every candidate mismatches

        host.Runner.OnLaunch = l =>
        {
            File.WriteAllBytes(l.OutputFilePath, CarrierBytes);
            File.WriteAllBytes(SecondVolumePath(l.OutputFilePath), TriggerBytes);
        };

        Task<BruteForceRunResult> runTask = host.Manager.BruteForceRARVersionAsync(options);

        await WaitUntilAsync(() => host.Runner.Launches.Count >= 1, "the first candidate to launch");
        FakeRunner.Launch launch1 = host.Runner.Launches[0];

        await WithTimeoutAsync(launch1.CancellationRequested.Task, "the mismatch cleanup to cancel launch 1");

        await AssertBlockedAsync(() => host.Runner.Launches.Count == 1,
            "no second candidate may launch while launch 1's producer task is unobserved");

        launch1.Exit.TrySetResult(1);

        await WaitUntilAsync(() => host.Runner.Launches.Count == 2, "the second candidate to launch");
        host.Runner.Launches[1].Exit.TrySetResult(1);

        BruteForceRunResult result = await WithTimeoutAsync(runTask, "the run to finish");
        Assert.False(result.Success);
        Assert.Equal(2, host.Runner.Launches.Count);
    }

    [Fact]
    public async Task MidCandidateError_LogsErrorRow_ObservesProducer_AndContinues()
    {
        // CAV path, two versions. Malformed bytes are still hashable (a mismatch, not a throw), so
        // this induces a DETERMINISTIC post-launch exception instead: launch 1 writes volume 1 +
        // volume 2 (the CAV trigger), then the test opens volume 1 with FileShare.None and holds the
        // handle, so Manager's hash read throws while Exit is also held. Manager's contract for a
        // mid-candidate error is an error row + CONTINUE (not propagation) — but it must still
        // observe the producer before the next candidate launches.
        using AssemblyTestHost host = NewHost();
        host.AddSecondVersion();
        BruteForceOptions options = host.Options(fixture: null, completeAllVolumes: true);
        // No matching hash needed — the hash read itself throws before any comparison happens.

        FakeRunner.Launch? launch1 = null;
        FileStream? lockHandle = null;
        host.Runner.OnLaunch = l =>
        {
            if (launch1 is null)
            {
                launch1 = l;
                File.WriteAllBytes(l.OutputFilePath, CarrierBytes);
                File.WriteAllBytes(SecondVolumePath(l.OutputFilePath), TriggerBytes);
                lockHandle = new FileStream(l.OutputFilePath, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            else
            {
                // Candidate 2 just needs to reach a clean (non-matching) completion so the run finishes.
                File.WriteAllBytes(l.OutputFilePath, CarrierBytes);
                l.Exit.TrySetResult(1);
            }
        };

        List<BruteForceProgressEventArgs> progressEvents = CollectProgressEvents(host.Manager);
        Task<BruteForceRunResult> runTask = host.Manager.BruteForceRARVersionAsync(options);

        await WaitUntilAsync(() => launch1 is not null, "the first candidate to launch");
        await WithTimeoutAsync(launch1!.CancellationRequested.Task, "the error cleanup to cancel launch 1");

        await AssertBlockedAsync(() => host.Runner.Launches.Count == 1,
            "no second candidate may launch while launch 1's producer task is unobserved after the hash-read error");

        lockHandle!.Dispose();
        launch1.Exit.TrySetResult(1);

        BruteForceRunResult result = await WithTimeoutAsync(runTask, "the run to finish");
        Assert.False(result.Success);
        Assert.Equal(2, host.Runner.Launches.Count);
        // The real contract: exactly one CombinationFailed row (candidate 1's hash-read error);
        // candidate 2's clean non-match must not also be flagged as a failure.
        Assert.Single(progressEvents, e => e.CombinationFailed);
        Assert.True(host.Log.Count("RAR execution failed") >= 1); // secondary
    }

    [Fact]
    public async Task ProducerFault_BecomesOneErrorRow_AndNextCandidateRuns()
    {
        // Standard (non-CAV) path, two versions. Launch 1 faults immediately — Manager's
        // fault-surfacing PLAIN await (RARCompressDirectoryAsync's natural-completion return)
        // rethrows into the generic catch (one error row); the catch's own observation call must
        // NOT rethrow again (quiet observer), and candidate 2 must still run.
        using AssemblyTestHost host = NewHost();
        host.AddSecondVersion();
        BruteForceOptions options = host.Options(fixture: null, completeAllVolumes: false);
        // No matching hash — candidate 2 must also reach a clean (non-matching) completion.

        int launchCount = 0;
        host.Runner.OnLaunch = l =>
        {
            launchCount++;
            if (launchCount == 1)
            {
                l.Exit.TrySetException(new InvalidOperationException("simulated producer fault"));
            }
            else
            {
                File.WriteAllBytes(l.OutputFilePath, CarrierBytes);
                l.Exit.TrySetResult(1);
            }
        };

        List<BruteForceProgressEventArgs> progressEvents = CollectProgressEvents(host.Manager);
        BruteForceRunResult result = await WithTimeoutAsync(
            host.Manager.BruteForceRARVersionAsync(options), "the run to finish");

        Assert.False(result.Success);
        Assert.Equal(2, host.Runner.Launches.Count);
        // The real contract: exactly one CombinationFailed row (candidate 1's fault); candidate
        // 2's clean non-match must not also be flagged as a failure.
        Assert.Single(progressEvents, e => e.CombinationFailed);
        Assert.True(host.Log.Count("RAR execution failed") >= 1); // secondary
    }

    [Fact]
    public async Task LateFault_AfterVolumeTrigger_IsFailedCombination_NeverAMatch()
    {
        // CAV path. The fault lands AFTER the volume-2 trigger has already passed the initial
        // IsFaulted check AND after volume 1's hash has MATCHED — while Manager is in the winning,
        // PLAINLY-awaited "completing all volumes" wait. That await must PROPAGATE the fault (one
        // CombinationFailed row), never silently accept it as a match — guards the
        // silently-accepted-fault regression a single quiet observer on all paths would reintroduce.
        using AssemblyTestHost host = NewHost();
        BruteForceOptions options = host.Options(fixture: null, completeAllVolumes: true);
        options.Hashes.Add(CarrierCrc()); // volume 1 matches — the fault must land AFTER that passes

        FakeRunner.Launch? launch = null;
        host.Runner.OnLaunch = l =>
        {
            launch = l;
            File.WriteAllBytes(l.OutputFilePath, CarrierBytes); // matching set
            File.WriteAllBytes(SecondVolumePath(l.OutputFilePath), TriggerBytes); // volume-2 trigger
        };

        List<BruteForceProgressEventArgs> progressEvents = CollectProgressEvents(host.Manager);
        Task<BruteForceRunResult> runTask = host.Manager.BruteForceRARVersionAsync(options);

        await WaitUntilAsync(() => launch is not null, "the candidate to launch");
        // Quick gate: wait until Manager has passed the initial IsFaulted check, matched volume 1's
        // hash, and is now plainly awaiting the (still-held) producer to finish all volumes.
        await WaitUntilAsync(() => host.Log.Count("First volume matched, completing all volumes") >= 1,
            "Manager to reach the winning, plain-awaited completion wait");

        launch!.Exit.TrySetException(new InvalidOperationException("simulated late producer fault"));

        BruteForceRunResult result = await WithTimeoutAsync(runTask, "the run to finish");

        Assert.False(result.Success);
        Assert.Empty(result.Matches);
        // The real contract: exactly one CombinationFailed row for this candidate.
        Assert.Single(progressEvents, e => e.CombinationFailed);
        Assert.True(host.Log.Count("RAR execution failed") >= 1); // secondary
    }

    [Fact]
    public async Task LateFault_AlreadyCompletedAtWinningCheck_IsFailedCombination_NeverAMatch()
    {
        // CAV path. Closes the SPECIFIC race the previous test's timing cannot reach: here the
        // fault is set SYNCHRONOUSLY from within the first BruteForceProgress event raised after
        // launch (which fires strictly AFTER the CAV block's own IsFaulted check has already
        // passed — Manager.cs ~861 — and strictly BEFORE actualRARFilePath/hash computation).
        // Because BOTH the event dispatch and the FakeRunner Exit TCS completion are synchronous,
        // there is no wall-clock race: by the time Manager reaches the winning-path check, the
        // producer task is ALREADY Faulted (IsCompleted == true). A conditional
        // "if (!runningProcessTask.IsCompleted)" guard around that check's await (the pre-fix
        // shape) would skip observing it entirely and finalize this as a false match.
        using AssemblyTestHost host = NewHost();
        BruteForceOptions options = host.Options(fixture: null, completeAllVolumes: true);
        options.Hashes.Add(CarrierCrc()); // volume 1 matches — a false "match" is the failure mode

        FakeRunner.Launch? launch = null;
        host.Runner.OnLaunch = l =>
        {
            launch = l;
            File.WriteAllBytes(l.OutputFilePath, CarrierBytes); // matching set
            File.WriteAllBytes(SecondVolumePath(l.OutputFilePath), TriggerBytes); // volume-2 trigger
        };

        bool faulted = false;
        host.Manager.BruteForceProgress += (_, _) =>
        {
            // The pre-launch progress event fires with launch still null (skipped here). The
            // post-CAV-check event — the next one — fires with launch set; faulting Exit
            // synchronously right here (still inside Manager's own call stack) deterministically
            // lands the fault before the winning-path check, not merely "as soon as possible"
            // relative to a polled signal.
            if (launch is not null && !faulted)
            {
                faulted = true;
                launch.Exit.TrySetException(new InvalidOperationException("simulated already-faulted-at-check producer fault"));
            }
        };

        List<BruteForceProgressEventArgs> progressEvents = CollectProgressEvents(host.Manager);
        BruteForceRunResult result = await WithTimeoutAsync(
            host.Manager.BruteForceRARVersionAsync(options), "the run to finish");

        Assert.False(result.Success);
        Assert.Empty(result.Matches);
        Assert.Single(host.Runner.Launches);
        Assert.Single(progressEvents, e => e.CombinationFailed);
        Assert.True(host.Log.Count("RAR execution failed") >= 1); // secondary
    }

    [Fact]
    public async Task StopDuringWinningWait_ObservesProducer_BeforePropagatingCancellation()
    {
        // CAV path. A volume already exists and its hash matches, so Manager reaches the winning
        // "completing all volumes" wait (the actualRARFilePath==null branch does not intercept
        // this exit). Stop() is called, then the held Exit resolves as a genuine task
        // cancellation (TrySetCanceled) — exercising the OperationCanceledException catch
        // (~Manager.cs:1064) specifically, which every other lifecycle test here reaches via a
        // different exit. That catch must observe the producer before propagating.
        using AssemblyTestHost host = NewHost();
        BruteForceOptions options = host.Options(fixture: null, completeAllVolumes: true);
        options.Hashes.Add(CarrierCrc());

        FakeRunner.Launch? launch = null;
        host.Runner.OnLaunch = l =>
        {
            launch = l;
            File.WriteAllBytes(l.OutputFilePath, CarrierBytes);
            File.WriteAllBytes(SecondVolumePath(l.OutputFilePath), TriggerBytes);
        };

        Task<BruteForceRunResult> runTask = host.Manager.BruteForceRARVersionAsync(options);

        await WaitUntilAsync(() => launch is not null, "the candidate to launch");
        await WaitUntilAsync(() => host.Log.Count("First volume matched, completing all volumes") >= 1,
            "Manager to reach the winning, plain-awaited completion wait");

        host.Manager.Stop();
        await WithTimeoutAsync(launch!.CancellationRequested.Task, "Stop() to cancel the running producer");

        launch.Exit.TrySetCanceled();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task UserStop_ObservesProducerBeforeReturn()
    {
        // Standard (non-CAV) path. No files are ever written — the monitor never finds a second
        // volume; Manager.Stop() (the real cancellation API) is what unblocks the monitor wait, and
        // the run must still BLOCK on real observation of the held producer before it can report
        // cancellation.
        using AssemblyTestHost host = NewHost();
        BruteForceOptions options = host.Options(fixture: null, completeAllVolumes: false);

        FakeRunner.Launch? launch = null;
        host.Runner.OnLaunch = l => launch = l;

        OperationCompletionStatus? finalStatus = null;
        host.Manager.BruteForceStatusChanged += (_, e) =>
        {
            if (e.NewStatus == OperationStatus.Completed)
            {
                finalStatus = e.CompletionStatus;
            }
        };

        Task<BruteForceRunResult> runTask = host.Manager.BruteForceRARVersionAsync(options);

        await WaitUntilAsync(() => launch is not null, "the candidate to launch");

        host.Manager.Stop();

        await WithTimeoutAsync(launch!.CancellationRequested.Task, "Stop() to cancel the running producer");

        await AssertBlockedAsync(() => !runTask.IsCompleted,
            "the run must not complete while the producer task is unobserved after Stop()");

        launch.Exit.TrySetResult(1);

        BruteForceRunResult result = await WithTimeoutAsync(runTask, "the run to finish");
        Assert.False(result.Success);
        Assert.Equal(OperationCompletionStatus.Cancelled, finalStatus);
    }

    /// <summary>
    /// The CRC32 <see cref="HashCalculator"/> reports for <paramref name="bytes"/>, computed via a
    /// disposable scratch file — the exact production code path, not a re-derivation.
    /// </summary>
    private string CrcOf(byte[] bytes)
    {
        string scratch = Path.Combine(TempDir, $"scratch-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(scratch, bytes);
        return HashCalculator.Calculate(HashType.CRC32, scratch);
    }

    // ---- Legacy (non-assembly) complete-all-volumes per-volume verification ----
    // Reaching this block needs _useAssembly == false (no SRRFilePath) AND CompleteAllVolumes AND a
    // non-empty ExpectedVolumeCrcs. AssemblyTestHost.Options only fills ExpectedVolumeCrcs from a
    // fixture, and every fixture supplies an SRR path (which engages assembly instead) — so these
    // populate the public collections directly. Before these tests, the legacy per-volume
    // verification block was executed by nothing in the suite.

    [Fact]
    public async Task LegacyCav_AllVolumeCrcsMatch_IsAMatch()
    {
        using AssemblyTestHost host = NewHost();
        BruteForceOptions options = host.Options(fixture: null, completeAllVolumes: true,
            originalRarFileNamesOverride: ["t.rar", "t.r00"]);
        options.Hashes.Add(CarrierCrc());
        options.ExpectedVolumeCrcs["t.rar"] = CarrierCrc();
        options.ExpectedVolumeCrcs["t.r00"] = CrcOf(TriggerBytes);

        host.Runner.OnLaunch = l =>
        {
            File.WriteAllBytes(l.OutputFilePath, CarrierBytes);
            File.WriteAllBytes(SecondVolumePath(l.OutputFilePath), TriggerBytes);
            l.Exit.TrySetResult(0);
        };

        BruteForceRunResult result = await WithTimeoutAsync(
            host.Manager.BruteForceRARVersionAsync(options), "the legacy CAV run to finish");

        Assert.True(result.Success);
        Assert.DoesNotContain(host.Log.Entries, e => e.Message.Contains("CRC mismatch", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Log.Entries, e => e.Message.Contains("volume(s), expected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LegacyCav_SecondVolumeCrcMismatch_IsNoMatch_AndLogsTheMismatch()
    {
        // Volume 1's CRC stays correct so the first-volume gate still matches; only the SECOND
        // volume's expected CRC is wrong, so the per-volume block is the sole thing that can
        // reject this candidate. Pins both the rejection and the exact log wording.
        using AssemblyTestHost host = NewHost();
        BruteForceOptions options = host.Options(fixture: null, completeAllVolumes: true,
            originalRarFileNamesOverride: ["t.rar", "t.r00"]);
        options.Hashes.Add(CarrierCrc());
        options.ExpectedVolumeCrcs["t.rar"] = CarrierCrc();
        options.ExpectedVolumeCrcs["t.r00"] = "ffffffff"; // deliberately wrong

        host.Runner.OnLaunch = l =>
        {
            File.WriteAllBytes(l.OutputFilePath, CarrierBytes);
            File.WriteAllBytes(SecondVolumePath(l.OutputFilePath), TriggerBytes);
            l.Exit.TrySetResult(0);
        };

        BruteForceRunResult result = await WithTimeoutAsync(
            host.Manager.BruteForceRARVersionAsync(options), "the legacy CAV run to finish");

        Assert.False(result.Success);
        Assert.Contains(host.Log.Entries, e =>
            e.Message.Contains("first volume matched but", StringComparison.Ordinal)
            && e.Message.Contains("CRC mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Legacy_DuplicateHashAcrossCandidates_SecondCarrierDeleted_WhenDeleteDuplicatesSet()
    {
        // Two versions produce byte-identical non-matching carriers. The first records the hash;
        // the second sees it already in fileHashes (isDuplicateHash) and — with
        // DeleteDuplicateCRCFiles set and DeleteRARFiles NOT set — must delete its own carrier
        // while the first candidate's file stays. Pins the legacy duplicate arm, which is
        // otherwise covered only on the assembly side.
        using AssemblyTestHost host = NewHost();
        host.AddSecondVersion();
        BruteForceOptions options = host.Options(fixture: null, completeAllVolumes: false,
            deleteRarFiles: false, deleteDuplicates: true);
        options.Hashes.Add("ffffffff"); // never matches, so every candidate is a mismatch

        List<string> written = [];
        host.Runner.OnLaunch = l =>
        {
            File.WriteAllBytes(l.OutputFilePath, CarrierBytes); // identical bytes => duplicate hash
            written.Add(l.OutputFilePath);
            l.Exit.TrySetResult(0);
        };

        BruteForceRunResult result = await WithTimeoutAsync(
            host.Manager.BruteForceRARVersionAsync(options), "the duplicate-hash run to finish");

        Assert.False(result.Success);
        Assert.Equal(2, written.Count);
        Assert.True(File.Exists(written[0]), "the first candidate's carrier is not a duplicate and must be kept");
        Assert.False(File.Exists(written[1]), "the second candidate's carrier is a duplicate and must be deleted");
    }
}
