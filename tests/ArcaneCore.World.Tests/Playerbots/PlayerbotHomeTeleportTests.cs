using System.Collections.Concurrent;
using System.Numerics;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Reload;
using ArcaneCore.World.Social;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The home return mode (<see cref="PlayerbotHomeOptions.ReturnMode"/>, <c>World:Playerbots:Home</c>): walk, teleport, or teleport when
/// the walk is long or unsafe. The live failure it answers: Westfall-homed level-14 bots that had drifted to the Darkshire road in
/// Duskwood (about -10703,-918) and to Loch Modan (about -5886,-2951) walked home across higher-level zones and died in death loops on
/// the way, until the operator sent them home by hand (world.log 2026-10-11: Wfbotfk "death loop ... attackers=202/531/569", then
/// <c>.playerbot send Wfbot* SentinelHill force</c>). No reference server anchors a bot to an area: the thresholds are ArcaneCore's, the
/// teleport is the follower's catch-up teleport (vmangos PartyBotAI <c>HandleGonameCommand</c>, the ordinary teleport service).
/// </summary>
public sealed class PlayerbotHomeTeleportTests
{
    private static readonly PlayerbotHomeOptions Auto = new();

    // --- the rules alone --------------------------------------------------------------------------------------------

    [Theory]
    // Auto: another map, a blocked walk, farther than 1500 yards, or an area on the way more than 5 levels above the bot.
    [InlineData(PlayerbotHomeReturnMode.Auto, false, float.NaN, 0, 14, false, PlayerbotHomeTeleportReason.OtherMap)]
    [InlineData(PlayerbotHomeReturnMode.Auto, true, 300f, 0, 14, true, PlayerbotHomeTeleportReason.WalkBlocked)]
    [InlineData(PlayerbotHomeReturnMode.Auto, true, 1960f, 0, 14, false, PlayerbotHomeTeleportReason.Distance)]
    [InlineData(PlayerbotHomeReturnMode.Auto, true, 1500.5f, 0, 14, false, PlayerbotHomeTeleportReason.Distance)]
    [InlineData(PlayerbotHomeReturnMode.Auto, true, 1500f, 0, 14, false, PlayerbotHomeTeleportReason.None)]      // the threshold itself walks
    [InlineData(PlayerbotHomeReturnMode.Auto, true, 600f, 20, 14, false, PlayerbotHomeTeleportReason.Danger)]    // 20 > 14 + 5
    [InlineData(PlayerbotHomeReturnMode.Auto, true, 600f, 19, 14, false, PlayerbotHomeTeleportReason.None)]      // 19 is not above 19
    [InlineData(PlayerbotHomeReturnMode.Auto, true, 600f, 0, 1, false, PlayerbotHomeTeleportReason.None)]        // no level known: safe
    [InlineData(PlayerbotHomeReturnMode.Auto, true, 300f, 18, 14, false, PlayerbotHomeTeleportReason.None)]      // short and safe: walk
    // Teleport: every return; Walk: never, however far, on another map too.
    [InlineData(PlayerbotHomeReturnMode.Teleport, true, 120f, 0, 14, false, PlayerbotHomeTeleportReason.Mode)]
    [InlineData(PlayerbotHomeReturnMode.Teleport, false, float.NaN, 0, 14, false, PlayerbotHomeTeleportReason.OtherMap)]
    [InlineData(PlayerbotHomeReturnMode.Walk, true, 6200f, 60, 14, true, PlayerbotHomeTeleportReason.None)]
    [InlineData(PlayerbotHomeReturnMode.Walk, false, float.NaN, 0, 14, false, PlayerbotHomeTeleportReason.None)]
    public void Decide_FollowsTheMode(PlayerbotHomeReturnMode mode, bool onHomeMap, float distance, int areaLevel, int botLevel, bool blocked,
        PlayerbotHomeTeleportReason expected)
    {
        var options = new PlayerbotHomeOptions { ReturnMode = mode };
        Assert.Equal(expected, PlayerbotHomeRules.Decide(new PlayerbotHomeFacts(onHomeMap, distance, areaLevel, botLevel, blocked), options));
    }

