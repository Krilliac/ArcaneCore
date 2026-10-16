using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Battlegrounds;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Playerbots.Dungeon;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Battlegrounds.PlayerbotBattlegroundWorldTests;
using Content = ArcaneCore.World.Tests.Playerbots.Battlegrounds.BattlegroundBotTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Battlegrounds;

/// <summary>
/// How bots play Warsong Gulch and get there, end to end in the battleground scenario world (<see cref="BattlegroundBotTestWorld"/>: real
/// handlers, the manual clock, managed bots; a compact synthetic Warsong Gulch on map 489, since the test world has no map 489 navigation
/// data). Live 2026-10-16 ~20:20 UTC: a real Horde player in Warsong Gulch with four bots reported "bots just stand there" and "bots running
/// to their bodies instead of waiting to be rezzed by the spirit healer"; world.debug.log showed attackers holding with no flag to take
/// (activity no-flag, Zoustak/Zarmun/Ziavos/Vigeigrern), casters below 10% mana holding at range in the Horde flag room (Bokae, Poforound),
/// and bots walking again and again to battlemasters they could not reach (Gitrol 62 times; the Warsong Emissary spawns of the Call to
/// Arms event while it was off).
/// </summary>
public sealed class PlayerbotWarsongPlayWorldTests
{
    private static readonly string IdleInMatch = PlayerbotAnomalies.Key("bg", "idle-in-match");
    private static readonly string ObjectiveUnreachable = PlayerbotAnomalies.Key("bg", "objective-unreachable");

    private static Task<AnomalyCapture> CaptureAsync(ScenarioBot bot) => bot.ReadAsync(p => PlayerbotAnomalies.CaptureFor(p));

    private static Vector3 At(Player p) => new(p.X, p.Y, p.Z);

    /// <summary>An Alliance bot and a parked scripted Horde opponent in one Warsong Gulch match whose gates are open.</summary>
    private static async Task<(ScenarioBot Ally, ScenarioBot Horde, WarsongGulch Wsg)> MatchAsync(ScenarioContext context, string name, byte allyClass = 1,
        Vector3? park = null)
    {
        ScenarioBot ally = await context.LoginAsync(name, 1, allyClass);
        await context.AutonomousAsync(ally);
        ScenarioBot horde = await ScriptedHordeQueuesAsync(context, name + "h", BattlegroundType.WarsongGulch, Content.HordeWsgMaster);
        PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
        Vector3 at = park ?? Content.HordeParking;
        await PortScriptedAsync(context, horde, 489, (at.X, at.Y, at.Z));
        await context.StepAsync("the Alliance bot is in the match", () => context.WaitUntilAsync("in the match",
            () => coordinator.Find(ally.BotId)?.Phase == PlayerbotBattlegroundPhase.InMatch, TimeSpan.FromSeconds(60)));
        var wsg = (WarsongGulch)(await context.BattlegroundOfAsync(ally))!;
        coordinator.Options.QueueChancePerMinute = 0f;
        await context.StepAsync("the gates open", () => context.WaitUntilAsync("in progress", () => wsg.Status == BattlegroundStatus.InProgress, StartWait));
        return (ally, horde, wsg);
    }

