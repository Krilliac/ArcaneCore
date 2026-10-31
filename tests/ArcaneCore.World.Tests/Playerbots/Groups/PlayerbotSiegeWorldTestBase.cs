using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using GridDefines = ArcaneCore.Game.Maps.Grid.GridDefines;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Groups;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// A siege on the scenario world (real handlers, the manual clock, open flat ground): two bot-led raids under one plan, led by bots with
/// nobody else involved. The way runs east from where the bots log in (<see cref="Origin"/>): waypoints at 10 and 20 yards, two guards at 62, the objective ogre
/// (<see cref="Objective"/>) at 70.
/// </summary>
public abstract class PlayerbotSiegeWorldTestBase
{
    private protected const uint GuardA = 990801;
    private protected const uint GuardB = 990802;

    /// <summary>The creature the sieges are after: a level 10 body that takes a raid a while.</summary>
    private protected const uint Objective = 990803;

    /// <summary>Where a fresh bot character stands (the graveyard it logs in at): the way and its creatures are laid out from here.</summary>
    private protected static readonly Vector3 Origin = new(-8913f, 550f, StartZ);

    private protected static (CreatureTemplate Template, CreatureSpawn Spawn) Guard(uint entry, float dx, float dy, uint health = 60, uint npcFlags = 0, uint faction = 14, byte creatureType = 7) => (
        new CreatureTemplate
        {
            Entry = entry, Name = "Siege Guard " + entry, Faction = faction, CreatureType = creatureType, MinLevel = 10, MaxLevel = 10, DisplayIds = [903], NpcFlags = npcFlags,
            MinLevelHealth = health, MaxLevelHealth = health, MinMeleeDamage = 1, MaxMeleeDamage = 1, MeleeBaseAttackTime = 2000, Rank = 0,
        },
        new CreatureSpawn
        {
            Guid = entry, Entry = entry, MapId = 0, X = Origin.X + dx, Y = Origin.Y + dy, Z = Origin.Z, SpawnTimeMinSeconds = 600, SpawnTimeMaxSeconds = 600,
        });

    private protected static Vector3 At(float dx) => new(Origin.X + dx, Origin.Y, Origin.Z);
    private protected static async Task<GroupTestWorld> StartWorld(bool guards, Action<PlayerbotOptions>? configure = null)
        => await GroupTestWorld.StartAsync([], configure,
            extraCreatures: guards ? [Guard(GuardA, 62f, 2f), Guard(GuardB, 63f, -2f), Guard(Objective, 70f, 0f, health: 120)] : [Guard(Objective, 70f, 0f, health: 120)]);

    /// <summary>Two warriors, two priests and two mages, each level 10, at the start; waypoints at 10 and 20 yards.</summary>
    private protected static async Task<(Guid Id, string Name)[]> Enlist(GroupTestWorld world, params float[] waypoints)
    {
        (Guid, string)[] bots =
        [
            await world.AddBotAsync("Sgatank", 1, 1, 10, [GroupTestWorld.Taunt]),
            await world.AddBotAsync("Sgbtank", 1, 1, 10, [GroupTestWorld.Taunt]),
            await world.AddBotAsync("Sgaheal", 1, 5, 10, []),
            await world.AddBotAsync("Sgbheal", 1, 5, 10, []),
            await world.AddBotAsync("Sgamage", 1, 8, 10, []),
            await world.AddBotAsync("Sgbmage", 1, 8, 10, []),
        ];
        await world.RunAsync(2_000); // the bots are in the world and the coordinator has seen them
        await world.OnWorldAsync(() =>
        {
            foreach (float dx in waypoints) Assert.Null(world.Coordinator.AddSiegeWaypoint(0, At(dx)));
            return true;
        });
        return bots;
    }

    private protected static async Task<PlayerbotSiege> Start(GroupTestWorld world, (Guid Id, string Name)[] bots, int raids)
    {
        (PlayerbotSiege? siege, string? refusal) = await world.OnWorldAsync(() =>
        {
            var skipped = new List<(Guid, string)>();
            PlayerbotSiege? started = world.Coordinator.StartSiege([.. bots.Select(b => b.Id)], Objective, raids, skipped, out string? why);
            return (started, why ?? string.Join(',', skipped.Select(s => s.Item2)));
        });
        Assert.NotNull(siege);
        Assert.True(string.IsNullOrEmpty(refusal), refusal);
        Assert.True(await world.RunUntilAsync(60_000, () => siege.Raids.Count == raids
            && siege.Raids.All(r => r.Group is { State: PlayerbotGroupState.Engaging })), await world.TraceAsync());
        return siege;
    }

