using System.Numerics;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotDatasetFreshnessTests
{
    [Fact]
    public async Task SameMapDatasetIdentityRecreatesPrivateWorkerContext()
    {
        var first = new NavMeshDatasetDescriptor("dataset-a", Guid.NewGuid());
        var second = new NavMeshDatasetDescriptor("dataset-b", Guid.NewGuid());
        var seen = new List<NavMeshDatasetDescriptor>();
        await using var executor = new PlayerbotNavigationExecutor(dataset =>
        {
            seen.Add(dataset);
            return new StraightPathfinder();
        });

        Assert.True(executor.TrySubmit(Request(first), out PlayerbotNavigationJob a));
        Assert.Equal(PlayerbotNavigationResultStatus.Completed,
            (await a.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        a.Dispose();
        Assert.True(executor.TrySubmit(Request(second), out PlayerbotNavigationJob b));
        Assert.Equal(PlayerbotNavigationResultStatus.Completed,
            (await b.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        b.Dispose();

        Assert.Equal([first, second], seen);
    }

    [Fact]
    public void RawPlanFollowupRetainsTheDatasetDescriptor()
    {
        var dataset = new NavMeshDatasetDescriptor("dataset", Guid.NewGuid());
        var plan = new PlayerbotAsyncNavigationPlan(1, Vector3.Zero, dataset);

        Assert.False(plan.TryGet(Vector3.Zero, new(20, 0, 0), PathOptions.Default, out _));
        Assert.Equal(dataset, plan.NextRequest!.Dataset);
        Assert.Equal(0, plan.NextRequest.MaxRouteYards);
    }

    [Fact]
    public async Task ReplacingLivePrimaryInvalidatesGatedResultAndNewPlanUsesReplacementDataset()
    {
        string firstDirectory = Path.Combine(Path.GetTempPath(), "arcanecore-dataset-a-" + Guid.NewGuid().ToString("N"));
        string secondDirectory = Path.Combine(Path.GetTempPath(), "arcanecore-dataset-b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var first = new NavMeshPathfinder(firstDirectory);
        var second = new NavMeshPathfinder(secondDirectory);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<NavMeshDatasetDescriptor>();
        object seenLock = new();
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<IQuestContentStore>(new QuestTravelFixture(-8648.95f));
            services.AddSingleton<ICreatureDataStore>(new QuestTravelFixture(-8648.95f));
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        PlayerbotWorldDestinations? destinations = null;
        await using var executor = new PlayerbotNavigationExecutor(dataset =>
        {
            lock (seenLock)
            {
                seen.Add(dataset);
                return seen.Count == 1 ? new GatedPathfinder(gate, entered) : new StraightPathfinder();
            }
        }, capacity: 1);

        try
        {
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(pathfinder: first);
                host.WorldServices.GetRequiredService<QuestNpcFeature>().Options.OrdinaryRewardQuestIds = [QuestTravelFixture.QuestId];
                return true;
            });
            destinations = await host.OnWorldAsync(() => new PlayerbotWorldDestinations(session,
                new PlayerbotOptions { Enabled = true, MaxRouteYards = 20_000 }, executor));
            Assert.True(await host.OnWorldAsync(() => destinations!.Update(session.Player!, 0, 500)));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(first.Dataset, Assert.Single(seen));

            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(pathfinder: second);
                Assert.True(destinations!.Update(session.Player!, 0, 500));
                Assert.True(destinations.IsPlanning);
                return true;
            });
            Assert.Equal(0, executor.ActivePlans);

            gate.SetResult(true);
            await WaitUntilAsync(() => executor.Outstanding == 0, "old dataset worker drain");
            Assert.True(await host.OnWorldAsync(() => destinations!.Update(session.Player!, 0, 500)));
            await WaitUntilAsync(() => { lock (seenLock) return seen.Count == 2; }, "replacement dataset worker admission");
            lock (seenLock)
            {
                Assert.Equal(first.Dataset, seen[0]);
                Assert.Equal(second.Dataset, seen[1]);
            }
        }
        finally
        {
            if (destinations is not null) await host.OnWorldAsync(() => destinations.Stop());
            gate.TrySetResult(true);
            await executor.DisposeAsync();
            session.Kick();
            await session.ManagedClosed;
            try { Directory.Delete(firstDirectory, recursive: true); } catch (IOException) { }
            try { Directory.Delete(secondDirectory, recursive: true); } catch (IOException) { }
        }
    }

    private static PlayerbotNavigationRequest Request(NavMeshDatasetDescriptor dataset)
        => new(1, Vector3.Zero, new(10, 0, 0), PathOptions.Default, Dataset: dataset);

    private sealed class StraightPathfinder : IPathfinder
    {
        public bool Enabled => true;
        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => PathResult.StraightLine(start, end, PathType.Normal);
    }

    private sealed class GatedPathfinder(TaskCompletionSource<bool> gate, TaskCompletionSource<bool> entered) : IPathfinder
    {
        public bool Enabled => true;
        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            entered.TrySetResult(true);
            gate.Task.GetAwaiter().GetResult();
            return PathResult.StraightLine(start, end, PathType.Normal);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, string description)
    {
        for (int i = 0; i < 500; i++)
        {
            if (predicate()) return;
            await Task.Delay(10);
        }
        throw new TimeoutException($"timed out waiting for: {description}");
    }
}
