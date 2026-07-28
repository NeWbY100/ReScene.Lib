using System.Diagnostics;
using ReScene.Core;
using ReScene.RAR;

namespace ReScene.Tests;

/// <summary>
/// Direct tests for <see cref="Manager.FinalizeAssembledSet"/> — the transactional placement step
/// for a guided-assembly win: moves the reconstructor's ordered <c>WrittenPaths</c> verbatim into
/// the output directory, computing each destination file name per <see
/// cref="RAROptions.RenameToOriginalNames"/> (verbatim reuse of the assembled file's own name, or a
/// candidate-slug-based generated name that preserves the volume's own suffix via <see
/// cref="RARVolumeNaming.GetBaseName"/>). These exercise it directly against a
/// filesystem fixture (no rar.exe, no SRR involved) — mirrors <see
/// cref="RenameMatchedOutputTests"/>'s style for the legacy finalizer.
/// </summary>
public class ManagerAssemblyFinalizeTests : TempDirTestBase
{
    private readonly string _rarOutputDir;
    private readonly string _assembledDir;
    private readonly Manager _manager;

    public ManagerAssemblyFinalizeTests()
    {
        _rarOutputDir = Path.Combine(TempDir, "output");
        _assembledDir = Path.Combine(TempDir, "assembled-scratch");
        Directory.CreateDirectory(_rarOutputDir);
        Directory.CreateDirectory(_assembledDir);
        _manager = new Manager();
    }

    private string CreateAssembled(string fileName, string contents = "data")
    {
        string path = Path.Combine(_assembledDir, fileName);
        File.WriteAllText(path, contents);
        return path;
    }

    private static BruteForceOptions MakeOptions(bool renameToOriginalNames)
        => new("winrar", "release", "output")
        {
            RAROptions = new RAROptions
            {
                RenameToOriginalNames = renameToOriginalNames,
            },
        };

    [Fact]
    public void OriginalNames_PlacesUnderWorkOutput()
    {
        // The assembled files already carry the ORIGINAL (SRR-recorded) volume names — with
        // RenameToOriginalNames on, the finalizer must place them verbatim, unchanged, directly
        // under the output directory.
        string v1 = CreateAssembled("release.rar");
        string v2 = CreateAssembled("release.r00");

        BruteForceOptions options = MakeOptions(renameToOriginalNames: true);

        (IReadOnlyList<string> placed, bool complete) =
            _manager.FinalizeAssembledSet(options, [v1, v2], "release", _rarOutputDir);

        Assert.True(complete);
        string expectedDest0 = Path.Combine(_rarOutputDir, "release.rar");
        string expectedDest1 = Path.Combine(_rarOutputDir, "release.r00");
        Assert.Equal([expectedDest0, expectedDest1], placed);
        Assert.True(File.Exists(expectedDest0));
        Assert.True(File.Exists(expectedDest1));
        Assert.False(File.Exists(v1));
        Assert.False(File.Exists(v2));
    }

    [Fact]
    public void GeneratedNames_PreservesPartNNSuffix_Distinct()
    {
        // Regression pin: a naive Path.GetFileNameWithoutExtension-based rename would strip only
        // ".rar" from both "release.part01.rar" and "release.part02.rar" (leaving "release.part01"
        // / "release.part02" as the "base"), or worse, collapse both to the same generated name.
        // RARVolumeNaming.GetBaseName strips the WHOLE ".partNN.rar" suffix, so the two volumes'
        // generated names stay DISTINCT.
        string v1 = CreateAssembled("release.part01.rar");
        string v2 = CreateAssembled("release.part02.rar");

        BruteForceOptions options = MakeOptions(renameToOriginalNames: false);

        (IReadOnlyList<string> placed, bool complete) =
            _manager.FinalizeAssembledSet(options, [v1, v2], "slug", _rarOutputDir);

        Assert.True(complete);
        string expectedDest0 = Path.Combine(_rarOutputDir, "slug-assembled.part01.rar");
        string expectedDest1 = Path.Combine(_rarOutputDir, "slug-assembled.part02.rar");
        Assert.Equal([expectedDest0, expectedDest1], placed);
        Assert.NotEqual(expectedDest0, expectedDest1);
        Assert.True(File.Exists(expectedDest0));
        Assert.True(File.Exists(expectedDest1));
    }

