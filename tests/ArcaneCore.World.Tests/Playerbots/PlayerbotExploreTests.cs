using System.Numerics;
using System.Reflection;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FriendlyNearSpawn_IsSkippedEvenIfTheCreatureIsHostileInTheOtherDirection(bool reverseFriendly)
    {
        const float startX = -8949.95f, y = -132.49f, z = 83.53f;
        uint nearFaction = reverseFriendly ? 16u : 14u;
        var content = new CreatureContent(
            [new CreatureTemplate { Entry = 99101, Name = "friendly", MinLevel = 8, MaxLevel = 8, Faction = nearFaction },
             new CreatureTemplate { Entry = 99102, Name = "hostile", MinLevel = 8, MaxLevel = 8, Faction = 15 }],
            [new CreatureSpawn { Guid = 1, Entry = 99101, MapId = 0, X = startX + 130, Y = y, Z = z },
             new CreatureSpawn { Guid = 2, Entry = 99102, MapId = 0, X = startX - 250, Y = y, Z = z }], [], [], []);
        var factions = new FactionTemplateCatalog([
            new FactionTemplateRecord(1, 1, 0, 1, 8, 0), // friendly to faction 16's own mask
            new FactionTemplateRecord(14, 0, 0, 2, 1, 0),
            new FactionTemplateRecord(15, 0, 0, 4, 0, 1),
            new FactionTemplateRecord(16, 0, 0, 8, 0, 1), // hostile to the player, but the player is friendly to it
        ]);
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<ICreatureDataStore>(new TestContent(content));
            services.AddSingleton(factions);
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
                var chosen = (PlayerbotExploreAtlas.Candidate?)typeof(PlayerbotBrain)
                    .GetField("_exploreCandidate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(brain);
                Assert.Equal(99102u, chosen!.Value.Spawn.Entry);
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ManyHazardousSites_ChooseSafeOrBackOffAndWander(bool safeAvailable)
    {
        const float startX = -8949.95f, y = -132.49f, z = 83.53f;
        int cx = PlayerbotExploreAtlas.CellOf(startX), cy = PlayerbotExploreAtlas.CellOf(y);
        var spawns = new List<CreatureSpawn>();
        uint guid = 1;
        for (int dx = -4; dx <= 4; dx++)
            for (int dy = -4; dy <= 4; dy++)
            {
                float x = (cx + dx + 0.5f) * PlayerbotExploreAtlas.CellYards;
                float siteY = (cy + dy + 0.5f) * PlayerbotExploreAtlas.CellYards;
                spawns.Add(new CreatureSpawn { Guid = guid++, Entry = 99111, MapId = 0, X = x, Y = siteY, Z = z });
                spawns.Add(new CreatureSpawn { Guid = guid++, Entry = 99112, MapId = 0, X = x + 10, Y = siteY, Z = z });
            }
        if (safeAvailable)
            spawns.Add(new CreatureSpawn { Guid = 9999, Entry = 99111, MapId = 0, X = startX - 1700, Y = y, Z = z });
        var content = new CreatureContent(
            [new CreatureTemplate { Entry = 99111, Name = "safe level", MinLevel = 8, MaxLevel = 8, Faction = 14 },
             new CreatureTemplate { Entry = 99112, Name = "strong nearby", MinLevel = 20, MaxLevel = 20, Faction = 14 }],
            spawns, [], [], []);
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
                session.Player!.Level = 8;
                session.ManagedBudget = new ManagedActionBudget(1);
                typeof(PlayerbotBrain).GetMethod("Explore", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(brain, [session.Player, 500u]);
                var chosen = (PlayerbotExploreAtlas.Candidate?)typeof(PlayerbotBrain)
                    .GetField("_exploreCandidate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(brain);
                if (safeAvailable)
                {
                    Assert.NotNull(chosen);
                    Assert.Equal(9999u, chosen.Value.Spawn.Guid);
                    Assert.NotNull(typeof(PlayerbotBrain).GetField("_route", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(brain));
                }
                else
                {
                    Assert.Null(chosen);
                    uint retryAfter = (uint)typeof(PlayerbotBrain).GetField("_exploreSearchAfter", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .GetValue(brain)!;
                    Assert.True(unchecked((int)(retryAfter - host.World.NowMs)) > 0);
                    Assert.True((int)typeof(PlayerbotBrain).GetField("_roamDirection", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .GetValue(brain)! > 0);
                }
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    [Theory]
    [InlineData("elapsed")]
    [InlineData("budget")]
    [InlineData("root")]
    [InlineData("jump")]
    public async Task TransientMovementPause_KeepsTheExploreSiteAndRoute(string reason)
    {
        const float startX = -8949.95f, y = -132.49f, z = 83.53f;
        var content = new CreatureContent(
            [new CreatureTemplate { Entry = 99121, Name = "grind", MinLevel = 8, MaxLevel = 8, Faction = 14 }],
            [new CreatureSpawn { Guid = 1, Entry = 99121, MapId = 0, X = startX - 250, Y = y, Z = z }], [], [], []);
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
                FieldInfo routeField = typeof(PlayerbotBrain).GetField("_route", BindingFlags.Instance | BindingFlags.NonPublic)!;
                FieldInfo siteField = typeof(PlayerbotBrain).GetField("_exploreCandidate", BindingFlags.Instance | BindingFlags.NonPublic)!;
                session.ManagedBudget = new ManagedActionBudget(1);
                explore.Invoke(brain, [player, 500u]);
                object? route = routeField.GetValue(brain);
                Assert.NotNull(route);
                Assert.NotNull(siteField.GetValue(brain));
                if (reason == "budget")
                {
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.True(PlayerbotMovementControl.Stop(session, player));
                }
                // Force the next advance to start motion again, so a zero budget or movement lock actually refuses it.
                PlayerbotMotion.Reset(player);
                if (reason == "budget") session.ManagedBudget = new ManagedActionBudget(0);
                if (reason is "root" or "jump")
                {
                    var movement = player.Movement;
                    movement.Flags |= reason == "root" ? MovementFlags.Root : MovementFlags.Jumping;
                    player.ApplyMovement(movement, host.World.NowMs);
                }
                explore.Invoke(brain, [player, reason == "elapsed" ? 0u : 500u]);
                Assert.Same(route, routeField.GetValue(brain));
                Assert.NotNull(siteField.GetValue(brain));
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task TeleportFarFromSite_RechoosesFromTheNewPosition()
    {
        const float startX = -8949.95f, y = -132.49f, z = 83.53f;
        var content = new CreatureContent(
            [new CreatureTemplate { Entry = 99131, Name = "grind", MinLevel = 8, MaxLevel = 8, Faction = 14 }],
            [new CreatureSpawn { Guid = 1, Entry = 99131, MapId = 0, X = startX + 140, Y = y, Z = z },
             new CreatureSpawn { Guid = 2, Entry = 99131, MapId = 0, X = startX - 250, Y = y, Z = z }], [], [], []);
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
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new AlwaysRoute());
                Player player = session.Player!;
                player.Level = 8;
                MethodInfo explore = typeof(PlayerbotBrain).GetMethod("Explore", BindingFlags.Instance | BindingFlags.NonPublic)!;
                FieldInfo site = typeof(PlayerbotBrain).GetField("_exploreCandidate", BindingFlags.Instance | BindingFlags.NonPublic)!;
                session.ManagedBudget = new ManagedActionBudget(1);
                explore.Invoke(brain, [player, 500u]);
                Assert.Equal(1u, ((PlayerbotExploreAtlas.Candidate?)site.GetValue(brain))!.Value.Spawn.Guid);
                player.SetPosition(startX - 400, y, z, player.Orientation);
                session.ManagedBudget = new ManagedActionBudget(1);
                explore.Invoke(brain, [player, 500u]);
                Assert.Equal(2u, ((PlayerbotExploreAtlas.Candidate?)site.GetValue(brain))!.Value.Spawn.Guid);
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

    private sealed class AlwaysRoute : IPathfinder
    {
        public bool Enabled => true;
        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => PathResult.StraightLine(start, end, PathType.Normal);
    }
}
