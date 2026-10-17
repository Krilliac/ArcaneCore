using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The bot home area (<see cref="PlayerbotHomeArea"/>, <see cref="PlayerbotHomeReturn"/>, the brain's use of them). The expected
/// values are derived from the area's documented rules: a circle on one map in two dimensions, a 0.75 return fraction, a goal choice
/// restricted to the circle. No reference server has a bot home, so there is no external byte or value to compare with.
/// </summary>
public sealed class PlayerbotHomeTests
{
    private const float StartX = -8949.95f;

    private static PlayerbotHomeArea Area(float x, float y, float radius, uint map = 0)
        => new(map, new Vector3(x, y, 0f), radius, "test");

    [Fact]
    public void HomeArea_Contains_UsesTwoDimensionalDistanceOnTheHomeMapOnly()
    {
        PlayerbotHomeArea area = Area(10f, 20f, 100f);

        Assert.True(area.Contains(0, 10f, 20f));
        Assert.True(area.Contains(0, 10f + 60f, 20f + 80f));   // 3-4-5: exactly 100 yards, the edge is inside
        Assert.False(area.Contains(0, 10f + 60.01f, 20f + 80f));
        Assert.False(area.Contains(1, 10f, 20f));              // the same coordinates on another map
        Assert.False(area.Contains(0, float.NaN, 20f));
        Assert.False(area.Contains(0, 10f, float.PositiveInfinity));

        // The height is not part of the distance: a spawn 5000 yards above the centre is inside.
        Assert.True(area.Contains(new CreatureSpawn { Guid = 1, Entry = 1, MapId = 0, X = 20f, Y = 20f, Z = 5000f }));
        Assert.False(area.Contains(new CreatureSpawn { Guid = 2, Entry = 1, MapId = 0, X = 500f, Y = 20f, Z = 0f }));

        Assert.Equal(75f, area.ReturnRadius);
        Assert.Equal(80f, area.ExploreRadius);
        Assert.Equal(5f, area.DistanceTo(13f, 24f)); // 3-4-5
    }

    [Fact]
    public void HomeArea_QuestInside_RequiresEnderAndKillObjectiveSpawnsInside()
    {
        // Spawns: 100 and 500 inside the 100-yard circle at the origin, 600 far outside, 700 inside on map 1 only.
        CreatureContent content = Content(
            Spawn(1, 100, 0, 10, 0), Spawn(2, 500, 0, 20, 0), Spawn(3, 600, 0, 5000, 0), Spawn(4, 700, 1, 30, 0));
        var store = new QuestStore(new QuestContent(
            [
                new QuestTemplate { Entry = 1, Method = 2, ReqCreatureOrGOId1 = 500 },   // ender and objective inside
                new QuestTemplate { Entry = 2 },                                          // ender outside
                new QuestTemplate { Entry = 3, ReqCreatureOrGOId1 = 600 },                // objective outside
                new QuestTemplate { Entry = 4 },                                          // nobody ends it
                new QuestTemplate { Entry = 5, ReqCreatureOrGOId1 = -3 },                 // a game object objective is not checked
                new QuestTemplate { Entry = 6 },                                          // its ender spawns on another map only
            ],
            [],
            [
                new CreatureQuestRelation { Id = 100, Quest = 1 }, new CreatureQuestRelation { Id = 600, Quest = 2 },
                new CreatureQuestRelation { Id = 100, Quest = 3 }, new CreatureQuestRelation { Id = 100, Quest = 5 },
                new CreatureQuestRelation { Id = 700, Quest = 6 },
            ]));
        PlayerbotHomeArea area = Area(0f, 0f, 100f);

        Assert.True(area.QuestInside(store, content, store.Get(1)!));
        Assert.False(area.QuestInside(store, content, store.Get(2)!));
        Assert.False(area.QuestInside(store, content, store.Get(3)!));
        Assert.False(area.QuestInside(store, content, store.Get(4)!));
        Assert.True(area.QuestInside(store, content, store.Get(5)!));
        Assert.False(area.QuestInside(store, content, store.Get(6)!));

        Assert.True(area.HasSpawnInside(content, 100));
        Assert.False(area.HasSpawnInside(content, 600));
        Assert.False(area.HasSpawnInside(content, 700));   // inside the circle's coordinates, but on another map
        Assert.False(area.HasSpawnInside(content, 9999));  // no spawn at all

        // The answers are cached per quest; new creature content (a reload) drops them.
        CreatureContent reloaded = Content(Spawn(1, 100, 0, 10, 0), Spawn(2, 500, 0, 20, 0), Spawn(3, 600, 0, 40, 0));
        Assert.False(area.QuestInside(store, content, store.Get(2)!));
        Assert.True(area.QuestInside(store, reloaded, store.Get(2)!));
        Assert.True(area.QuestInside(store, reloaded, store.Get(3)!));
    }

    [Theory]
    [InlineData("Wfbot*", true, "Wfbot", true)]
    [InlineData("wf*", true, "wf", true)]                       // two letters: the shortest prefix
    [InlineData("abcdefghijk*", true, "abcdefghijk", true)]     // eleven letters: the longest prefix
    [InlineData("a*", false, null, false)]                      // one letter would anchor the whole fleet
    [InlineData("abcdefghijkl*", false, null, false)]           // twelve letters is a full name, not a prefix
    [InlineData("*", false, null, false)]
    [InlineData("Wf*bot", false, null, false)]                  // a wildcard only at the end
    [InlineData("Wf1*", false, null, false)]                    // letters only
    [InlineData("Wfbotaa", false, "Wfbotaa", true)]             // one bot by name
    [InlineData("Abcdefghijkl", false, "Abcdefghijkl", true)]   // twelve letters: the longest name
    [InlineData("Abcdefghijklm", false, null, false)]           // thirteen: no character has it
    [InlineData("", false, null, false)]
    [InlineData("   ", false, null, false)]
    public void HomeSelector_Parse_AcceptsANameAPrefixOrAnId_AndRefusesTheRest(string text, bool prefix, string? parsedText, bool accepted)
    {
        Assert.Equal(accepted, PlayerbotHomeSelector.TryParse(text, out PlayerbotHomeSelector selector));
        if (!accepted) return;
        Assert.Equal(prefix, selector.Prefix);
        Assert.Equal(parsedText, selector.Text);
        Assert.Null(selector.Id);
    }

    [Fact]
    public void HomeSelector_AnIdMatchesOnlyThatBot_AndAPrefixIgnoresCase()
    {
        Guid id = Guid.NewGuid();
        Assert.True(PlayerbotHomeSelector.TryParse(id.ToString(), out PlayerbotHomeSelector byId));
        Assert.Equal(id, byId.Id);
        Assert.True(byId.Matches(Status(id, "Anyname")));
        Assert.False(byId.Matches(Status(Guid.NewGuid(), "Anyname")));

        Assert.True(PlayerbotHomeSelector.TryParse("WFBOT*", out PlayerbotHomeSelector prefix));
        Assert.True(prefix.Matches(Status(Guid.NewGuid(), "Wfbotaa")));
        Assert.False(prefix.Matches(Status(Guid.NewGuid(), "Xwfbot")));      // a prefix, not a substring

        Assert.True(PlayerbotHomeSelector.TryParse("wfbotaa", out PlayerbotHomeSelector exact));
        Assert.True(exact.Matches(Status(Guid.NewGuid(), "Wfbotaa")));
        Assert.False(exact.Matches(Status(Guid.NewGuid(), "Wfbotab")));
        Assert.False(exact.Matches(Status(Guid.NewGuid(), "Wfbotaaa")));     // not a prefix match
    }

    [Fact]
    public async Task HomeReturn_Evaluate_KeepsTheHysteresis_ReportsOffMap_AndSetsABlockedWalkAside()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                uint map = player.Map!.MapId;
                var ret = new PlayerbotHomeReturn();
                // The centre lies 90 yards east of the bot; radius 100 (return radius 75).
                PlayerbotHomeArea area = new(map, new Vector3(player.X + 90f, player.Y, player.Z), 100f, null);
                Assert.Equal(PlayerbotHomeState.Inside, ret.Evaluate(player, area, 0));

                // 150 yards out: outside; at 90 yards the bot is inside the radius but not the return radius, so it keeps walking.
                player.Relocate(player.X - 60f, player.Y, player.Z, 0, host.World.NowMs);
                Assert.Equal(PlayerbotHomeState.Returning, ret.Evaluate(player, area, 0));
                player.Relocate(player.X + 60f, player.Y, player.Z, 0, host.World.NowMs);   // 90 from the centre
                Assert.Equal(PlayerbotHomeState.Returning, ret.Evaluate(player, area, 100));
                player.Relocate(player.X + 20f, player.Y, player.Z, 0, host.World.NowMs);   // 70 from the centre
                Assert.Equal(PlayerbotHomeState.Inside, ret.Evaluate(player, area, 200));
                // A bot that is inside leaves the area only past the full radius, not the return radius.
                player.Relocate(player.X - 25f, player.Y, player.Z, 0, host.World.NowMs);   // 95 from the centre
                Assert.Equal(PlayerbotHomeState.Inside, ret.Evaluate(player, area, 300));
                Assert.Equal("inside:100", ret.Report);                                     // rounded to 10 yards

                // A walk that cannot be followed is set aside for BlockedMs, and ends when the bot is back inside.
                player.Relocate(player.X - 100f, player.Y, player.Z, 0, host.World.NowMs);  // 195 from the centre
                Assert.Equal(PlayerbotHomeState.Returning, ret.Evaluate(player, area, 400));
                ret.MarkBlocked(1_000);
                Assert.Equal(PlayerbotHomeState.Blocked, ret.Evaluate(player, area, 1_000 + PlayerbotHomeReturn.BlockedMs - 1));
                Assert.StartsWith("blocked:", ret.Report);
                Assert.Equal(PlayerbotHomeState.Returning, ret.Evaluate(player, area, 1_000 + PlayerbotHomeReturn.BlockedMs));
                ret.MarkBlocked(2_000);
                player.Relocate(player.X + 150f, player.Y, player.Z, 0, host.World.NowMs);  // 45 from the centre
                Assert.Equal(PlayerbotHomeState.Inside, ret.Evaluate(player, area, 2_001));

                // Another map: the bot is left alone and the distance is unknown.
                PlayerbotHomeArea elsewhere = new(map + 1, area.Centre, 100f, null);
                Assert.Equal(PlayerbotHomeState.OffMap, ret.Evaluate(player, elsewhere, 3_000));
                Assert.Equal("off-map", ret.Report);
                Assert.True(float.IsNaN(ret.Distance));
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task Brain_WithHome_GrindsOnlyCreaturesInsideTheArea()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Creature? nearOutside = null, farInside = null;
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                // The home circle is centred 6 yards west of the bot with radius 12: the bot is inside. The creature 8 yards east
                // is nearer to the bot but 14 yards from the centre (outside); the one 12 yards west is 6 from the centre (inside).
                nearOutside = Hostile(6, 7, 2);
                farInside = Hostile(7, 7, 3);
                nearOutside.Relocate(player.X + 8, player.Y, player.Z, 0, host.World.NowMs);
                farInside.Relocate(player.X - 12, player.Y, player.Z, 0, host.World.NowMs);
                player.Map!.AddObject(nearOutside);
                player.Map.AddObject(farInside);
                return true;
            });
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(nearOutside!.Guid)
                && session.Player.VisibleObjects.Contains(farInside!.Guid), "both candidates visible");
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                Assert.Same(nearOutside, PlayerbotBrain.FindTarget(player)); // without a home the nearer one is chosen
                var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true })
                {
                    Home = new PlayerbotHomeArea(player.Map!.MapId, new Vector3(player.X - 6f, player.Y, player.Z), 12f, null),
                };
                session.ManagedBudget = new ManagedActionBudget(2);
                brain.Update(1_000);
                Assert.Equal(farInside!.Entry, brain.TargetEntry);
                Assert.NotEqual(PlayerbotGoalKind.Home, brain.Goal);
                brain.Stop();
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task Brain_WithoutHome_ChoosesTheSameTargetAsBefore()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Creature? near = null, far = null;
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                near = Hostile(6, 7, 2);
                far = Hostile(7, 7, 3);
                near.Relocate(player.X + 8, player.Y, player.Z, 0, host.World.NowMs);
                far.Relocate(player.X - 12, player.Y, player.Z, 0, host.World.NowMs);
                player.Map!.AddObject(near);
                player.Map.AddObject(far);
                return true;
            });
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(near!.Guid)
                && session.Player.VisibleObjects.Contains(far!.Guid), "both candidates visible");
            await host.World.InvokeAsync(() =>
            {
                var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true });
                Assert.Null(brain.Home);
                Assert.Null(brain.HomeReport);
                Assert.Null(brain.HomeDescription);
                session.ManagedBudget = new ManagedActionBudget(2);
                brain.Update(1_000);
                Assert.Equal(near!.Entry, brain.TargetEntry);
                Assert.Equal(PlayerbotHomeState.Inside, brain.HomeReturn.State); // never evaluated
                brain.Stop();
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task Brain_WithHome_SkipsQuestTravelToGiversOutsideTheArea()
    {
        // The quest giver and ender stands 300 yards east of the bot (QuestTravelFixture).
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            var fixture = new QuestTravelFixture(StartX + 300f);
            services.AddSingleton<IQuestContentStore>(fixture);
            services.AddSingleton<ICreatureDataStore>(fixture);
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                Player player = session.Player!;
                uint map = player.Map!.MapId;
                var options = new PlayerbotOptions { Enabled = true };

                // Control: without a home the giver is a travel candidate.
                Assert.True(new PlayerbotWorldDestinations(session, options).HasQuestCandidate(player));

                // A home around the bot (the giver is 300 yards away, the radius 100): no trip.
                PlayerbotHomeArea around = new(map, new Vector3(player.X, player.Y, player.Z), 100f, null);
                Assert.False(new PlayerbotWorldDestinations(session, options) { Home = around }.HasQuestCandidate(player));

                // A home around the giver: the trip is wanted again.
                PlayerbotHomeArea atGiver = new(map, new Vector3(player.X + 300f, player.Y, player.Z), 100f, null);
                Assert.True(new PlayerbotWorldDestinations(session, options) { Home = atGiver }.HasQuestCandidate(player));

                var brain = new PlayerbotBrain(session, options) { Home = around };
                session.ManagedBudget = new ManagedActionBudget(2);
                brain.Update(1_000);
                Assert.NotEqual(PlayerbotGoalKind.Quest, brain.Goal);
                Assert.Equal(0u, brain.QuestId);
                brain.Stop();
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task Brain_OutsideHome_WithNoErrand_WalksBackAndStopsInsideTheReturnRadius()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                Player player = session.Player!;
                // The centre is 300 yards east; radius 100, return radius 75.
                var area = new PlayerbotHomeArea(player.Map!.MapId, new Vector3(player.X + 300f, player.Y, player.Z), 100f, "Test");
                var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true }) { Home = area };
                float start = player.X;

                session.ManagedBudget = new ManagedActionBudget(2);
                brain.Update(1_000);
                Assert.Equal(PlayerbotGoalKind.Home, brain.Goal);
                Assert.Equal(PlayerbotHomeState.Returning, brain.HomeReturn.State);
                Assert.StartsWith("returning:300", brain.HomeReport);

                bool sawTheBand = false;
                int steps = 0;
                while (brain.HomeReturn.State != PlayerbotHomeState.Inside && steps++ < 200)
                {
                    PlayerbotMotion.ElapseForTests(player, 1_000);
                    session.ManagedBudget = new ManagedActionBudget(2);
                    brain.Update(1_000);
                    // Between the return radius and the radius a walking bot is still walking (no stop at the edge).
                    float distance = area.DistanceTo(player.X, player.Y);
                    if (brain.HomeReturn.State == PlayerbotHomeState.Returning && distance > area.ReturnRadius && distance <= area.Radius)
                        sawTheBand = true;
                }

                Assert.Equal(PlayerbotHomeState.Inside, brain.HomeReturn.State);
                Assert.True(player.X > start);
                Assert.True(sawTheBand, "the bot stopped at the edge instead of walking on to the return radius");
                Assert.True(area.DistanceTo(player.X, player.Y) <= area.ReturnRadius + 10f,
                    $"stopped {area.DistanceTo(player.X, player.Y)} yards from the centre");

                // Home again: the walk-back goal is gone and the bot does not walk home from inside.
                session.ManagedBudget = new ManagedActionBudget(2);
                brain.Update(1_000);
                Assert.NotEqual(PlayerbotGoalKind.Home, brain.Goal);
                brain.Stop();
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task Brain_OutsideHome_InCombatOrRetreating_DoesNotWalkBack()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Creature? hostile = null;
            await host.World.InvokeAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                Player player = session.Player!;
                hostile = Hostile(910099, 7, 4);
                hostile.Relocate(player.X + 8, player.Y, player.Z, 0, host.World.NowMs);
                player.Map!.AddObject(hostile);
                return true;
            });
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(hostile!.Guid), "hostile visible");
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                var area = new PlayerbotHomeArea(player.Map!.MapId, new Vector3(player.X + 300f, player.Y, player.Z), 100f, null);
                var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true }) { Home = area };
                float before = player.X;

                // In combat: the bot fights, the walk home waits.
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(session.TryManagedAction(WorldOpcode.CmsgAttackswing, PlayerbotNavigation.GuidPayload(hostile!.Guid.Value)));
                player.Map!.Combat.DealDamage(hostile, player, 1);
                Assert.True(player.Combat.IsInCombat);
                session.ManagedBudget = new ManagedActionBudget(2);
                brain.Update(1_000);
                Assert.NotEqual(PlayerbotGoalKind.Home, brain.Goal);
                Assert.Equal(before, player.X);

                // Retreating: the retreat owns the bot.
                brain.Risk.Retreat.Start(player, [hostile], new Dictionary<ObjectGuid, Vector3>(), [], "test");
                Assert.True(brain.Risk.Retreat.Active);
                session.ManagedBudget = new ManagedActionBudget(2);
                brain.Update(1_000);
                Assert.NotEqual(PlayerbotGoalKind.Home, brain.Goal);
                brain.Stop();
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task Brain_OutsideHome_WithAVendorErrand_RunsTheErrandFirst()
    {
        var fixture = new TownVendorFixture();
        TownVendorServices.Current.Value = fixture;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(configureServices:
                services => services.AddSingleton<ArcaneCore.Data.Content.Spells.ISpellContentStore>(fixture));
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(TownVendorFixture.Guid), "town NPC visible");
                await host.World.InvokeAsync(() =>
                {
                    WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                    Player player = session.Player!;
                    // The bot stands next to the trainer and vendor; its home is 600 yards east, so it is outside.
                    var area = new PlayerbotHomeArea(player.Map!.MapId, new Vector3(player.X + 600f, player.Y, player.Z), 100f, null);

                    // Nothing to do at the NPC (no money for the trainer): the bot walks home.
                    player.Money = 0;
                    var broke = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true }) { Home = area };
                    session.ManagedBudget = new ManagedActionBudget(4);
                    broke.Update(500);
                    Assert.Equal(PlayerbotGoalKind.Home, broke.Goal);
                    broke.Stop();

                    // Something useful to do there: the errand keeps its priority over the walk home.
                    player.Money = 100;
                    var errand = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true }) { Home = area };
                    session.ManagedBudget = new ManagedActionBudget(4);
                    errand.Update(500);
                    Assert.Equal(PlayerbotGoalKind.Train, errand.Goal);
                    Assert.Equal(TownVendorFixture.Entry, errand.TargetEntry);
                    errand.Stop();
                    return true;
                });
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { TownVendorServices.Current.Value = null; }
    }

    /// <summary>Walk mode (<see cref="PlayerbotHomeReturnMode.Walk"/>): a bot on another map is left alone. Auto and Teleport: PlayerbotHomeTeleportTests.</summary>
    [Fact]
    public async Task Brain_OnAnotherMap_ReportsOffMapAndDoesNotWalk()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                Player player = session.Player!;
                var area = new PlayerbotHomeArea(player.Map!.MapId + 1, new Vector3(player.X + 300f, player.Y, player.Z), 100f, "Elsewhere");
                var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, Home = { ReturnMode = PlayerbotHomeReturnMode.Walk } }) { Home = area };
                session.ManagedBudget = new ManagedActionBudget(2);
                brain.Update(1_000);
                Assert.Equal(PlayerbotHomeState.OffMap, brain.HomeReturn.State);
                Assert.Equal("off-map", brain.HomeReport);
                Assert.NotEqual(PlayerbotGoalKind.Home, brain.Goal);
                Assert.Contains("state=offmap", brain.HomeDescription);
                Assert.Contains("distance=n/a", brain.HomeDescription);
                brain.Stop();
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>Walk mode, so the off-map bot stays there (Auto would teleport it home: PlayerbotHomeTeleportTests).</summary>
    [Fact]
    public async Task Brain_OffMap_IsNotStarved_ItGrindsLikeABotWithNoHome()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Creature? near = null;
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                near = Hostile(6, 7, 2);
                near.Relocate(player.X + 8, player.Y, player.Z, 0, host.World.NowMs);
                player.Map!.AddObject(near);
                return true;
            });
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(near!.Guid), "the candidate is visible");
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                // The home is on another map: the in-area filters would skip every creature here (RED: the creature is never chosen).
                var area = new PlayerbotHomeArea(player.Map!.MapId + 1, new Vector3(player.X, player.Y, player.Z), 100f, "Elsewhere");
                var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, Home = { ReturnMode = PlayerbotHomeReturnMode.Walk } }) { Home = area };
                session.ManagedBudget = new ManagedActionBudget(2);
                brain.Update(1_000);
                Assert.Equal(PlayerbotHomeState.OffMap, brain.HomeReturn.State);
                Assert.Equal(near!.Entry, brain.TargetEntry);
                Assert.NotEqual(PlayerbotGoalKind.Home, brain.Goal);
                Assert.Null(brain.ActiveFilterHome);
                brain.Stop();
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task Brain_DiesOutsideHome_RecoversItsCorpse_ThenReturnsHome()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            var clock = new TestClock(1_000);
            await host.World.InvokeAsync(() =>
            {
                DeathHooks.Register(host.World, new DeathHooks(new DeathOptions(), clock));
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                Player player = session.Player!;
                var area = new PlayerbotHomeArea(player.Map!.MapId, new Vector3(player.X + 300f, player.Y, player.Z), 100f, null);
                var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, AllowedMaps = [0, 1] }) { Home = area };

                // The bot walks home, then dies on the way.
                session.ManagedBudget = new ManagedActionBudget(2);
                brain.Update(1_000);
                Assert.Equal(PlayerbotGoalKind.Home, brain.Goal);
                player.Health = 0;
                player.Map!.Combat.KillPlayer(player);
                Assert.True(player.Map.Combat.RepopPlayer(player));

                // A ghost recovers; it does not walk home.
                session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(1_000);
                Assert.Equal(PlayerbotGoalKind.Recover, brain.Goal);
                clock.Seconds += 31;
                for (int i = 0; i < 20 && !player.IsAlive; i++)
                {
                    PlayerbotMotion.ElapseForTests(player, 1_000);
                    session.ManagedBudget = new ManagedActionBudget(4);
                    brain.Update(1_000);
                    Assert.Equal(PlayerbotGoalKind.Recover, brain.Goal);
                }

                Assert.True(player.IsAlive, "the ghost never reclaimed its corpse");
                // Alive, still outside: the walk home is planned afresh.
                var goals = new List<PlayerbotGoalKind>();
                for (int i = 0; i < 10 && brain.Goal != PlayerbotGoalKind.Home; i++)
                {
                    PlayerbotMotion.ElapseForTests(player, 1_000);
                    session.ManagedBudget = new ManagedActionBudget(2);
                    brain.Update(1_000);
                    goals.Add(brain.Goal);
                }

                Assert.True(brain.Goal == PlayerbotGoalKind.Home, string.Join(",", goals));
                Assert.Equal(PlayerbotHomeState.Returning, brain.HomeReturn.State);
                brain.Stop();
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task Brain_SettingAHome_DropsTheIntent_AndClearingItRestoresTheOldBehaviour()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                Player player = session.Player!;
                var area = new PlayerbotHomeArea(player.Map!.MapId, new Vector3(player.X + 300f, player.Y, player.Z), 100f, null);
                var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true });
                brain.Home = area;
                Assert.Same(area, brain.Home);
                Assert.StartsWith("inside", brain.HomeReport);
                session.ManagedBudget = new ManagedActionBudget(2);
                brain.Update(1_000);
                Assert.Equal(PlayerbotGoalKind.Home, brain.Goal);

                brain.Home = null;
                Assert.Null(brain.HomeReport);
                Assert.Equal(PlayerbotHomeState.Inside, brain.HomeReturn.State);
                PlayerbotMotion.ElapseForTests(player, 1_000);
                session.ManagedBudget = new ManagedActionBudget(2);
                brain.Update(1_000);
                Assert.NotEqual(PlayerbotGoalKind.Home, brain.Goal);
                brain.Stop();
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task Brain_OutsideHome_SlidingAlongWallWithoutNetHomewardProgress_MarksWalkBlockedAndAutoTeleports()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                var options = new PlayerbotOptions { Enabled = true };
                var area = new PlayerbotHomeArea(player.Map!.MapId, new Vector3(player.X + 300f, player.Y, player.Z), 100f, "Test");

                // 1. An incomplete partial path that slides 20 yards sideways along a wall while closing on the home centre by only ~2.3 yards
                // (< 25% of travel) is refused outright by PlayerbotNavigation.PlanToward.
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new LongIncompleteWallSlidePathfinder());
                Assert.False(PlayerbotNavigation.TryPlanToward(player, area.Centre, options, out _));

                // 2. Even when 8-yard wall-hugging routes are returned and the bot moves every tick (> 0.05 yards), failing to close on
                // the home centre by at least ProgressYards (4 yards) does not reset _failures; after MaxFailures (3) routes the walk is
                // marked WalkBlocked and ReturnMode.Auto teleports the bot home.
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new NonProgressingWallRoutesPathfinder());
                var brain = new PlayerbotBrain(session, options) { Home = area };
                int ticks = 0;
                while (brain.HomeReturn.Teleports == 0 && ticks++ < 30)
                {
                    PlayerbotMotion.ElapseForTests(player, 1_000);
                    session.ManagedBudget = new ManagedActionBudget(4);
                    brain.Update(1_000);
                }

                Assert.Equal(1, brain.HomeReturn.Teleports);
                Assert.Equal(PlayerbotHomeTeleportReason.WalkBlocked, brain.HomeReturn.LastTeleport);
                brain.Stop();
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    private static PlayerbotStatus Status(Guid id, string name)
        => new(id, name, ManagedPlayerbotState.Running, true, PlayerbotGoalKind.Explore, 0, 0, 0, 0, null);

    private static CreatureSpawn Spawn(uint guid, uint entry, uint map, float x, float y)
        => new() { Guid = guid, Entry = entry, MapId = map, X = x, Y = y, Z = 0f };

    private static CreatureContent Content(params CreatureSpawn[] spawns)
        => new(spawns.Select(spawn => spawn.Entry).Distinct().Select(entry => new CreatureTemplate { Entry = entry, Name = "c" + entry }),
            spawns, [], [], []);

    private static Creature Hostile(uint entry, uint type, uint low)
        => new(low, new CreatureTemplate { Entry = entry, Name = "candidate", CreatureType = type, MinLevelHealth = 20, MaxLevelHealth = 20 },
            null, new CreatureContent([], [], [], [], []), new Random(1));

    private sealed class TestClock(long seconds) : DeathClock
    {
        public long Seconds { get; set; } = seconds;
        public override long UnixSeconds => Seconds;
    }

    private sealed class LongIncompleteWallSlidePathfinder : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => Vector3.Distance(start, end) < 1f
                ? new PathResult(PathType.Normal, [start, end])
                : new PathResult(PathType.Incomplete, [start, new Vector3(start.X + 3f, start.Y + 20f, start.Z)]);
    }

    private sealed class NonProgressingWallRoutesPathfinder : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => Vector3.Distance(start, end) < 1f
                ? new PathResult(PathType.Normal, [start, end])
                : new PathResult(PathType.Normal, [start, new Vector3(start.X + 0.5f, start.Y + 8f, start.Z)]);
    }
}