    [Fact]
    public void GeneratedNames_OldStyleSuffixes()
    {
        // Old-style volumes (.rar/.r00/.r01): GetBaseName falls back to
        // Path.GetFileNameWithoutExtension, so each volume's own single-extension suffix
        // (".rar"/".r00"/".r01") is preserved on the generated name.
        string v1 = CreateAssembled("release.rar");
        string v2 = CreateAssembled("release.r00");
        string v3 = CreateAssembled("release.r01");

        BruteForceOptions options = MakeOptions(renameToOriginalNames: false);

        (IReadOnlyList<string> placed, bool complete) =
            _manager.FinalizeAssembledSet(options, [v1, v2, v3], "slug", _rarOutputDir);

        Assert.True(complete);
        string expectedDest0 = Path.Combine(_rarOutputDir, "slug-assembled.rar");
        string expectedDest1 = Path.Combine(_rarOutputDir, "slug-assembled.r00");
        string expectedDest2 = Path.Combine(_rarOutputDir, "slug-assembled.r01");
        Assert.Equal([expectedDest0, expectedDest1, expectedDest2], placed);
        Assert.True(File.Exists(expectedDest0));
        Assert.True(File.Exists(expectedDest1));
        Assert.True(File.Exists(expectedDest2));
    }

    [Fact]
    public void GeneratedNames_NoCollisionWithRetainedCarriers()
    {
        // DeleteRARFiles=false: the candidate's own carrier file is retained in rarOutputDir under
        // its OWN (candidate-generated) name. The finalizer's "{candidateSlug}-assembled{suffix}"
        // naming must never collide with it.
        string carrierPath = Path.Combine(_rarOutputDir, "570-m5.rar");
        File.WriteAllText(carrierPath, "carrier-bytes");

        string v1 = CreateAssembled("release.rar");
        BruteForceOptions options = MakeOptions(renameToOriginalNames: false);

        (IReadOnlyList<string> placed, bool complete) =
            _manager.FinalizeAssembledSet(options, [v1], "570-m5", _rarOutputDir);

        Assert.True(complete);
        string expectedDest = Path.Combine(_rarOutputDir, "570-m5-assembled.rar");
        Assert.Equal([expectedDest], placed);
        Assert.True(File.Exists(expectedDest));

        // The carrier is a distinct file, untouched by the finalizer.
        Assert.True(File.Exists(carrierPath));
        Assert.Equal("carrier-bytes", File.ReadAllText(carrierPath));
    }

    [Fact]
    public void Transactional_RejectsWhenAnyDestinationOccupied()
    {
        // Task 9 minor: this test's original name ("RollsBackWhenDestinationOccupied") overstated
        // what it pins. ExecuteMovePlan validates every destination BEFORE moving anything (see its
        // own remarks above), so this is destination-preflight REJECTION — nothing is ever moved,
        // so there is nothing to roll back. Renamed to match the actual mechanism.
        string v1 = CreateAssembled("release.rar");
        string v2 = CreateAssembled("release.r00");

        // A different file already occupies what would be volume 2's destination.
        string decoyPath = Path.Combine(_rarOutputDir, "slug-assembled.r00");
        File.WriteAllText(decoyPath, "decoy");

        BruteForceOptions options = MakeOptions(renameToOriginalNames: false);

        (IReadOnlyList<string> placed, bool complete) =
            _manager.FinalizeAssembledSet(options, [v1, v2], "slug", _rarOutputDir);

        Assert.False(complete);
        Assert.Empty(placed);

        // Nothing was moved — not even volume 1, whose destination was free — because the whole
        // move map is validated before any file is touched (ExecuteMovePlan's own invariant).
        Assert.True(File.Exists(v1));
        Assert.True(File.Exists(v2));
        Assert.Equal("decoy", File.ReadAllText(decoyPath));
        Assert.False(File.Exists(Path.Combine(_rarOutputDir, "slug-assembled.rar")));
    }

    // ---- Task 10: retention matrix (flow-level) ----
    //
    // Pins every outcome x deletion-flag combination through the REAL Manager flow (AssemblyTestHost
    // + FakeRunner + RecordingLogger), asserted on BOTH artifact classes: the assembled scratch dir
    // (the reconstruction's own output, under "<work>/output/assembled-{candidateSlug}") and the
    // carrier (the fake rar run's own produced volumes, under "<work>/output/{candidateSlug}.*").

    private static byte[] MakePayload(int n, int seed) =>
        [.. Enumerable.Range(0, n).Select(i => (byte)((i * 31 + seed) % 251))];

