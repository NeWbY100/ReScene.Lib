using System.Diagnostics;
using System.Text;
using ReScene.Core;
using ReScene.Core.Cryptography;
using ReScene.Core.IO;
using ReScene.RAR;

namespace ReScene.Tests;

/// <summary>
/// Tests for the Manager-side SRR-guided-assembly ENGAGEMENT preflight and candidate
/// flow (the quick gate, incomplete-snapshot retry, and post-retry classification/
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

    /// <summary>Old-style second-volume path (".r00") for <paramref name="firstVolumePath"/> — the
    /// candidate's OWN carrier naming, matching <see cref="RARVolumeNaming"/>'s early-termination/
    /// CAV monitor probes (mirrors the identical helper in ManagerProducerLifecycleTests).</summary>
    private static string SecondVolumePath(string firstVolumePath)
        => Path.Combine(Path.GetDirectoryName(firstVolumePath)!, Path.GetFileNameWithoutExtension(firstVolumePath) + ".r00");

    /// <summary>
    /// Copies <paramref name="fixture"/>'s ENTIRE produced volume set to the candidate's flat
    /// carrier output paths — volume 1 at <paramref name="carrierFirstVolumePath"/>, successors via
    /// old-style naming — the shape a real CompleteAllVolumes rar run leaves behind once every
    /// volume has actually been written (as opposed to the header-only-stub tests above, which
    /// simulate a producer still mid-flight).
    /// </summary>
    private static void CopyFullProducedSet(AssemblyFixture fixture, string carrierFirstVolumePath)
    {
        File.Copy(fixture.ProducedFirstVolumePath, carrierFirstVolumePath, overwrite: true);

        string? producedNext = RARVolumeNaming.GetNextVolumePath(fixture.ProducedFirstVolumePath, isOldNaming: true);
        string? carrierNext = RARVolumeNaming.GetNextVolumePath(carrierFirstVolumePath, isOldNaming: true);
        while (producedNext != null && File.Exists(producedNext))
        {
            File.Copy(producedNext, carrierNext!, overwrite: true);
            producedNext = RARVolumeNaming.GetNextVolumePath(producedNext, isOldNaming: true);
            carrierNext = RARVolumeNaming.GetNextVolumePath(carrierNext!, isOldNaming: true);
        }
    }

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

    // ---- Pack-order diagnostic on quick-gate mismatch ----
    //
    // A field incident: a user's .rarrc injected -ds (a rar default switch), so the LOCAL rar
    // packed a release's files in a different order than the actual release — RARStream's by-name
    // lookup still finds each entry's bytes correctly, so this never surfaces as a parse error, only
    // as a silent hash mismatch. The tests below build a minimal two-file SRR/original ("a.bin" then
    // "b.bin") and a hand-built, standalone produced carrier holding the same two entries — reordered
    // for the positive case, matching order for the negative one. options.Hashes is left empty
    // (fixture: null), which guarantees a quick-gate mismatch regardless of assembled content.

    private const int PackOrderPayloadSize = 8;

    /// <summary>Minimal single-volume SRR ("t.rar") whose embedded original headers list "a.bin"
    /// then "b.bin" — the SRR/"expected" order the pack-order diagnostic compares the produced
    /// carrier's own first-entry name against.</summary>
    private string BuildTwoFileOrderSrr(string srrFileName) =>
        new SRRTestDataBuilder().AddSRRHeader("t")
            .AddRARFileWithHeaders("t.rar", hb => hb
                .AddMarker()
                .AddArchiveHeader()
                .AddFileHeader("a.bin", packedSize: PackOrderPayloadSize, unpackedSize: PackOrderPayloadSize,
                    method: 0x30, extraFlags: RARFileFlags.None)
                .AddFileHeader("b.bin", packedSize: PackOrderPayloadSize, unpackedSize: PackOrderPayloadSize,
                    method: 0x30, extraFlags: RARFileFlags.None)
                .AddEndArchive())
            .BuildToFile(TempDir, srrFileName);

    /// <summary>
    /// Writes a real, standalone single-volume RAR4 carrier at <paramref name="path"/> holding
    /// "a.bin" then "b.bin" (or reversed, when <paramref name="reversed"/> is true), each with
    /// <see cref="PackOrderPayloadSize"/> distinct store-mode bytes. Store mode plus RARStream's
    /// by-name lookup means physical order never changes which bytes get spliced in for a given
    /// name — only which entry is FIRST in the file, which is exactly what the diagnostic inspects.
    /// </summary>
    private static void WriteTwoFileCarrier(string path, bool reversed)
    {
        byte[] aPayload = [.. Enumerable.Repeat((byte)0xAA, PackOrderPayloadSize)];
        byte[] bPayload = [.. Enumerable.Repeat((byte)0xBB, PackOrderPayloadSize)];
        (string Name, byte[] Data)[] entries = reversed
            ? [("b.bin", bPayload), ("a.bin", aPayload)]
            : [("a.bin", aPayload), ("b.bin", bPayload)];

        using FileStream fs = new(path, FileMode.Create, FileAccess.Write);
        using BinaryWriter bw = new(fs);
        var hb = new RAR4HeaderBuilder(bw);
        hb.AddMarker();
        hb.AddArchiveHeader();
        foreach ((string name, byte[] data) in entries)
        {
            hb.AddFileHeader(name, packedSize: (uint)data.Length, unpackedSize: (uint)data.Length,
                method: 0x30, extraFlags: RARFileFlags.None);
            bw.Write(data);
        }

        hb.AddEndArchive();
    }

    [Fact]
    public async Task NonMatch_ProducedPacksDifferentOrder_LogsPackOrderWarningOnce()
    {
        // Both candidates' produced carrier packs "b.bin" before "a.bin" — the reverse of the
        // SRR/original order — while options.Hashes stays empty (fixture: null), guaranteeing a
        // quick-gate mismatch on both. The pack-order diagnostic must fire on both attempts but log
        // only once (the once-per-run guard), at Warning level.
        string srr = BuildTwoFileOrderSrr("order-mismatch.srr");

        using AssemblyTestHost host = NewHost();
        host.AddSecondVersion();
        BruteForceOptions options = host.Options(fixture: null, completeAllVolumes: false,
            srrFilePathOverride: srr, originalRarFileNamesOverride: ["t.rar"]);

        host.Runner.OnLaunch = l =>
        {
            WriteTwoFileCarrier(l.OutputFilePath, reversed: true);
            l.Exit.TrySetResult(1); // single-volume carrier: no second volume ever appears
        };

        await WithTimeoutAsync(host.Manager.BruteForceRARVersionAsync(options), "the run to finish");

        Assert.Equal(2, host.Runner.Launches.Count);
        Assert.Equal(1, host.Log.Count("packs files in a different order"));
        const string expectedWarning =
            "Produced archive packs files in a different order than the release ('b.bin' before 'a.bin') — an /etc/rarfiles.lst order list or a rar default switch such as -ds from .rarrc or the RAR environment variable can cause this.";
        RecordingLogger.LogEntry warningEntry = Assert.Single(host.Log.Entries, e => e.Message == expectedWarning);
        Assert.Equal("Warning", warningEntry.Level);
        // Pins the log PANEL, not just the severity -- the diagnostic runs inside the per-candidate
        // quick-gate loop and must land in the Phase 2 view alongside the rest of that candidate's
        // output, not the System panel.
        Assert.Equal(LogTarget.Phase2, warningEntry.Target);
    }

    [Fact]
    public async Task NonMatch_ProducedPacksDifferentOrder_GuardResetsEachRun_WarnsOncePerRun()
    {
        // _packOrderGuidanceLogged is reset at the top of BruteForceRARVersionAsync (alongside
        // _useAssembly/_inconclusiveGuidanceLogged/_nonAsciiOrderFallbackLogged) -- prove it
        // actually RESETS rather than latching for the Manager's whole lifetime: running the same
        // order-divergent set twice on the SAME Manager/host must warn once per run, twice total.
        string srr = BuildTwoFileOrderSrr("order-mismatch-tworuns.srr");

        using AssemblyTestHost host = NewHost();
        BruteForceOptions options = host.Options(fixture: null, completeAllVolumes: false,
            srrFilePathOverride: srr, originalRarFileNamesOverride: ["t.rar"]);

        host.Runner.OnLaunch = l =>
        {
            WriteTwoFileCarrier(l.OutputFilePath, reversed: true);
            l.Exit.TrySetResult(1); // single-volume carrier: no second volume ever appears
        };

        await WithTimeoutAsync(host.Manager.BruteForceRARVersionAsync(options), "the first run to finish");
        Assert.Equal(1, host.Log.Count("packs files in a different order"));

        await WithTimeoutAsync(host.Manager.BruteForceRARVersionAsync(options), "the second run to finish");
        Assert.Equal(2, host.Log.Count("packs files in a different order"));

        Assert.Equal(2, host.Runner.Launches.Count);
    }

    [Fact]
    public async Task NonMatch_ProducedSameFirstFileOrder_NoPackOrderWarning()
    {
        // Negative case: the produced carrier's first entry ("a.bin") matches the SRR/original's
        // first entry. Still a guaranteed quick-gate mismatch (empty options.Hashes), but nothing
        // about the order is suspicious, so the diagnostic must stay silent.
        string srr = BuildTwoFileOrderSrr("same-order-mismatch.srr");

        using AssemblyTestHost host = NewHost();
        BruteForceOptions options = host.Options(fixture: null, completeAllVolumes: false,
            srrFilePathOverride: srr, originalRarFileNamesOverride: ["t.rar"]);

        host.Runner.OnLaunch = l =>
        {
            WriteTwoFileCarrier(l.OutputFilePath, reversed: false);
            l.Exit.TrySetResult(1); // single-volume carrier: no second volume ever appears
        };

        await WithTimeoutAsync(host.Manager.BruteForceRARVersionAsync(options), "the run to finish");

        Assert.Single(host.Runner.Launches);
        // Negative arm: assert ABSENCE on the wording-stable core of the warning, not on any
        // cause enumeration that may be reworded. A broad substring makes this the strongest
        // spurious-warning detector; the positive flow above pins the full current wording.
        Assert.DoesNotContain(host.Log.Entries,
            e => e.Message.Contains("packs files in a different order", StringComparison.Ordinal));
    }

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
        // A populated file order is the production shape here: the planner fills it for every
        // set, including sets whose preflight later declines assembly. The legacy run below must
        // ignore it completely — rar's own ordering, no -ds, no explicit input tail.
        BruteForceOptions options = host.Options(fixture: null, completeAllVolumes: false,
            srrFilePathOverride: srr, originalRarFileNamesOverride: ["a.rar"],
            orderedArchiveFiles: ["b.bin", "a.bin"]);
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
        Assert.DoesNotContain("-ds", host.Runner.Launches[0].Arguments);
        Assert.Null(host.Runner.Launches[0].InputPaths);
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

    // ---- Quick gate, incomplete-snapshot retry, and post-retry classification/retention ----
    //
    // ATTEMPT PROBE: AssembleCandidateAsync logs one DEBUG line per invocation —
    // "Assembly attempt for {candidateSlug}: volumes={volumeCount}" — the tests below count THOSE
    // via RecordingLogger ("Assembled hash" is logged only once per candidate, post-retry, so it
    // cannot count attempts).
    //
    // None of these assert BruteForceRunResult.Success/Matches: these fixtures' carriers are
    // deliberately INCOMPLETE beyond whatever the quick gate itself needs (a single produced volume,
    // or two for the mirror-shift cases) — the win path (below) performs its OWN full-set
    // assembly once a quick-gate match falls through, and for a carrier this incomplete that full
    // attempt genuinely runs out of source (one more "Assembly attempt" line, counted in the
    // assertions below), rejecting the candidate as a no-match. That is expected and correct — full-
    // set success/failure has its own dedicated tests further down. These tests stay scoped to what
    // this section actually owns: the quick gate's own classification, logging, and retention.

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

        // The quick gate's own attempts: 1 (fails, SourceExhausted) + 1 (retry, succeeds) = 2; the
        // win path then makes a THIRD, full-set assembly attempt. This test's carrier only ever
        // has 2 produced volumes on disk (this fixture's original set needs 3 to reconstruct in
        // full), so that full attempt itself hits SourceExhausted and the candidate is rejected as a
        // no-match — expected, and outside THIS test's scope (the quick gate's own retry mechanic,
        // already proven by the "Assembled hash for... match: True" line below).
        Assert.Equal(3, host.Log.Count("Assembly attempt"));
        Assert.Contains(host.Log.Entries, e => e.Message.Contains("Assembled hash for", StringComparison.Ordinal)
            && e.Message.Contains("match: True", StringComparison.Ordinal));
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
        // The missed-retry-window regression: retryEligible must be snapshotted
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

    // ---- Quick-gate match must commit the ASSEMBLED bytes, never
    // the carrier's ----

    [Fact]
    public async Task NonCav_QuickMatch_NeverCommitsCarrierUnderOriginalName()
    {
        // The strongest correctness pin for the win path. Before its finalizer existed,
        // a quick-gate MATCH on an assembly candidate either fell through into the LEGACY full-per-
        // volume-verification/RenameMatchedOutput code (committing the CARRIER's
        // own produced-shape bytes under the original name) or was retained-but-never-finalized (a
        // temporary guard). Now the win path finalizes the ASSEMBLED reconstruction. A
        // single-volume, mirror-header fixture (originalHasExtTime != producedHasExtTime, a small
        // payload — no cross-volume spanning needed) makes the carrier's own bytes PROVABLY
        // different from the original's, so asserting the committed bytes equal the ORIGINAL'S (and
        // differ from the carrier's) can only pass if the win path committed the assembled
        // reconstruction, never the carrier.
        string fixtureDir = Path.Combine(TempDir, "fixture");
        Directory.CreateDirectory(fixtureDir);
        AssemblyFixture fixture = AssemblyFixtureBuilder.Build(fixtureDir, 15_000,
            [("a.bin", Payload(500, 1))], originalHasExtTime: true, producedHasExtTime: false);

        // Confirms the fixture actually exercises the distinction: if the carrier were byte-identical
        // to the original, this test could not tell an assembled-commit from a carrier-commit.
        byte[] originalBytes = File.ReadAllBytes(fixture.OriginalVolumePaths[0]);
        byte[] carrierBytes = File.ReadAllBytes(fixture.ProducedFirstVolumePath);
        Assert.NotEqual(originalBytes, carrierBytes);

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
        Assert.True(result.Success);
        Assert.True(File.Exists(committedPath));
        byte[] committedBytes = File.ReadAllBytes(committedPath);
        Assert.Equal(originalBytes, committedBytes);
        Assert.NotEqual(carrierBytes, committedBytes);
        Assert.Contains(host.Log.Entries, e => e.Message.Contains("SRR-guided assembly", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NonCav_QuickMatch_FirstVolumeSuccess()
    {
        // Non-CAV: the single assembled volume IS the mode's whole outcome. A matching-shape,
        // single-volume fixture (no mirror-shift mechanics needed here — that is the demonstrator
        // test's job) proves the basic non-CAV win path: exactly one assembled volume is finalized
        // and reported as a success.
        string fixtureDir = Path.Combine(TempDir, "fixture");
        Directory.CreateDirectory(fixtureDir);
        AssemblyFixture fixture = BuildSingleVolumeFixture(fixtureDir);

        using AssemblyTestHost host = NewHost();
        BruteForceOptions options = host.Options(fixture, completeAllVolumes: false);

        host.Runner.OnLaunch = l =>
        {
            File.Copy(fixture.ProducedFirstVolumePath, l.OutputFilePath, overwrite: true);
            l.Exit.TrySetResult(1); // single-volume fixture: no second volume ever appears
        };

        BruteForceRunResult result = await WithTimeoutAsync(
            host.Manager.BruteForceRARVersionAsync(options), "the run to finish");

        Assert.True(result.Success);
        Assert.Single(result.Matches);
        Assert.Single(result.Matches[0].Files);

        string committedPath = Path.Combine(host.WorkDir, "output", fixture.OriginalVolumeNames[0]);
        Assert.True(File.Exists(committedPath));
        Assert.Equal(File.ReadAllBytes(fixture.OriginalVolumePaths[0]), File.ReadAllBytes(committedPath));
    }

    // ---- Full assembly, guarded per-volume verification, and finalization ----

    [Fact]
    public async Task Cav_EndToEnd_ExtTimeScenario_MatchesAndVerifiesAllVolumes()
    {
        // The flagship: the same mirror-shift fixture (original headers smaller, so reconstructing
        // an original volume needs bytes physically located in the NEXT produced volume) as the
        // quick-gate tests above, but with the FULL produced set dropped up front — the shape a real
        // CompleteAllVolumes rar run leaves behind once every volume is actually written. Proves the
        // win path's full-set assembly, per-volume CRC verification, and finalization end to end.
        string fixtureDir = Path.Combine(TempDir, "fixture");
        Directory.CreateDirectory(fixtureDir);
        AssemblyFixture fixture = BuildMirrorShiftFixture(fixtureDir);
        Assert.True(fixture.OriginalVolumePaths.Count > 1); // genuinely multi-volume — spans a boundary

        using AssemblyTestHost host = NewHost();
        BruteForceOptions options = host.Options(fixture, completeAllVolumes: true);

        host.Runner.OnLaunch = l =>
        {
            CopyFullProducedSet(fixture, l.OutputFilePath);
            l.Exit.TrySetResult(0);
        };

        BruteForceRunResult result = await WithTimeoutAsync(
            host.Manager.BruteForceRARVersionAsync(options), "the run to finish");

        Assert.True(result.Success);
        Assert.Contains(host.Log.Entries, e => e.Message.Contains("SRR-guided assembly", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Log.Entries, e => e.Message.Contains("CRC mismatch", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Log.Entries, e => e.Message.Contains("volume(s), expected", StringComparison.Ordinal));

        for (int i = 0; i < fixture.OriginalVolumePaths.Count; i++)
        {
            string committedPath = Path.Combine(host.WorkDir, "output", fixture.OriginalVolumeNames[i]);
            Assert.True(File.Exists(committedPath));
            Assert.Equal(
                HashCalculator.Calculate(HashType.CRC32, fixture.OriginalVolumePaths[i]),
                HashCalculator.Calculate(HashType.CRC32, committedPath));
        }
    }

    [Fact]
    public async Task Cav_FullVerifyMismatch_IsNoMatch_NotSuccess()
    {
        // Corrupt ONE later original CRC (never volume 1's — that must stay correct so the quick
        // gate still matches): full assembly succeeds (the bytes ARE correct), but per-volume
        // verification catches the seeded mismatch — rejected, not a match, run continues to
        // exhaustion.
        string fixtureDir = Path.Combine(TempDir, "fixture");
        Directory.CreateDirectory(fixtureDir);
        AssemblyFixture fixture = BuildMirrorShiftFixture(fixtureDir);
        Assert.True(fixture.OriginalVolumeNames.Count > 1);

        using AssemblyTestHost host = NewHost();
        BruteForceOptions options = host.Options(fixture, completeAllVolumes: true);
        options.ExpectedVolumeCrcs[fixture.OriginalVolumeNames[1]] = "00000000"; // deliberately wrong

        host.Runner.OnLaunch = l =>
        {
            CopyFullProducedSet(fixture, l.OutputFilePath);
            l.Exit.TrySetResult(0);
        };

        BruteForceRunResult result = await WithTimeoutAsync(
            host.Manager.BruteForceRARVersionAsync(options), "the run to finish");

        Assert.False(result.Success);
        Assert.Contains(host.Log.Entries, e => e.Message.Contains("CRC mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoCrcMap_FirstHashOnly_ParityPreserved()
    {
        // Empty CRC map: success rests on the quick gate's volume-1 hash alone (first-hash-only
        // parity) — full assembly and finalization still run (they don't depend on the CRC
        // map), but the per-volume verification block itself must never engage.
        string fixtureDir = Path.Combine(TempDir, "fixture");
        Directory.CreateDirectory(fixtureDir);
        AssemblyFixture fixture = BuildMirrorShiftFixture(fixtureDir);

        using AssemblyTestHost host = NewHost();
        BruteForceOptions options = host.Options(fixture, completeAllVolumes: true);
        options.ExpectedVolumeCrcs.Clear();

        host.Runner.OnLaunch = l =>
        {
            CopyFullProducedSet(fixture, l.OutputFilePath);
            l.Exit.TrySetResult(0);
        };

        BruteForceRunResult result = await WithTimeoutAsync(
            host.Manager.BruteForceRARVersionAsync(options), "the run to finish");

        Assert.True(result.Success);
        Assert.DoesNotContain(host.Log.Entries, e => e.Message.Contains("CRC mismatch", StringComparison.Ordinal));

        for (int i = 0; i < fixture.OriginalVolumePaths.Count; i++)
        {
            string committedPath = Path.Combine(host.WorkDir, "output", fixture.OriginalVolumeNames[i]);
            Assert.True(File.Exists(committedPath));
        }
    }

    [Fact]
    public async Task Cav_QualifiedSetNames_FinalizeAndCleanupHandleSubdirectories()
    {
        // Qualified set names (directoryPrefix "CD2"): the reconstructor writes
        // assemblyDir/CD2/t.rar etc. Finalization must flatten to <work>/output/t.rar
        // (Path.GetFileName strips the qualifier), and the empty assemblyDir/CD2/ subdirectory left
        // behind must be removed recursively as part of success cleanup — never assumed flat.
        string fixtureDir = Path.Combine(TempDir, "fixture");
        Directory.CreateDirectory(fixtureDir);
        AssemblyFixture fixture = AssemblyFixtureBuilder.Build(fixtureDir, 15_000,
            [("a.bin", Payload(500, 1))], originalHasExtTime: true, producedHasExtTime: true,
            directoryPrefix: "CD2");

        using AssemblyTestHost host = NewHost();
        BruteForceOptions options = host.Options(fixture, completeAllVolumes: true);

        FakeRunner.Launch? launch = null;
        host.Runner.OnLaunch = l =>
        {
            launch = l;
            CopyFullProducedSet(fixture, l.OutputFilePath);
            l.Exit.TrySetResult(0);
        };

        BruteForceRunResult result = await WithTimeoutAsync(
            host.Manager.BruteForceRARVersionAsync(options), "the run to finish");

        Assert.True(result.Success);

        string committedPath = Path.Combine(host.WorkDir, "output", "t.rar");
        Assert.True(File.Exists(committedPath));
        Assert.Equal(File.ReadAllBytes(fixture.OriginalVolumePaths[0]), File.ReadAllBytes(committedPath));

        string assemblyDir = Path.Combine(host.WorkDir, "output", $"assembled-{Path.GetFileNameWithoutExtension(launch!.OutputFilePath)}");
        Assert.False(Directory.Exists(assemblyDir)); // fully removed, including the CD2/ subdir
    }

    // ---- Ordered input (-ds + explicit file list) ----
    //
    // When SRR-guided assembly is engaged AND the SRR supplied an archived-file order
    // (RAROptions.OrderedArchiveFiles), the engine must drive rar with that exact order — an
    // explicit file-list tail plus -ds — instead of its own platform mask, so solid-set byte order
    // stops depending on the local machine's rarfiles.lst/name-sort default. The composition
    // happens once per candidate, BEFORE the candidate ever launches: the pre-execution
    // BruteForceProgress event (fired unconditionally, win or lose) and the fake runner's own
    // recorded launch are enough to prove it — none of these carriers need to resolve to an actual
    // match.

    /// <summary>A file-name list whose composed "./name" tail comfortably exceeds the
    /// command-line-length guard (25,000 chars): 3000 short ASCII names is roughly 48,000 chars
    /// once joined, well past the threshold regardless of the fake harness's own (short) exe/output
    /// path lengths.</summary>
    private static List<string> BuildOversizedOrderedFileList() =>
        [.. Enumerable.Range(0, 3000).Select(i => $"file{i:D5}.bin")];

    [Fact]
    public async Task AssemblyMode_OrderedArchiveFiles_AddsDsAndExplicitTail()
    {
        string srr = BuildTwoFileOrderSrr("ordered-input.srr");
        string sep = Path.DirectorySeparatorChar.ToString();

        using AssemblyTestHost host = NewHost();
        BruteForceOptions options = host.Options(fixture: null, completeAllVolumes: false,
            srrFilePathOverride: srr, originalRarFileNamesOverride: ["t.rar"],
            orderedArchiveFiles: ["b.bin", "a.cue"]);

        BruteForceProgressEventArgs? firstEvent = null;
        host.Manager.BruteForceProgress += (_, e) => firstEvent ??= e;
        host.Runner.OnLaunch = l => l.Exit.TrySetResult(0); // no carrier written: a clean no-match

        await WithTimeoutAsync(host.Manager.BruteForceRARVersionAsync(options), "the run to finish");

        Assert.Single(host.Runner.Launches);
        FakeRunner.Launch launch = host.Runner.Launches[0];
        // Boundary assertion (not just the composed ExecutedArguments event string below): proves
        // the engine-added -cfg- actually reaches the invocation FakeRunner records, the same way
        // -ds is already pinned on the next line.
        Assert.Contains("-cfg-", launch.Arguments);
        Assert.Contains("-ds", launch.Arguments);
        Assert.Equal([$".{sep}b.bin", $".{sep}a.cue"], launch.InputPaths);

        Assert.NotNull(firstEvent);
        Assert.Contains("-ds", firstEvent!.ExecutedArguments.Split(' '));
        Assert.Equal(Manager.JoinExecutedArguments([$".{sep}b.bin", $".{sep}a.cue"]), firstEvent.InputFileArguments);
    }

    [Fact]
    public async Task AssemblyMode_NoOrderedArchiveFiles_KeepsMaskAndNoDs()
    {
        // Same assembly-engaged set as above, but OrderedArchiveFiles is left empty (the
        // AssemblyTestHost.Options default) — the engine must fall back to today's mask untouched.
        string srr = BuildTwoFileOrderSrr("ordered-input-empty.srr");

        using AssemblyTestHost host = NewHost();
        BruteForceOptions options = host.Options(fixture: null, completeAllVolumes: false,
            srrFilePathOverride: srr, originalRarFileNamesOverride: ["t.rar"]);

        BruteForceProgressEventArgs? firstEvent = null;
        host.Manager.BruteForceProgress += (_, e) => firstEvent ??= e;
        host.Runner.OnLaunch = l => l.Exit.TrySetResult(0);

        await WithTimeoutAsync(host.Manager.BruteForceRARVersionAsync(options), "the run to finish");

        Assert.Single(host.Runner.Launches);
        FakeRunner.Launch launch = host.Runner.Launches[0];
        Assert.DoesNotContain("-ds", launch.Arguments);
        Assert.Null(launch.InputPaths);

        Assert.NotNull(firstEvent);
        Assert.DoesNotContain("-ds", firstEvent!.ExecutedArguments.Split(' '));
        Assert.Equal("", firstEvent.InputFileArguments);
    }

    [Fact]
    public async Task AssemblyMode_OrderedFilesExceedLengthGuard_FallsBackToListFileButKeepsDs()
    {
        string srr = BuildTwoFileOrderSrr("ordered-input-oversized.srr");
        List<string> names = BuildOversizedOrderedFileList();

        using AssemblyTestHost host = NewHost();
        BruteForceOptions options = host.Options(fixture: null, completeAllVolumes: false,
            srrFilePathOverride: srr, originalRarFileNamesOverride: ["t.rar"],
            orderedArchiveFiles: names);

        BruteForceProgressEventArgs? firstEvent = null;
        host.Manager.BruteForceProgress += (_, e) => firstEvent ??= e;
        host.Runner.OnLaunch = l => l.Exit.TrySetResult(0);

        await WithTimeoutAsync(host.Manager.BruteForceRARVersionAsync(options), "the run to finish");

        Assert.Single(host.Runner.Launches);
        FakeRunner.Launch launch = host.Runner.Launches[0];
        Assert.Contains("-ds", launch.Arguments);

        string expectedListPath = Path.Combine(host.WorkDir, "rar-file-order.lst");
        Assert.Equal([$"@{expectedListPath}"], launch.InputPaths);
        Assert.True(File.Exists(expectedListPath));
        Assert.Equal(names, File.ReadAllText(expectedListPath, Encoding.ASCII).Split('\n'));

        Assert.NotNull(firstEvent);
        Assert.Equal(Manager.JoinExecutedArguments([$"@{expectedListPath}"]), firstEvent!.InputFileArguments);
    }

    [Fact]
    public async Task AssemblyMode_LengthGuardCrossedOnlyByEngineAddedSwitches_FallsBackToListFile()
    {
        // The guard must measure the REAL final command line — exe path + the switches this
        // candidate actually runs with (including engine-added -cfg-/-ds, not just the display-form
        // filteredArguments) + output path + tail — not an approximation that omits them. This
        // harness's CommandLineArguments is a single empty combination, so the candidate's only
        // switches ARE the engine-added ones: "-cfg- -ds". Size a single-entry ordered list so the
        // PRE-final total (exe + 0 switches + output + tail) sits at or under the guard while the
        // POST-final total (exe + "-cfg- -ds" + output + tail) sits over it — i.e. the threshold is
        // crossed ONLY by switches BuildFinalArguments adds, never visible to a guard that measures
        // display args alone.
        string srr = BuildTwoFileOrderSrr("ordered-input-boundary.srr");

        using AssemblyTestHost host = NewHost();

        // Mirrors TryProcessCommandLinesAsync's own exe-path/output-path composition for this
        // harness's single "rar100" candidate with empty CommandLineArguments and both attribute
        // toggles off (TriState.Unchecked default) — fully deterministic ahead of the run, so the
        // synthetic name below can target the guard precisely.
        string expectedRarExeFilePath = Path.Combine(host.VersionsDir, "rar100", RarExecutable.FileName);
        string expectedRarFilePath = Path.Combine(host.WorkDir, "output", "rar100-.rar");

        int newSwitchesLength = Manager.JoinExecutedArguments(["-cfg-", "-ds"]).Length; // engine-added-only total
        const int oldTotal = 24_996; // <= 25,000: a guard measuring display args alone would still admit this
        int newTotal = oldTotal + newSwitchesLength; // > 25,000: the REAL total, which must be rejected instead

        int tailLength = oldTotal - expectedRarExeFilePath.Length - expectedRarFilePath.Length;
        string name = new('x', tailLength - 2); // 2 = the "./" ("." + one separator char) tail-entry prefix

        BruteForceOptions options = host.Options(fixture: null, completeAllVolumes: false,
            srrFilePathOverride: srr, originalRarFileNamesOverride: ["t.rar"],
            orderedArchiveFiles: [name]);

        BruteForceProgressEventArgs? firstEvent = null;
        host.Manager.BruteForceProgress += (_, e) => firstEvent ??= e;
        host.Runner.OnLaunch = l => l.Exit.TrySetResult(0);

        await WithTimeoutAsync(host.Manager.BruteForceRARVersionAsync(options), "the run to finish");

        Assert.Single(host.Runner.Launches);
        FakeRunner.Launch launch = host.Runner.Launches[0];
        Assert.Equal(expectedRarFilePath, launch.OutputFilePath); // confirms the mirrored path composition above
        Assert.True(newTotal > 25_000, "test setup sanity: the real total must exceed the guard");

        Assert.Contains("-ds", launch.Arguments);
        string expectedListPath = Path.Combine(host.WorkDir, "rar-file-order.lst");
        Assert.Equal([$"@{expectedListPath}"], launch.InputPaths);
        Assert.True(File.Exists(expectedListPath));
        Assert.Equal([name], File.ReadAllText(expectedListPath, Encoding.ASCII).Split('\n'));

        Assert.NotNull(firstEvent);
        Assert.Equal(Manager.JoinExecutedArguments([$"@{expectedListPath}"]), firstEvent!.InputFileArguments);
    }

    [Fact]
    public async Task AssemblyMode_OrderedFilesExceedLengthGuardAndNonAscii_FallsBackToMaskWithOneWarning()
    {
        string srr = BuildTwoFileOrderSrr("ordered-input-oversized-nonascii.srr");
        List<string> names = BuildOversizedOrderedFileList();
        names[0] = "café-" + names[0]; // one non-ASCII character is enough to void the @listfile fallback

        using AssemblyTestHost host = NewHost();
        // TWO candidates, so the once-per-run warning contract is actually observable — with a
        // single candidate, per-candidate repetition and once-per-run are indistinguishable.
        BruteForceOptions options = host.Options(fixture: null, completeAllVolumes: false,
            srrFilePathOverride: srr, originalRarFileNamesOverride: ["t.rar"],
            orderedArchiveFiles: names,
            commandLineArguments: [[], [new("-x1", 100)]]);

        BruteForceProgressEventArgs? firstEvent = null;
        host.Manager.BruteForceProgress += (_, e) => firstEvent ??= e;
        host.Runner.OnLaunch = l => l.Exit.TrySetResult(0);

        await WithTimeoutAsync(host.Manager.BruteForceRARVersionAsync(options), "the run to finish");

        Assert.Equal(2, host.Runner.Launches.Count);
        foreach (FakeRunner.Launch launch in host.Runner.Launches)
        {
            Assert.DoesNotContain("-ds", launch.Arguments);
            Assert.Null(launch.InputPaths);
        }

        Assert.False(File.Exists(Path.Combine(host.WorkDir, "rar-file-order.lst")));

        Assert.NotNull(firstEvent);
        Assert.Equal("", firstEvent!.InputFileArguments);

        const string expectedWarning =
            "File names exceed the command-line limit and are not ASCII — using rar's own ordering for this run";
        Assert.Single(host.Log.Entries, e => e.Target == LogTarget.Phase2 && e.Message == expectedWarning);
        Assert.Single(host.Log.WarningMessages, m => m == expectedWarning);
    }
}