    private protected static Player Leader(GroupTestWorld world, PlayerbotSiegeRaid raid) => world.World.Host.World.FindOnlinePlayer(raid.Group!.LeaderGuid)!;

    private protected static PlayerbotStay StayAt(GroupTestWorld world, Vector3 at)
        => new(0, at, 30f, 1, unchecked(world.World.Host.World.NowMs + 600_000));

    private protected sealed class FrozenDeathClock(long seconds) : DeathClock
    {
        public override long UnixSeconds => seconds;
    }

    /// <summary>One raid of two is left without a living leader (nobody promotes a new one): the other raid must keep waiting for it.</summary>
    private protected static async Task<(PlayerbotSiege Siege, PlayerbotSiegeRaid Fallen)> LeaderlessRaid(GroupTestWorld world)
    {
        (Guid Id, string Name)[] bots = await Enlist(world, 10f, 20f);
        PlayerbotSiege siege = await Start(world, bots, raids: 2);
        PlayerbotSiegeRaid fallen = siege.Raids[1];
        await world.OnWorldAsync(() =>
        {
            Player leader = Leader(world, fallen);
            // The death clock follows the manual world clock: a ghost bot would reclaim its body after the 30 s delay within this run.
            // The test is about a leaderless raid, so the death clock stands still and the leader stays dead.
            WorldRuntime runtime = world.World.Host.World;
            DeathHooks.Register(runtime, new DeathHooks(DeathHooks.For(runtime).Options, new FrozenDeathClock(DeathHooks.For(runtime).Clock.UnixSeconds)));
            leader.Health = 0;
            leader.Map!.Combat.KillPlayer(leader);
            fallen.PromoteTriedMs = Math.Max(1u, world.World.Host.World.NowMs); // no promotion before the run below keeps it off
            return true;
        });
        return (siege, fallen);
    }

    // --- the retreat: disengage, rally, one per engagement ------------------------------------------------------------------

    private protected static uint Now(GroupTestWorld world) => world.World.Host.World.NowMs;

    private protected static ObjectGuid Skull(GroupTestWorld world, PlayerbotSiegeRaid raid)
        => world.GroupManager.GetGroup(raid.Group!.LeaderGuid) is { } server ? server.TargetIcons[7] : default;

    private protected static List<Player> Living(GroupTestWorld world, PlayerbotSiegeRaid raid)
        => [.. raid.Group!.Members.Where(m => !m.Real && m.Joined).Select(m => world.World.Host.World.FindOnlinePlayer(m.Guid)).OfType<Player>().Where(p => p.IsAlive)];

    private protected static float YardsFrom(Player player, Vector3 point) => MathF.Sqrt(((player.X - point.X) * (player.X - point.X)) + ((player.Y - point.Y) * (player.Y - point.Y)));

    /// <summary>Kill every living member of <paramref name="raid"/> whose name contains <paramref name="part"/> (the bots are named after their class).</summary>
    private protected static Task<bool> KillAsync(GroupTestWorld world, PlayerbotSiegeRaid raid, string part) => world.OnWorldAsync(() =>
    {
        foreach (var member in raid.Group!.Members.Where(m => m.Name.Contains(part, StringComparison.Ordinal)))
            if (world.World.Host.World.FindOnlinePlayer(member.Guid) is { IsAlive: true } player)
            {
                player.Health = 0;
                player.Map!.Combat.KillPlayer(player);
            }

        return true;
    });

    /// <summary>The scenario world with guards a raid cannot kill during a test (600 health at 62 and 63 yards), the objective far behind them (150 yards, out of the fight).</summary>
    private protected static async Task<GroupTestWorld> StartStrongGuards()
        => await GroupTestWorld.StartAsync([], null, extraCreatures: [Guard(GuardA, 62f, 2f, health: 600), Guard(GuardB, 63f, -2f, health: 600), Guard(Objective, 150f, 0f, health: 120)]);

    /// <summary>Where a retreat of <paramref name="siege"/> goes: the waypoint behind the step (the raid pulls from the second waypoint, so this is 40 yards out).</summary>
    private protected static Vector3 RetreatPoint(PlayerbotSiege siege) => siege.Step > 0 ? siege.Waypoints[siege.Step - 1] : siege.Rally;

