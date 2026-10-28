using System.Numerics;
using System.Reflection;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ArcaneCore.World.Bots;

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

    // Bot QA #286/#290/#293/#295/#309/#282/#310: an unhomed level 7-8 bot among level 10-14 creatures, the nearest fit over 1000 yards away.
    private const float StrandX = -8949.95f, StrandY = -132.49f, StrandZ = 83.53f;
    private static readonly Vector3 StrandBind = new(-11500f, -132.49f, 83.53f);

    private static CreatureContent StrandedContent(bool fitting, bool strandedBind = false)
    {
        var templates = new List<CreatureTemplate>
        {
            new() { Entry = 99201, Name = "strong", MinLevel = 12, MaxLevel = 12, Faction = 14 },
            new() { Entry = 99202, Name = "fit", MinLevel = 8, MaxLevel = 8, Faction = 14 },
        };
        var spawns = new List<CreatureSpawn>();
        uint guid = 1;
        foreach ((float dx, float dy) in new[] { (300f, 0f), (-350f, 120f), (0f, 400f), (0f, -440f), (450f, 450f) })
            spawns.Add(new CreatureSpawn { Guid = guid++, Entry = 99201, MapId = 0, X = StrandX + dx, Y = StrandY + dy, Z = StrandZ });
        if (strandedBind)
            foreach ((float dx, float dy) in new[] { (60f, 0f), (-80f, 50f), (0f, 90f) })
                spawns.Add(new CreatureSpawn { Guid = guid++, Entry = 99201, MapId = 0, X = StrandBind.X + dx, Y = StrandBind.Y + dy, Z = StrandZ });
        spawns.Add(new CreatureSpawn { Guid = guid++, Entry = 99202, MapId = 0, X = StrandX + (fitting ? 200 : 2000), Y = StrandY, Z = StrandZ });
        return new CreatureContent(templates, spawns, [], [], []);
    }

    /// <summary>Gives a stalled Explore goal up for a level 8 bot and reports whether it ended at its bind point.</summary>
    private static async Task<bool> StallExploringAsync(CreatureContent content, bool homed, byte level = 8, bool bindAtBot = false)
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<ICreatureDataStore>(new TestContent(content));
            services.AddSingleton(new FactionTemplateCatalog([
                new FactionTemplateRecord(1, 1, 0, 1, 0, 0),
                new FactionTemplateRecord(14, 0, 0, 8, 0, 1)]));
            services.AddSingleton<IWorldFeature, ManualStrandClock>();
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, Risk = { Enabled = false } });
        try
        {
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new AlwaysRoute());
                Player player = session.Player!;
                player.Level = level;
                player.SetPosition(StrandX, StrandY, StrandZ, 0);
                Vector3 bind = bindAtBot ? new(StrandX + 20, StrandY, StrandZ) : StrandBind;
                player.Home = new HomeBind(0, 12, bind.X, bind.Y, bind.Z);
                if (homed) brain.Home = new PlayerbotHomeArea(0, new Vector3(StrandX, StrandY, StrandZ), 750f, "Test");
                brain.HoldGoalForTests(PlayerbotGoalKind.Explore, 0, 0);
                brain.GiveUpStalledGoal(player, shareable: false);
            });
            for (int tick = 0; tick < 100; tick++)
            {
                bool arrived = await host.OnWorldAsync(() =>
                {
                    PlayerbotMovementControl.Update(session, session.Player!);
                    return session.Player!.IsInWorld && Vector2.Distance(new(session.Player.X, session.Player.Y), new(StrandBind.X, StrandBind.Y)) < 5f;
                });
                if (arrived) return true;
                await host.World.AdvanceClockAsync(50);
            }
            return await host.OnWorldAsync(() => brain.BindTeleported); // a no-op teleport onto the same spot counts
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task StalledExplore_UnhomedBotWithNothingFittingWithinThreeRings_TeleportsToItsBindPoint()
        => Assert.True(await StallExploringAsync(StrandedContent(fitting: false), homed: false));

    [Fact]
    public async Task StalledExplore_UnhomedBotWithAFittingSpawn200YardsAway_StaysPut()
        => Assert.False(await StallExploringAsync(StrandedContent(fitting: true), homed: false));

    [Fact]
    public async Task StalledExplore_BotAlreadyAtItsBindPoint_IsNotTeleported()
        => Assert.False(await StallExploringAsync(StrandedContent(fitting: false), homed: false, bindAtBot: true));

    [Fact]
    public async Task StalledExplore_BindPointStrandedForItsLevelToo_IsNotTeleported()
        => Assert.False(await StallExploringAsync(StrandedContent(fitting: false, strandedBind: true), homed: false));

    [Fact]
    public async Task StalledExplore_OverLevelledBotWithNothingToFight_IsNotTeleported()
        => Assert.False(await StallExploringAsync(StrandedContent(fitting: false), homed: false, level: 30));

    [Fact]
    public async Task StalledExplore_HomedBotAmongStrongCreatures_IsNotTeleported()
        => Assert.False(await StallExploringAsync(StrandedContent(fitting: false), homed: true));

    [Fact]
    public async Task PlanWander_RefusesPartialWallSlidingLegThatStopsShortOfDestination_AndTurnsImmediately()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var options = new PlayerbotOptions { Enabled = true, Risk = { Enabled = false } };
        var brain = new PlayerbotBrain(session, options);
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                player.SetPosition(100f, 100f, 83.53f, 0f);
                // Any wander ray east (cos > 0, into the canyon wall at X=110) returns a partial path sliding along the wall to (110, end.Y)
                // which is > 8 yards short of the 48-yard destination; rays away from the wall succeed cleanly.
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new EastWallPartialPathfinder(110f));

                TileWait wait = default;
                var intoWall = new Vector3(148f, 115f, 83.53f);
                Assert.Equal(PlanOutcome.Refused, PlayerbotNavigation.PlanWander(player, intoWall, options, ref wait, host.World.NowMs, out PlayerbotRoute? refusedRoute));
                Assert.Null(refusedRoute);

                // First Explore think faces east (0 rad), hits the wall refusal, and increments _exploreRefusals to 1;
                // the very next think switches immediately to full-circle golden-angle spread and walks away from the wall.
                MethodInfo explore = typeof(PlayerbotBrain).GetMethod("Explore", BindingFlags.Instance | BindingFlags.NonPublic)!;
                FieldInfo routeField = typeof(PlayerbotBrain).GetField("_route", BindingFlags.Instance | BindingFlags.NonPublic)!;
                bool walkedAway = false;
                for (int think = 0; think < 6; think++)
                {
                    session.ManagedBudget = new ManagedActionBudget(2);
                    explore.Invoke(brain, [player, 500u]);
                    if (routeField.GetValue(brain) is PlayerbotRoute valid && valid.Points[^1].X < 95f)
                    {
                        walkedAway = true;
                        break;
                    }
                }

                Assert.True(walkedAway, "bot should immediately turn away from the wall after a refused partial wander leg");
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// The mesh can finish a short step and nothing else (a street, a room, a canyon: every 48-yard ray stops at a wall more than
    /// <see cref="PlayerbotNavigation.WanderMaxShortfallYards"/> short, so <see cref="PlayerbotNavigation.PlanWander"/> refuses it).
    /// Live 2026-10-17 bot QA, the Explore stalls whose trace is <c>wander-refused</c> and then ~120s of
    /// <c>phase=explore-wander-plan moving:no dest:none</c>: the heading changed and the ray stayed 48 yards, so every think refused
    /// and the bot stood until the stall watch gave the goal up. The next leg has to be short enough that finishing it moves the bot
    /// by the stall watch's own progress.
    /// </summary>
    [Fact]
    public async Task WanderRefused_NextLegIsShortEnoughToFinishInsteadOfStandingUntilTheStall()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var options = new PlayerbotOptions { Enabled = true, Risk = { Enabled = false } };
        var brain = new PlayerbotBrain(session, options);
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                player.SetPosition(100f, 100f, 83.53f, 0f);
                // Twelve yards of mesh in every direction, then a wall. A 48-yard ray stops 36 yards short and is refused;
                // an 18-yard ray (progress 10 + the 8-yard shortfall allowance) stops 6 yards short and is walked.
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new ShortOpenGroundPathfinder(12f));

                MethodInfo explore = typeof(PlayerbotBrain).GetMethod("Explore", BindingFlags.Instance | BindingFlags.NonPublic)!;
                FieldInfo routeField = typeof(PlayerbotBrain).GetField("_route", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var origin = new Vector3(player.X, player.Y, player.Z);
                PlayerbotRoute? walked = null;
                for (int think = 0; think < 4 && walked is null; think++)
                {
                    session.ManagedBudget = new ManagedActionBudget(2);
                    explore.Invoke(brain, [player, 500u]);
                    if (routeField.GetValue(brain) is PlayerbotRoute route
                        && Vector2.Distance(new(origin.X, origin.Y), new(route.Points[^1].X, route.Points[^1].Y))
                            >= PlayerbotStallWatch.ProgressYards)
                        walked = route;
                }

                Assert.NotNull(walked);
                Assert.True(Vector2.Distance(new(origin.X, origin.Y), new(walked!.Points[^1].X, walked.Points[^1].Y))
                    < PlayerbotBrain.ExploreLegYards - 1f,
                    "the leg that finally walked must be the shortened ray, not another refused 48-yard one");
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// A shortened ray that is planned and then not walked (the advance is refused) must not be the next think's target.
    /// The refusal count was cleared when the route was planned, before <see cref="PlayerbotNavigation.TryAdvance"/>, so a
    /// failed walk left it at 0. The following think refused the 48-yard ray, came back to refusal 1, and aimed at the same
    /// heading again for the rest of the stall.
    /// </summary>
    [Fact]
    public async Task WanderWalkRefused_NextLegIsADifferentShortenedPoint()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var options = new PlayerbotOptions { Enabled = true, Risk = { Enabled = false } };
        var brain = new PlayerbotBrain(session, options);
        var paths = new RecordingShortOpenPathfinder(12f);
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                player.SetPosition(100f, 100f, 83.53f, 0f);
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: paths);
                MethodInfo explore = typeof(PlayerbotBrain).GetMethod("Explore", BindingFlags.Instance | BindingFlags.NonPublic)!;

                session.ManagedBudget = new ManagedActionBudget(2);
                explore.Invoke(brain, [player, 500u]); // 48-yard ray, refused: the mesh stops 36 yards short
                Assert.DoesNotContain(paths.Ends, IsShortenedRay);

                options.MoveSpeed = 0f; // the shortened route is planned, then TryAdvance refuses it (no speed)
                session.ManagedBudget = new ManagedActionBudget(2);
                explore.Invoke(brain, [player, 500u]);
                Vector3 failed = Assert.Single(paths.Ends, IsShortenedRay);
                options.MoveSpeed = 7f;

                int before = paths.Ends.Count;
                session.ManagedBudget = new ManagedActionBudget(2);
                explore.Invoke(brain, [player, 500u]);
                Vector3 next = paths.Ends.Skip(before).First(end => RayYards(end) > 10f);
                Assert.True(IsShortenedRay(next),
                    $"the leg after a failed walk must stay shortened, not go back to a 48-yard ray ({RayYards(next):F1} yd)");
                float turned = MathF.Abs(MathF.Atan2(next.Y - 100f, next.X - 100f) - MathF.Atan2(failed.Y - 100f, failed.X - 100f));
                if (turned > MathF.PI) turned = (MathF.PI * 2f) - turned;
                Assert.True(turned > 0.5f, $"the failed walk's point was kept (turn {turned:F2} rad)");
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }

        static float RayYards(Vector3 end) => Vector2.Distance(new(100f, 100f), new(end.X, end.Y));
        // A full explore leg is 48 yards. The shortened one is progress (10) plus the 8-yard shortfall allowance.
        static bool IsShortenedRay(Vector3 end) => RayYards(end) is > 10f and < PlayerbotBrain.ExploreLegYards - 1f;
    }

    /// <summary>
    /// Pocket, canyon or slope: every compass ray is cut to 6 yards, so the planner refuses all of them and the bot stood
    /// <c>moving:no dest:none</c> until the stall watch gave Explore up. When the map's pathfinder can pick mesh points
    /// (<see cref="IMeshPointQuery"/>) the wander must fall back to a reachable one instead of turning rays forever.
    /// </summary>
    [Fact]
    public async Task WanderRaysAllRefused_TakesAReachableMeshPoint()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var options = new PlayerbotOptions { Enabled = true, Risk = { Enabled = false } };
        var brain = new PlayerbotBrain(session, options);
        var meshPoint = new Vector3(125f, 100f, 83.53f);
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                player.SetPosition(100f, 100f, 83.53f, 0f);
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new BendMeshPathfinder(6f, meshPoint));

                MethodInfo explore = typeof(PlayerbotBrain).GetMethod("Explore", BindingFlags.Instance | BindingFlags.NonPublic)!;
                FieldInfo routeField = typeof(PlayerbotBrain).GetField("_route", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var origin = new Vector2(player.X, player.Y);
                PlayerbotRoute? walked = null;
                for (int think = 0; think < 6 && walked is null; think++)
                {
                    session.ManagedBudget = new ManagedActionBudget(2);
                    explore.Invoke(brain, [player, 500u]);
                    if (routeField.GetValue(brain) is PlayerbotRoute route) walked = route;
                }

                Assert.NotNull(walked);
                Vector3 end = walked!.Points[^1];
                Assert.True(Vector2.Distance(origin, new(end.X, end.Y)) >= PlayerbotStallWatch.ProgressYards,
                    "the leg must move the bot by the stall watch's own progress");
                Assert.True(Vector2.Distance(new(meshPoint.X, meshPoint.Y), new(end.X, end.Y)) <= 1f,
                    $"the leg should end at the mesh point, not at {end}");
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// GiveUpStalledGoal presets the refusal count to 3 for Explore. Refusals were only traced at count 1, so after any give-up
    /// every later refusal was silent and the stall diagnostics showed a bare <c>moving:no dest:none</c>.
    /// </summary>
    [Fact]
    public async Task WanderRefusedAfterGiveUp_IsTraced()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, Risk = { Enabled = false } });
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                player.SetPosition(100f, 100f, 83.53f, 0f);
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new ShortOpenGroundPathfinder(0f));
                typeof(PlayerbotBrain).GetField("_exploreRefusals", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(brain, 3);

                session.ManagedBudget = new ManagedActionBudget(2);
                typeof(PlayerbotBrain).GetMethod("Explore", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(brain, [player, 500u]);

                Assert.Null(typeof(PlayerbotBrain).GetField("_route", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(brain));
                Assert.Contains("wander-refused", brain.FormatDiagnosticSnapshot(player));
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>Every ray is cut to <c>rayYards</c>; only a path to the mesh point is complete. The mesh point is what the query offers.</summary>
    private sealed class BendMeshPathfinder(float rayYards, Vector3 meshPoint) : IPathfinder, IMeshPointQuery
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            if (Vector2.Distance(new(end.X, end.Y), new(meshPoint.X, meshPoint.Y)) <= 1f)
                return PathResult.StraightLine(start, end, PathType.Normal);
            float dx = end.X - start.X, dy = end.Y - start.Y;
            float distance = MathF.Sqrt((dx * dx) + (dy * dy));
            if (!float.IsFinite(distance) || distance <= rayYards + 0.05f)
                return PathResult.StraightLine(start, end, PathType.Normal);
            float scale = rayYards / distance;
            return PathResult.StraightLine(start, new Vector3(start.X + (dx * scale), start.Y + (dy * scale), end.Z), PathType.Normal | PathType.Incomplete);
        }

        public bool TryGetRandomPointAround(uint mapId, Vector3 center, float radius, Random random, PathOptions? options, out Vector3 point)
        {
            point = meshPoint;
            return true;
        }

        public MeshRaycastResult Raycast(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => MeshRaycastResult.Unavailable(end);
    }

    /// <summary>Completes a ray of at most <paramref name="openYards"/> and records each requested end.</summary>
    private sealed class RecordingShortOpenPathfinder(float openYards) : IPathfinder
    {
        public bool Enabled => true;
        public List<Vector3> Ends { get; } = [];

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            Ends.Add(end);
            float dx = end.X - start.X, dy = end.Y - start.Y;
            float distance = MathF.Sqrt((dx * dx) + (dy * dy));
            if (!float.IsFinite(distance) || distance <= openYards + 0.05f)
                return PathResult.StraightLine(start, end, PathType.Normal);
            float scale = openYards / distance;
            var clamped = new Vector3(start.X + (dx * scale), start.Y + (dy * scale), end.Z);
            return PathResult.StraightLine(start, clamped, PathType.Normal | PathType.Incomplete);
        }
    }

    /// <summary>Completes a ray of at most <paramref name="openYards"/>; a longer ray ends there, short of its point.</summary>
    private sealed class ShortOpenGroundPathfinder(float openYards) : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            float dx = end.X - start.X, dy = end.Y - start.Y;
            float distance = MathF.Sqrt((dx * dx) + (dy * dy));
            if (!float.IsFinite(distance) || distance <= openYards + 0.05f)
                return PathResult.StraightLine(start, end, PathType.Normal);
            float scale = openYards / distance;
            var clamped = new Vector3(start.X + (dx * scale), start.Y + (dy * scale), end.Z);
            return PathResult.StraightLine(start, clamped, PathType.Normal | PathType.Incomplete);
        }
    }

    private sealed class EastWallPartialPathfinder(float wallX) : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            if (end.X <= wallX)
                return PathResult.StraightLine(start, end, PathType.Normal);

            var clamped = new Vector3(wallX, end.Y, end.Z);
            return PathResult.StraightLine(start, clamped, PathType.Normal | PathType.Incomplete);
        }
    }

    private sealed class ManualStrandClock : IWorldFeature
    {
        public void Attach(ArcaneCore.Game.Maps.WorldRuntime world) => world.UseManualClock();
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