    /// <summary>
    /// After a capture both flags are away for 23 s (BattleGroundWS.cpp: both flag events despawn, FlagRespawnTimeMs). Before, an attacker
    /// found no stand object to walk to and held where it stood ("no-flag", idle-in-match), in its own flag room 60 yards from the enemy flag;
    /// mangosbot's BGTactics::selectObjective sends it to the enemy flag position (WS_FLAG_POS_*) whatever the flag's state, so it is there when
    /// the flag comes back.
    /// </summary>
    [Fact]
    public async Task AfterACapture_TheAttackerWaitsAtTheEnemyStand_WhileTheFlagRespawns_InsteadOfHolding()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync();
        await RunAsync(world, "bg-wsg-respawn-wait", async context =>
        {
            (ScenarioBot ally, _, WarsongGulch wsg) = await MatchAsync(context, "Bgrespawn");
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            await context.StepAsync("the bot captures once", () => context.WaitUntilAsync("captured", () => wsg.TeamScore(Team.Alliance) == 1, TimeSpan.FromSeconds(90)));
            ScenarioContext.Expect(wsg.FlagState(Team.Horde) == WsgFlagState.WaitRespawn, $"the Horde flag is {wsg.FlagState(Team.Horde)} right after the capture");

            await context.StepAsync("during the respawn the bot walks to the Horde stand", async () =>
            {
                // 60 yards at run speed is about nine seconds; the flag is back after 23.
                await WaitExplainedAsync(context, "at the Horde stand while the flag respawns",
                    () => wsg.FlagState(Team.Horde) == WsgFlagState.WaitRespawn
                        && Vector3.Distance(At(ally.RequirePlayer()), Content.HordeFlag) <= PlayerbotBattlegroundAI.GuardYards + 2f,
                    TimeSpan.FromSeconds(18), ally);
                ScenarioContext.Expect(coordinator.Describe(ally.BotId)?.EndsWith(":waiting-flag", StringComparison.Ordinal) == true, coordinator.Describe(ally.BotId) ?? "none");
            });
        });
    }

    /// <summary>
    /// A caster with nothing to cast (no spell for the fight, or below 10% mana) closes in and swings. Before, the battleground AI never
    /// did (idleTooLong was always false): it held at its 25-yard range for the whole fight (live: Bokae, mage, and Poforound, shaman, in
    /// the Horde flag room below 10% mana for 45 s of one fight). vmangos CombatBotBaseAI::BeginChasing; PlayerbotBrain.RangedIdleLimitMs.
    /// </summary>
    [Fact]
    public async Task RangedBotWithNothingToCast_ClosesInAndSwings_InsteadOfStandingAtRange()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync();
        await RunAsync(world, "bg-wsg-ranged-idle", async context =>
        {
            // A mage (class 8) that knows no attack spell: at range it has nothing to cast.
            (ScenarioBot ally, ScenarioBot horde, WarsongGulch wsg) = await MatchAsync(context, "Bgrangedmg", allyClass: 8);
            Vector3 start = await ally.ReadAsync(At);
            await context.PlaceAsync(horde, 489, start.X + 15f, start.Y, start.Z);
            await context.StepAsync("the mage closes in and swings at the Horde player within 30 s", () => WaitExplainedAsync(context, "swinging in melee",
                () => ally.RequirePlayer() is { } mage && horde.RequirePlayer() is { } enemy && ReferenceEquals(mage.Combat.Victim, enemy)
                    && MapCombat.CanReachWithMeleeAutoAttack(mage, enemy),
                TimeSpan.FromSeconds(30), ally, horde));
        });
    }

    /// <summary>
    /// The owner saw "bots running to their bodies instead of waiting to be rezzed by the spirit healer". A dead bot in Warsong Gulch waits
    /// beside the graveyard's spirit guide for its area resurrection wave (cmangos battlegrounds.cpp npc_spirit_guideAI; the fix is
    /// PlayerbotRecovery's, branch claude/fix-bot-bg-spirit-healer 91c6f53e, merged here): it never runs back to its corpse in the enemy flag room.
    /// </summary>
    [Fact]
    public async Task DeadBot_WaitsAtTheSpiritGuide_AndNeverRunsToItsBody()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync();
        await RunAsync(world, "bg-wsg-spirit-guide", async context =>
        {
            (ScenarioBot ally, _, WarsongGulch wsg) = await MatchAsync(context, "Bgghostwait");
            Vector3 corpse = Content.HordeFlag + new Vector3(0f, 5f, 0f);
            await context.PlaceAsync(ally, 489, corpse.X, corpse.Y, corpse.Z);
            await context.ReadAsync(() =>
            {
                Player player = ally.RequirePlayer();
                // The Alliance spirit guides at the Alliance graveyards (the release puts the ghost at one of them).
                foreach ((uint id, float dx, float dy) in new[] { (769u, -15f, 0f), (771u, -20f, 10f) })
                    DungeonBotHost.AddCreature(player.Map!, 13116u + id, Content.AllianceFlag.X + dx - 3f, Content.AllianceFlag.Y + dy, Content.WsgFloor,
                        (uint)NpcFlags.SpiritGuide, context.World.NowMs, faction: 1);
                player.Health = 0;
                player.Map!.Combat.KillPlayer(player);
                return true;
            });

            float nearestToCorpse = float.MaxValue;
            await context.StepAsync("the bot releases", () => context.WaitUntilAsync("a ghost", () => (ally.RequirePlayer().Flags & PlayerFlags.Ghost) != 0, TimeSpan.FromSeconds(30)));
            await context.StepAsync("for 40 s the ghost stays by the spirit guide (or is raised there)", async () =>
            {
                for (int second = 0; second < 40; second++)
                {
                    await context.IdleAsync(TimeSpan.FromSeconds(1));
                    nearestToCorpse = MathF.Min(nearestToCorpse, await ally.ReadAsync(p => Vector3.Distance(At(p), corpse)));
                }

                ScenarioContext.Expect(nearestToCorpse > 30f, $"the ghost ran to its body: {nearestToCorpse:F1} yd from the corpse at the closest");
                ScenarioContext.Expect(wsg.Status == BattlegroundStatus.InProgress, "the match ended");
            });
        });
    }

    /// <summary>
    /// A battlemaster spawned only by a game event (the Warsong Emissary 15105, spawn 190554, in game event 19 "Call to Arms: Warsong
    /// Gulch") is no battlemaster while the event is off. Live: Bokae, Slouviabrias, Lurudiand, Dragliak and others walked to its spot in the
    /// Undercity, found nobody ("no living battlemaster at (1633.35, 180.9, -62.2)") and were set aside, again and again.
    /// </summary>
    [Fact]
    public async Task BattlemasterSpawnedOnlyByAGameEvent_IsNotWalkedTo_WhileTheEventIsOff()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync();
        await RunAsync(world, "bg-master-event-gated", async context =>
        {
            ScenarioBot ally = await context.LoginAsync("Bgmastergate", 1, characterClass: 1);
            using AnomalyCapture capture = await CaptureAsync(ally);
            await context.ReadAsync(() =>
            {
                // The game events refuse the Alliance Warsong Gulch battlemaster's spawn (its event is off): it is not in the world.
                CreatureMapSystem creatures = ally.RequirePlayer().Map!.FindUpdater<CreatureMapSystem>()!;
                creatures.SpawnGate = new RefuseSpawns(Content.AllianceWsgMaster);
                return true;
            });
            await context.AutonomousAsync(ally);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            await context.IdleAsync(TimeSpan.FromSeconds(30));
            ScenarioContext.Expect(!coordinator.Events.Any(e => e.Contains("goes to the WarsongGulch battlemaster", StringComparison.Ordinal)),
                string.Join(" | ", coordinator.Events));
            ScenarioContext.ExpectEqual(0, capture.Count(ObjectiveUnreachable), capture.Describe());
        });
    }

    /// <summary>
    /// A battlemaster whose route was refused is not tried again from the same zone. Live: Gitrol, in Silverpine Forest, was sent 62 times to
    /// the Undercity battlemaster (inside the city, reached by its elevators, which the navigation mesh does not join): each time the route
    /// was refused, the type was set aside for ten minutes, and then the same battlemaster was picked again.
    /// </summary>
    [Fact]
    public async Task BattlemasterWhoseRouteWasRefused_IsNotPickedAgainFromTheSameZone_AfterTheSetAside()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync();
        await RunAsync(world, "bg-master-refused", async context =>
        {
            ScenarioBot ally = await context.LoginAsync("Bgmasterrefu", 1, characterClass: 1);
            Vector3 start = await ally.ReadAsync(At);
            // The bot stands 40 yards from the battlemasters and no route reaches them.
            await context.PlaceAsync(ally, 0, start.X + 40f, start.Y, start.Z);
            await context.ReadAsync(() =>
            {
                WorldCollision.Of(context.World).Install(pathfinder: new NoRoutePathfinder());
                return true;
            });
            using AnomalyCapture capture = await CaptureAsync(ally);
            await context.AutonomousAsync(ally);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            int Walks() => coordinator.Events.Count(e => e.Contains("goes to the WarsongGulch battlemaster", StringComparison.Ordinal));
            await context.StepAsync("the first walk is refused", () => context.WaitUntilAsync("set aside",
                () => coordinator.Events.Any(e => e.Contains("could not walk to the WarsongGulch battlemaster", StringComparison.Ordinal)), TimeSpan.FromSeconds(60)));
            ScenarioContext.ExpectEqual(1, Walks(), string.Join(" | ", coordinator.Events));

            // Past the ten-minute set-aside the same battlemaster is still out of reach from here.
            await context.IdleAsync(TimeSpan.FromMilliseconds(PlayerbotBattlegroundCoordinator.SetAsideMs + 60_000));
            ScenarioContext.ExpectEqual(1, Walks(), string.Join(" | ", coordinator.Events));
            ScenarioContext.Expect(capture.Count(ObjectiveUnreachable) <= 2, capture.Describe());
        });
    }

    /// <summary>
    /// A bot bound to its match but standing on another map routes nothing (the objectives are coordinates of map 489).
    /// </summary>
    [Fact]
    public async Task BoundBotOffTheMatchMap_RoutesNoBattlegroundObjective()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync();
        await RunAsync(world, "bg-wsg-off-map", async context =>
        {
            (ScenarioBot ally, _, WarsongGulch wsg) = await MatchAsync(context, "Bgoffmap");
            using AnomalyCapture capture = await CaptureAsync(ally);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            Vector3 start = new(StartXOf(), StartYOf(), StartZOf());
            await context.PlaceAsync(ally, 0, start.X, start.Y, start.Z);
            bool bound = await context.ReadAsync(() => context.Services.GetRequiredService<BattlegroundFeature>().Manager.StateOf(ally.Guid).InBattleground);
            if (!bound) return; // the server unbinds a player that leaves the match's map: nothing to route
            await context.IdleAsync(TimeSpan.FromSeconds(10));
            Vector3 now = await ally.ReadAsync(At);
            ScenarioContext.Expect(Vector3.Distance(now, start) < 2f, $"the bot walked on map 0 towards a Warsong Gulch objective: now at {now}");
            ScenarioContext.ExpectEqual(0, capture.Count(ObjectiveUnreachable), capture.Describe());
        });
    }

    private static float StartXOf() => ScenarioTestContent.StartX;

    private static float StartYOf() => ScenarioTestContent.StartY;

    private static float StartZOf() => ScenarioTestContent.StartZ;

    /// <summary>A game-event gate that refuses the listed creature spawns (their event is off).</summary>
    private sealed class RefuseSpawns(params uint[] refused) : ISpawnGate
    {
        public bool AllowsCreature(uint spawnGuid) => !refused.Contains(spawnGuid);

        public bool AllowsGameObject(uint spawnGuid) => true;

        public IEnumerable<uint> GatedCreatures => refused;

        public IEnumerable<uint> GatedGameObjects => [];
    }

    /// <summary>No route anywhere (the Undercity's elevators, for the navigation mesh).</summary>
    private sealed class NoRoutePathfinder : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null) => PathResult.None(start);
    }
}
