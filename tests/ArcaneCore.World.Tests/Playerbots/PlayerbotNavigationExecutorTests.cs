using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotNavigationExecutorTests
{
    [Fact]
    public async Task CapacityIncludesActiveQueuedAndUnconsumedResults()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var executor = new PlayerbotNavigationExecutor(() => new GatedPathfinder(gate), capacity: 2);

        Assert.True(executor.TrySubmit(Request(1), out PlayerbotNavigationJob first));
        Assert.True(executor.TrySubmit(Request(1), out PlayerbotNavigationJob second));
        Assert.False(executor.TrySubmit(Request(1), out _));
        Assert.Equal(2, executor.Outstanding);

        gate.SetResult(true);
        await first.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await second.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        // The second result is complete but still unconsumed, so the slot remains held.
        Assert.Equal(2, executor.Outstanding);
        Assert.False(executor.TrySubmit(Request(1), out _));

        first.Dispose();
        second.Dispose();
        Assert.True(executor.TrySubmit(Request(1), out PlayerbotNavigationJob third));
        third.Dispose();
    }

    [Fact]
    public async Task CancellationPublishesDistinctCanceledResultAndReleasesAfterConsume()
    {
        await using var executor = new PlayerbotNavigationExecutor(() => new CancellationPathfinder(), capacity: 1);
        Assert.True(executor.TrySubmit(Request(1), out PlayerbotNavigationJob job));
        job.Cancel();

        PlayerbotNavigationResult result = await job.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PlayerbotNavigationResultStatus.Canceled, result.Status);
        Assert.Equal(1, executor.Outstanding);
        job.Dispose();
        Assert.Equal(0, executor.Outstanding);
    }

    [Fact]
    public async Task AMapChangeCreatesANewPrivatePathfinderContext()
    {
        int factories = 0;
        await using var executor = new PlayerbotNavigationExecutor(() =>
        {
            Interlocked.Increment(ref factories);
            return new StraightPathfinder();
        }, capacity: 2);

        Assert.True(executor.TrySubmit(Request(1), out PlayerbotNavigationJob first));
        PlayerbotNavigationResult firstResult = await first.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        first.Dispose();
        Assert.Equal(PlayerbotNavigationResultStatus.Completed, firstResult.Status);

        Assert.True(executor.TrySubmit(Request(2), out PlayerbotNavigationJob second));
        PlayerbotNavigationResult secondResult = await second.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        second.Dispose();
        Assert.Equal(PlayerbotNavigationResultStatus.Completed, secondResult.Status);
        Assert.Equal(2, factories);
    }

    private static PlayerbotNavigationRequest Request(uint map)
        => new(map, Vector3.Zero, new Vector3(10, 0, 0), PathOptions.Default);

    [Fact]
    public async Task PlanCachesHaveBoundedAdmissionAndAreReleasedOnShutdown()
    {
        var executor = new PlayerbotNavigationExecutor(() => new StraightPathfinder());
        var leases = new List<IDisposable>();
        for (int i = 0; i < PlayerbotNavigationExecutor.DefaultCapacity; i++)
        {
            Assert.True(executor.TryAcquirePlan(out IDisposable lease));
            leases.Add(lease);
        }
        Assert.False(executor.TryAcquirePlan(out _));
        Assert.Equal(PlayerbotNavigationExecutor.DefaultCapacity, executor.ActivePlans);
        leases[0].Dispose();
        Assert.True(executor.TryAcquirePlan(out IDisposable replacement));
        await executor.DisposeAsync();
        Assert.Equal(0, executor.ActivePlans);
        replacement.Dispose();
        foreach (IDisposable lease in leases) lease.Dispose();
    }

    private sealed class GatedPathfinder(TaskCompletionSource<bool> gate) : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            gate.Task.GetAwaiter().GetResult();
            return PathResult.StraightLine(start, end, PathType.Normal);
        }
    }

    private sealed class CancellationPathfinder : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            Assert.NotNull(options);
            Assert.True(options.CancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)), "cancellation did not reach the running query");
            options.CancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("a cancelled query must throw");
        }
    }

    private sealed class StraightPathfinder : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => PathResult.StraightLine(start, end, PathType.Normal);
    }
}
