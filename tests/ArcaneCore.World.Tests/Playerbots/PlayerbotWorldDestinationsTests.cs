using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.World.Playerbots;
using ArcaneCore.Game;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using System.Numerics;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotWorldDestinationsTests
{
    [Fact]
    public async Task BlockedNearestHintRotatesToReachableSameEntryOnNextThink()
    {
        const float startX = -8949.95f;
        var content = new CreatureContent(
            [new CreatureTemplate { Entry = 6, Name = "objective", DisplayIds = [49] }],
            [new CreatureSpawn { Guid = 20001, Entry = 6, MapId = 0, X = startX + 300, Y = -132.49f, Z = 83.53f },
             new CreatureSpawn { Guid = 20002, Entry = 6, MapId = 0, X = startX - 400, Y = -132.49f, Z = 83.53f }], [], [], []);
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            services.AddSingleton<ICreatureDataStore>(new DestinationContent(content)));
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                var pathfinder = new BlockEastPathfinder();
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: pathfinder);
                var player = session.Player!;
                var destinations = new PlayerbotWorldDestinations(session, new PlayerbotOptions { Enabled = true });
                session.ManagedBudget = new ManagedActionBudget(1);
                float before = player.X;
                Assert.False(destinations.Update(player, 6, 500));
                Assert.Equal(1, pathfinder.Calls);
                Assert.Equal(before, player.X);
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(destinations.Update(player, 6, 500));
                Assert.Equal(2, pathfinder.Calls);
                Assert.Equal(before, player.X);
                Assert.Equal(6u, destinations.TargetEntry);
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(destinations.Update(player, 6, 500));
                Assert.True(player.X < before);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task ObjectiveChange_ReplansTowardRealSpawnBeyondVisibilityUsingNormalMovement()
    {
        var content = new CreatureContent(
            [new CreatureTemplate { Entry = 6, Name = "east", DisplayIds = [49] },
             new CreatureTemplate { Entry = 7, Name = "west", DisplayIds = [49] }],
            [.. Enumerable.Range(1, 4100).Select(i => new CreatureSpawn { Guid = (uint)i, Entry = 99, MapId = 0, X = 0, Y = 0, Z = 83.53f }),
             new CreatureSpawn { Guid = 10001, Entry = 6, MapId = 0, X = -8648.95f, Y = -132.49f, Z = 83.53f },
             new CreatureSpawn { Guid = 10002, Entry = 7, MapId = 0, X = -9248.95f, Y = -132.49f, Z = 83.53f }], [], [], []);
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            services.AddSingleton<ICreatureDataStore>(new DestinationContent(content)));
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor());
                var player = session.Player!;
                var destinations = new PlayerbotWorldDestinations(session, new PlayerbotOptions { Enabled = true });
                float start = player.X;
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(destinations.Update(player, 6, 500));
                Assert.Equal(start, player.X);
                Assert.Equal(6u, destinations.TargetEntry);
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(destinations.Update(player, 6, 500));
                Assert.True(player.X > start);
                float east = player.X;
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(destinations.Update(player, 7, 500));
                // Already moving: changing the objective redirects the next heartbeat.
                Assert.True(player.X < east);
                Assert.Equal(7u, destinations.TargetEntry);
                Assert.DoesNotContain(player.VisibleObjects, guid => player.Map!.FindObject(guid) is Creature c && c.Entry is 6 or 7);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    private sealed class DestinationContent(CreatureContent content) : ICreatureDataStore
    {
        public Task<CreatureContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(content);
    }

    private sealed class FlatFloor : ILineOfSight
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        { hit = to; return false; }
        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => 83.53f;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        { info = default; return false; }
    }

    private sealed class BlockEastPathfinder : IPathfinder
    {
        public bool Enabled => true;
        public int Calls { get; private set; }

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            Calls++;
            return end.X > start.X ? PathResult.None(start) : PathResult.StraightLine(start, end, PathType.Normal);
        }
    }

    [Fact]
    public void StaticSpawnDestinationRejectsNonFiniteCoordinates()
    {
        CreatureSpawn valid = new() { Guid = 1, Entry = 6, MapId = 0, X = 1, Y = 2, Z = 3 };
        CreatureSpawn invalid = valid with { Z = float.NaN };
        Assert.True(PlayerbotWorldDestinations.IsFiniteDestination(valid));
        Assert.False(PlayerbotWorldDestinations.IsFiniteDestination(invalid));
    }

    [Fact]
    public void PreferredObjectiveEntryUsesLoadedImmutableSpawnContent()
    {
        CreatureSpawn spawn = new() { Guid = 77, Entry = 6, MapId = 1, X = 10, Y = 20, Z = 30 };
        var content = new CreatureContent(
            [new CreatureTemplate { Entry = 6, Name = "objective" }], [spawn], [], [], []);
        Assert.True(PlayerbotWorldDestinations.IsDestinationEntry(content, QuestStore.Empty, spawn, 6));
        Assert.False(PlayerbotWorldDestinations.IsDestinationEntry(content, QuestStore.Empty, spawn, 7));
        Assert.Equal((uint)77, content.GetSpawns(1).Single().Guid);
    }

    [Fact]
    public void NonPreferredEntryRequiresNpcFlagAndQuestRelation()
    {
        CreatureSpawn spawn = new() { Guid = 88, Entry = 42, MapId = 0, X = 1, Y = 2, Z = 3 };
        var template = new CreatureTemplate { Entry = 42, Name = "giver", NpcFlags = 2 };
        var quest = new QuestTemplate { Entry = 9 };
        var content = new CreatureContent([template], [spawn], [], [], []);
        var quests = new QuestStore(new QuestContent([quest], [new CreatureQuestRelation { Id = 42, Quest = 9 }], []));
        Assert.True(PlayerbotWorldDestinations.IsDestinationEntry(content, quests, spawn, 0));
        Assert.Equal(new uint[] { 42 }, quests.CreatureStartersOf(9));
        Assert.Empty(quests.CreatureEndersOf(9));
        Assert.Equal(spawn, Assert.Single(content.GetSpawns(0, 42)));
        Assert.Empty(content.GetSpawns(1, 42));
    }
}
