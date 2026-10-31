using ArcaneCore.Kernel.Net;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Net;

/// <summary>A deadline that fired after a frame's reads had finished must not stay cancelled for the next frame.</summary>
public sealed class ReadDeadlineTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(20);

    /// <summary>Arms the deadline and waits (event-driven, no timing assertion) until it has fired.</summary>
    private static async Task ArmAndWaitUntilFiredAsync(ReadDeadline deadline)
    {
        deadline.Arm();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        TaskCompletionSource fired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = deadline.Token.Register(static s => ((TaskCompletionSource)s!).TrySetResult(), fired);
        await fired.Task.WaitAsync(guard.Token);
        Assert.True(deadline.Expired);
    }

    [Fact]
    public async Task ATimerThatFiresBeforeDisarm_DoesNotLeaveTheNextFrameCancelled()
    {
        using var deadline = new ReadDeadline(CancellationToken.None, Budget);
        await ArmAndWaitUntilFiredAsync(deadline); // the frame completed, but the timer won the race with Disarm

        deadline.Disarm();
        deadline.Arm(); // next frame

        Assert.False(deadline.Token.IsCancellationRequested);
        Assert.False(deadline.Expired);
    }

    [Fact]
    public async Task ADisarmedDeadline_IsNotReportedExpired()
    {
        using var deadline = new ReadDeadline(CancellationToken.None, Budget);
        await ArmAndWaitUntilFiredAsync(deadline);

        deadline.Disarm();

        Assert.False(deadline.Expired);
    }

    [Fact]
    public async Task ATimerThatFiresAfterDisarm_IsClearedByTheNextArm()
    {
        using var deadline = new ReadDeadline(CancellationToken.None, Budget);
        await ArmAndWaitUntilFiredAsync(deadline);

        deadline.Arm(); // no Disarm in between: Arm itself must not inherit the stale cancellation

        Assert.False(deadline.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task ARealTimeout_StillCancelsAPendingRead()
    {
        using var deadline = new ReadDeadline(CancellationToken.None, Budget);
        deadline.Arm();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(TimeSpan.FromSeconds(30), deadline.Token));
        Assert.True(deadline.Expired);
    }

    [Fact]
    public async Task OuterCancellation_IsNotATimeout_AndSurvivesDisarmAndArm()
    {
        using var outer = new CancellationTokenSource();
        using var deadline = new ReadDeadline(outer.Token, TimeSpan.FromMinutes(5));
        deadline.Arm();
        await outer.CancelAsync();

        deadline.Disarm();
        deadline.Arm();

        Assert.True(deadline.Token.IsCancellationRequested);
        Assert.False(deadline.Expired);
    }
}