    /// <summary>Old-style second-volume path (".r00") for <paramref name="firstVolumePath"/>.</summary>
    private static string SecondVolumePath(string firstVolumePath)
        => Path.Combine(Path.GetDirectoryName(firstVolumePath)!, Path.GetFileNameWithoutExtension(firstVolumePath) + ".r00");

    /// <summary>
    /// Copies <paramref name="fixture"/>'s entire produced volume set to the candidate's flat
    /// carrier output paths — the shape a real CompleteAllVolumes rar run leaves behind once every
    /// volume has actually been written (mirrors the identical helper in ManagerAssemblyFlowTests).
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

    /// <summary>Polls <paramref name="condition"/> until true, failing with a clear message instead
    /// of hanging indefinitely if it never becomes true within <paramref name="timeout"/> (default
    /// 5s) — used for test-setup synchronization, not as the assertion itself.</summary>
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
    /// the test indefinitely if it never completes within <paramref name="timeout"/> (default 10s) —
    /// so a genuine regression (e.g. broken cancellation-token propagation) produces a deterministic
    /// test failure instead of wedging the whole suite.</summary>
    private static async Task WithTimeoutAsync(Task task, string because, TimeSpan? timeout = null)
    {
        Task winner = await Task.WhenAny(task, Task.Delay(timeout ?? TimeSpan.FromSeconds(10)));
        if (winner != task)
        {
            throw new TimeoutException($"Timed out waiting for: {because}");
        }

        await task;
    }

    /// <summary>Replaces the LAST original volume's expected CRC with a value that can never match:
    /// the quick gate (volume 1 only) still passes, but the win path's full per-volume verification
    /// then rejects the candidate — exercises ApplyMismatchRetention from that second call site.</summary>
    private static void CorruptOneLaterCrc(BruteForceOptions options)
    {
        string lastVolumeName = options.RAROptions.OriginalRARFileNames[^1];
        options.ExpectedVolumeCrcs[lastVolumeName] = "FFFFFFFF";
    }