    /// <summary>One raid of six, waypoints at 20 and 40 yards, run until it fights the guards with its leader more than 12 yards from its retreat point.</summary>
    private protected static async Task<(PlayerbotSiege Siege, PlayerbotSiegeRaid Raid)> IntoTheFight(GroupTestWorld world)
    {
        (Guid Id, string Name)[] bots = await Enlist(world, 20f, 40f);
        PlayerbotSiege siege = await Start(world, bots, raids: 1);
        PlayerbotSiegeRaid raid = siege.Raids[0];
        Assert.True(await world.RunUntilAsync(240_000, () => siege.Step >= 1 && raid.WasInCombat && YardsFrom(Leader(world, raid), RetreatPoint(siege)) > 12f), await world.TraceAsync());
        return (siege, raid);
    }

    /// <summary>Keep the two strong guards at full health (a test that needs the fight to outlast the rally hold).</summary>
    private protected static void TopUpGuards(GroupTestWorld world, PlayerbotSiegeRaid raid)
    {
        if (Leader(world, raid).Map?.FindUpdater<CreatureMapSystem>() is not { } system) return;
        foreach (Creature guard in system.Creatures)
            if (guard.Entry is GuardA or GuardB && guard.IsAlive) guard.Health = guard.MaxHealth;
    }

    // --- push through a straggler fight -------------------------------------------------------------------------------------

    /// <summary>The scenario world with the objective 300 yards out: nothing hostile is within the pull range of a leader on the way (waypoints at 20, 40 and 60 yards).</summary>
    private protected static Task<GroupTestWorld> StartEmptyWay() => GroupTestWorld.StartAsync([], null, extraCreatures: [Guard(Objective, 300f, 0f, health: 120)]);

    /// <summary>The combat flag a far-away attacker leaves on the members: set again on every check of the clock, so the raid is in combat for as long as the test runs.</summary>
    private protected static void KeepInCombat(GroupTestWorld world, PlayerbotSiegeRaid raid)
    {
        foreach (Player member in Living(world, raid)) member.Map!.Combat.SetInCombatState(member, 5_500);
    }

    // --- the real paths: stalemate, two raids, lag ----------------------------------------------------------------------------

    private protected static float MaxLag(GroupTestWorld world, PlayerbotSiegeRaid raid)
    {
        Player leader = Leader(world, raid);
        return Living(world, raid).Where(p => p.Guid != leader.Guid).Select(p => MathF.Sqrt(((p.X - leader.X) * (p.X - leader.X)) + ((p.Y - leader.Y) * (p.Y - leader.Y)))).DefaultIfEmpty(0f).Max();
    }

    /// <summary>The fixture of the stalemate tests: two guards 45 yards out that the raid cannot kill while they are topped up; the way: waypoints at the start, then 150, 200 and 250 yards.</summary>
    private protected static async Task<(GroupTestWorld World, PlayerbotSiege Siege, PlayerbotSiegeRaid Raid)> StalemateFixture(params float[] waypoints)
    {
        GroupTestWorld world = await GroupTestWorld.StartAsync([], null,
            extraCreatures: [Guard(GuardA, 45f, 2f, health: 600), Guard(GuardB, 46f, -2f, health: 600), Guard(Objective, 600f, 0f, health: 120)]);
        (Guid Id, string Name)[] bots = await Enlist(world, waypoints.Length > 0 ? waypoints : [4f, 150f, 200f, 250f]);
        PlayerbotSiege siege = await Start(world, bots, raids: 1);
        return (world, siege, siege.Raids[0]);
    }

    private protected static void TopUpMembers(GroupTestWorld world, PlayerbotSiegeRaid raid)
    {
        foreach (Player member in Living(world, raid)) member.Health = member.MaxHealth;
    }

    /// <summary>The runs of the guard stalemate test: 3, or the number in SIEGE_STALEMATE_RUNS (a loop to measure how often it fails).</summary>
    public static IEnumerable<object[]> StalemateRuns()
        => Enumerable.Range(1, int.TryParse(Environment.GetEnvironmentVariable("SIEGE_STALEMATE_RUNS"), out int n) && n > 0 ? n : 3).Select(i => new object[] { i });

    private protected static int _stalemateRows, _stalemateRetreatRows;

    private protected static int StalemateRunCount() => StalemateRuns().Count();

