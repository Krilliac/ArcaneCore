using System.Diagnostics;
using System.Numerics;
using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Npc;
using ArcaneCore.World.Tests.Playerbots.Risk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The live playerbot stalls of bot QA clusters 1 and 2 (2026-10-11..16): a bot that stands still with a goal until the 120-second stall
/// watch gives it up. Each test drives the real brain through the stall's cause and asserts the bot walks on.
/// </summary>
public sealed class PlayerbotBrainStallTests
{
    /// <summary>
    /// Cluster 1 (bot QA #249, #281, #845 and the explore-starvation family; 343 of 414 stall diagnostics <c>moving:no dest:none</c>, 270
    /// in phase explore): the think's optional path budget was spent before Explore (by the grind planner's refused search), and Explore
    /// returned without a step. It now still takes its short wander leg.
    /// </summary>
    [Fact]
    public async Task SpentOptionalPathBudget_StillTakesTheWanderLeg()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500, Risk = { Enabled = false } });
        try
        {
            Vector3 start = await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(player.Z));
                session.Services.GetRequiredService<SpellFeature>().System.Store = new SpellStore([], [], []);
                return new Vector3(player.X, player.Y, player.Z);
            });
            bool spent = true;
            for (int think = 0; think < 6; think++)
            {
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    var probe = new PlayerbotThinkProbe();
                    probe.Begin();
                    try
                    {
                        // What the refused far search of a planner before Explore costs (a whole 32 768-node search).
                        PathSearchCounters.RecordSearch(32_768, 0, capped: true);
                        spent &= !probe.HasOptionalPlanBudget;
                        session.ManagedBudget = new ManagedActionBudget(8);
                        brain.Update(500);
                    }
                    finally { probe.End(); }
                    PlayerbotMotion.ElapseForTests(player, 500);
                    return true;
                });
            }

            Assert.True(spent, "the simulated search did not spend the think's optional path budget: the test proves nothing");
            float moved = await host.OnWorldAsync(() => Vector3.Distance(start, new Vector3(session.Player!.X, session.Player.Y, session.Player.Z)));
            Assert.Equal(PlayerbotGoalKind.Explore, brain.Goal);
            Assert.True(moved > 2f, $"six thinks with the optional path budget spent moved the exploring bot {moved:F2} yd");
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// Cluster 1, its cause (bot QA #249, #250, #778, #779, #782): the grind planner ran first on every think, and its refused search (the
    /// next spawn of the camp each think, every one beyond reach) spent the think's optional path budget, so the explore after it never
    /// had any. A refused grind plan now backs the planner off (4 s, doubling), and the thinks between go to the explore.
    /// </summary>
    [Fact]
    public async Task RefusedGrindPlan_BacksThePlannerOff_AndTheBotExplores()
    {
        await using QuestWorld quest = await QuestWorld.StartAsync(new QuestInteractionFixture());
        var brain = new PlayerbotBrain(quest.Bot, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500, Risk = { Enabled = false } });
        var pathfinder = new RefuseFarEastPathfinder();
        try
        {
            Vector3 start = await quest.Host.OnWorldAsync(() =>
            {
                Player player = quest.Bot.Player!;
                quest.Accept(quest.Bot);
                pathfinder.LimitX = player.X + 150f;
                // Three spawns of the quest's creature (entry 90) 200 yards east, beyond the bot's sight: the grind planner's destinations.
                // Level 30, so the explore's level-matched site search does not head there as well.
                quest.Host.WorldServices.GetRequiredService<CreatureWorldFeature>().Install(new CreatureContent(
                    [new CreatureTemplate { Entry = 90, Name = "far quest mob", MinLevel = 30, MaxLevel = 30, Faction = 14, DisplayIds = [49] }],
                    [new CreatureSpawn { Guid = 960001, Entry = 90, MapId = player.MapId, X = player.X + 200, Y = player.Y, Z = player.Z },
                     new CreatureSpawn { Guid = 960002, Entry = 90, MapId = player.MapId, X = player.X + 205, Y = player.Y + 10, Z = player.Z },
                     new CreatureSpawn { Guid = 960003, Entry = 90, MapId = player.MapId, X = player.X + 210, Y = player.Y - 10, Z = player.Z }],
                    [], [], []));
                WorldCollision.Of(quest.Host.World).Install(lineOfSight: new FlatFloor(player.Z), pathfinder: pathfinder);
                quest.Bot.Services.GetRequiredService<SpellFeature>().System.Store = new SpellStore([], [], []);
                return new Vector3(player.X, player.Y, player.Z);
            });
            for (int think = 0; think < 6; think++)
            {
                await quest.Host.OnWorldAsync(() =>
                {
                    Player player = quest.Bot.Player!;
                    var probe = new PlayerbotThinkProbe();
                    probe.Begin();
                    try
                    {
                        quest.Bot.ManagedBudget = new ManagedActionBudget(8);
                        brain.Update(500);
                    }
                    finally { probe.End(); }
                    PlayerbotMotion.ElapseForTests(player, 500);
                    return true;
                });
                await Task.Delay(50);
            }

            float moved = await quest.Host.OnWorldAsync(() => Vector3.Distance(start, new Vector3(quest.Bot.Player!.X, quest.Bot.Player.Y, quest.Bot.Player.Z)));
            Assert.True(pathfinder.Refused.Count(phase => phase == "grind destinations") >= 1,
                $"the grind planner never searched toward the far camp (searches refused in phases: {string.Join(", ", pathfinder.Refused)}): the test proves nothing");
            Assert.Equal(1, pathfinder.Refused.Count(phase => phase == "grind destinations"));
            Assert.True(moved > 2f, $"six thinks moved the bot {moved:F2} yd (goal {brain.Goal})");
        }
        finally { brain.Stop(); }
    }

    /// <summary>
    /// Cluster 2, the linger (bot QA #580, #894, #834: "stalled 120s: goal=Combat target=0", moving:no dest:none, phase threats): the bot
    /// is in combat with nothing to fight and nothing it can reach. It stands while the server's combat timer may still end it, and walks
    /// on (explores) once <see cref="PlayerbotBrain.CombatLingerLimitMs"/> has passed instead of standing until the stall watch.
    /// </summary>
    [Fact]
    public async Task CombatWithNothingToFight_PastTheLingerLimit_TheBotWalksOn()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync(options => options.Risk.Enabled = false);
        Vector3 start = await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            world.Session.Services.GetRequiredService<SpellFeature>().System.Store = new SpellStore([], [], []);
            player.Map!.Combat.SetInCombatState(player, 3_600_000);
            Assert.True(player.Combat.IsInCombat);
            Assert.Null(player.Combat.Victim);
            Assert.Empty(player.Combat.Attackers);
            return new Vector3(player.X, player.Y, player.Z);
        });

        // While the server's combat timer may still end the linger: the bot stands, with the Combat goal.
        bool walkedEarly = await world.ThinkUntilAsync(PlayerbotBrain.CombatLingerLimitMs - 2_000,
            () => world.Brain.Goal != PlayerbotGoalKind.Combat || RiskTestWorld.Distance(world.Player, start) > 0.5f);
        Assert.False(walkedEarly, $"the bot left the linger early: goal {world.Brain.Goal}");

        bool explores = await world.ThinkUntilAsync(6_000, () => world.Brain.Goal == PlayerbotGoalKind.Explore);
        Assert.True(explores, $"past the linger limit the bot still has goal {world.Brain.Goal}");
        bool walked = await world.ThinkUntilAsync(5_000, () => RiskTestWorld.Distance(world.Player, start) > 3f);
        float distance = await world.OnWorldAsync(() => RiskTestWorld.Distance(world.Player, start));
        Assert.True(await world.OnWorldAsync(() => world.Player.Combat.IsInCombat), "the combat flag ended by itself: the test proves nothing");
        Assert.True(walked, $"the lingering bot moved only {distance:F2} yd (goal {world.Brain.Goal})");
    }

    /// <summary>
    /// Cluster 2, the target the bot cannot hurt (bot QA #697, #700, #862, #834: goal=Combat stalls, moving:no dest:none, phase combat, the
    /// same target entry again within minutes): the creature the bot fights stands in melee range but soft-evades (it cannot reach the bot,
    /// <see cref="Creature.IsEvadingUnreachable"/>) and regenerates to full as an evading creature does, so the fight never gets anywhere.
    /// Its health never falls and the bot makes no step, so after
    /// <see cref="PlayerbotBrain.FightNoProgressMs"/> the bot sets it aside, and for about a respawn (<see cref="PlayerbotBrain.StalledTargetMs"/>).
    /// </summary>
    [Fact]
    public async Task ATargetTheBotCannotHurt_IsSetAside_AndNotTakenBackSoon()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync(options => options.Risk.Enabled = false);
        Creature mob = await world.OnWorldAsync(() =>
        {
            world.Session.Services.GetRequiredService<SpellFeature>().System.Store = new SpellStore([], [], []);
            return world.Spawn(RiskTestWorld.Template(960101, health: 500), 2f, 0f);
        });
        await world.SeeAsync(mob);
        await world.OnWorldAsync(() => { world.Engage(mob); return true; });
        // The creature soft-evades from here on and is back at full health at every think (the server's own update clears the flag).
        void Evade()
        {
            mob.IsEvadingUnreachable = mob.IsAlive;
            if (mob.IsAlive) mob.Health = mob.MaxHealth;
        }
        await world.OnWorldAsync(() => { Evade(); return true; });

        Assert.True(await world.ThinkUntilAsync(5_000, () => ReferenceEquals(world.Brain.InspectionTarget, mob), Evade),
            "the bot never fought the creature: the test proves nothing");
        uint heldFrom = await world.OnWorldAsync(() => world.Host.World.NowMs);
        bool released = await world.ThinkUntilAsync(PlayerbotBrain.FightNoProgressMs + 5_000,
            () => !ReferenceEquals(world.Brain.InspectionTarget, mob), Evade);
        uint heldMs = await world.OnWorldAsync(() => world.Host.World.NowMs) - heldFrom;
        Assert.True(await world.OnWorldAsync(() => mob.IsAlive), "the creature died: the test proves nothing");
        Assert.True(released, $"the bot held the target it cannot hurt for {heldMs} ms");

        bool takenBack = await world.ThinkUntilAsync(60_000, () => ReferenceEquals(world.Brain.InspectionTarget, mob), Evade);
        Assert.False(takenBack, "the target set aside was taken back within a minute");
    }

    /// <summary>
    /// Bot QA #787: the overrun report named only the last phase of the think. It now also names the slowest phase so far.
    /// </summary>
    [Fact]
    public async Task AnOverrunThink_NamesItsSlowestPhase_NotOnlyItsLast()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true });
        try
        {
            using AnomalyCapture cap = await host.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(session.Player!));
            await host.OnWorldAsync(() =>
            {
                var probe = new PlayerbotThinkProbe();
                probe.Begin();
                try
                {
                    PlayerbotThinkProbe.Phase("grind destinations");
                    Spin(20);
                    PlayerbotThinkProbe.Phase("explore advance");
                    brain.CheckThinkOverran(session.Player!, 900, 2000);
                }
                finally { probe.End(); }
                return true;
            });
            Assert.Equal(1, cap.Count(PlayerbotAnomalies.Key("brain", "think-overran")));
            Assert.Contains("slowest phase grind destinations", cap.Describe());
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    private static void Spin(double ms)
    {
        long until = Stopwatch.GetTimestamp() + (long)(ms * Stopwatch.Frequency / 1000);
        while (Stopwatch.GetTimestamp() < until) Thread.SpinWait(50);
    }

    /// <summary>
    /// Regression for #778/#779/#782: when a quest goal fails and the brain falls through to <see cref="PlayerbotGoalKind.Explore"/>,
    /// <see cref="PlayerbotBrain.QuestId"/> is cleared instead of leaking a stale quest id into Explore; and if a non-Quest goal stalls
    /// while holding a non-zero <see cref="PlayerbotBrain.QuestId"/>, <c>GiveUpStalledGoal</c> suspends that quest and clears <c>QuestId</c>.
    /// </summary>
    [Fact]
    public async Task FailedQuestGoal_DoesNotLeakQuestIdIntoExplore_AndGiveUpSuspendsHeldQuestOnAnyGoal()
    {
        await using QuestWorld quest = await QuestWorld.StartAsync(new QuestInteractionFixture(), manualClock: true);
        var pathfinder = new RefuseFarEastPathfinder();
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = quest.Bot.Player!;
            var giver = (Creature)player.Map!.FindObject(QuestInteractionFixture.Guid)!;
            giver.SetPosition(player.X + 25f, player.Y, player.Z, 0);
            pathfinder.LimitX = player.X + 5f;
            WorldCollision.Of(quest.Host.World).Install(lineOfSight: new FlatFloor(player.Z), pathfinder: pathfinder);
            quest.Bot.Services.GetRequiredService<SpellFeature>().System.Store = new SpellStore([], [], []);
            return true;
        });
        var brain = new PlayerbotBrain(quest.Bot, new PlayerbotOptions { Enabled = true, StallSeconds = 30, Risk = { Enabled = false } });
        try
        {
            await quest.Host.OnWorldAsync(() =>
            {
                Player player = quest.Bot.Player!;
                quest.Bot.ManagedBudget = new ManagedActionBudget(8);
                brain.Update(500);
                Assert.Equal(PlayerbotGoalKind.Explore, brain.Goal);
                Assert.Equal(0u, brain.QuestId);

                brain.HoldGoalForTests(PlayerbotGoalKind.Explore, 0, QuestInteractionFixture.QuestId);
                brain.GiveUpStalledGoal(player);
                Assert.Equal(0u, brain.QuestId);
                Assert.True(brain.Suspensions.IsQuestSuspended(QuestInteractionFixture.QuestId, quest.Host.World.NowMs));
                return true;
            });
        }
        finally { brain.Stop(); }
    }

    /// <summary>
    /// Regression for #778: when a dead-end pin triggers <c>EscapeIsland</c> and <c>Home.Centre</c> is within <see cref="PlayerbotStallWatch.ProgressYards"/>
    /// of the stalled position, the brain skips the no-op home teleport and escapes to the bind point farther away.
    /// </summary>
    [Fact]
    public async Task DeadEndPin_WhenHomeCentreIsAtStallSpot_SkipsNoOpHomeTeleport_AndEscapesToFarBindPoint()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            services.AddSingleton<IWorldFeature, ManualStallClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, Risk = { Enabled = false } });
        try
        {
            float startX = await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                var pathfinder = new RefuseFarEastPathfinder { LimitX = player.X + 500f };
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(player.Z), pathfinder: pathfinder);
                brain.Home = new PlayerbotHomeArea(player.MapId, new Vector3(player.X, player.Y, player.Z), 750f, "PinnedHome");
                player.Home = new HomeBind(player.MapId, player.ZoneId, player.X - 200f, player.Y, player.Z);
                return player.X;
            });
            for (int stall = 0; stall < PlayerbotStallWatch.PinStallsToEscalate; stall++)
            {
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    brain.HoldGoalForTests(PlayerbotGoalKind.Explore, 922, 0);
                    brain.GiveUpStalledGoal(player);
                    return true;
                });
                await host.World.AdvanceClockAsync(1_000);
            }
            for (int tick = 0; tick < 40; tick++)
            {
                await host.OnWorldAsync(() => PlayerbotMovementControl.Update(session, session.Player!));
                await host.World.AdvanceClockAsync(50);
            }

            float finalX = await host.OnWorldAsync(() => session.Player!.X);
            Assert.Equal(startX - 200f, finalX, 1);
            Assert.Equal(0u, brain.QuestId);
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    private sealed class FlatFloor(float z) : ILineOfSight
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit) { hit = to; return false; }
        public float? GetModelHeight(uint mapId, float x, float y, float z2, float maxSearchDistance) => z;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float z2, out ModelAreaInfo info) { info = default; return false; }
    }

    /// <summary>
    /// A mesh on which nothing east of <see cref="LimitX"/> can be reached: such a search runs to its node cap and is refused (the phase of
    /// the think it ran in is noted). Everything else is reached straight.
    /// </summary>
    private sealed class RefuseFarEastPathfinder : IPathfinder
    {
        public float LimitX { get; set; } = float.PositiveInfinity;
        public List<string> Refused { get; } = [];
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            if (end.X <= LimitX) return PathResult.StraightLine(start, end, PathType.Normal);
            PathSearchCounters.RecordSearch(32_768, 32_768, capped: true);
            Refused.Add(PlayerbotThinkProbe.CurrentPhase ?? "(no think)");
            return PathResult.None(start);
        }
    }

    /// <summary>A world with the quest-interaction fixture's questgiver in sight (its kill quest hunts entry 90) and a character database.</summary>
    private sealed class QuestWorld : IAsyncDisposable
    {
        private QuestWorld(WorldTestHost host, WorldSession bot) => (Host, Bot) = (host, bot);

        public WorldTestHost Host { get; }

        public WorldSession Bot { get; }

        public void Accept(WorldSession session)
        {
            var body = new PacketWriter(12);
            body.WriteUInt64(QuestInteractionFixture.Guid.Value);
            body.WriteUInt32(QuestInteractionFixture.QuestId);
            session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(session.TryManagedAction(WorldOpcode.CmsgQuestgiverAcceptQuest, body.ToArray()));
        }

        public static async Task<QuestWorld> StartAsync(QuestInteractionFixture fixture, bool manualClock = false)
        {
            string databasePath = Path.Combine(Path.GetTempPath(), "arcane-playerbot-stall-" + Guid.NewGuid().ToString("N") + ".db");
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite", ["Database:ConnectionString"] = "Data Source=" + databasePath,
            }).Build();
            await using (ServiceProvider bootstrap = new ServiceCollection().AddLogging().AddCharacterDatabase(configuration).BuildServiceProvider())
                await bootstrap.GetRequiredService<CharacterDbInitializer>().InitializeAsync();
            QuestInteractionTestServices.Current.Value = fixture;
            WorldTestHost host;
            try
            {
                host = WorldTestHost.Start(configureServices: services =>
                {
                    services.AddCharacterDatabase(configuration);
                    if (manualClock) services.AddSingleton<IWorldFeature, ManualStallClock>();
                });
            }
            finally { QuestInteractionTestServices.Current.Value = null; }
            WorldSession bot = await PlayerbotMovementControlTests.EnterAsync(host);
            await host.WaitForWorldAsync(() => bot.Player!.VisibleObjects.Contains(QuestInteractionFixture.Guid), "questgiver visibility");
            return new QuestWorld(host, bot);
        }

        public async ValueTask DisposeAsync()
        {
            Bot.Kick();
            await Bot.ManagedClosed;
            await Host.DisposeAsync();
        }
    }

    private sealed class ManualStallClock : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }
}