    [Theory]
    // outcome        deleteRar deleteDup expectAssembledSurvives expectCarrierSurvives
    [InlineData("quickMismatch", true, false, false, false)]
    [InlineData("quickMismatch", false, false, true, true)]
    [InlineData("duplicate", false, true, false, false)] // dup flag deletes dups even when deleteRar=false
    [InlineData("duplicate", false, false, true, true)]
    [InlineData("fullMismatch", true, false, false, false)]
    [InlineData("fullMismatch", false, false, true, true)]
    [InlineData("error", true, false, true, true)] // diagnosis: Error retains BOTH regardless of flags
    [InlineData("exception", true, false, true, true)]
    [InlineData("cancellation", true, false, true, true)]
    [InlineData("success", true, false, false, false)] // rename=true; assembled MOVED out; carrier DELETED
    [InlineData("success", false, false, false, true)] // rename=true; carrier retained in work area
    [InlineData("successGen", true, false, false, false)] // rename=FALSE: generated names, carrier deleted
    [InlineData("successGen", false, false, false, true)] // rename=FALSE: generated names, carrier retained
    public async Task RetentionMatrix(string outcome, bool deleteRarFiles,
        bool deleteDuplicateCrcFiles, bool expectAssembledInWorkArea, bool expectCarrierInWorkArea)
    {
        using var host = new AssemblyTestHost(TempDir);
        AssemblyFixture f = AssemblyFixtureBuilder.Build(host.Root, 15_000,
            [("a.bin", MakePayload(40_000, 1))], originalHasExtTime: true, producedHasExtTime: false);
        BruteForceOptions options = host.Options(f, completeAllVolumes: true,
            deleteRarFiles, deleteDuplicateCrcFiles);

        switch (outcome)
        {
            case "quickMismatch":
                options.Hashes.Clear();
                options.Hashes.Add("00000000");
                break; // never matches
            case "duplicate":
                // Two fake versions; both launches drop the SAME produced set; candidate 2's quick
                // hash equals candidate 1's -> duplicate handling.
                host.AddSecondVersion();
                options.Hashes.Clear();
                options.Hashes.Add("00000000");
                break;
            case "fullMismatch":
                CorruptOneLaterCrc(options);
                break; // quick passes, full verify fails
            case "error":
                f = f with { }; // launch writes garbage vol 1 instead (via StageProducedSet below)
                break;
            case "exception":
            case "cancellation":
                // Synchronization for both (codex rev-5 B2): the fault/cancellation must land AFTER
                // the quick gate has already matched and written a real assembled artifact to disk,
                // never before — see RunManagerAsync below.
                break;
            case "success":
                break; // fixture CRCs already match
            case "successGen":
                options = host.Options(f, completeAllVolumes: true, deleteRarFiles,
                    deleteDuplicateCrcFiles, renameToOriginal: false);
                break;
        }

        // ---- Local driver helpers (close over f/outcome, per the switch above) ----

        // Writes this launch's carrier bytes: garbage (deterministic Status.Error on every attempt,
        // mirrors ManagerAssemblyFlowTests.Cav_PersistentError_IsFailedCombination_AndRetains) or the
        // fixture's full produced set. Exit resolves immediately EXCEPT for garbage (RunManagerAsync
        // releases it once the first attempt is observed) and exception/cancellation (RunManagerAsync
        // releases it only once the quick gate has already matched and written a real artifact) —
        // every OTHER outcome's producer is done as soon as it launches, matching the shape a real
        // CompleteAllVolumes rar run leaves behind once every volume has actually been written.
        void StageProducedSet(AssemblyFixture fixture, FakeRunner.Launch launch, bool garbage)
        {
            if (garbage)
            {
                File.WriteAllBytes(launch.OutputFilePath, new byte[4]); // shorter than the RAR4 marker
                File.WriteAllBytes(SecondVolumePath(launch.OutputFilePath), [0x00]); // stub: just needs to exist
                // Exit held — see RunManagerAsync's "error" branch.
            }
            else
            {
                CopyFullProducedSet(fixture, launch.OutputFilePath);
                if (outcome is not ("exception" or "cancellation"))
                {
                    launch.Exit.TrySetResult(0);
                }
                // else: Exit held — see RunManagerAsync's "exception"/"cancellation" branch.
            }
        }

        // Starts the run and performs the per-outcome Exit/Stop choreography.
        async Task RunManagerAsync(AssemblyTestHost testHost, BruteForceOptions runOptions, string runOutcome)
        {
            Task<BruteForceRunResult> runTask = testHost.Manager.BruteForceRARVersionAsync(runOptions);

            if (runOutcome == "error")
            {
                // The garbage bytes never change between attempts, so releasing after the FIRST
                // attempt is observed is sufficient — any retry lands on the same deterministic error
                // (no content race to guard, mirrors the existing Cav_PersistentError test).
                await WaitUntilAsync(() => testHost.Log.Count("Assembly attempt") >= 1,
                    "the first assembly attempt to run");
                testHost.Runner.Launches[0].Exit.TrySetResult(1);
            }
            else if (runOutcome is "exception" or "cancellation")
            {
                await WaitUntilAsync(() => testHost.Runner.Launches.Count >= 1, "the candidate to launch");
                FakeRunner.Launch launch = testHost.Runner.Launches[0];
                string assemblyDir = Path.Combine(testHost.WorkDir, "output",
                    $"assembled-{Path.GetFileNameWithoutExtension(launch.OutputFilePath)}");
                string assembledVol1 = Path.Combine(assemblyDir, f.OriginalVolumeNames[0]);

                // Wait for BOTH the attempt probe AND the assembled artifact actually existing: the
                // fault/cancellation must be provoked only once something real exists to retain (an
                // immediate fault would abort before any assembly, leaving the retention assertions
                // below with nothing to target — codex rev-5 B2).
                await WaitUntilAsync(
                    () => testHost.Log.Count("Assembly attempt") >= 1 && File.Exists(assembledVol1),
                    "the quick gate to match and write the assembled first volume");

                if (runOutcome == "exception")
                {
                    // Faults the producer task itself: Manager's "let it finish completing all
                    // volumes" await (after the quick-gate match) rethrows this directly into the
                    // generic catch — no retention call on that path, so both classes survive.
                    launch.Exit.TrySetException(new InvalidOperationException("producer fault"));
                }
                else
                {
                    // Cancels via the real Stop() path; CancellationRequested resolves once Manager's
                    // linked token reaches this launch. A real rar process swallows the cancellation
                    // and returns exit 1 (see RARProcess.RunAsync's own remarks) — FakeRunner never
                    // does this automatically (by design), so the test does it explicitly here. That
                    // lets Manager's already-cancelled token fault the SUBSEQUENT full-set assembly
                    // call instead, which Manager re-throws uncaught out of BruteForceRARVersionAsync.
                    //
                    // Bounded wait + finally (review fix): a plain unbounded await here would let a
                    // cancellation-propagation regression (Stop() failing to reach this launch's
                    // token) wedge the whole suite — Exit stays unresolved, Manager's own internal
                    // await never completes, and the OUTER 10s safety net below never even gets a
                    // chance to run, since control never reaches it. Bounding this wait AND always
                    // releasing Exit in finally means such a regression instead fails THIS row
                    // deterministically, with a clear message, and leaves no producer task dangling.
                    try
                    {
                        testHost.Manager.Stop();
                        await WithTimeoutAsync(launch.CancellationRequested.Task,
                            "Manager.Stop() to reach this launch's cancellation token");
                    }
                    finally
                    {
                        launch.Exit.TrySetResult(1);
                    }
                }
            }

            try
            {
                await WithTimeoutAsync(runTask, "the managed run to finish");
            }
            catch (OperationCanceledException) when (runOutcome == "cancellation")
            {
                // Expected: Manager.cs has no top-level catch around the candidate loop, so the
                // already-cancelled token faulting the win path's full-set assembly call escapes
                // BruteForceRARVersionAsync uncaught. The retention assertions below confirm both
                // artifact classes survived this abort — the whole point of this row.
            }
        }

        host.Runner.OnLaunch = l => StageProducedSet(f, l, garbage: outcome == "error");
        Task run = RunManagerAsync(host, options, outcome);
        await run;

        // Per-candidate observability (codex plan-rev-3 B3): the expectations target the candidate
        // the outcome is ABOUT. Candidate ORDER IS NOT GUARANTEED by directory enumeration (codex
        // rev-5 B4) — derive identity from the actual launches.
        static string VersionOf(FakeRunner.Launch l) =>
            Path.GetFileName(l.OutputFilePath).Split('-')[0]; // "rar100-..." / "rar200-..."
        string targetVersion = outcome == "duplicate"
            ? VersionOf(host.Runner.Launches[1])   // the SECOND launch is the duplicate
            : VersionOf(host.Runner.Launches[0]);
        string outputDir = Path.Combine(host.WorkDir, "output");
        string? targetAssembledDir = Directory.Exists(outputDir)
            ? Directory.GetDirectories(outputDir, $"assembled-{targetVersion}*").FirstOrDefault()
            : null;
        bool assembledSurvives = targetAssembledDir is not null && Directory.EnumerateFiles(targetAssembledDir).Any();
        Assert.Equal(expectAssembledInWorkArea, assembledSurvives);

        // Excludes finalized "-assembled.*" placements (successGen mode) from the carrier check: the
        // GENERATED destination name is "{candidateSlug}-assembled{suffix}" (FinalizeAssembledSet),
        // which also starts with the version prefix — without this exclusion, a successful
        // successGen run's own placed output would be misread as a surviving carrier even when the
        // raw carrier itself was actually deleted.
        bool CarrierSurvives(string version) =>
            Directory.Exists(outputDir) && Directory.EnumerateFiles(outputDir)
                .Any(p2 => Path.GetFileName(p2).StartsWith(version, StringComparison.OrdinalIgnoreCase)
                    && !Path.GetFileName(p2).Contains("-assembled", StringComparison.OrdinalIgnoreCase));

        bool carrierSurvives = CarrierSurvives(targetVersion);
        Assert.Equal(expectCarrierInWorkArea, carrierSurvives);

        if (outcome == "duplicate")
        {
            // The UNIQUE first candidate follows the ordinary no-match flags, independent of the
            // duplicate handling — with deleteRarFiles=false it must SURVIVE. Identity from the
            // FIRST launch, never from directory order (codex rev-5 B4).
            string firstVersion = VersionOf(host.Runner.Launches[0]);
            Assert.Equal(!deleteRarFiles, CarrierSurvives(firstVersion));
        }

        if (outcome == "success")
        {
            // The assembled volumes were MOVED to <work>/output under original names.
            Assert.All(f.OriginalVolumeNames, n =>
                Assert.True(File.Exists(Path.Combine(host.WorkDir, "output", Path.GetFileName(n)))));
        }

        if (outcome == "successGen")
        {
            // Generated suffix-preserving names (spec §5): slug-assembled.rar / .r00 / …
            string[] placed = Directory.GetFiles(Path.Combine(host.WorkDir, "output"), "*-assembled.*");
            Assert.Equal(f.OriginalVolumeNames.Count, placed.Length);
            Assert.Contains(placed, p2 => p2.EndsWith("-assembled.rar", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(placed, p2 => p2.EndsWith("-assembled.r00", StringComparison.OrdinalIgnoreCase));
        }
    }
}
