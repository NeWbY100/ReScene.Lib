using ReScene.Core.IO;

namespace ReScene.Tests;

/// <summary>
/// The brute-force progress denominator is an approximation (BruteForceProgressCalculator scales by a
/// Phase-1 version ratio while per-version RAR 6.x timestamp skips vary), so a late combination can push
/// the running count past the estimated size. A progress event must never throw for that — a throw would
/// abort the whole run — so the constructor clamps instead.
/// </summary>
public sealed class OperationProgressEventArgsTests
{
    [Fact]
    public void Constructor_ProgressedExceedsSize_ClampsToSize_DoesNotThrow()
    {
        var e = new OperationProgressEventArgs(operationSize: 10, operationProgressed: 13, startDateTime: DateTime.Now);

        Assert.Equal(10, e.OperationProgressed);
        Assert.Equal(0, e.OperationRemaining);
        Assert.Equal(100.0, e.Progress);
    }

    [Fact]
    public void Constructor_NegativeProgressed_ClampsToZero()
    {
        var e = new OperationProgressEventArgs(operationSize: 10, operationProgressed: -5, startDateTime: DateTime.Now);

        Assert.Equal(0, e.OperationProgressed);
        Assert.Equal(10, e.OperationRemaining);
    }

    [Fact]
    public void Constructor_InRange_IsUnchanged()
    {
        var e = new OperationProgressEventArgs(operationSize: 10, operationProgressed: 4, startDateTime: DateTime.Now);

        Assert.Equal(4, e.OperationProgressed);
        Assert.Equal(6, e.OperationRemaining);
        Assert.Equal(40.0, e.Progress);
    }

    [Fact]
    public void Constructor_ZeroOrNegativeSize_StillThrows()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new OperationProgressEventArgs(0, 0, DateTime.Now));
}
