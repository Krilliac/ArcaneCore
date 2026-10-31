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
/// The no-path rally and its promotion, a flapping path, a raid pushing through combat, and stalemate steps. Split from the former <c>PlayerbotSiegeWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotSiegeWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotSiegeNoPathRallyWorldTests : PlayerbotSiegeWorldTestBase
{
    [Fact]
    public async Task ASiegeWhoseOnlyLeaderIsOffTheSiegeMap_IsNotWon_ByAGridLoadedAtTheObjectivesPositionThere()
    {
        // The Deadmines world logs the bots in elsewhere than the open one: the objective stands 30 yards east of their start.
        Vector3 start = new(-8939.45f, -132.49f, 83.58f);
        (CreatureTemplate template, CreatureSpawn spawn) = Guard(Objective, 0f, 0f, health: 120);
        spawn = spawn with { X = start.X + 30f, Y = start.Y };
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([], null, dungeon: true, extraCreatures: [(template, spawn)]);
        (Guid, string)[] bots =
        [
            await world.AddBotAsync("Sgatank", 1, 1, 10, [GroupTestWorld.Taunt]),
            await world.AddBotAsync("Sgbtank", 1, 1, 10, [GroupTestWorld.Taunt]),
        ];
        await world.RunAsync(2_000);
        await world.OnWorldAsync(() =>
        {
            Assert.Null(world.Coordinator.AddSiegeWaypoint(0, start with { X = start.X + 10f }));
            return true;
        });
        PlayerbotSiege siege = await Start(world, [.. bots.Select(b => (b.Item1, b.Item2))], raids: 1);
        PlayerbotSiegeRaid raid = siege.Raids[0];
        Assert.True(await world.RunUntilAsync(60_000, () => siege.ObjectiveSeen), await world.TraceAsync());

        // The leader stands on another map (the Deadmines), on the objective's own X/Y: the grid there is loaded and no such creature lives there.
        // (The raid dissolves when its leader leaves the map, so the commander's tick never gets that far; the judgement of the objective itself must
        // refuse such a witness too, and is called here as the tick would call it.)
        Player witness = await world.OnWorldAsync(() => Leader(world, raid));
        Assert.True(await world.OnWorldAsync(() =>
        {
            Vector3 at = siege.ObjectivePosition;
            return world.World.Services.GetRequiredService<ArcaneCore.World.Teleport.TeleportFeature>().Teleports.TeleportTo(witness, 36, at.X, at.Y, at.Z, 0f);
        }), "teleport refused");
        Assert.True(await world.RunUntilAsync(20_000, () => witness.MapId == 36), await world.TraceAsync());
        bool won = await world.OnWorldAsync(() =>
        {
            Assert.True(witness.Map!.FindUpdater<CreatureMapSystem>()!.IsGridLoaded(siege.ObjectivePosition.X, siege.ObjectivePosition.Y),
                "the grid at the objective's X/Y on the other map must be loaded for this test to mean anything");
            Assert.True(siege.ObjectiveSeen);
            typeof(PlayerbotGroupCoordinator).GetMethod("WatchObjective", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(world.Coordinator, [siege, witness, world.World.Host.World.NowMs]);
            return siege.Won;
        });

        Assert.False(won, "a witness on another map judged the objective dead");
        Assert.False(siege.ObjectiveUnseen);
    }

    [Fact]
    public async Task ANoPathRally_RetriesTheWalk_AndRetreatsAgainOnceThePathIsBack()
    {
        await using GroupTestWorld world = await StartStrongGuards();
        (PlayerbotSiege siege, PlayerbotSiegeRaid raid) = await IntoTheFight(world);
        await KillAsync(world, raid, "mage");
        uint[] allowed = world.Options.AllowedMaps;

        world.Options.AllowedMaps = [1];
        Assert.True(await world.RunUntilAsync(30_000, () => raid.Order == PlayerbotSiegeOrder.Rally && raid.RetreatNoPath), await world.TraceAsync());
        Vector3 point = RetreatPoint(siege);
        Assert.True(YardsFrom(Leader(world, raid), point) > PlayerbotSiegeCommander.RallyYards);

        // The path comes back: within a few retries the flag clears and the raid retreats again, the leader walking to its point.
        world.Options.AllowedMaps = allowed;
        Assert.True(await world.RunUntilAsync(15_000, () => !raid.RetreatNoPath), await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(15_000, () => raid.Order == PlayerbotSiegeOrder.Retreat || raid.Order == PlayerbotSiegeOrder.Rally && !raid.RetreatNoPath), await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(60_000, () => YardsFrom(Leader(world, raid), point) <= PlayerbotSiegeCommander.RallyYards + 1.5f), await world.TraceAsync());
        Assert.Equal(1, raid.Retreats);
    }

    [Fact]
    public async Task APromotion_DuringANoPathRally_RestartsTheClockAndClearsTheFlag()
    {
        await using GroupTestWorld world = await StartStrongGuards();
        (_, PlayerbotSiegeRaid raid) = await IntoTheFight(world);
        await KillAsync(world, raid, "mage");
        world.Options.AllowedMaps = [1];
        Assert.True(await world.RunUntilAsync(30_000, () => raid.Order == PlayerbotSiegeOrder.Rally && raid.RetreatNoPath && raid.RallySinceMs != 0), await world.TraceAsync());
        (uint oldSince, Guid oldLeader) = (raid.RallySinceMs, raid.Group!.LeaderBotId);

        // The maps stay refused: the new leader has no path either, so only the promotion itself can restart the clock.
        await world.OnWorldAsync(() =>
        {
            Player leader = Leader(world, raid);
            leader.Health = 0;
            leader.Map!.Combat.KillPlayer(leader);
            return true;
        });
        Assert.True(await world.RunUntilAsync(30_000, () => raid.Group.LeaderBotId != oldLeader), await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(1_500, () => raid.RallySinceMs != oldSince), "the new leader inherited the clock of the no-path rally" + Environment.NewLine + await world.TraceAsync());
        Assert.True(raid.RallyClockRestarted);
    }

    [Fact]
    public async Task APromotion_AtARealRetreatPoint_KeepsTheTimeAtThePoint()
    {
        await using GroupTestWorld world = await StartStrongGuards();
        (_, PlayerbotSiegeRaid raid) = await IntoTheFight(world);
        await KillAsync(world, raid, "mage");
        Assert.True(await world.RunUntilAsync(60_000, () => { TopUpGuards(world, raid); return raid.Order == PlayerbotSiegeOrder.Rally && !raid.RetreatNoPath && raid.RallySinceMs != 0; }), await world.TraceAsync());
        (uint since, Guid oldLeader) = (raid.RallySinceMs, raid.Group!.LeaderBotId);

        await world.OnWorldAsync(() =>
        {
            Player leader = Leader(world, raid);
            leader.Health = 0;
            leader.Map!.Combat.KillPlayer(leader);
            return true;
        });
        Assert.True(await world.RunUntilAsync(30_000, () => raid.Group.LeaderBotId != oldLeader), await world.TraceAsync());
        var changed = new List<uint>();
        await world.RunUntilAsync(2_000, () => { TopUpGuards(world, raid); if (raid.RallySinceMs != since) changed.Add(raid.RallySinceMs); return false; });
        Assert.Empty(changed);
        Assert.False(raid.RallyClockRestarted);
    }

    [Fact]
    public async Task AFlappingPath_DoesNotKeepTheRallyBoundFromEver_BeingReached()
    {
        await using GroupTestWorld world = await StartStrongGuards();
        (PlayerbotSiege siege, PlayerbotSiegeRaid raid) = await IntoTheFight(world);
        await KillAsync(world, raid, "mage");
        uint[] allowed = world.Options.AllowedMaps;
        world.Options.AllowedMaps = [1];
        Assert.True(await world.RunUntilAsync(30_000, () => raid.RetreatLatched), await world.TraceAsync());

        // The path flaps: it is back when a retry comes (the flag clears), and refused again at once (the next walk finds none).
        Vector3 point = RetreatPoint(siege);
        int flaps = 0;
        uint refusedAt = Now(world);
        var trail = new List<string>();
        bool wasFlagged = false;
        uint began = Now(world);
        bool ended = await world.RunUntilAsync(2 * PlayerbotSiegeCommander.RallyHoldMs + 3_000 + 20_000, () =>
        {
            TopUpGuards(world, raid);
            if (unchecked(Now(world) - began) % 2000 < 50) trail.Add($"[{unchecked(Now(world) - began)} {raid.Order} noPath={raid.RetreatNoPath} since={raid.RallySinceMs} restarted={raid.RallyClockRestarted} leader={Leader(world, raid).IsAlive}]");
            if (raid.RetreatNoPath)
            {
                // Refused for a while, so the leader rallies (and a retry finds the path back only after that).
                if (unchecked(Now(world) - refusedAt) >= 3_500) { world.Options.AllowedMaps = allowed; PlayerbotMotion.ForgetLoops(Leader(world, raid)); wasFlagged = true; }
            }
            else if (wasFlagged)
            {
                // The route the retry planned is dropped (a stale route, a lift ride that ended): the next walk plans again and finds none.
                world.Options.AllowedMaps = [1];
                refusedAt = Now(world);
                Player dragged = Leader(world, raid);
                dragged.Relocate(point.X + 25f, point.Y, point.Z, 0, Now(world)); // pursuit keeps the leader off the point
                typeof(PlayerbotGroupAI).GetProperty("_route", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(raid.Group!.Find(raid.Group.LeaderBotId)!.AI, null);
                wasFlagged = false;
                flaps++;
            }
            return !raid.RetreatLatched;
        });
        Assert.True(ended, $"the latch outlived every bound after {flaps} flaps, {unchecked(Now(world) - began)} ms" + Environment.NewLine + string.Join(" ", trail));
        Assert.Equal(1, raid.Retreats);
    }

    [Fact]
    public async Task ARaidInCombatWithAFarAttackerAndNoHostileNearItsLeader_PushesThrough_AndAdvancesWithinTheBound()
    {
        await using GroupTestWorld world = await StartEmptyWay();
        (Guid Id, string Name)[] bots = await Enlist(world, 20f, 40f, 60f);
        PlayerbotSiege siege = await Start(world, bots, raids: 1);
        PlayerbotSiegeRaid raid = siege.Raids[0];
        await world.OnWorldAsync(() => { KeepInCombat(world, raid); return true; });
        float startX = await world.OnWorldAsync(() => Leader(world, raid).X);
        uint began = Now(world);

        bool pushed = false, detailed = false, leaderStep = false;
        var orders = new HashSet<PlayerbotSiegeOrder>();
        uint firstStepMs = 0;
        bool done = await world.RunUntilAsync(PlayerbotSiegeCommander.PushThroughQuietMs + 90_000, () =>
        {
            KeepInCombat(world, raid);
            orders.Add(raid.Order);
            Player leader = Leader(world, raid);
            pushed |= raid.Push == PlayerbotSiegePush.Quiet && leader.Combat.IsInCombat && raid.LastFacts.Hostiles == 0;
            detailed |= raid.Detail.Contains("push=quiet", StringComparison.Ordinal);
            leaderStep |= raid.Group!.Find(raid.Group.LeaderBotId)?.AI?.Step.Contains(":push", StringComparison.Ordinal) == true;
            if (siege.Step >= 1 && firstStepMs == 0) firstStepMs = unchecked(Now(world) - began);
            return siege.Step >= 2;
        });

        Assert.True(done, $"the raid never pushed through (step {siege.Step}, order {raid.Order}, push {raid.Push}, facts {raid.LastFacts})" + Environment.NewLine + await world.TraceAsync());
        Assert.True(pushed && detailed && leaderStep, $"pushed {pushed} detailed {detailed} leader step {leaderStep}");
        Assert.InRange(firstStepMs, 1u, PlayerbotSiegeCommander.PushThroughQuietMs + 40_000);
        Assert.True(await world.OnWorldAsync(() => Leader(world, raid).X - startX > 30f), "the leader walked on");
        // No retreat began or was counted, and nobody was ever told to disengage.
        Assert.Equal(0, raid.Retreats);
        Assert.DoesNotContain(PlayerbotSiegeOrder.Retreat, orders);
        Assert.DoesNotContain(PlayerbotSiegeOrder.Rally, orders);
    }

    [Fact]
    public async Task OnTheFinalLeg_TheLegToTheLastWaypoint_ARaidInCombatWithNoHostileNear_DoesNotPushThrough()
    {
        await using GroupTestWorld world = await StartEmptyWay();
        (Guid Id, string Name)[] bots = await Enlist(world, 20f, 40f, 60f);
        PlayerbotSiege siege = await Start(world, bots, raids: 1);
        PlayerbotSiegeRaid raid = siege.Raids[0];
        await world.OnWorldAsync(() => { siege.Step = siege.Waypoints.Count - 1; /* NB3: the leg to the last waypoint is the final leg */ KeepInCombat(world, raid); return true; });

        var pushes = new List<string>();
        uint began = Now(world);
        Assert.False(await world.RunUntilAsync(PlayerbotSiegeCommander.PushThroughQuietMs + 20_000, () =>
        {
            KeepInCombat(world, raid);
            // The order of the decision before the fight (an advance) stands for the first tick of the commander.
            if (unchecked(Now(world) - began) > 1_000 && (raid.Push != PlayerbotSiegePush.None || raid.Order == PlayerbotSiegeOrder.Advance))
                pushes.Add($"{raid.Order}/{raid.Push} at {Now(world)}");
            return false;
        }));
        Assert.Empty(pushes);
        Assert.Equal(siege.Waypoints.Count - 1, siege.Step);
        Assert.Equal(0, raid.Retreats);
    }

    [Fact]
    public async Task AStalemateWithARealHostileNearTheLeader_DisengagesTheMembers_AndTheRaidAdvancesTogether()
    {
        // Two guards 45 yards out that the raid cannot kill while they are topped up; the way: a waypoint at the start, then 150, 200 and 250 yards.
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([], null,
            extraCreatures: [Guard(GuardA, 45f, 2f, health: 600), Guard(GuardB, 46f, -2f, health: 600), Guard(Objective, 600f, 0f, health: 120)]);
        (Guid Id, string Name)[] bots = await Enlist(world, 4f, 150f, 200f, 250f);
        PlayerbotSiege siege = await Start(world, bots, raids: 1);
        PlayerbotSiegeRaid raid = siege.Raids[0];

        bool pushed = false, hostileNear = false, engagedBefore = false, guardsDown = false;
        float lagAtStep = -1f;
        var engagedWhilePushing = new List<string>();
        var marksWhilePushing = new List<string>();
        var leaderFought = new List<string>();
        var wrongSince = new Dictionary<ObjectGuid, uint>();
        var trail = new List<string>();
        uint startedAt = Now(world), pushSince = 0;
        int stepSeen = 0;
        bool done = await world.RunUntilAsync(PlayerbotSiegeCommander.StalemateMs + 250_000, () =>
        {
            // The guards cannot be killed until the push begins (then the raid, fighting back what attacks it, may finish them).
            if (!pushed) TopUpGuards(world, raid);
            // The healers cannot keep up with the guards over three minutes in this fixture: the raid is kept healthy, as a stalemate needs.
            foreach (Player member in Living(world, raid)) member.Health = member.MaxHealth;
            if (unchecked(Now(world) - startedAt) / 10_000 >= trail.Count)
                trail.Add($"+{unchecked(Now(world) - startedAt)}ms step={siege.Step} order={raid.Order} push={raid.Push} combat={raid.LastFacts.InCombat} hostiles={raid.LastFacts.Hostiles} inCombatMs={raid.LastFacts.MsInCombatAtStep} hp={raid.LastFacts.AverageHealthPct:F0} leaderx={Leader(world, raid).X - Origin.X:F1}");
            if (raid.Push == PlayerbotSiegePush.None && raid.LastFacts.InCombat)
                engagedBefore |= raid.Group!.Members.Any(m => !m.Real && m.AI?.TargetUnit is not null);
            if (raid.Push == PlayerbotSiegePush.Stalemate)
            {
                pushed = true;
                hostileNear |= raid.LastFacts.Hostiles > 0;
                if (pushSince == 0) pushSince = Now(world);
                // A second into the push the skull is off its unit and nobody is the raid's pull target (the marks are taken off when the push begins).
                if (unchecked(Now(world) - pushSince) >= 1_000 && (!Skull(world, raid).IsEmpty || !raid.PullTarget.IsEmpty))
                    marksWhilePushing.Add($"skull {Skull(world, raid)}, pull target {raid.PullTarget}, {unchecked(Now(world) - pushSince)} ms into the push");
                // The leader walks: away from its waypoint, more than a second into the push, it has no target (its attackers follow it).
                if (unchecked(Now(world) - pushSince) >= 1_000 && !raid.AtWaypoint && raid.Group!.Members.First(m => m.BotId == raid.Group.LeaderBotId).AI?.TargetUnit is { } leaderTarget)
                    leaderFought.Add($"the leader fights {leaderTarget.Guid} {unchecked(Now(world) - pushSince)} ms into the push, away from its waypoint");
                // Three seconds into the push (the marks are off, the targets dropped) a member fights only what attacks a member of the raid.
                if (unchecked(Now(world) - pushSince) >= 3_000)
                    foreach (var member in raid.Group!.Members.Where(m => !m.Real && m.Joined))
                    {
                        // A member's target may be one think old (the creature switched victims since): only a target that stays wrong for 2.5 s counts. Right is an attacker of any living member of the raid.
                        bool wrong = world.World.Host.World.FindOnlinePlayer(member.Guid) is { IsAlive: true } && member.AI?.TargetUnit is { } target
                            && !Living(world, raid).Any(p => p.Combat.Attackers.Any(a => ReferenceEquals(a, target)));
                        if (!wrong) wrongSince.Remove(member.Guid);
                        else if (!wrongSince.TryAdd(member.Guid, Now(world)) && unchecked(Now(world) - wrongSince[member.Guid]) >= 2_500)
                            engagedWhilePushing.Add($"{member.Name} fights {member.AI?.TargetUnit?.Guid} that has not attacked it for 2.5 s, {unchecked(Now(world) - pushSince)} ms into the push");
                    }

                // Six seconds into the push the guards are finished off (the raid is topped up in this fixture, and the test is about where the raid goes, not about a damage race).
                if (!guardsDown && unchecked(Now(world) - pushSince) >= 6_000)
                {
                    guardsDown = true;
                    Player tank = Leader(world, raid);
                    foreach (Creature guard in tank.Map!.FindUpdater<CreatureMapSystem>()!.Creatures.Where(c => c.Entry is GuardA or GuardB && c.IsAlive).ToArray())
                        tank.Map.Combat.DealDamage(tank, guard, guard.Health);
                }
            }
            else pushSince = 0;

            if (siege.Step != stepSeen)
            {
                stepSeen = siege.Step;
                if (stepSeen == 2) lagAtStep = MaxLag(world, raid);
            }

            return siege.Step >= 2;
        });

        string story = string.Join(Environment.NewLine, trail);
        Assert.True(done, $"the step never moved (step {siege.Step}, order {raid.Order}, push {raid.Push}, facts {raid.LastFacts})" + Environment.NewLine + story + Environment.NewLine + await world.TraceAsync());
        Assert.True(engagedBefore && pushed && hostileNear, $"engaged before {engagedBefore}, pushed {pushed}, hostile near the leader {hostileNear}" + Environment.NewLine + story);
        Assert.Empty(engagedWhilePushing);
        Assert.Empty(marksWhilePushing);
        Assert.Empty(leaderFought);
        Assert.Equal(0, raid.Retreats);
        // The step moved with every member within LagYards of the leader (a tick of walking as tolerance), and nobody is left behind afterwards.
        Assert.InRange(lagAtStep, 0f, PlayerbotSiegeCommander.LagYards + 6f);
        Assert.True(await world.RunUntilAsync(30_000, () => MaxLag(world, raid) <= PlayerbotSiegeCommander.LagYards + 6f), "a member was left behind: " + MaxLag(world, raid));
    }

    [Fact]
    public async Task TwoRaids_OnlyOneQualifies_NoPush_UntilBothDo_AndTheStepThenMovesTogether()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([], null,
            extraCreatures: [Guard(GuardA, 0f, 85f, health: 600), Guard(Objective, 300f, 0f, health: 120)]);
        (Guid Id, string Name)[] bots = await Enlist(world, 20f, 40f, 60f);
        PlayerbotSiege siege = await Start(world, bots, raids: 2);
        PlayerbotSiegeRaid first = siege.Raids[0], second = siege.Raids[1];

        // The second raid stands by a guard it cannot kill; the first has nothing hostile within 55 yards.
        await world.OnWorldAsync(() =>
        {
            foreach (var member in second.Group!.Members.Where(m => !m.Real))
            {
                Player shifted = world.World.Host.World.FindOnlinePlayer(member.Guid)!;
                shifted.Relocate(Origin.X, Origin.Y + 75f, shifted.Z, 0, Now(world));
            }

            KeepInCombat(world, first);
            KeepInCombat(world, second);
            return true;
        });

        var violations = new List<string>();
        bool Invariant()
        {
            // A raid pushes only when the other is out of combat or pushes too.
            foreach ((PlayerbotSiegeRaid a, PlayerbotSiegeRaid b, string name) in new[] { (first, second, "first"), (second, first, "second") })
                if (a.PushingThrough && b.LastFacts.InCombat && PlayerbotSiegeCommander.PushOf(b.LastFacts) == PlayerbotSiegePush.None)
                    violations.Add($"{name} raid pushes ({a.Push}) at {Now(world)} while the other fights without qualifying: {b.LastFacts}");
            return false;
        }

        // Phase 1: the first raid qualifies by the quiet rule long before the end, and does not push.
        bool firstQualified = false;
        await world.RunUntilAsync(PlayerbotSiegeCommander.PushThroughQuietMs + 30_000, () =>
        {
            TopUpGuards(world, second);
            KeepInCombat(world, first);
            KeepInCombat(world, second);
            firstQualified |= PlayerbotSiegeCommander.PushOf(first.LastFacts) == PlayerbotSiegePush.Quiet;
            return Invariant();
        });
        Assert.True(firstQualified, "the first raid never qualified for a push: " + first.LastFacts + Environment.NewLine + await world.TraceAsync());
        Assert.Empty(violations);
        Assert.Equal((PlayerbotSiegePush.None, PlayerbotSiegeOrder.Assault, 0), (first.Push, first.Order, siege.Step));
        Assert.True(second.LastFacts.Hostiles > 0 && second.LastFacts.InCombat, "the second raid fights the guard: " + second.LastFacts);

        // Phase 2: the guard dies, the second raid goes quiet and qualifies; the push starts for both and the step moves on together.
        await world.OnWorldAsync(() =>
        {
            Player killer = Leader(world, second);
            Creature guard = killer.Map!.FindUpdater<CreatureMapSystem>()!.Creatures.First(c => c.Entry == GuardA);
            killer.Map.Combat.DealDamage(killer, guard, guard.Health);
            return true;
        });
        bool bothPushed = false;
        bool moved = await world.RunUntilAsync(150_000, () =>
        {
            KeepInCombat(world, first);
            KeepInCombat(world, second);
            bothPushed |= first.PushingThrough && second.PushingThrough;
            Invariant();
            return siege.Step >= 1;
        });
        Assert.True(moved, $"the step never moved (first {first.Order}/{first.Push} {first.LastFacts}; second {second.Order}/{second.Push} {second.LastFacts})" + Environment.NewLine + await world.TraceAsync());
        Assert.True(bothPushed, "the raids never pushed together");
        Assert.Empty(violations);
        Assert.Equal(0, first.Retreats + second.Retreats);
    }

    [Fact]
    public async Task AMemberHeldFarAway_BlocksTheStepOfAPushingRaid_UntilItCatchesUp()
    {
        await using GroupTestWorld world = await StartEmptyWay();
        world.Options.Party.TeleportToLeader = false; // the member walks back, it is not summoned
        (Guid Id, string Name)[] bots = await Enlist(world, 20f, 40f, 60f);
        PlayerbotSiege siege = await Start(world, bots, raids: 1);
        PlayerbotSiegeRaid raid = siege.Raids[0];
        var held = raid.Group!.Members.First(m => !m.Real && m.BotId != raid.Group.LeaderBotId);
        void Hold()
        {
            Player far = world.World.Host.World.FindOnlinePlayer(held.Guid)!;
            far.Relocate(Origin.X, Origin.Y + 120f, far.Z, 0, Now(world));
        }

        await world.OnWorldAsync(() => { KeepInCombat(world, raid); Hold(); return true; });

        // The raid qualifies, the leader walks to the first waypoint and waits there: the member is 120 yards away.
        bool granted = false;
        var stepped = new List<string>();
        await world.RunUntilAsync(PlayerbotSiegeCommander.PushThroughQuietMs + 40_000, () =>
        {
            KeepInCombat(world, raid);
            Hold();
            granted |= raid.Push == PlayerbotSiegePush.Quiet;
            if (siege.Step != 0) stepped.Add($"step {siege.Step} at {Now(world)} with the member {MaxLag(world, raid):F0} yd from the leader");
            return false;
        });
        Assert.True(granted, "the push was never granted: " + raid.LastFacts);
        Assert.Empty(stepped);
        Assert.InRange(YardsFrom(Leader(world, raid), siege.Waypoints[0]), 0f, PlayerbotSiegeCommander.WaypointYards + 2f);
        Assert.True(raid.LastFacts.MaxLagYards > PlayerbotSiegeCommander.LagYards, "the member is far: " + raid.LastFacts);

        // Released, the member catches up (it walks), and the step moves on with it inside the bound.
        float lagAtStep = -1f;
        bool moved = await world.RunUntilAsync(120_000, () =>
        {
            KeepInCombat(world, raid);
            if (siege.Step >= 1 && lagAtStep < 0f) lagAtStep = MaxLag(world, raid);
            return siege.Step >= 1;
        });
        Assert.True(moved, $"the step never moved after the member was released: {raid.LastFacts}" + Environment.NewLine + await world.TraceAsync());
        Assert.InRange(lagAtStep, 0f, PlayerbotSiegeCommander.LagYards + 6f);
    }
}
