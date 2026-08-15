using ReScene.Core;

namespace ReScene.Tests;

/// <summary>
/// Tests for <see cref="Manager.BruteForceRARVersionAsync"/>'s single-run-at-a-time guard: the
/// run mutates per-run instance state (the linked CTS, assembly/once-per-run flags, the
/// options snapshot), so a second call while one is executing must be rejected promptly — no
/// status events, no state disturbance for the running call — and the instance must remain
/// reusable for sequential runs after both normal and failed completions.
/// </summary>
public class ManagerConcurrencyGuardTests : TempDirTestBase
{
    [Fact]
    public async Task BruteForceRARVersionAsync_CalledWhileRunning_RejectsPromptlyWithoutDisturbingTheRun()
    {
        using AssemblyTestHost host = new(TempDir);
        TaskCompletionSource launched = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeRunner.Launch? firstLaunch = null;
        host.Runner.OnLaunch = l =>
        {
            firstLaunch ??= l;
            launched.TrySetResult();
        };

        BruteForceOptions options = host.Options(fixture: null, completeAllVolumes: false);
        Task<BruteForceRunResult> runTask = host.Manager.BruteForceRARVersionAsync(options);
        await launched.Task; // the first run is genuinely inside a candidate now

        // The rejection must be prompt — a guard-less Manager would instead START a second run
        // (corrupting the first run's CTS and state) and block on its own fake process forever.
        Task<BruteForceRunResult> second = host.Manager.BruteForceRARVersionAsync(options);
        Task winner = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(second, winner);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second);

        firstLaunch!.Exit.TrySetResult(0);
        BruteForceRunResult result = await runTask; // the first run completes undisturbed
        Assert.False(result.Success); // no match expected here — only a clean, un-cancelled finish
    }

    [Fact]
    public async Task BruteForceRARVersionAsync_SequentialRuns_ReuseTheInstance()
    {
        // The guard must release on the setup-failure path too: two consecutive failed runs on
        // ONE instance, both returning results rather than tripping the guard.
        string missingRoot = Path.Combine(TempDir, "no-such-root");
        var options = new BruteForceOptions(missingRoot, TempDir, TempDir);

        using var manager = new Manager();

        Assert.False((await manager.BruteForceRARVersionAsync(options)).Success);
        Assert.False((await manager.BruteForceRARVersionAsync(options)).Success);
    }
}
