using System.Numerics;
using System.Reflection;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotExploreTests
{
    [Fact]
    public async Task NoQuestWork_ExploresTowardRightLevelSpawnsInsteadOfLowLevelSpawns()
    {
        const float startX = -8949.95f, y = -132.49f, z = 83.53f;
        var content = new CreatureContent(
            [new CreatureTemplate { Entry = 99001, Name = "low", MinLevel = 1, MaxLevel = 1, Faction = 14 },
             new CreatureTemplate { Entry = 99002, Name = "right", MinLevel = 8, MaxLevel = 8, Faction = 14 }],
            [new CreatureSpawn { Guid = 99001, Entry = 99001, MapId = 0, X = startX + 250, Y = y, Z = z },
             new CreatureSpawn { Guid = 99002, Entry = 99002, MapId = 0, X = startX - 250, Y = y, Z = z }], [], [], []);
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<ICreatureDataStore>(new TestContent(content));
            services.AddSingleton(new FactionTemplateCatalog([
                new FactionTemplateRecord(1, 1, 0, 1, 0, 0),
                new FactionTemplateRecord(14, 0, 0, 8, 0, 1)]));
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, Risk = { Enabled = false } });
        try
        {
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor());
                Player player = session.Player!;
                player.Level = 8;
                session.ManagedBudget = new ManagedActionBudget(1);
                typeof(PlayerbotBrain).GetMethod("Explore", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(brain, [player, 500u]);
                var route = (PlayerbotRoute?)typeof(PlayerbotBrain).GetField("_route", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(brain);
                Assert.NotNull(route);
                Assert.True(route.Points.Last().X < startX - 20, $"Explore headed to {route.Points.Last().X}, not the level-8 spawns");
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public void ManySpawnsInOneCell_DoNotIncreaseThinkTimeCandidateChecks()
    {
        var content = new CreatureContent(
            [new CreatureTemplate { Entry = 99003, Name = "right", MinLevel = 8, MaxLevel = 8, Faction = 14 }],
            Enumerable.Range(1, 30_000).Select(i => new CreatureSpawn
            { Guid = (uint)i, Entry = 99003, MapId = 0, X = 100 + i % 10, Y = 100, Z = 20 }), [], [], []);
        var atlas = new PlayerbotExploreAtlas(content);
        PlayerbotExploreAtlas.Candidate? choice = atlas.Find(0, 8, new Vector3(100, 100, 20), _ => true,
            (_, _) => false, out int examined);
        Assert.NotNull(choice);
        Assert.InRange(examined, 1, PlayerbotExploreAtlas.SamplesPerFactionCell);
    }

    [Fact]
    public void ExhaustedCell_RotatesToTheNextLevelAppropriateSite()
    {
        var content = new CreatureContent(
            [new CreatureTemplate { Entry = 99004, Name = "right", MinLevel = 8, MaxLevel = 8, Faction = 14 }],
            [new CreatureSpawn { Guid = 1, Entry = 99004, MapId = 0, X = 100, Y = 0, Z = 20 },
             new CreatureSpawn { Guid = 2, Entry = 99004, MapId = 0, X = 300, Y = 0, Z = 20 }], [], [], []);
        var atlas = new PlayerbotExploreAtlas(content);
        var from = new Vector3(0, 0, 20);
        PlayerbotExploreAtlas.Candidate first = Assert.IsType<PlayerbotExploreAtlas.Candidate>(
            atlas.Find(0, 8, from, _ => true, (_, _) => false, out _));
        PlayerbotExploreAtlas.Candidate next = Assert.IsType<PlayerbotExploreAtlas.Candidate>(
            atlas.Find(0, 8, from, _ => true, (x, y) => x == first.CellX && y == first.CellY, out _));
        Assert.Equal(1u, first.Spawn.Guid);
        Assert.Equal(2u, next.Spawn.Guid);
    }

    [Fact]
    public void StrongNearbyCreature_MakesAGrindSiteUnsafe()
    {
        var content = new CreatureContent(
            [new CreatureTemplate { Entry = 99005, Name = "right", MinLevel = 8, MaxLevel = 8, Faction = 14 },
             new CreatureTemplate { Entry = 99006, Name = "strong", MinLevel = 18, MaxLevel = 18, Faction = 14 }],
            [new CreatureSpawn { Guid = 1, Entry = 99005, MapId = 0, X = 100, Y = 0, Z = 20 },
             new CreatureSpawn { Guid = 2, Entry = 99006, MapId = 0, X = 120, Y = 0, Z = 20 }], [], [], []);
        var atlas = new PlayerbotExploreAtlas(content);
        PlayerbotExploreAtlas.Candidate candidate = Assert.IsType<PlayerbotExploreAtlas.Candidate>(
            atlas.Find(0, 8, new Vector3(0, 0, 20), _ => true, (_, _) => false, out _));
        Assert.True(atlas.HasStrongHazard(candidate, 8, faction => faction == 14));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DepletedOrStalledSite_BrainHeadsForAnotherSpawnCell(bool stalled)
    {
        const float startX = -8949.95f, y = -132.49f, z = 83.53f;
        var content = new CreatureContent(
            [new CreatureTemplate { Entry = 99007, Name = "right", MinLevel = 8, MaxLevel = 8, Faction = 14 }],
            [new CreatureSpawn { Guid = 1, Entry = 99007, MapId = 0, X = startX + 140, Y = y, Z = z },
             new CreatureSpawn { Guid = 2, Entry = 99007, MapId = 0, X = startX - 160, Y = y, Z = z }], [], [], []);
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<ICreatureDataStore>(new TestContent(content));
            services.AddSingleton(new FactionTemplateCatalog([
                new FactionTemplateRecord(1, 1, 0, 1, 0, 0),
                new FactionTemplateRecord(14, 0, 0, 8, 0, 1)]));
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, Risk = { Enabled = false } });
        try
        {
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor());
                Player player = session.Player!;
                player.Level = 8;
                MethodInfo explore = typeof(PlayerbotBrain).GetMethod("Explore", BindingFlags.Instance | BindingFlags.NonPublic)!;
                FieldInfo route = typeof(PlayerbotBrain).GetField("_route", BindingFlags.Instance | BindingFlags.NonPublic)!;
                session.ManagedBudget = new ManagedActionBudget(1);
                explore.Invoke(brain, [player, 500u]);
                Assert.True(((PlayerbotRoute)route.GetValue(brain)!).Points.Last().X > startX);
                if (stalled)
                    brain.GiveUpStalledGoal(player, shareable: false);
                else
                {
                    // A deterministic arrived-and-empty observation: no visible target and the old route has finished.
                    player.SetPosition(startX + 140, y, z, player.Orientation);
                    route.SetValue(brain, null);
                }
                session.ManagedBudget = new ManagedActionBudget(1);
                explore.Invoke(brain, [player, 500u]);
                var chosen = (PlayerbotExploreAtlas.Candidate?)typeof(PlayerbotBrain)
                    .GetField("_exploreCandidate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(brain);
                Assert.Equal(2u, chosen!.Value.Spawn.Guid);
                Assert.NotNull(route.GetValue(brain));
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    private sealed class TestContent(CreatureContent content) : ICreatureDataStore
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
}
