using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>World-thread ownership tests for the bounded asynchronous destination planner.</summary>
public sealed class PlayerbotAsyncDestinationTests
{
    [Fact]
    public async Task GatedFarDestination_HoldsPlanningIntentUntilWorkerCompletes()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arcanecore-async-destination-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var nav = new NavMeshPathfinder(directory);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<IQuestContentStore>(new QuestTravelFixture(-8648.95f));
            services.AddSingleton<ICreatureDataStore>(new QuestTravelFixture(-8648.95f));
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        await using var executor = new PlayerbotNavigationExecutor(() => new GatedPathfinder(gate), capacity: 1);
        PlayerbotWorldDestinations? destinations = null;
        try
        {
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(pathfinder: nav);
                host.WorldServices.GetRequiredService<QuestNpcFeature>().Options.OrdinaryRewardQuestIds = [QuestTravelFixture.QuestId];
                return true;
            });
            destinations = await host.OnWorldAsync(() =>
                new PlayerbotWorldDestinations(session, new PlayerbotOptions { Enabled = true, MaxRouteYards = 20_000 }, executor));

            bool submitted = await host.OnWorldAsync(() => destinations!.Update(session.Player!, 0, 500));
            Assert.True(submitted);
            Assert.True(destinations!.IsPlanning);
            Assert.True(await host.OnWorldAsync(() => destinations!.Update(session.Player!, 0, 500)));
            Assert.True(destinations!.IsPlanning);

            gate.SetResult(true);
            await WaitForWorldBoundedAsync(host, () =>
            {
                if (!destinations!.IsPlanning) return true;
                destinations.Update(session.Player!, 0, 500);
                return !destinations.IsPlanning;
            }, "async destination completion");
        }
        finally
        {
            if (destinations is not null)
                await host.OnWorldAsync(() => destinations!.Stop());
            gate.TrySetResult(true);
            await executor.DisposeAsync();
            await WaitUntilAsync(() => executor.Outstanding == 0, "initial destination worker shutdown");
            session.Kick();
            await session.ManagedClosed;
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ChangingPreferredEntry_SupersedesPendingPlanAndReleasesLease()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arcanecore-async-supersession-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var nav = new NavMeshPathfinder(directory);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<IQuestContentStore>(new QuestTravelFixture(-8648.95f));
            services.AddSingleton<ICreatureDataStore>(new QuestTravelFixture(-8648.95f));
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        PlayerbotWorldDestinations? destinations = null;
        var pathfinder = new CountingGatedPathfinder(gate, entered, secondEntered);
        await using var executor = new PlayerbotNavigationExecutor(() => pathfinder, capacity: 1);
        try
        {
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(pathfinder: nav);
                host.WorldServices.GetRequiredService<QuestNpcFeature>().Options.OrdinaryRewardQuestIds = [QuestTravelFixture.QuestId];
                return true;
            });
            destinations = await host.OnWorldAsync(() =>
                new PlayerbotWorldDestinations(session, new PlayerbotOptions { Enabled = true, MaxRouteYards = 20_000 }, executor));

            Assert.True(await host.OnWorldAsync(() => destinations!.Update(session.Player!, 0, 500)));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, executor.ActivePlans);
            Assert.Equal(1, executor.Outstanding);

            // The second owner call changes the destination identity while the first worker is still in FindPath.
            // uint.MaxValue is a deliberate missing preferred entry: it cannot become a visible or eligible target.
            Assert.False(await host.OnWorldAsync(() => destinations!.Update(session.Player!, uint.MaxValue, 500)));
            Assert.False(destinations!.IsPlanning);
            Assert.Equal(0, executor.ActivePlans);

            // The canceled old request must drain before the bounded worker slot is reused.
            await WaitUntilAsync(() => executor.Outstanding == 0, "superseded destination worker shutdown");
            gate.SetResult(true);
            Assert.True(await host.OnWorldAsync(() => destinations!.Update(session.Player!, QuestTravelFixture.Entry, 500)));
            await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, pathfinder.Calls);
            Assert.Equal(QuestTravelFixture.Entry, destinations!.TargetEntry);
        }
        finally
        {
            if (destinations is not null)
                await host.OnWorldAsync(() => destinations!.Stop());
            gate.TrySetResult(true);
            await executor.DisposeAsync();
            session.Kick();
            await session.ManagedClosed;
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task DeathDuringPendingPlan_CancelsWorkerWithoutApplyingStaleRoute()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arcanecore-async-death-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var nav = new NavMeshPathfinder(directory);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<IQuestContentStore>(new QuestTravelFixture(-8648.95f));
            services.AddSingleton<ICreatureDataStore>(new QuestTravelFixture(-8648.95f));
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        PlayerbotWorldDestinations? destinations = null;
        await using var executor = new PlayerbotNavigationExecutor(() => new GatedPathfinder(gate, entered), capacity: 1);
        try
        {
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(pathfinder: nav);
                host.WorldServices.GetRequiredService<QuestNpcFeature>().Options.OrdinaryRewardQuestIds = [QuestTravelFixture.QuestId];
                return true;
            });
            destinations = await host.OnWorldAsync(() =>
                new PlayerbotWorldDestinations(session, new PlayerbotOptions { Enabled = true, MaxRouteYards = 20_000 }, executor));

            Assert.True(await host.OnWorldAsync(() => destinations!.Update(session.Player!, 0, 500)));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, executor.ActivePlans);

            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                player.Health = 0;
                player.Map!.Combat.KillPlayer(player);
                Assert.False(player.IsAlive);
                Assert.False(destinations!.Update(player, 0, 500));
                return true;
            });
            Assert.False(destinations!.IsPlanning);
            Assert.Equal(0, executor.ActivePlans);
            await WaitUntilAsync(() => executor.Outstanding == 0, "dead bot destination worker shutdown");

            // Releasing the gate after invalidation must not publish the old path as a route.
            gate.SetResult(true);
            Assert.False(await host.OnWorldAsync(() => destinations!.Update(session.Player!, 0, 500)));
            Assert.False(destinations!.IsPlanning);
        }
        finally
        {
            if (destinations is not null)
                await host.OnWorldAsync(() => destinations!.Stop());
            gate.TrySetResult(true);
            await executor.DisposeAsync();
            session.Kick();
            await session.ManagedClosed;
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task MapInstanceChange_CancelsPendingPlanBeforeOldResultCanPublish()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arcanecore-async-map-change-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var nav = new NavMeshPathfinder(directory);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<IQuestContentStore>(new QuestTravelFixture(-8648.95f));
            services.AddSingleton<ICreatureDataStore>(new QuestTravelFixture(-8648.95f));
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        PlayerbotWorldDestinations? destinations = null;
        await using var executor = new PlayerbotNavigationExecutor(() => new GatedPathfinder(gate, entered), capacity: 1);
        try
        {
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(pathfinder: nav);
                host.WorldServices.GetRequiredService<QuestNpcFeature>().Options.OrdinaryRewardQuestIds = [QuestTravelFixture.QuestId];
                return true;
            });
            destinations = await host.OnWorldAsync(() =>
                new PlayerbotWorldDestinations(session, new PlayerbotOptions { Enabled = true, MaxRouteYards = 20_000 }, executor));

            Assert.True(await host.OnWorldAsync(() => destinations!.Update(session.Player!, 0, 500)));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, executor.ActivePlans);

            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                TeleportService teleports = host.WorldServices.GetRequiredService<TeleportFeature>().Teleports;
                Assert.True(teleports.TeleportTo(player, 1, -441.8f, -2596.08f, 96.2f, 0));
                return true;
            });
            await WaitForWorldBoundedAsync(host, () =>
            {
                if (session.Player is not { } player) return false;
                PlayerbotMovementControl.Update(session, player);
                return player.Map?.MapId == 1;
            }, "managed player map transfer");

            Assert.False(await host.OnWorldAsync(() => destinations!.Update(session.Player!, 0, 500)));
            Assert.False(destinations!.IsPlanning);
            Assert.Equal(0, executor.ActivePlans);
            await WaitUntilAsync(() => executor.Outstanding == 0, "map-changed destination worker shutdown");

            // The old gate is released only after map invalidation and worker drain; no stale route may appear.
            gate.SetResult(true);
            Assert.False(await host.OnWorldAsync(() => destinations!.Update(session.Player!, 0, 500)));
            Assert.False(destinations!.IsPlanning);
        }
        finally
        {
            if (destinations is not null)
                await host.OnWorldAsync(() => destinations!.Stop());
            gate.TrySetResult(true);
            await executor.DisposeAsync();
            session.Kick();
            await session.ManagedClosed;
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task SameMapIdDifferentInstance_ValidatePlanningCancelsPendingPlan()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arcanecore-async-instance-change-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var nav = new NavMeshPathfinder(directory);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<IQuestContentStore>(new QuestTravelFixture(-8648.95f));
            services.AddSingleton<ICreatureDataStore>(new QuestTravelFixture(-8648.95f));
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        PlayerbotWorldDestinations? destinations = null;
        Map? originalMap = null;
        await using var executor = new PlayerbotNavigationExecutor(() => new GatedPathfinder(gate, entered), capacity: 1);
        try
        {
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(pathfinder: nav);
                host.WorldServices.GetRequiredService<QuestNpcFeature>().Options.OrdinaryRewardQuestIds = [QuestTravelFixture.QuestId];
                return true;
            });
            destinations = await host.OnWorldAsync(() =>
                new PlayerbotWorldDestinations(session, new PlayerbotOptions { Enabled = true, MaxRouteYards = 20_000 }, executor));

            Assert.True(await host.OnWorldAsync(() => destinations!.Update(session.Player!, 0, 500)));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, executor.ActivePlans);

            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                originalMap = player.Map!;
                Map instance = host.World.GetMap(originalMap!.MapId, 42);
                originalMap!.RemovePlayer(player);
                instance.AddPlayer(player);
                Assert.Equal(originalMap!.MapId, player.MapId);
                Assert.NotSame(originalMap, player.Map);
                destinations!.ValidatePlanning(player);
                return true;
            });
            Assert.False(destinations!.IsPlanning);
            Assert.Equal(0, executor.ActivePlans);
            await WaitUntilAsync(() => executor.Outstanding == 0, "same-map-instance destination worker shutdown");

            // The canceled worker is released only after identity fencing; publishing its old answer cannot recreate planning.
            gate.SetResult(true);
            Assert.False(destinations!.IsPlanning);
        }
        finally
        {
            if (destinations is not null)
                await host.OnWorldAsync(() => destinations!.Stop());
            gate.TrySetResult(true);
            await executor.DisposeAsync();
            if (originalMap is not null)
            {
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    if (!ReferenceEquals(player.Map, originalMap))
                    {
                        player.Map?.RemovePlayer(player);
                        originalMap!.AddPlayer(player);
                    }
                });
            }
            session.Kick();
            await session.ManagedClosed;
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task CreatureDefinitionsRevision_CancelsPendingPlanAndReplansAfterDrain()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arcanecore-async-content-revision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var nav = new NavMeshPathfinder(directory);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<IQuestContentStore>(new QuestTravelFixture(-8648.95f));
            services.AddSingleton<ICreatureDataStore>(new QuestTravelFixture(-8648.95f));
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        PlayerbotWorldDestinations? destinations = null;
        CreatureDefinitions? previous = null;
        await using var executor = new PlayerbotNavigationExecutor(() =>
            new CountingGatedPathfinder(gate, entered, secondEntered, holdFirstUntilReleased: true), capacity: 1);
        try
        {
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(pathfinder: nav);
                host.WorldServices.GetRequiredService<QuestNpcFeature>().Options.OrdinaryRewardQuestIds = [QuestTravelFixture.QuestId];
                return true;
            });
            destinations = await host.OnWorldAsync(() =>
                new PlayerbotWorldDestinations(session, new PlayerbotOptions { Enabled = true, MaxRouteYards = 20_000 }, executor));

            Assert.True(await host.OnWorldAsync(() => destinations!.Update(session.Player!, 0, 500)));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            CreatureWorldFeature feature = host.WorldServices.GetRequiredService<CreatureWorldFeature>();
            int versionBefore = await host.OnWorldAsync(() => feature.Content.DefinitionsVersion);
            previous = await host.OnWorldAsync(() =>
            {
                CreatureContent current = feature.Content;
                CreatureTemplate template = current.FindTemplate(QuestTravelFixture.Entry)!;
                return current.SwapDefinitions(new CreatureContent([template], [], [], [], []));
            });
            int versionAfter = await host.OnWorldAsync(() => feature.Content.DefinitionsVersion);
            Assert.Equal(versionBefore + 1, versionAfter);

            // RefreshEntries observes the new generation, invalidates the old identity, and leaves a deferred replacement plan.
            Assert.True(await host.OnWorldAsync(() => destinations!.Update(session.Player!, 0, 500)));
            Assert.True(destinations!.IsPlanning);
            Assert.Equal(1, executor.Outstanding);
            Assert.Equal(1, executor.ActivePlans);

            // The old worker ignores cancellation while held, so replacement submission cannot race its release.
            gate.SetResult(true);
            await WaitUntilAsync(() => executor.Outstanding == 0, "content-revision worker shutdown");
            Assert.True(await host.OnWorldAsync(() => destinations!.Update(session.Player!, 0, 500)));
            await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, executor.ActivePlans);
        }
        finally
        {
            if (destinations is not null)
                await host.OnWorldAsync(() => destinations!.Stop());
            gate.TrySetResult(true);
            await executor.DisposeAsync();
            if (previous is not null)
            {
                CreatureWorldFeature feature = host.WorldServices.GetRequiredService<CreatureWorldFeature>();
                await host.OnWorldAsync(() => feature.Content.RestoreDefinitions(previous));
            }
            session.Kick();
            await session.ManagedClosed;
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task StoppingDestination_CancelsPendingWorkerWithoutReleasingOwnerEarly()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var executor = new PlayerbotNavigationExecutor(() => new GatedPathfinder(gate, entered, holdUntilReleased: true), capacity: 1);
        Assert.True(executor.TrySubmit(new PlayerbotNavigationRequest(0, Vector3.Zero, new(10, 0, 0), PathOptions.Default), out PlayerbotNavigationJob job));
        Assert.Equal(1, executor.Outstanding);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        job.Dispose();
        Assert.Equal(1, executor.Outstanding);
        gate.SetResult(true);
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => executor.Outstanding == 0, "stopped destination worker shutdown");
    }

    private sealed class GatedPathfinder(TaskCompletionSource<bool> gate, TaskCompletionSource<bool>? entered = null,
        bool holdUntilReleased = false) : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            entered?.TrySetResult(true);
            gate.Task.WaitAsync(TimeSpan.FromSeconds(5), holdUntilReleased ? CancellationToken.None
                : options?.CancellationToken ?? CancellationToken.None).GetAwaiter().GetResult();
            options?.CancellationToken.ThrowIfCancellationRequested();
            return PathResult.StraightLine(start, end, PathType.Normal);
        }
    }

    private sealed class CountingGatedPathfinder(TaskCompletionSource<bool> gate,
        TaskCompletionSource<bool> firstEntered, TaskCompletionSource<bool> secondEntered,
        bool holdFirstUntilReleased = false) : IPathfinder
    {
        private int _calls;

        public bool Enabled => true;
        public int Calls => Volatile.Read(ref _calls);

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            int call = Interlocked.Increment(ref _calls);
            if (call == 1) firstEntered.TrySetResult(true);
            if (call == 2) secondEntered.TrySetResult(true);
            CancellationToken waitCancellation = call == 1 && holdFirstUntilReleased
                ? CancellationToken.None
                : options?.CancellationToken ?? CancellationToken.None;
            gate.Task.WaitAsync(TimeSpan.FromSeconds(5), waitCancellation)
                .GetAwaiter().GetResult();
            options?.CancellationToken.ThrowIfCancellationRequested();
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

    private static async Task WaitForWorldBoundedAsync(WorldTestHost host, Func<bool> condition, string description)
    {
        for (int i = 0; i < 500; i++)
        {
            if (await host.World.InvokeAsync(condition)) return;
            await Task.Delay(10);
        }

        throw new TimeoutException($"timed out waiting for: {description}");
    }
}