    /// <summary>How far from the waypoint the probe of the waiting-leader test is held once it has been fought: past <see cref="PlayerbotSiegeCommander.SiegeAnswerLeashYards"/> by more than the 3-yard slack of the bound plus a melee reach
    /// (a leader that chased it to melee stands clear of the bound), yet, on the side the leader fought on, within <see cref="PlayerbotGroupAI.AssistYards"/> of a leader standing at the waypoint (42 against 50), so the probe stays
    /// one of the answerable attackers of the leader for the whole hold and only the answer leash keeps the leader at its waypoint.</summary>
    internal const float ProbeKiteYards = PlayerbotSiegeCommander.SiegeAnswerLeashYards + 12f;

    /// <summary>How long the probe is held past the leash: long enough for the walk home and a stretch standing there, well under the lag hold (<see cref="PlayerbotGroupCoordinator.LagHoldMs"/>) that moves the step on.</summary>
    internal const uint ProbeHoldMs = 12_000;

    /// <summary>How long after the probe is put past the leash the leader may still have it as its target: a combat think or two (<see cref="PlayerbotOptions.CombatThinkIntervalMs"/>).</summary>
    internal const uint ProbeDropByMs = 1_000;

    /// <summary>By when the leader must be back within <see cref="PlayerbotSiegeCommander.WaypointYards"/> of its waypoint: it fights within the leash, 30 yards at the most, which it walks back in under 5 s.</summary>
    internal const uint ProbeHomeByMs = 7_000;