    [Fact]
    public void HighestOnLine_SamplesTheStraightLineEveryTwentyYards()
    {
        // A 10-yard strip of level 30 at x 500..510 on a 1000-yard line of level 10; nothing known beyond x 990.
        var levels = new PlayerbotAreaLevelLookup((x, y, z) => x is >= 500f and < 510f ? 30 : x > 990f ? 0 : 10);
        Assert.Equal(30, levels.HighestOnLine(new Vector3(0f, 0f, 0f), new Vector3(1000f, 0f, 0f)));
        Assert.Equal(10, levels.HighestOnLine(new Vector3(0f, 0f, 0f), new Vector3(400f, 0f, 0f)));
        Assert.Equal(0, levels.HighestOnLine(new Vector3(995f, 0f, 0f), new Vector3(999f, 0f, 0f)));
        Assert.Equal(0, levels.LevelAt(float.NaN, 0f, 0f));
    }

    // --- configuration ----------------------------------------------------------------------------------------------

    [Fact]
    public void TheDefaults_AreAuto1500Yards5Levels()
    {
        Assert.Equal(PlayerbotHomeReturnMode.Auto, Auto.ReturnMode);
        Assert.Equal(1500f, Auto.TeleportDistanceYards);
        Assert.Equal(5, Auto.DangerLevelMargin);
        Assert.Same(new PlayerbotOptions().Home.GetType(), typeof(PlayerbotHomeOptions));
        new PlayerbotOptions().Validate();
    }

