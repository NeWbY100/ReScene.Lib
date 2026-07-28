using ReScene.Core;
using ReScene.Core.Cryptography;
using ReScene.Core.IO;

namespace ReScene.Tests;

/// <summary>
/// Tests for the Manager-side SRR-guided-assembly ENGAGEMENT preflight (Task 7): a once-per-set
/// check, run before the attribute loop, that resolves to one of three outcomes — Success (engages
/// assembly, wired starting Task 8), UnsupportedSrr (falls through to the existing legacy candidate
/// loop, completely unchanged), or Error (the whole set fails before any candidate is launched, not
/// a silent legacy fallback). This file grows alongside the assembly candidate flow in later tasks.
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
}