    private protected static float Yards3d(Unit a, Unit b) => MathF.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)) + ((a.Z - b.Z) * (a.Z - b.Z)));

    /// <summary>One line of what the waiting leader and its attacker are doing (the failure story of the answer tests).</summary>
    private protected static string AnswerLine(GroupTestWorld world, PlayerbotSiegeRaid raid, Guid leaderBot, Player leader, Vector3 waypoint, Creature? attacker, uint began)
    {
        var ai = raid.Group!.Members.First(m => m.BotId == leaderBot).AI;
        return $"+{unchecked(Now(world) - began)}ms atWp={raid.AtWaypoint} push={raid.Push} answerStep={raid.AnsweringStep} answerAge={(raid.AnswerRequestedMs == 0 ? -1 : (long)unchecked(Now(world) - raid.AnswerRequestedMs))} off={FlatYards(leader, waypoint):F1} "
            + $"att={(attacker is null ? "-" : $"{FlatYards(attacker, waypoint):F1}off hp={attacker.Health} victimLeader={ReferenceEquals(attacker.Combat.Victim, leader)} dist={FlatYards(leader, new Vector3(attacker.X, attacker.Y, attacker.Z)):F1}")} step={ai?.Step} target={(ai?.TargetUnit is null ? "-" : "set")}";
    }

    private protected static Creature? NextAttacker(Player leader)
        => leader.Map?.FindUpdater<CreatureMapSystem>()?.Creatures.FirstOrDefault(g => g.Entry is GuardA or GuardB && g.IsAlive);

    private protected static float FlatYards(Unit unit, Vector3 point) => MathF.Sqrt(((unit.X - point.X) * (unit.X - point.X)) + ((unit.Y - point.Y) * (unit.Y - point.Y)));

    private protected sealed class WaitingSince { public uint Since; }

    private protected static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PlayerbotSiegeRaid, WaitingSince> Waiting = new();

    /// <summary>
    /// Any other guard that already followed the raid to the waypoint would be answered first (the leader fights the nearest attacker) and TopUpGuards keeps it at full health, while sending it into evade from the waypoint
    /// leaves it running home out of combat for 14 s (so killing the probe drops the raid out of combat and ends the push). Reset every other live guard to its spawn point in combat with the held straggler.
    /// </summary>
    private protected static void ResetOtherGuardsToStraggler(CreatureMapSystem system, PlayerbotSiegeRaid raid, Player leader, Creature probe, uint now)
    {
        Player? straggler = raid.Group?.Members.FirstOrDefault(m => !m.Real && m.BotId != raid.Group.LeaderBotId) is { } held ? leader.Map?.FindPlayer(held.Guid) : null;
        foreach (Creature other in system.Creatures.Where(g => g.Entry is GuardA or GuardB && g.IsAlive && !ReferenceEquals(g, probe)))
        {
            ResetGuardToHome(system, other, now);
            if (straggler is not null)
            {
                other.CalledAssistance = true;
                system.AttackStart(other, straggler);
                other.Combat.Threat.AddThreat(straggler, 100_000f);
            }
        }
    }

    /// <summary>Put a guard back at its spawn point out of combat, at once (no evade run home): its auras of the fight, its combat, threat and motion are dropped.</summary>
    private protected static void ResetGuardToHome(CreatureMapSystem system, Creature guard, uint now)
    {
        system.ResetAurasOnEvade(guard);
        system.StopCombatInPlace(guard);
        guard.Motion.Clear();
        system.StopMoving(guard);
        guard.Relocate(guard.Home.X, guard.Home.Y, guard.Home.Z, guard.Home.Orientation, now);
    }

    /// <summary>
    /// A ranged attacker on the waiting leader, as the tests make one: a guard 25 yards from the waypoint that cannot move (rooted, so it neither walks to the leader nor is pushed about by the test) and has the leader on its threat
    /// list. Returns it once the waiting leader's lag hold has run for 5 s, or null before.
    /// </summary>
    private protected static Creature? SendRangedAttacker(PlayerbotSiege siege, PlayerbotSiegeRaid raid, Player leader, int stepAtPush, uint now, float yards = 25f)
    {
        // The lag clock runs from the first read of the straggler, before the leader arrives: "waiting 5 s" is counted here, from the leader's arrival at the waypoint with its push in force.
        WaitingSince box = Waiting.GetOrCreateValue(raid);
        if (siege.Step != stepAtPush || !raid.AtWaypoint || raid.PushInForce == PlayerbotSiegePush.None) box.Since = 0;
        else if (box.Since == 0)
        {
            box.Since = Math.Max(1u, now);
            raid.LagSinceMs = 0; // the members were already behind (they fight the guards): the lag hold of these tests counts from the leader's arrival, so the clock starts afresh here
        }
        if (box.Since == 0 || raid.LagSinceMs == 0 || unchecked(now - box.Since) <= 5_000
            || leader.Map?.FindUpdater<CreatureMapSystem>() is not { } system || NextAttacker(leader) is not { } guard)
            return null;
        ResetOtherGuardsToStraggler(system, raid, leader, guard, now);
        Vector3 waypoint = siege.Waypoints[stepAtPush];
        system.SetAiImmobilized(guard, true, combatOnly: false);
        guard.Relocate(waypoint.X + yards, waypoint.Y, guard.Z, 0, now);
        guard.Combat.Threat.AddThreat(leader, 100_000f);
        return guard;
    }

    private protected const uint WeakFoodItem = 992101;
    private protected const uint WeakFoodSpell = 992001;

    // --- the furthest-waypoint credit and the route tether -------------------------------------------------------------------

    /// <summary>Moves the leader of <paramref name="raid"/> (and its members, so none lags) to <paramref name="x"/> yards east of the start, <paramref name="y"/> north of it.</summary>
    private protected static void Teleport(GroupTestWorld world, PlayerbotSiegeRaid raid, float x, float y = 0f)
    {
        foreach (var member in raid.Group!.Members.Where(m => !m.Real && m.Joined))
            if (world.World.Host.World.FindOnlinePlayer(member.Guid) is { } player)
                player.Relocate(Origin.X + x, Origin.Y + y, Origin.Z, 0, Now(world));
    }

    /// <summary>Four waypoints (15, 30, 45 and 60 yards), no guards, the objective at 70 yards or (<paramref name="objectiveFar"/>) 400, out of any pull range: the raids are teleported over the waypoints.</summary>
    private protected static async Task<(GroupTestWorld World, PlayerbotSiege Siege)> CreditFixture(int raids, bool objectiveFar = true)
    {
        GroupTestWorld world = objectiveFar
            ? await GroupTestWorld.StartAsync([], null, extraCreatures: [Guard(Objective, 400f, 0f, health: 120)])
            : await StartWorld(guards: false);
        (Guid Id, string Name)[] bots = await Enlist(world, 15f, 30f, 45f, 60f);
        PlayerbotSiege siege = await Start(world, bots, raids);
        Assert.True(siege.Step <= 1, $"the siege is at step {siege.Step} already");
        return (world, siege);
    }

    /// <summary>The first step the siege moves to after now, or -1 within <paramref name="ms"/>.</summary>
    private protected static async Task<int> NextStep(GroupTestWorld world, PlayerbotSiege siege, uint ms)
    {
        int before = siege.Step, seen = -1;
        await world.RunUntilAsync(ms, () =>
        {
            if (siege.Step != before) seen = siege.Step;
            return seen >= 0;
        });
        return seen;
    }

    /// <summary>The distinct steps the siege goes through (the first is the step now) until it reaches <paramref name="target"/> or <paramref name="ms"/> have run.</summary>
    private protected static async Task<List<int>> StepsUntil(GroupTestWorld world, PlayerbotSiege siege, int target, uint ms)
    {
        var seen = new List<int> { siege.Step };
        await world.RunUntilAsync(ms, () =>
        {
            if (siege.Step != seen[^1]) seen.Add(siege.Step);
            return siege.Step >= target;
        });
        return seen;
    }

    private protected static void AssertOneAtATime(List<int> seen, string story)
    {
        for (int i = 1; i < seen.Count; i++) Assert.True(seen[i] == seen[i - 1] + 1, $"the step went {string.Join(" -> ", seen)}: {story}");
    }

    // --- the per-step deadline ------------------------------------------------------------------------------------------------

    /// <summary>Keeps every raid 60 yards behind the start (where no waypoint is) until the step has lasted <see cref="PlayerbotGroupCoordinator.StepDeadlineMs"/> minus <paramref name="beforeMs"/>.</summary>
    private protected static Task<bool> HoldAwayUntilDeadline(GroupTestWorld world, PlayerbotSiege siege, uint beforeMs)
        => world.RunUntilAsync(PlayerbotGroupCoordinator.StepDeadlineMs + 60_000, () =>
        {
            foreach (PlayerbotSiegeRaid raid in siege.Raids) Teleport(world, raid, -60f);
            return unchecked(Now(world) - siege.StepSinceMs) >= PlayerbotGroupCoordinator.StepDeadlineMs - beforeMs;
        });

    // --- what the scan counts, and the marching leader ----------------------------------------------------------------------------

    /// <summary>One raid, one waypoint 100 yards east (the way covers x 0..100), the given creatures near it and the objective far away; the creatures stand where they were put.</summary>
    private protected static async Task<(GroupTestWorld World, PlayerbotSiege Siege, PlayerbotSiegeRaid Raid)> ScanFixture(
        Func<List<(CreatureTemplate Template, CreatureSpawn Spawn)>> creatures, float waypointZ = 0f)
    {
        List<(CreatureTemplate, CreatureSpawn)> extra = creatures();
        extra.Add(Guard(Objective, 400f, 0f, health: 120));
        GroupTestWorld world = await GroupTestWorld.StartAsync([], null, extraCreatures: [.. extra]);
        (Guid Id, string Name)[] bots = await Enlist(world);
        await world.OnWorldAsync(() =>
        {
            Assert.Null(world.Coordinator.AddSiegeWaypoint(0, new Vector3(Origin.X + 100f, Origin.Y, Origin.Z + waypointZ)));
            return true;
        });
        PlayerbotSiege siege = await Start(world, bots, raids: 1);
        await world.OnWorldAsync(() =>
        {
            CreatureMapSystem system = Leader(world, siege.Raids[0]).Map!.FindUpdater<CreatureMapSystem>()!;
            foreach (Creature creature in system.Creatures.Where(c => c.Entry != Objective)) system.SetAiImmobilized(creature, true, combatOnly: false);
            return true;
        });
        return (world, siege, siege.Raids[0]);
    }

    private protected static Creature Spawned(GroupTestWorld world, PlayerbotSiegeRaid raid, uint entry)
        => Leader(world, raid).Map!.FindUpdater<CreatureMapSystem>()!.Creatures.First(c => c.Entry == entry);

    /// <summary>One raid, waypoints at 20 and 40, a guard A far to the north of the way, a guard B far to the south; the objective far away.</summary>
    private protected static async Task<(GroupTestWorld World, PlayerbotSiege Siege, PlayerbotSiegeRaid Raid)> TetherFixture()
    {
        GroupTestWorld world = await GroupTestWorld.StartAsync([], null,
            extraCreatures: [Guard(GuardA, 30f, 50f, health: 600), Guard(GuardB, 30f, -50f, health: 600), Guard(Objective, 400f, 0f, health: 120)]);
        (Guid Id, string Name)[] bots = await Enlist(world, 20f, 40f);
        PlayerbotSiege siege = await Start(world, bots, raids: 1);
        // The guards stand where they were put: neither walks to the raid.
        await world.OnWorldAsync(() =>
        {
            Player leader = Leader(world, siege.Raids[0]);
            CreatureMapSystem system = leader.Map!.FindUpdater<CreatureMapSystem>()!;
            foreach (Creature guard in system.Creatures.Where(g => g.Entry is GuardA or GuardB)) system.SetAiImmobilized(guard, true, combatOnly: false);
            return true;
        });
        return (world, siege, siege.Raids[0]);
    }
}