    [Fact]
    public void TheKeys_BindFromWorldPlayerbotsHome()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["World:Playerbots:Home:ReturnMode"] = "Walk",
            ["World:Playerbots:Home:TeleportDistanceYards"] = "900",
            ["World:Playerbots:Home:DangerLevelMargin"] = "8",
        }).Build();
        PlayerbotHomeOptions home = PlayerbotOptions.Bind(configuration).Home;
        Assert.Equal(PlayerbotHomeReturnMode.Walk, home.ReturnMode);
        Assert.Equal(900f, home.TeleportDistanceYards);
        Assert.Equal(8, home.DangerLevelMargin);
    }

    [Theory]
    [InlineData(nameof(PlayerbotHomeOptions.TeleportDistanceYards), 99f)]
    [InlineData(nameof(PlayerbotHomeOptions.TeleportDistanceYards), 20001f)]
    [InlineData(nameof(PlayerbotHomeOptions.TeleportDistanceYards), float.NaN)]
    [InlineData(nameof(PlayerbotHomeOptions.DangerLevelMargin), -1f)]
    [InlineData(nameof(PlayerbotHomeOptions.DangerLevelMargin), 61f)]
    public void AValueOutOfRange_IsRefusedAtStart(string key, float value)
    {
        var options = new PlayerbotOptions();
        System.Reflection.PropertyInfo property = typeof(PlayerbotHomeOptions).GetProperty(key)!;
        property.SetValue(options.Home, property.PropertyType == typeof(int) ? (object)(int)value : value);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("World:Playerbots:Home: " + key, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUndefinedMode_IsRefused()
    {
        Assert.Throws<InvalidOperationException>(new PlayerbotOptions { Home = { ReturnMode = (PlayerbotHomeReturnMode)9 } }.Validate);
        Assert.Throws<InvalidOperationException>(new PlayerbotOptions { Home = null! }.Validate);
    }

    /// <summary>Every key is live: <c>.reload config</c> applies it to the shared options the brains read, and refuses a value out of range.</summary>
    [Fact]
    public void EveryKey_IsLive_WithItsRangeCheck()
    {
        foreach (string key in typeof(PlayerbotHomeOptions).GetProperties().Select(p => "World:Playerbots:Home:" + p.Name))
            Assert.True(Assert.Single(WorldConfigKeys.All, k => k.Path == key).Live, key);

        var view = new WorldConfigView(new Game.Maps.WorldRuntimeOptions(), null, Playerbots: new PlayerbotOptions());
        Key("ReturnMode").Apply!(view, PlayerbotHomeReturnMode.Walk);
        Assert.Equal(PlayerbotHomeReturnMode.Walk, view.Playerbots!.Home.ReturnMode);
        Assert.NotNull(Key("ReturnMode").Check!((PlayerbotHomeReturnMode)9));
        Assert.NotNull(Key("TeleportDistanceYards").Check!(50f));
        Assert.Null(Key("TeleportDistanceYards").Check!(2000f));
        Assert.NotNull(Key("DangerLevelMargin").Check!(70));
        Assert.Null(Key("DangerLevelMargin").Check!(3));
    }

    // --- the brain ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// A bot 2000 yards from its centre on the home map: Auto and Teleport teleport it to the centre (the ordinary teleport service, the
    /// near teleport acknowledged as a client would), log it at Information with the reason and the distance, and count it in the status
    /// line and the inspection; Walk walks (RED for the old behaviour: the walk is what killed the Westfall bots).
    /// </summary>
    [Theory]
    [InlineData(PlayerbotHomeReturnMode.Auto, PlayerbotHomeTeleportReason.Distance)]
    [InlineData(PlayerbotHomeReturnMode.Teleport, PlayerbotHomeTeleportReason.Mode)]
    [InlineData(PlayerbotHomeReturnMode.Walk, PlayerbotHomeTeleportReason.None)]
    public async Task ABotFarAwayOnTheHomeMap_TeleportsInAutoAndTeleport_AndWalksInWalk(PlayerbotHomeReturnMode mode, PlayerbotHomeTeleportReason reason)
    {
        var logs = new ListLoggerFactory();
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<ILoggerFactory>(logs));
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            var options = new PlayerbotOptions { Enabled = true, Home = { ReturnMode = mode } };
            PlayerbotHomeArea? area = null;
            PlayerbotBrain? brain = null;
            float startX = 0f;
            await host.World.InvokeAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                Player player = session.Player!;
                startX = player.X;
                area = new PlayerbotHomeArea(player.Map!.MapId, new Vector3(player.X + 2000f, player.Y, player.Z), 100f, "Farhome");
                brain = new PlayerbotBrain(session, options) { Home = area };
                session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(1_000);
                Assert.Equal(PlayerbotGoalKind.Home, brain.Goal);
                Assert.Equal(reason, brain.HomeReturn.LastJudgement);
                return true;
            });

            if (reason == PlayerbotHomeTeleportReason.None)
            {
                await host.World.InvokeAsync(() =>
                {
                    Player player = session.Player!;
                    for (int i = 0; i < 5; i++)
                    {
                        PlayerbotMotion.ElapseForTests(player, 1_000);
                        session.ManagedBudget = new ManagedActionBudget(4);
                        brain!.Update(1_000);
                    }

                    Assert.Equal(0, brain!.HomeReturn.Teleports);
                    Assert.Equal(PlayerbotHomeState.Returning, brain.HomeReturn.State);
                    Assert.True(player.X > startX + 10f && player.X < startX + 100f, $"walked to x={player.X} from {startX}");
                    Assert.DoesNotContain(",teleports=", brain.HomeReport, StringComparison.Ordinal);
                    brain.Stop();
                    return true;
                });
                Assert.DoesNotContain(logs.Lines, line => line.Contains("teleports home", StringComparison.Ordinal));
                return;
            }

            // The near teleport is acknowledged at the bot's next think (PlayerbotMovementControl), as a client's.
            await host.WaitForWorldAsync(() =>
            {
                session.ManagedBudget = new ManagedActionBudget(4);
                brain!.Update(1_000);
                return area!.DistanceTo(session.Player!.X, session.Player.Y) < 1f
                    && !host.WorldServices.GetRequiredService<TeleportFeature>().Teleports.IsBeingTeleported(session.Player);
            }, "the bot arrives at its home's centre");
            await host.World.InvokeAsync(() =>
            {
                session.ManagedBudget = new ManagedActionBudget(4);
                brain!.Update(1_000);
                Assert.Equal(1, brain.HomeReturn.Teleports);
                Assert.Equal(reason, brain.HomeReturn.LastTeleport);
                Assert.Equal(PlayerbotHomeState.Inside, brain.HomeReturn.State);
                Assert.Equal("inside:0,teleports=1", brain.HomeReport);
                Assert.Contains($"mode={mode.ToString().ToLowerInvariant()} teleports=1 last-teleport={reason.ToString().ToLowerInvariant()}",
                    brain.HomeDescription, StringComparison.Ordinal);
                brain.Stop();
                return true;
            });
            string logged = Assert.Single(logs.Lines, line => line.Contains("teleports home", StringComparison.Ordinal));
            Assert.Contains($"Playerbot Controlone teleports home to Farhome ({PlayerbotHomeRules.Describe(reason)}; 2000 yd)", logged, StringComparison.Ordinal);
            Assert.Equal(LogLevel.Information, logs.LevelOf(logged));
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// A short way home walks in Auto; the same way through an area far above the bot's level (a fixture strip of level 30 for a level-1
    /// bot) teleports, and one only a little above it walks.
    /// </summary>
    [Theory]
    [InlineData(0, PlayerbotHomeTeleportReason.None)]
    [InlineData(6, PlayerbotHomeTeleportReason.None)]     // 6 is not more than 1 + 5
    [InlineData(30, PlayerbotHomeTeleportReason.Danger)]
    public async Task AShortWayHome_WalksInAuto_UnlessItCrossesAHigherLevelArea(int stripLevel, PlayerbotHomeTeleportReason expected)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                Player player = session.Player!;
                Assert.Equal(1, (int)player.Level);
                float strip = player.X + 150f;
                var area = new PlayerbotHomeArea(player.Map!.MapId, new Vector3(player.X + 300f, player.Y, player.Z), 100f, null)
                {
                    AreaLevels = new PlayerbotAreaLevelLookup((x, y, z) => x >= strip && x < strip + 15f ? stripLevel : 3),
                };
                var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true }) { Home = area };
                float before = player.X;
                session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(1_000);
                Assert.Equal(PlayerbotGoalKind.Home, brain.Goal);
                Assert.Equal(expected, brain.HomeReturn.LastJudgement);
                Assert.Equal(expected == PlayerbotHomeTeleportReason.None ? 0 : 1, brain.HomeReturn.Teleports);
                if (expected == PlayerbotHomeTeleportReason.None)
                {
                    PlayerbotMotion.ElapseForTests(player, 1_000);
                    session.ManagedBudget = new ManagedActionBudget(4);
                    brain.Update(1_000);
                    Assert.True(player.X > before, "the bot did not walk");
                }

                brain.Stop();
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// A bot on another map than its home: Auto teleports it home (a far teleport, the worldport acknowledged as a client's) when the
    /// home map is allowed (<see cref="PlayerbotMapPolicy"/>); with the home map outside <c>AllowedMaps</c> it stays off-map; Walk leaves
    /// it alone (the old behaviour).
    /// </summary>
    [Theory]
    [InlineData(PlayerbotHomeReturnMode.Auto, true, true)]
    [InlineData(PlayerbotHomeReturnMode.Auto, false, false)]
    [InlineData(PlayerbotHomeReturnMode.Walk, true, false)]
    public async Task ABotOnAnotherMap_TeleportsHomeInAuto_WhenTheMapPolicyAllowsIt(PlayerbotHomeReturnMode mode, bool kalimdorAllowed, bool teleports)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            // The home is the Valley of Trials on Kalimdor (map 1); the bot starts on Eastern Kingdoms (map 0).
            var valley = new Vector3(-618.5f, -4251.7f, 38.7f);
            var options = new PlayerbotOptions { Enabled = true, AllowedMaps = kalimdorAllowed ? [0, 1] : [0], Home = { ReturnMode = mode } };
            var area = new PlayerbotHomeArea(1, valley, 200f, "Valley");
            PlayerbotBrain? brain = null;
            await host.World.InvokeAsync(() =>
            {
                Assert.Equal(0u, session.Player!.MapId);
                brain = new PlayerbotBrain(session, options) { Home = area };
                session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(1_000);
                Assert.Equal(teleports ? 1 : 0, brain.HomeReturn.Teleports);
                if (!teleports)
                {
                    Assert.Equal(PlayerbotHomeState.OffMap, brain.HomeReturn.State);
                    Assert.Equal("off-map", brain.HomeReport);
                    Assert.NotEqual(PlayerbotGoalKind.Home, brain.Goal);
                    Assert.Null(brain.ActiveFilterHome); // off-map: it behaves as a bot with no home
                }
                else
                {
                    Assert.Equal(PlayerbotHomeTeleportReason.OtherMap, brain.HomeReturn.LastTeleport);
                    Assert.Equal(PlayerbotGoalKind.Home, brain.Goal);
                }

                return true;
            });
            if (teleports)
            {
                TeleportService service = host.WorldServices.GetRequiredService<TeleportFeature>().Teleports;
                await host.WaitForWorldAsync(() =>
                {
                    Player player = session.Player!;
                    PlayerbotMovementControl.Update(session, player);
                    return player.IsInWorld && player.Map?.MapId == 1 && service.StageOf(player) is null;
                }, "the bot arrives on Kalimdor");
                await host.World.InvokeAsync(() =>
                {
                    session.ManagedBudget = new ManagedActionBudget(4);
                    brain!.Update(1_000);
                    Assert.Equal(PlayerbotHomeState.Inside, brain.HomeReturn.State);
                    Assert.True(area.DistanceTo(session.Player!.X, session.Player.Y) < 1f);
                    return true;
                });
            }

            await host.World.InvokeAsync(() => { brain!.Stop(); return true; });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>Never in combat: a bot fighting 2000 yards from home in Teleport mode fights on; the teleport itself refuses a bot in combat.</summary>
    [Fact]
    public async Task NeverInCombat()
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
                hostile = new Creature(4, new CreatureTemplate { Entry = 910098, Name = "candidate", CreatureType = 7, MinLevelHealth = 20, MaxLevelHealth = 20 },
                    null, new CreatureContent([], [], [], [], []), new Random(1));
                hostile.Relocate(player.X + 8, player.Y, player.Z, 0, host.World.NowMs);
                player.Map!.AddObject(hostile);
                return true;
            });
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(hostile!.Guid), "hostile visible");
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                var area = new PlayerbotHomeArea(player.Map!.MapId, new Vector3(player.X + 2000f, player.Y, player.Z), 100f, null);
                var options = new PlayerbotOptions { Enabled = true, Home = { ReturnMode = PlayerbotHomeReturnMode.Teleport } };
                var brain = new PlayerbotBrain(session, options) { Home = area };
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(session.TryManagedAction(WorldOpcode.CmsgAttackswing, PlayerbotNavigation.GuidPayload(hostile!.Guid.Value)));
                player.Map!.Combat.DealDamage(hostile, player, 1);
                Assert.True(player.Combat.IsInCombat);
                float before = player.X;
                session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(1_000);
                Assert.NotEqual(PlayerbotGoalKind.Home, brain.Goal);
                Assert.Equal(0, brain.HomeReturn.Teleports);

                // The teleport's own gate, reached directly: refused in combat (not only skipped by the brain's out-of-combat block).
                var ret = new PlayerbotHomeReturn();
                Assert.Equal(PlayerbotHomeState.Returning, ret.Evaluate(player, area, host.World.NowMs));
                Assert.False(ret.TryTeleport(session, player, area, options, PlayerbotHomeTeleportReason.Mode, host.World.NowMs));
                Assert.Equal(0, ret.Teleports);
                Assert.Equal(PlayerbotHomeState.Blocked, ret.State); // set aside, not walked instead
                Assert.False(host.WorldServices.GetRequiredService<TeleportFeature>().Teleports.IsBeingTeleported(player));
                Assert.True(MathF.Abs(player.X - before) < 20f);
                brain.Stop();
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>Never dead or as a ghost: a dead bot recovers its corpse; the teleport refuses a dead bot.</summary>
    [Fact]
    public async Task NeverDeadOrAGhost()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                Player player = session.Player!;
                var area = new PlayerbotHomeArea(player.Map!.MapId, new Vector3(player.X + 2000f, player.Y, player.Z), 100f, null);
                var options = new PlayerbotOptions { Enabled = true, Home = { ReturnMode = PlayerbotHomeReturnMode.Teleport } };
                var brain = new PlayerbotBrain(session, options) { Home = area };
                Game.Death.DeathHooks.Register(host.World, new Game.Death.DeathHooks(new Game.Death.DeathOptions(), Game.Death.DeathClock.System));
                player.Health = 0;
                player.Map!.Combat.KillPlayer(player);
                // Dead (a body, not yet a ghost): the teleport refuses it.
                var ret = new PlayerbotHomeReturn();
                ret.Evaluate(player, area, host.World.NowMs);
                Assert.False(ret.TryTeleport(session, player, area, options, PlayerbotHomeTeleportReason.Mode, host.World.NowMs));
                Assert.True(player.Map.Combat.RepopPlayer(player)); // a ghost now
                var ghost = new PlayerbotHomeReturn();
                ghost.Evaluate(player, area, host.World.NowMs);
                Assert.False(ghost.TryTeleport(session, player, area, options, PlayerbotHomeTeleportReason.Mode, host.World.NowMs));
                Assert.Equal(0, ret.Teleports + ghost.Teleports);

                // The brain of a ghost recovers its corpse; it does not teleport home.
                for (int i = 0; i < 3; i++)
                {
                    session.ManagedBudget = new ManagedActionBudget(4);
                    brain.Update(1_000);
                    Assert.Equal(PlayerbotGoalKind.Recover, brain.Goal);
                }

                Assert.Equal(0, brain.HomeReturn.Teleports);
                brain.Stop();
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// Never while grouped: a bot in a real player's group belongs to its party AI (the feature does not even run the brain then, its home
    /// shows <c>suspended</c>); the brain's own teleport refuses a grouped bot as well and sets the return aside instead of walking it.
    /// </summary>
    [Fact]
    public async Task NeverInAGroup()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        await using WorldTestClient master = await host.EnterWorldAsync("HOMEMASTER", "Homemaster");
        try
        {
            await host.World.InvokeAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                Player player = session.Player!;
                GroupManager groups = host.WorldServices.GetRequiredService<SocialFeature>().Context.Groups;
                groups.Invite(host.World.FindOnlinePlayer("Homemaster")!, player.Name);
                groups.Accept(player);
                Assert.NotNull(groups.GetGroup(player.Guid));

                var area = new PlayerbotHomeArea(player.Map!.MapId, new Vector3(player.X + 2000f, player.Y, player.Z), 100f, null);
                var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, Home = { ReturnMode = PlayerbotHomeReturnMode.Teleport } })
                {
                    Home = area,
                };
                float before = player.X;
                session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(1_000);
                Assert.Equal(0, brain.HomeReturn.Teleports);
                Assert.Equal(PlayerbotHomeState.Blocked, brain.HomeReturn.State);
                Assert.NotEqual(PlayerbotGoalKind.Home, brain.Goal);
                Assert.True(MathF.Abs(player.X - before) < 20f);
                brain.Stop();
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>A walk home set aside as blocked teleports at once in Auto; in Walk it stays set aside (the old behaviour).</summary>
    [Theory]
    [InlineData(PlayerbotHomeReturnMode.Auto, 1)]
    [InlineData(PlayerbotHomeReturnMode.Walk, 0)]
    public async Task ABlockedWalk_TeleportsInAuto(PlayerbotHomeReturnMode mode, int teleports)
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
                var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, Home = { ReturnMode = mode } }) { Home = area };
                session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(1_000);
                Assert.Equal(PlayerbotGoalKind.Home, brain.Goal);
                Assert.Equal(0, brain.HomeReturn.Teleports); // a short, safe walk

                brain.HomeReturn.MarkBlocked(host.World.NowMs); // a stall on the walk (PlayerbotStallWatch)
                Assert.True(brain.HomeReturn.WalkBlocked);
                PlayerbotMotion.ElapseForTests(player, 1_000);
                session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(1_000);
                Assert.Equal(teleports, brain.HomeReturn.Teleports);
                if (teleports == 1) Assert.Equal(PlayerbotHomeTeleportReason.WalkBlocked, brain.HomeReturn.LastTeleport);
                else Assert.Equal(PlayerbotHomeState.Blocked, brain.HomeReturn.State);
                brain.Stop();
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// <c>.reload config</c> changes the mode live: the reload key writes the options object the running brain reads, and its next
    /// decision follows the new mode (Walk walks, then Teleport teleports the same bot).
    /// </summary>
    [Fact]
    public async Task AConfigReload_ChangesTheModeLive()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                Player player = session.Player!;
                var options = new PlayerbotOptions { Enabled = true };
                var view = new WorldConfigView(new Game.Maps.WorldRuntimeOptions(), null, Playerbots: options);
                Key("ReturnMode").Apply!(view, PlayerbotHomeReturnMode.Walk);
                var area = new PlayerbotHomeArea(player.Map!.MapId, new Vector3(player.X + 300f, player.Y, player.Z), 100f, null);
                var brain = new PlayerbotBrain(session, options) { Home = area };
                session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(1_000);
                Assert.Equal(PlayerbotGoalKind.Home, brain.Goal);
                Assert.Equal(0, brain.HomeReturn.Teleports);
                Assert.Contains("mode=walk", brain.HomeDescription, StringComparison.Ordinal);

                Key("ReturnMode").Apply!(view, PlayerbotHomeReturnMode.Teleport);
                PlayerbotMotion.ElapseForTests(player, 1_000);
                session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(1_000);
                Assert.Equal(1, brain.HomeReturn.Teleports);
                Assert.Equal(PlayerbotHomeTeleportReason.Mode, brain.HomeReturn.LastTeleport);
                Assert.Contains("mode=teleport", brain.HomeDescription, StringComparison.Ordinal);
                brain.Stop();
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// The live places on the real terrain and AreaTable.dbc: a Westfall home at Sentinel Hill, a level-14 bot on the Darkshire road in
    /// Duskwood and one in Loch Modan. Both are past the 1500-yard threshold (Auto teleports them: Distance); Walk would walk them.
    /// </summary>
    [RealAreaTerrainFact]
    public void RealTerrain_TheLiveStrays_AreTeleportedHomeInAuto()
    {
        string dbc = Environment.GetEnvironmentVariable("ARCANECORE_TEST_DBC_DIR")!;
        string root = Environment.GetEnvironmentVariable(RealTerrainBotFactAttribute.Variable)!;
        var terrain = new TerrainManager(root)
        {
            Areas = new AreaTable(AreaTableDbcReader.Read(Path.Combine(dbc, "AreaTable.dbc")).Select(r => new AreaTemplate(
                r.Entry, r.MapId, r.ZoneId, r.ExploreFlag, r.Flags, r.AreaLevel, r.Name, r.Team, r.LiquidTypeId)), MapRegistry.Default),
        };
        PlayerbotAreaLevelLookup levels = PlayerbotAreaLevelLookup.FromTerrain(terrain, 0);
        var sentinelHill = new Vector3(-10630f, 1080f, 40f);
        var darkshireRoad = new Vector3(-10703f, -918f, 40f);
        var lochModan = new Vector3(-5886f, -2951f, 300f);
        var ravenHill = new Vector3(-10650f, 450f, 40f);
        var area = new PlayerbotHomeArea(0, sentinelHill, 750f, "SentinelHill") { AreaLevels = levels };

        // Measured on the 5875 AreaTable.dbc and terrain: Sentinel Hill is an area of level 15, the Darkshire road one of 28 (Duskwood),
        // Raven Hill one of 24 (the QA death loops), the Loch Modan stray's spot one of 13.
        Assert.Equal(15, levels.LevelAt(sentinelHill.X, sentinelHill.Y, sentinelHill.Z));
        Assert.Equal(28, levels.LevelAt(darkshireRoad.X, darkshireRoad.Y, darkshireRoad.Z));
        Assert.Equal(24, levels.LevelAt(ravenHill.X, ravenHill.Y, ravenHill.Z));
        Assert.Equal(13, levels.LevelAt(lochModan.X, lochModan.Y, lochModan.Z));

        // The live strays: both farther than 1500 yards, so Auto teleports them; Walk walked them (and they died on the way).
        Assert.Equal(PlayerbotHomeTeleportReason.Distance, Decide(area, levels, darkshireRoad, 14, PlayerbotHomeReturnMode.Auto));
        Assert.Equal(PlayerbotHomeTeleportReason.Distance, Decide(area, levels, lochModan, 14, PlayerbotHomeReturnMode.Auto));
        Assert.Equal(PlayerbotHomeTeleportReason.None, Decide(area, levels, darkshireRoad, 14, PlayerbotHomeReturnMode.Walk));
        Assert.True(area.DistanceTo(darkshireRoad.X, darkshireRoad.Y) is > 1990f and < 2010f);

        // Raven Hill is only about 630 yards away, but the way home starts in a level-24 area: a level-14 bot teleports, a level-20 one walks.
        Assert.Equal(24, levels.HighestOnLine(ravenHill, sentinelHill));
        Assert.Equal(PlayerbotHomeTeleportReason.Danger, Decide(area, levels, ravenHill, 14, PlayerbotHomeReturnMode.Auto));
        Assert.Equal(PlayerbotHomeTeleportReason.None, Decide(area, levels, ravenHill, 20, PlayerbotHomeReturnMode.Auto));

        // A short way inside Westfall (330 yards, no area above 15) walks.
        var eastOfTheHill = new Vector3(-10300f, 1100f, 40f);
        Assert.Equal(15, levels.HighestOnLine(eastOfTheHill, sentinelHill));
        Assert.Equal(PlayerbotHomeTeleportReason.None, Decide(area, levels, eastOfTheHill, 14, PlayerbotHomeReturnMode.Auto));
    }

    private static PlayerbotHomeTeleportReason Decide(PlayerbotHomeArea area, PlayerbotAreaLevelLookup levels, Vector3 bot, int level,
        PlayerbotHomeReturnMode mode)
        => PlayerbotHomeRules.Decide(new PlayerbotHomeFacts(true, area.DistanceTo(bot.X, bot.Y), levels.HighestOnLine(bot, area.Centre), level, false),
            new PlayerbotHomeOptions { ReturnMode = mode });

    private static WorldConfigKey Key(string name) => WorldConfigKeys.All.Single(k => k.Path == "World:Playerbots:Home:" + name);

    /// <summary>Every log line with its level (the return teleport's Information line).</summary>
    private sealed class ListLoggerFactory : ILoggerFactory
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Line)> _lines = new();

        public IEnumerable<string> Lines => _lines.Select(l => l.Line);

        public LogLevel LevelOf(string line) => _lines.First(l => l.Line == line).Level;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(ListLoggerFactory owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => owner._lines.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}
