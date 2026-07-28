using System.Diagnostics;
using ReScene.Core;
using ReScene.Core.Cryptography;
using ReScene.Core.IO;
using ReScene.RAR;

namespace ReScene.Tests;

/// <summary>
/// Tests for the Manager-side SRR-guided-assembly ENGAGEMENT preflight (Task 7) and candidate
/// flow (Task 8: the quick gate, incomplete-snapshot retry, and post-retry classification/
/// retention). The preflight is a once-per-set check, run before the attribute loop, that
/// resolves to one of three outcomes — Success (engages assembly), UnsupportedSrr (falls through
/// to the existing legacy candidate loop, completely unchanged), or Error (the whole set fails
/// before any candidate is launched, not a silent legacy fallback). This file grows alongside the
/// assembly candidate flow in later tasks.
/// </summary>
public class ManagerAssemblyFlowTests : TempDirTestBase
{
    private static readonly byte[] CarrierBytes = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];

    private AssemblyTestHost NewHost() => new(TempDir);

    /// <summary>
    /// The CRC32 <see cref="HashCalculator"/> would report for <see cref="CarrierBytes"/>, computed
    /// via a disposable scratch file — the exact production code path, not a re-derivation of the
    /// algorithm (mirrors the identical helper in ManagerProducerLifecycleTests).
    /// </summary>
    private string CarrierCrc()
    {
        string scratch = Path.Combine(TempDir, $"scratch-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(scratch, CarrierBytes);
        return HashCalculator.Calculate(HashType.CRC32, scratch);
    }

    /// <summary>Awaits <paramref name="task"/>, failing fast with a clear message instead of hanging
    /// the test indefinitely if the run never completes (e.g. a regression that lets an Error
    /// preflight fall through to a launch that FakeRunner never resolves).</summary>
    private static async Task<T> WithTimeoutAsync<T>(Task<T> task, string because, TimeSpan? timeout = null)
    {
        Task winner = await Task.WhenAny(task, Task.Delay(timeout ?? TimeSpan.FromSeconds(5)));
        if (winner != task)
        {
            throw new TimeoutException($"Timed out waiting for: {because}");
        }

        return await task;
    }

    /// <summary>Polls <paramref name="condition"/> until true, failing with a clear message if it
    /// never becomes true within <paramref name="timeout"/> (default 5s) — used for test-setup
    /// synchronization (e.g. "wait until the first assembly attempt has run"), not as the
    /// assertion itself.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string because, TimeSpan? timeout = null)
    {
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(5);
        Stopwatch sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > limit)
            {
                throw new TimeoutException($"Timed out waiting for: {because}");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Old-style second-volume path (".r00") for <paramref name="firstVolumePath"/> — the
    /// candidate's OWN carrier naming, matching <see cref="RARVolumeNaming"/>'s early-termination/
    /// CAV monitor probes (mirrors the identical helper in ManagerProducerLifecycleTests).</summary>
    private static string SecondVolumePath(string firstVolumePath)
        => Path.Combine(Path.GetDirectoryName(firstVolumePath)!, Path.GetFileNameWithoutExtension(firstVolumePath) + ".r00");

    private static byte[] Payload(int n, int seed) =>
        [.. Enumerable.Range(0, n).Select(i => (byte)((i * 31 + seed) % 251))];

    /// <summary>
    /// Mirror-shift fixture: a single file split across several volumes, with the produced
    /// shape's EXT_TIME presence differing from the original's — the exact shape <see
    /// cref="SRRAssemblyTests.MirrorShift_ReadsAcrossProducedBoundary"/> proves reads across the
    /// produced-volume boundary. Reconstructing ORIGINAL volume 1 alone needs a few bytes physically
    /// located at the START of PRODUCED volume 2 (a single EXT_TIME field's size difference, 5
    /// bytes, for this single-file/single-piece-per-volume shape) — never more.
    /// </summary>
    private static AssemblyFixture BuildMirrorShiftFixture(string dir) =>
        AssemblyFixtureBuilder.Build(dir, 15_000, [("a.bin", Payload(40_000, 1))],
            originalHasExtTime: false, producedHasExtTime: true);

    /// <summary>
    /// Single-volume fixture: the whole payload fits inside one volume for BOTH shapes (no
    /// cross-volume spanning at all), for scenarios that only need a valid SRR plus a real,
    /// complete carrier volume — not the mirror-shift mechanics.
    /// </summary>
    private static AssemblyFixture BuildSingleVolumeFixture(string dir) =>
        AssemblyFixtureBuilder.Build(dir, 15_000, [("a.bin", Payload(500, 1))],
            originalHasExtTime: true, producedHasExtTime: true);

    /// <summary>
    /// The first <paramref name="length"/> bytes of <paramref name="fullVolumeBytes"/> — short
    /// enough that no RAR4 file-header block can be parsed from it at all (marker(7) + archive
    /// header(13) = 20 bytes is the earliest offset a file-header block's OWN base header could
    /// even start being read, and this codebase's RAR4HeaderBuilder emits exactly those lengths),
    /// so RARStream registers ZERO packed-data volume entries for it — yet the file still EXISTS,
    /// satisfying the CAV/non-CAV second-volume-detection trigger.
    /// </summary>
    private static byte[] HeaderOnlyStub(byte[] fullVolumeBytes, int length = 20)
        => fullVolumeBytes[..Math.Min(length, fullVolumeBytes.Length)];

    [Fact]
    public async Task PreflightDecline_RunsLegacyFromCandidateOne_NoProducerCancelled()
    {
        // A Service block named "RR" is genuine, unconditional UnsupportedSrr evidence (see
        // SRRPreflightTests.RealEvidence_Declines_WithNamedDiagnostic, the "rrService" case) — the
        // engagement preflight must decline assembly and fall through to the existing legacy
        // candidate loop, completely unchanged: candidate 1's carrier output still gets hashed and
        // matched exactly as it would with no SRR involved at all.
        string srr = new SRRTestDataBuilder().AddSRRHeader("t")
            .AddRARFileWithHeaders("a.rar", 0, h => h
                .AddArchiveHeader()
                .AddServiceBlock("RR", 64, includeData: false)
                .AddEndArchive())
            .BuildToFile(TempDir, "t.srr");

        using AssemblyTestHost host = NewHost();
        BruteForceOptions options = host.Options(fixture: null, completeAllVolumes: false,
            srrFilePathOverride: srr, originalRarFileNamesOverride: ["a.rar"]);
        options.Hashes.Add(CarrierCrc());

        host.Runner.OnLaunch = launch =>
        {
            File.WriteAllBytes(launch.OutputFilePath, CarrierBytes);
            launch.Exit.TrySetResult(0);
        };

        BruteForceRunResult result = await WithTimeoutAsync(
            host.Manager.BruteForceRARVersionAsync(options), "the run to finish");

        Assert.True(result.Success);
        Assert.Single(result.Matches);
        Assert.Single(host.Runner.Launches);
        Assert.False(host.Runner.Launches[0].CancellationRequested.Task.IsCompleted);
        Assert.True(host.Log.Count("trying legacy reconstruction for this set") >= 1); // secondary
    }

    [Fact]
    public async Task PreflightError_FailsTheSet_BeforeAnyLaunch()
    {
        // Malformed SRR bytes (no recognizable block structure at all) make PreflightSet return
        // Error — see SRRPreflightTests.MalformedSrr_IsError_NotUnsupported. An unreadable/malformed
        // SRR must fail the whole set before any candidate is launched, never silently degrade to
        // legacy reconstruction the way a genuine UnsupportedSrr decline does.
        string srr = Path.Combine(TempDir, "bad.srr");
        File.WriteAllBytes(srr, [0x01, 0x02, 0x03]);

        using AssemblyTestHost host = NewHost();
        BruteForceOptions options = host.Options(fixture: null, completeAllVolumes: false,
            srrFilePathOverride: srr, originalRarFileNamesOverride: ["a.rar"]);

        OperationCompletionStatus? finalStatus = null;
        host.Manager.BruteForceStatusChanged += (_, e) =>
        {
            if (e.NewStatus == OperationStatus.Completed)
            {
                finalStatus = e.CompletionStatus;
            }
        };

        BruteForceRunResult result = await WithTimeoutAsync(
            host.Manager.BruteForceRARVersionAsync(options), "the run to finish");

        Assert.False(result.Success);
        Assert.Empty(host.Runner.Launches);
        Assert.Equal(OperationCompletionStatus.Error, finalStatus);
    }

    // ---- Task 8: quick gate, incomplete-snapshot retry, and post-retry classification/retention ----
    //
    // ATTEMPT PROBE: AssembleCandidateAsync logs one DEBUG line per invocation —
    // "Assembly attempt for {candidateSlug}: volumes={volumeCount}" — the tests below count THOSE
    // via RecordingLogger ("Assembled hash" is logged only once per candidate, post-retry, so it
    // cannot count attempts).
    //
    // None of these assert BruteForceRunResult.Success/Matches: whether a quick-gate MATCH goes on
    // to become a full winning combination runs through the pre-existing CAV full-per-volume-
    // verification and rename machinery, which still compares the CARRIER's own (produced-shape)
    // bytes against the original CRCs/volume count — correct for the legacy path, but not yet
    // assembly-aware (that full-set assembly wiring is a later task's job; AssembleCandidateAsync's
    // own volumeCount parameter already anticipates it via its int.MaxValue "full set" case). These
    // tests stay scoped to what Task 8 actually owns: the quick gate's own classification, logging,
    // and retention.

    [Fact]
    public async Task Cav_IncompleteSnapshot_RetriesOnceWithFreshSource()
    {
        // Mirror-shift fixture: reconstructing ORIGINAL volume 1 needs a few bytes physically
        // located at the START of PRODUCED volume 2. OnLaunch writes a COMPLETE produced volume 1
        // and a HEADER-ONLY (20-byte) stub of volume 2 — enough to satisfy the CAV monitor's
        // File.Exists trigger, but far short of what RARStream needs to register any packed bytes
        // for it — so the first quick-gate attempt runs out of source bytes (SourceExhausted)
        // while the producer (Exit) is still held. Manager awaits Exit and retries with a fresh
        // source; only then does the test complete volume 2 with its real remaining bytes and
        // release Exit, so the retry succeeds.
        string fixtureDir = Path.Combine(TempDir, "fixture");
        Directory.CreateDirectory(fixtureDir);
        AssemblyFixture fixture = BuildMirrorShiftFixture(fixtureDir);
        string producedVol2Path = RARVolumeNaming.GetNextVolumePath(fixture.ProducedFirstVolumePath, isOldNaming: true)!;
        byte[] fullVol2Bytes = File.ReadAllBytes(producedVol2Path);

        using AssemblyTestHost host = NewHost();
        BruteForceOptions options = host.Options(fixture, completeAllVolumes: true);

        FakeRunner.Launch? launch = null;
        host.Runner.OnLaunch = l =>
        {
            launch = l;
            File.Copy(fixture.ProducedFirstVolumePath, l.OutputFilePath, overwrite: true);
            File.WriteAllBytes(SecondVolumePath(l.OutputFilePath), HeaderOnlyStub(fullVol2Bytes));
            // Exit deliberately left unresolved — held through attempt 1's failure and the retry await.
        };

        Task<BruteForceRunResult> runTask = host.Manager.BruteForceRARVersionAsync(options);

        await WaitUntilAsync(() => launch is not null, "the candidate to launch");
        await WaitUntilAsync(() => host.Log.Count("Assembly attempt") >= 1, "the first assembly attempt to run");

        // This delay guards a DIFFERENT race than retryEligible (Manager now snapshots that BEFORE
        // starting the attempt, so it can never be affected by anything this test does after the
        // log line — see Cav_ProducerCompletesDuringAttempt_RetryStillTriggers, which proves that
        // directly and needs no delay at all). What THIS test still needs to guard against is
        // CONTENT: this line repairs volume 2's bytes on disk, and attempt 1 reads from that same
        // path. If the repair lands before attempt 1's own read reaches volume 2, attempt 1 would
        // see the ALREADY-COMPLETE file and succeed outright — never exercising the retry this test
        // is named for (only 1 attempt line, not 2). Attempt 1's own work (SRR preflight + a small
        // read) is pure local file I/O with no artificial delay, so this margin lets it genuinely
        // finish reading the OLD (truncated) bytes and reach the retry await first.
        await Task.Delay(200);
        File.WriteAllBytes(SecondVolumePath(launch!.OutputFilePath), fullVol2Bytes);
        launch.Exit.TrySetResult(0);

        await WithTimeoutAsync(runTask, "the run to finish");

        Assert.Equal(2, host.Log.Count("Assembly attempt"));
        Assert.Contains(host.Log.Entries, e => e.Message.Contains("Assembled hash for", StringComparison.Ordinal)
            && e.Message.Contains("match: True", StringComparison.Ordinal));
        Assert.Contains(host.Log.Entries, e => e.Message.Contains("Assembly match found for", StringComparison.Ordinal));
        Assert.Single(host.Runner.Launches); // the retry reuses the SAME producer — no relaunch
    }

    [Fact]
    public async Task Cav_PostRetry_SourceExhausted_IsNoMatch()
    {
        // Same mirror-shift shape as the retry-success test, but volume 2's header-only stub is
        // NEVER repaired — "genuinely one volume short". Attempt 1 (producer running) triggers the
        // retry await; the test releases Exit WITHOUT ever completing volume 2, so attempt 2 hits
        // the identical SourceExhausted outcome. CAV means the SourceExhausted-while-non-CAV
        // inconclusive case does not apply, so this is a real no-match with mismatch retention.
        string fixtureDir = Path.Combine(TempDir, "fixture");
        Directory.CreateDirectory(fixtureDir);
        AssemblyFixture fixture = BuildMirrorShiftFixture(fixtureDir);
        string producedVol2Path = RARVolumeNaming.GetNextVolumePath(fixture.ProducedFirstVolumePath, isOldNaming: true)!;
        byte[] stubVol2Bytes = HeaderOnlyStub(File.ReadAllBytes(producedVol2Path));

        using AssemblyTestHost host = NewHost();
        BruteForceOptions options = host.Options(fixture, completeAllVolumes: true, deleteRarFiles: true);

        FakeRunner.Launch? launch = null;
        host.Runner.OnLaunch = l =>
        {
            launch = l;
            File.Copy(fixture.ProducedFirstVolumePath, l.OutputFilePath, overwrite: true);
            File.WriteAllBytes(SecondVolumePath(l.OutputFilePath), stubVol2Bytes);
            // Exit deliberately left unresolved.
        };

        List<BruteForceProgressEventArgs> progressEvents = [];
        host.Manager.BruteForceProgress += (_, e) => progressEvents.Add(e);

        Task<BruteForceRunResult> runTask = host.Manager.BruteForceRARVersionAsync(options);

        await WaitUntilAsync(() => launch is not null, "the candidate to launch");
        await WaitUntilAsync(() => host.Log.Count("Assembly attempt") >= 1, "the first assembly attempt to run");

        // No delay needed here (unlike Cav_IncompleteSnapshot): this test never rewrites volume 2's
        // bytes, so there is no content race to guard against, and retryEligible is snapshotted
        // BEFORE the attempt starts — strictly before this test can possibly touch Exit — so it is
        // unaffected by whatever happens after this line.

        // Released WITHOUT ever completing volume 2.
        launch!.Exit.TrySetResult(0);

        await WithTimeoutAsync(runTask, "the run to finish");

        Assert.Equal(2, host.Log.Count("Assembly attempt"));
        Assert.DoesNotContain(progressEvents, e => e.CombinationFailed);

        string assemblyDir = Path.Combine(host.WorkDir, "output", $"assembled-{Path.GetFileNameWithoutExtension(launch.OutputFilePath)}");
        Assert.False(Directory.Exists(assemblyDir));
        Assert.False(File.Exists(launch.OutputFilePath));
    }

    [Fact]
    public async Task Cav_PersistentError_IsFailedCombination_AndRetains()
    {
        // Garbage vol1 (shorter than the 7-byte RAR4 marker skip, so RARStream's volume scan finds
        // ZERO parseable file headers regardless of vol2's content) plus a stub vol2 (just needs to
        // exist for the CAV vol-2-or-completion trigger — without SOME vol2 file, a held Exit would
        // deadlock before attempt 1 ever ran). RARStream's constructor throws ArgumentException
        // ("File not found in the archive") on every attempt, deterministically, since garbage vol1
        // never changes between attempts — SRRReconstructor's inner catch converts this to a
        // Status.Error RESULT (not a thrown exception out of AssembleCandidateAsync).
        string fixtureDir = Path.Combine(TempDir, "fixture");
        Directory.CreateDirectory(fixtureDir);
        AssemblyFixture fixture = BuildSingleVolumeFixture(fixtureDir);

        using AssemblyTestHost host = NewHost();
        BruteForceOptions options = host.Options(fixture, completeAllVolumes: true, deleteRarFiles: true);

        FakeRunner.Launch? launch = null;
        host.Runner.OnLaunch = l =>
        {
            launch = l;
            File.WriteAllBytes(l.OutputFilePath, new byte[4]); // garbage: shorter than the RAR4 marker
            File.WriteAllBytes(SecondVolumePath(l.OutputFilePath), [0x00]); // stub: just needs to exist
            // Exit deliberately left unresolved.
        };

        List<BruteForceProgressEventArgs> progressEvents = [];
        host.Manager.BruteForceProgress += (_, e) => progressEvents.Add(e);

        Task<BruteForceRunResult> runTask = host.Manager.BruteForceRARVersionAsync(options);

        await WaitUntilAsync(() => launch is not null, "the candidate to launch");
        await WaitUntilAsync(() => host.Log.Count("Assembly attempt") >= 1, "the first assembly attempt to run");

        // No delay needed (unlike Cav_IncompleteSnapshot): the garbage carrier is never rewritten,
        // so there is no content race, and retryEligible is snapshotted before the attempt starts —
        // strictly before this test can touch Exit — so it is unaffected by this test's timing.

        // Released WITHOUT repairing the garbage carrier.
        launch!.Exit.TrySetResult(0);

        await WithTimeoutAsync(runTask, "the run to finish");

        Assert.Equal(2, host.Log.Count("Assembly attempt"));
        Assert.Single(progressEvents, e => e.CombinationFailed);

        string assemblyDir = Path.Combine(host.WorkDir, "output", $"assembled-{Path.GetFileNameWithoutExtension(launch.OutputFilePath)}");
        Assert.True(Directory.Exists(assemblyDir)); // assembled dir retained despite DeleteRARFiles=true
        Assert.True(File.Exists(launch.OutputFilePath)); // carrier retained despite DeleteRARFiles=true
    }

    [Fact]
    public async Task Cav_ProducerCompletesDuringAttempt_RetryStillTriggers()
    {
        // The missed-retry-window regression (Fix Round 1): retryEligible must be snapshotted
        // BEFORE the first attempt starts, not sampled after it returns — a producer that finishes
        // WHILE the attempt is still reading (as opposed to strictly before or after) would make a
        // post-attempt IsCompleted check read true and wrongly skip a retry the incomplete snapshot
        // genuinely needs. Reproduced DETERMINISTICALLY, with no wall-clock racing at all: hooking
        // RecordingLogger.Logged completes Exit the INSTANT the first "Assembly attempt" DEBUG line
        // is recorded — i.e. synchronously, from within Manager's own call stack, before
        // AssembleCandidateAsync has opened the packed source or read a single byte. That puts the
        // producer's completion squarely INSIDE attempt 1's execution window. Volume 2's stub is
        // never repaired, so attempt 1 (and, if the retry fires, attempt 2) both fail with
        // SourceExhausted — this test only asserts the retry ENGAGES, not that it eventually
        // succeeds (Cav_IncompleteSnapshot covers that, with its own separate content-race guard).
        string fixtureDir = Path.Combine(TempDir, "fixture");
        Directory.CreateDirectory(fixtureDir);
        AssemblyFixture fixture = BuildMirrorShiftFixture(fixtureDir);
        string producedVol2Path = RARVolumeNaming.GetNextVolumePath(fixture.ProducedFirstVolumePath, isOldNaming: true)!;
        byte[] stubVol2Bytes = HeaderOnlyStub(File.ReadAllBytes(producedVol2Path));

        using AssemblyTestHost host = NewHost();
        BruteForceOptions options = host.Options(fixture, completeAllVolumes: true);

        FakeRunner.Launch? launch = null;
        host.Runner.OnLaunch = l =>
        {
            launch = l;
            File.Copy(fixture.ProducedFirstVolumePath, l.OutputFilePath, overwrite: true);
            File.WriteAllBytes(SecondVolumePath(l.OutputFilePath), stubVol2Bytes);
            // Exit deliberately left unresolved — completed by the log hook below instead.
        };

        bool triggered = false;
        host.Log.Logged += entry =>
        {
            if (!triggered && entry.Message.StartsWith("Assembly attempt for", StringComparison.Ordinal))
            {
                triggered = true;
                launch!.Exit.TrySetResult(0);
            }
        };

        await WithTimeoutAsync(host.Manager.BruteForceRARVersionAsync(options), "the run to finish");

        Assert.Equal(2, host.Log.Count("Assembly attempt"));
    }

    [Fact]
    public async Task NonCav_MirrorShift_IsInconclusive_LogsGuidanceOnce()
    {
        // Non-CAV: each candidate's producer is fully drained (auto-killed on volume-2 detection)
        // BEFORE the quick gate ever runs, so runningProcessTask is null and the retry never
        // engages regardless of quick.Status — this test is about the (single-attempt)
        // classification itself. Two versions, each writing the SAME mirror-shift volume 1 plus a
        // header-only stub volume 2 (enough to trigger the non-CAV early-termination monitor, but
        // insufficient for the assembly read to complete) — both land on SourceExhausted, which for
        // non-CAV is INCONCLUSIVE, not a no-match: the one-time guidance log must appear exactly
        // once across both candidates, and the per-candidate debug line must appear for each.
        string fixtureDir = Path.Combine(TempDir, "fixture");
        Directory.CreateDirectory(fixtureDir);
        AssemblyFixture fixture = BuildMirrorShiftFixture(fixtureDir);
        string producedVol2Path = RARVolumeNaming.GetNextVolumePath(fixture.ProducedFirstVolumePath, isOldNaming: true)!;
        byte[] stubVol2Bytes = HeaderOnlyStub(File.ReadAllBytes(producedVol2Path));

        using AssemblyTestHost host = NewHost();
        host.AddSecondVersion();
        BruteForceOptions options = host.Options(fixture, completeAllVolumes: false);

        host.Runner.OnLaunch = l =>
        {
            File.Copy(fixture.ProducedFirstVolumePath, l.OutputFilePath, overwrite: true);
            File.WriteAllBytes(SecondVolumePath(l.OutputFilePath), stubVol2Bytes);
            _ = RespondToCancellationAsync(l);
        };

        List<BruteForceProgressEventArgs> progressEvents = [];
        host.Manager.BruteForceProgress += (_, e) => progressEvents.Add(e);

        await WithTimeoutAsync(host.Manager.BruteForceRARVersionAsync(options), "the run to finish");

        Assert.Equal(2, host.Runner.Launches.Count);
        Assert.Equal(1, host.Log.Count("Some candidates are inconclusive without full volumes"));
        Assert.Equal(2, host.Log.Count("inconclusive (assembly needs produced volume 2+)"));
        Assert.DoesNotContain(progressEvents, e => e.CombinationFailed);

        static async Task RespondToCancellationAsync(FakeRunner.Launch l)
        {
            // The standard (non-CAV) path auto-cancels once its own vol-2 monitor detects the stub
            // file; RunAsync's returned task only completes once the test resolves Exit in response
            // (FakeRunner never wires cancellation to Exit itself — see FakeRunner's own remarks).
            await l.CancellationRequested.Task;
            l.Exit.TrySetResult(1);
        }
    }

    [Fact]
    public async Task NonMatch_AssemblyDirRetention_FollowsDeleteFlags()
    {
        // A genuine hash MISMATCH (not Error/SourceExhausted): a single-volume fixture (no
        // cross-volume spanning needed) reconstructs cleanly on the FIRST quick-gate attempt, but
        // options.Hashes is seeded with a value that can never match — landing in the
        // classification switch's default (real no-match) case. Verifies ApplyMismatchRetention's
        // flag-driven deletion of BOTH artifact classes (the assembled dir and the carrier volume).
        async Task<(bool AssemblyDirExists, bool CarrierExists)> RunScenarioAsync(bool deleteRarFiles)
        {
            string fixtureDir = Path.Combine(TempDir, $"fixture-{deleteRarFiles}");
            Directory.CreateDirectory(fixtureDir);
            AssemblyFixture fixture = BuildSingleVolumeFixture(fixtureDir);

            using AssemblyTestHost host = NewHost();
            BruteForceOptions options = host.Options(fixture, completeAllVolumes: false, deleteRarFiles: deleteRarFiles);
            options.Hashes.Clear();
            options.Hashes.Add("00000000"); // deliberately wrong — the assembled hash can never match

            FakeRunner.Launch? launch = null;
            host.Runner.OnLaunch = l =>
            {
                launch = l;
                File.Copy(fixture.ProducedFirstVolumePath, l.OutputFilePath, overwrite: true);
                l.Exit.TrySetResult(1); // single-volume fixture: no second volume ever appears
            };

            await WithTimeoutAsync(host.Manager.BruteForceRARVersionAsync(options), "the run to finish");

            string assemblyDir = Path.Combine(host.WorkDir, "output", $"assembled-{Path.GetFileNameWithoutExtension(launch!.OutputFilePath)}");
            return (Directory.Exists(assemblyDir), File.Exists(launch.OutputFilePath));
        }

        (bool AssemblyDirExists, bool CarrierExists) kept = await RunScenarioAsync(deleteRarFiles: false);
        Assert.True(kept.AssemblyDirExists);
        Assert.True(kept.CarrierExists);

        (bool AssemblyDirExists, bool CarrierExists) deleted = await RunScenarioAsync(deleteRarFiles: true);
        Assert.False(deleted.AssemblyDirExists);
        Assert.False(deleted.CarrierExists);
    }

    // ---- Fix Round 1: quick-gate match must never fall through into legacy finalization ----

    [Fact]
    public async Task NonCav_QuickMatch_NeverCommitsCarrierUnderOriginalName()
    {
        // The CRITICAL fix: before this guard existed, a quick-gate MATCH on an assembly candidate
        // fell through into the LEGACY full-per-volume-verification/RenameMatchedOutput code, which
        // operates on actualRARFilePath — the CARRIER's own produced-shape bytes, not the assembled
        // (SRR-guided) output. In non-CAV mode (this test) that legacy code has no CRC-map gate at
        // all — BuildExpectedInOrder's per-volume verification only ever engages for
        // CompleteAllVolumes — so it moved those WRONG bytes under the original name and reported a
        // false success. A single-volume, mirror-header fixture (originalHasExtTime !=
        // producedHasExtTime, a small payload — no cross-volume spanning needed) makes the
        // carrier's own bytes PROVABLY different from the original's, even though the ASSEMBLED
        // reconstruction of them is byte-identical (asserted below) — exactly the gap the guard
        // closes: nothing should ever be committed under the original name from the quick gate
        // alone, and the run must not report a false success.
        string fixtureDir = Path.Combine(TempDir, "fixture");
        Directory.CreateDirectory(fixtureDir);
        AssemblyFixture fixture = AssemblyFixtureBuilder.Build(fixtureDir, 15_000,
            [("a.bin", Payload(500, 1))], originalHasExtTime: true, producedHasExtTime: false);

        // Confirms the fixture actually exercises the bug: if the carrier were byte-identical to
        // the original, this test would not distinguish the fix from a no-op.
        Assert.NotEqual(File.ReadAllBytes(fixture.OriginalVolumePaths[0]), File.ReadAllBytes(fixture.ProducedFirstVolumePath));

        using AssemblyTestHost host = NewHost();
        BruteForceOptions options = host.Options(fixture, completeAllVolumes: false);

        FakeRunner.Launch? launch = null;
        host.Runner.OnLaunch = l =>
        {
            launch = l;
            File.Copy(fixture.ProducedFirstVolumePath, l.OutputFilePath, overwrite: true);
            l.Exit.TrySetResult(1); // single-volume fixture: no second volume ever appears
        };

        BruteForceRunResult result = await WithTimeoutAsync(
            host.Manager.BruteForceRARVersionAsync(options), "the run to finish");

        string committedPath = Path.Combine(host.WorkDir, "output", fixture.OriginalVolumeNames[0]);
        Assert.False(File.Exists(committedPath));
        Assert.False(result.Success);
        Assert.Contains(host.Log.Entries, e => e.Message.Contains("Assembly match found for", StringComparison.Ordinal));
    }
}
