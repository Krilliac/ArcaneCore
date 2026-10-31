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
/// A waiting leader answering attackers (the leash), a blacklisted waypoint, queued loot and resting during a push, and the no-progress bound. Split from the former <c>PlayerbotSiegeWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotSiegeWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotSiegeLeashWorldTests : PlayerbotSiegeWorldTestBase
{
    /// <summary>
    /// A guard that runs at the waiting leader from its spawn point is fought by the leader at its waypoint (the leader's AI takes it as its target and its melee hits it) and never evades on its way in. Once it has been hit it is
    /// put <see cref="PlayerbotSiegeWorldTestBase.ProbeKiteYards"/> from the waypoint, on the side the leader fought it on, and held there rooted for <see cref="PlayerbotSiegeWorldTestBase.ProbeHoldMs"/> with the leader on its threat list: a kiting attacker past the answer leash that
    /// stays within <see cref="PlayerbotGroupAI.AssistYards"/> of the leader for the whole hold (the test checks that, and that the step's answer is not spent), so the leash of the waiting leader's target selection is the one
    /// thing between the leader and it. For the whole hold the leader must not take it as its target again (from <see cref="PlayerbotSiegeWorldTestBase.ProbeDropByMs"/> on: a think or two), must not go past the leash, and must be back within
    /// <see cref="PlayerbotSiegeCommander.WaypointYards"/> of its waypoint by <see cref="PlayerbotSiegeWorldTestBase.ProbeHomeByMs"/> and stay there to the end of the hold; released, the guard comes back, and the step moves on without a denial or a refused waypoint.
    /// </summary>
    [Theory]
    [MemberData(nameof(StalemateRuns))]
    public async Task AnAttackerComingToTheWaitingLeader_IsFought_AndPastTheLeashIsNotFollowed_NoDenialNoRefusedWaypoint(int run)
    {
        (GroupTestWorld world, PlayerbotSiege siege, PlayerbotSiegeRaid raid) = await StalemateFixture();
        await using GroupTestWorld _ = world;
        world.Options.Party.TeleportToLeader = false;
        Guid leaderBot = raid.Group!.LeaderBotId;
        var held = raid.Group.Members.First(m => !m.Real && m.BotId != leaderBot);
        // One member is kept where the guards are, 110 yards behind the waypoint: it stays in the fight (the raid stays in combat) and the lag hold of the waiting leader runs.
        void Hold()
        {
            Player far = world.World.Host.World.FindOnlinePlayer(held.Guid)!;
            far.Relocate(Origin.X + 38f, Origin.Y, far.Z, 0, Now(world));
        }

        int stepAtPush = -1;
        bool sent = false, refused = false, targeted = false, hit = false, evaded = false, onLeader = false, kited = false, released = false, back = false;
        // The hold's books: whether the probe stayed answerable but for the leash (an attacker of the leader within AssistYards, the step's answer unspent), and what the leader did about it.
        bool attackerLost = false, tooFar = false, spent = false, targetedPastLeash = false, strayedHome = false;
        float kiteDistance = -1f, farthestInHold = -1f;
        uint kitedAt = 0;
        Creature? probe = null;
        float worstOff = 0f;
        var trail = new List<string>();
        uint began = Now(world);
        bool done = await world.RunUntilAsync(PlayerbotSiegeCommander.StalemateMs + 250_000, () =>
        {
            Player leader = Leader(world, raid);
            var leaderAi = raid.Group!.Members.First(m => m.BotId == leaderBot).AI;
            // Read before the top-up: the probe was at full health after the last one, so less now is damage dealt during this tick, by its victim's side.
            if (probe is not null && siege.Step == stepAtPush)
            {
                evaded |= probe.IsEvading;
                onLeader |= ReferenceEquals(probe.Combat.Victim, leader);
                targeted |= ReferenceEquals(leaderAi?.TargetUnit, probe);
                hit |= ReferenceEquals(leader.Combat.Victim, probe) && probe.Health < probe.MaxHealth;
            }

            TopUpGuards(world, raid);
            TopUpMembers(world, raid); // nobody dies: the only trigger is the guard sent at the leader
            if (raid.Push == PlayerbotSiegePush.Stalemate && stepAtPush < 0) { stepAtPush = siege.Step; }
            if (stepAtPush < 0) return false;
            Hold();
            if (unchecked(Now(world) - began) / 5_000 >= trail.Count)
                trail.Add($"+{unchecked(Now(world) - began)}ms step={siege.Step} sent={sent} push={raid.Push} denied={raid.PushDeniedStep} atWp={raid.AtWaypoint} x={leader.X - Origin.X:F1} lagSince={raid.LagSinceMs} off={worstOff:F1} "
                    + $"tgt={(leaderAi?.TargetUnit is null ? "-" : "set")} probe={(probe is null ? "-" : $"{Yards3d(leader, probe):F1}yd")} spent={raid.AnswerSpent}");
            // The members stay behind, fighting at the start (the lag hold of a pushing leader at its waypoint runs, about 110 yards); once the leader has waited 5 s a guard (the one farthest along x) turns on the leader, as the guard of a member that
            // died does. It runs at the leader from afar, and the leader must fight it at the waypoint.
            // Where the guards are by then is not this test's to wait for: a guard that chased the leader to the waypoint is still on it unless its soft leash fired during the chase, and since #1630 that is a coin toss (a chaser whose
            // spline is finalized, melee or ranged, refreshes its leash clock every 5 s, and so does every hit on it, so the clock is under 5 s old when the leader walks off; the chase to the waypoint takes about 15 s against the 12 s
            // of the leash; and an in-area member listed above the leader within 110% of its threat keeps the leader the victim, vmangos ThreatContainer::selectNextVictim). So every guard is sent back to its spawn point first, and the
            // probe runs at the leader from there, 100 yards out; it must arrive without evading, which the assertions below check.
            if (!sent && raid.AtWaypoint && raid.PushInForce != PlayerbotSiegePush.None && raid.LagSinceMs != 0 && unchecked(Now(world) - raid.LagSinceMs) > 5_000
                && leader.Map?.FindUpdater<CreatureMapSystem>() is { } system
                && system.Creatures.Where(g => g.Entry is GuardA or GuardB && g.IsAlive).OrderByDescending(g => MathF.Abs(g.X - leader.X)).FirstOrDefault() is { } guard)
            {
                ResetOtherGuardsToStraggler(system, raid, leader, guard, Now(world));
                ResetGuardToHome(system, guard, Now(world));
                system.AttackStart(guard, leader);
                guard.Combat.Threat.AddThreat(leader, 100_000f);
                probe = guard;
                sent = true;
            }

            if (sent && siege.Step == stepAtPush)
            {
                Vector3 waypoint = siege.Waypoints[stepAtPush];
                float off = YardsFrom(leader, waypoint);
                worstOff = Math.Max(worstOff, off);
                refused |= PlayerbotMotion.IsBlacklisted(leader, waypoint);
                // The leash: once the leader has fought the probe, the probe kites past the answer leash and is held there (rooted, so it neither walks back nor is pushed about) with the leader on its threat list. It goes to the side
                // of the waypoint the leader fought it on (the way it came): past the leash from the waypoint, yet within AssistYards of the leader wherever on its way home the leader is, since the leader fights within the leash
                // (so the probe is at most ProbeKiteYards plus its melee reach from the leader, and ProbeKiteYards from the waypoint itself). A rooted creature refreshes its leash clock at every leash check and runs no chase step,
                // so it neither soft-leashes nor evades for an unreachable victim while held.
                if (!kited && hit && probe is not null && leader.Map?.FindUpdater<CreatureMapSystem>() is { } creatures)
                {
                    kited = true;
                    kitedAt = Now(world);
                    creatures.SetAiImmobilized(probe, true, combatOnly: false);
                    float dx = probe.X - waypoint.X, dy = probe.Y - waypoint.Y, length = MathF.Sqrt((dx * dx) + (dy * dy));
                    (dx, dy) = length >= 1f ? (dx / length, dy / length) : (-1f, 0f);
                    probe.Relocate(waypoint.X + (ProbeKiteYards * dx), waypoint.Y + (ProbeKiteYards * dy), probe.Z, 0, Now(world));
                    kiteDistance = Yards3d(leader, probe);
                }

                if (kited && !released && probe is not null)
                {
                    uint sinceKite = unchecked(Now(world) - kitedAt);
                    if (!ReferenceEquals(probe.Combat.Victim, leader)) probe.Combat.Threat.AddThreat(leader, 100_000f);
                    // Answerable but for the leash: an attacker of the leader within AssistYards (3D, as SelectTarget measures), with the step's answer unspent (so the leash in force is SiegeAnswerLeashYards, not the disk).
                    attackerLost |= !leader.Combat.Attackers.Contains(probe);
                    tooFar |= Yards3d(leader, probe) > PlayerbotGroupAI.AssistYards;
                    spent |= raid.AnswerSpent;
                    // The leash itself: the leader drops the probe within a think or two and never takes it again while it is held; it is home by ProbeHomeByMs and stays home to the end of the hold.
                    if (sinceKite >= ProbeDropByMs) targetedPastLeash |= ReferenceEquals(leaderAi?.TargetUnit, probe);
                    back |= off <= PlayerbotSiegeCommander.WaypointYards;
                    if (sinceKite >= ProbeHomeByMs)
                    {
                        strayedHome |= off > PlayerbotSiegeCommander.WaypointYards;
                        farthestInHold = Math.Max(farthestInHold, off);
                    }

                    if (sinceKite >= ProbeHoldMs && leader.Map?.FindUpdater<CreatureMapSystem>() is { } holding)
                    {
                        released = true;
                        holding.SetAiImmobilized(probe, false, combatOnly: false);
                    }
                }
            }

            return siege.Step > stepAtPush || raid.PushDeniedStep == stepAtPush || raid.Retreats > 0;
        });

        string story = $"run {run}: done={done} sent={sent} step={siege.Step}/{stepAtPush} denied={raid.PushDeniedStep} refused={refused} worstOff={worstOff:F1} retreats={raid.Retreats} "
            + $"probe: onLeader={onLeader} evaded={evaded} kiteDistance={kiteDistance:F1} attackerLost={attackerLost} tooFar={tooFar} spent={spent}; "
            + $"leader: targeted={targeted} hit={hit} kited={kited} released={released} targetedPastLeash={targetedPastLeash} back={back} strayedHome={strayedHome} farthestInHold={farthestInHold:F1} {raid.LastFacts}"
            + Environment.NewLine + string.Join(Environment.NewLine, trail);
        Assert.True(sent, "the probe never fired: " + story);
        Assert.True(done && siege.Step > stepAtPush, "the step did not move: " + story);
        // The probe came all the way: it turned on the leader and never evaded on its run in from its spawn point.
        Assert.True(onLeader, "the probe never had the leader as its victim: " + story);
        Assert.False(evaded, "the probe evaded before the step moved: " + story);
        // The leader answered it at the waypoint before the step moved: its AI took the probe as its target and its melee hit it.
        Assert.True(targeted, "the waiting leader never took the probe as its target: " + story);
        Assert.True(hit, "the waiting leader never hit the probe: " + story);
        Assert.True(kited, "the probe was never sent past the leash: " + story);
        Assert.True(released, "the hold did not run its course before the step moved: " + story);
        // The hold was a test of the leash and nothing else: the probe stayed an attacker of the leader within AssistYards, and the answer of the step was not spent.
        Assert.True(kiteDistance <= PlayerbotGroupAI.AssistYards - 2f, "the probe was not held within AssistYards of the leader: " + story);
        Assert.False(attackerLost, "the probe stopped being an attacker of the leader during the hold: " + story);
        Assert.False(tooFar, "the leader was more than AssistYards from the held probe: " + story);
        Assert.False(spent, "the answer of the step was spent during the hold, so the disk, not the leash, kept the leader: " + story);
        // The leash: the waiting leader does not answer an attacker beyond SiegeAnswerLeashYards of its waypoint, does not follow it past the leash, and is home for the rest of the hold.
        Assert.False(targetedPastLeash, "the leader took the probe as its target while it was held past the leash: " + story);
        Assert.True(worstOff <= PlayerbotSiegeCommander.SiegeAnswerLeashYards + 3f, "the leader followed the probe past the leash: " + story);
        Assert.True(back, "the leader did not return to its waypoint once the probe was past the leash: " + story);
        Assert.False(strayedHome, $"the leader was not at its waypoint from {ProbeHomeByMs} ms to {ProbeHoldMs} ms into the hold: " + story);
        Assert.NotEqual(stepAtPush, raid.PushDeniedStep);
        Assert.False(refused, "the loop watch refused the waypoint: " + story);
    }

    [Fact]
    public async Task AMeleeWaitingLeaderShotByAnAttackerOutOfReach_ClosesAndFightsIt_ThenReturnsToTheWaypoint_NoDenial()
    {
        (GroupTestWorld world, PlayerbotSiege siege, PlayerbotSiegeRaid raid) = await StalemateFixture();
        await using GroupTestWorld _ = world;
        world.Options.Party.TeleportToLeader = false;
        Guid leaderBot = raid.Group!.LeaderBotId;
        var held = raid.Group.Members.First(m => !m.Real && m.BotId != leaderBot);
        // One member is kept 110 yards behind the waypoint, in the fight: the lag hold of the waiting leader runs and nobody covers it.
        void Hold()
        {
            Player far = world.World.Host.World.FindOnlinePlayer(held.Guid)!;
            far.Relocate(Origin.X + 38f, Origin.Y, far.Z, 0, Now(world));
        }

        int stepAtPush = -1;
        Creature? attacker = null;
        uint sentAt = 0, lagBefore = 0, onLeaderSince = 0, lastLine = 0, engagedAt = 0;
        bool engaged = false, killed = false, back = false, refused = false, flipped = false, lagKept = true;
        float leaderOff = 0f;
        var trail = new List<string>();
        uint began = Now(world);
        bool done = await world.RunUntilAsync(PlayerbotSiegeCommander.StalemateMs + 300_000, () =>
        {
            TopUpGuards(world, raid);
            TopUpMembers(world, raid);
            Player leader = Leader(world, raid);
            if (raid.Push == PlayerbotSiegePush.Stalemate && stepAtPush < 0) stepAtPush = siege.Step;
            if (stepAtPush < 0) return false;
            Hold();
            Vector3 waypoint = siege.Waypoints[stepAtPush];
            if (attacker is null && SendRangedAttacker(siege, raid, leader, stepAtPush, Now(world)) is { } sent)
            {
                attacker = sent;
                sentAt = Now(world);
                lagBefore = raid.LagSinceMs;
            }

            if (attacker is null) return false;
            if (unchecked(Now(world) - lastLine) >= 500) { lastLine = Now(world); trail.Add(AnswerLine(world, raid, leaderBot, leader, waypoint, attacker, began)); }
            if (attacker.IsAlive && !ReferenceEquals(attacker.Combat.Victim, leader)) attacker.Combat.Threat.AddThreat(leader, 100_000f); // it keeps shooting the leader
            float off = FlatYards(leader, waypoint);
            if (siege.Step == stepAtPush) leaderOff = Math.Max(leaderOff, off);
            // Reached out in the open: the leader is out of the disk and within a blow of the attacker, and has it as its target.
            if (!engaged && attacker.IsAlive && off > PlayerbotSiegeCommander.WaypointYards + 2f && FlatYards(leader, new Vector3(attacker.X, attacker.Y, attacker.Z)) <= 6f
                && ReferenceEquals(raid.Group.Members.First(m => m.BotId == leaderBot).AI?.TargetUnit, attacker))
            {
                engaged = true;
                engagedAt = Now(world);
            }

            // The fight goes on for 4 s (the answer outlasts the freshness of the leader's answer), then the attacker dies.
            if (engaged && attacker.IsAlive && unchecked(Now(world) - engagedAt) > 4_000) leader.Map!.Combat.Kill(null, attacker);
            killed |= !attacker.IsAlive;
            if (attacker.IsAlive && ReferenceEquals(attacker.Combat.Victim, leader)) onLeaderSince = onLeaderSince == 0 ? Now(world) : onLeaderSince;
            else onLeaderSince = 0;
            refused |= siege.Step == stepAtPush && PlayerbotMotion.IsBlacklisted(leader, waypoint);
            // The raid still reads the leader at the waypoint while it answers out of the disk, and the lag hold keeps running.
            if (siege.Step == stepAtPush && !killed && onLeaderSince != 0 && unchecked(Now(world) - onLeaderSince) > 3_000 && off > PlayerbotSiegeCommander.WaypointYards + 2f)
            {
                flipped |= !raid.AtWaypoint;
                lagKept &= raid.LagSinceMs == lagBefore;
            }

            back |= killed && (off <= PlayerbotSiegeCommander.WaypointYards || siege.Step > stepAtPush);
            return back || raid.PushDeniedStep == stepAtPush || raid.Retreats > 0 || unchecked(Now(world) - sentAt) > 80_000;
        });

        async Task<string> Story() => $"done={done} sent={attacker is not null} engaged={engaged} killed={killed} back={back} refused={refused} flipped={flipped} lagKept={lagKept} step={siege.Step}/{stepAtPush} denied={raid.PushDeniedStep} retreats={raid.Retreats} leaderOff={leaderOff:F1} {raid.LastFacts}"
            + Environment.NewLine + string.Join(Environment.NewLine, trail) + Environment.NewLine + await world.TraceAsync();
        if (attacker is null) Assert.Fail("the probe never fired: " + await Story());
        if (!engaged) Assert.Fail("the melee leader never closed on the attacker that was shooting it: " + await Story());
        if (leaderOff <= PlayerbotSiegeCommander.WaypointYards + 2f) Assert.Fail("the leader never left the disk, the probe proves nothing: " + await Story());
        if (leaderOff > PlayerbotSiegeCommander.SiegeAnswerLeashYards + PlayerbotSiegeCommander.WaypointYards) Assert.Fail("the leader went past the leash: " + await Story());
        if (!killed) Assert.Fail("the attacker did not die: " + await Story());
        if (!back) Assert.Fail("the leader did not return to the waypoint: " + await Story());
        if (raid.PushDeniedStep == stepAtPush) Assert.Fail("the push was denied: " + await Story());
        if (refused) Assert.Fail("the waypoint was blacklisted: " + await Story());
        if (flipped) Assert.Fail("the raid read the answering leader as away from its waypoint: " + await Story());
        if (!lagKept) Assert.Fail("the lag hold was reset by the answer: " + await Story());
    }

    [Fact]
    public async Task AMeleeWaitingLeaderChasingAnAttackerOutOfReach_WalksAChaseRoute_SoTheLoopWatchDoesNotCountItsEnds()
    {
        (GroupTestWorld world, PlayerbotSiege siege, PlayerbotSiegeRaid raid) = await StalemateFixture();
        await using GroupTestWorld _ = world;
        world.Options.Party.TeleportToLeader = false;
        Guid leaderBot = raid.Group!.LeaderBotId;
        var held = raid.Group.Members.First(m => !m.Real && m.BotId != leaderBot);
        int stepAtPush = -1;
        Creature? attacker = null;
        uint sentAt = 0, engagedAt = 0;
        bool engaged = false, killed = false, chasing = false;
        var plain = new List<string>();
        bool done = await world.RunUntilAsync(PlayerbotSiegeCommander.StalemateMs + 300_000, () =>
        {
            TopUpGuards(world, raid);
            TopUpMembers(world, raid);
            Player leader = Leader(world, raid);
            if (raid.Push == PlayerbotSiegePush.Stalemate && stepAtPush < 0) stepAtPush = siege.Step;
            if (stepAtPush < 0) return false;
            Player far = world.World.Host.World.FindOnlinePlayer(held.Guid)!;
            far.Relocate(Origin.X + 38f, Origin.Y, far.Z, 0, Now(world));
            if (attacker is null && SendRangedAttacker(siege, raid, leader, stepAtPush, Now(world)) is { } sent)
            {
                attacker = sent;
                sentAt = Now(world);
            }

            if (attacker is null) return false;
            if (attacker.IsAlive && !ReferenceEquals(attacker.Combat.Victim, leader)) attacker.Combat.Threat.AddThreat(leader, 100_000f);
            // Every route the leader walks that ends at the attacker is a chase: the watch must not count it (the waypoint walk, the chase, the walk back read as A, B, A).
            if (attacker.IsAlive && PlayerbotMotion.RouteOf(leader) is { } route && route.Points.Count > 0
                && FlatYards(leader, new Vector3(attacker.X, attacker.Y, attacker.Z)) > 3f
                && Vector3.Distance(route.Points[^1], new Vector3(attacker.X, attacker.Y, attacker.Z)) <= 4f)
            {
                chasing = true;
                if (!route.Chase) plain.Add($"t={Now(world) - sentAt} end=({route.Points[^1].X:F1},{route.Points[^1].Y:F1})");
            }

            if (!engaged && attacker.IsAlive && FlatYards(leader, new Vector3(attacker.X, attacker.Y, attacker.Z)) <= 6f)
            {
                engaged = true;
                engagedAt = Now(world);
            }

            if (engaged && attacker.IsAlive && unchecked(Now(world) - engagedAt) > 4_000) leader.Map!.Combat.Kill(null, attacker);
            killed |= !attacker.IsAlive;
            return killed || unchecked(Now(world) - sentAt) > 80_000;
        });

        string story = $"done={done} sent={attacker is not null} engaged={engaged} killed={killed} chasing={chasing}";
        if (attacker is null) Assert.Fail("the probe never fired: " + story);
        if (!chasing) Assert.Fail("the leader never walked a route to the attacker, the probe proves nothing: " + story);
        if (plain.Count > 0) Assert.Fail("the fight chase was planned as a plain route, so the loop watch counts its ends: " + story + " " + string.Join("; ", plain.Take(5)));
    }

    [Fact]
    public async Task AnAttackerThatKitesBeyondTheLeash_IsNotFollowedFurther_TheLeaderReturnsToTheWaypoint_NoDenial()
    {
        (GroupTestWorld world, PlayerbotSiege siege, PlayerbotSiegeRaid raid) = await StalemateFixture();
        await using GroupTestWorld _ = world;
        world.Options.Party.TeleportToLeader = false;
        Guid leaderBot = raid.Group!.LeaderBotId;
        var held = raid.Group.Members.First(m => !m.Real && m.BotId != leaderBot);
        void Hold()
        {
            Player far = world.World.Host.World.FindOnlinePlayer(held.Guid)!;
            far.Relocate(Origin.X + 38f, Origin.Y, far.Z, 0, Now(world));
        }

        int stepAtPush = -1;
        Creature? attacker = null;
        uint sentAt = 0, kitedAt = 0, lastLine = 0;
        bool chased = false, kited = false, back = false, refused = false;
        float leaderOff = 0f;
        var trail = new List<string>();
        uint began = Now(world);
        bool done = await world.RunUntilAsync(PlayerbotSiegeCommander.StalemateMs + 300_000, () =>
        {
            TopUpGuards(world, raid);
            TopUpMembers(world, raid);
            Player leader = Leader(world, raid);
            if (raid.Push == PlayerbotSiegePush.Stalemate && stepAtPush < 0) stepAtPush = siege.Step;
            if (stepAtPush < 0) return false;
            Hold();
            Vector3 waypoint = siege.Waypoints[stepAtPush];
            if (attacker is null && SendRangedAttacker(siege, raid, leader, stepAtPush, Now(world)) is { } sent)
            {
                attacker = sent;
                sentAt = Now(world);
            }

            if (attacker is null) return false;
            if (unchecked(Now(world) - lastLine) >= 500) { lastLine = Now(world); trail.Add(AnswerLine(world, raid, leaderBot, leader, waypoint, attacker, began)); }
            if (attacker.IsAlive && !ReferenceEquals(attacker.Combat.Victim, leader)) attacker.Combat.Threat.AddThreat(leader, 100_000f); // it keeps shooting the leader
            float off = FlatYards(leader, waypoint);
            if (siege.Step == stepAtPush) leaderOff = Math.Max(leaderOff, off);
            // The attacker stays 25 yards out until the leader has left the disk to answer it, then it kites to 45 yards (beyond the 30-yard leash) and stays there.
            if (!kited && off > 8f && ReferenceEquals(raid.Group.Members.First(m => m.BotId == leaderBot).AI?.TargetUnit, attacker))
            {
                chased = true;
                kited = true;
                kitedAt = Now(world);
                attacker.Relocate(waypoint.X + 45f, waypoint.Y, attacker.Z, 0, Now(world));
            }

            refused |= siege.Step == stepAtPush && PlayerbotMotion.IsBlacklisted(leader, waypoint);
            back |= kited && unchecked(Now(world) - kitedAt) > 2_000 && (off <= PlayerbotSiegeCommander.WaypointYards || siege.Step > stepAtPush);
            return back || raid.PushDeniedStep == stepAtPush || raid.Retreats > 0 || unchecked(Now(world) - sentAt) > 80_000 || !raid.PushingThrough;
        });

        async Task<string> Story() => $"done={done} sent={attacker is not null} chased={chased} kited={kited} back={back} refused={refused} step={siege.Step}/{stepAtPush} denied={raid.PushDeniedStep} retreats={raid.Retreats} leaderOff={leaderOff:F1} {raid.LastFacts}"
            + Environment.NewLine + string.Join(Environment.NewLine, trail) + Environment.NewLine + await world.TraceAsync();
        if (attacker is null) Assert.Fail("the probe never fired: " + await Story());
        if (!chased) Assert.Fail("the leader never left the disk to answer the attacker: " + await Story());
        if (leaderOff > PlayerbotSiegeCommander.SiegeAnswerLeashYards + 3f) Assert.Fail("the leader followed the kiting attacker past the leash: " + await Story());
        if (!back) Assert.Fail("the leader did not return to the waypoint: " + await Story());
        if (raid.PushDeniedStep == stepAtPush) Assert.Fail("the push was denied: " + await Story());
        if (refused) Assert.Fail("the waypoint was blacklisted: " + await Story());
    }

    [Fact]
    public async Task APushingLeaderWhoseWaypointIsBlacklisted_WalksOn_OnlyThatEntryIsLifted_NoDenial()
    {
        (GroupTestWorld world, PlayerbotSiege siege, PlayerbotSiegeRaid raid) = await StalemateFixture();
        await using GroupTestWorld _ = world;
        int stepAtPush = -1;
        bool injected = false, lifted = false, decoyKept = true, walked = false, counterKept = false;
        uint injectedAt = 0;
        float xAtInject = 0f;
        var decoy = new Vector3(Origin.X + 300f, Origin.Y + 300f, Origin.Z);
        var trail = new List<string>();
        uint began = Now(world);
        bool done = await world.RunUntilAsync(PlayerbotSiegeCommander.StalemateMs + 250_000, () =>
        {
            TopUpGuards(world, raid);
            TopUpMembers(world, raid);
            Player leader = Leader(world, raid);
            if (raid.Push == PlayerbotSiegePush.Stalemate && stepAtPush < 0) stepAtPush = siege.Step;
            if (stepAtPush < 0) return false;
            Vector3 waypoint = siege.Waypoints[stepAtPush];
            if (unchecked(Now(world) - began) / 5_000 >= trail.Count)
                trail.Add($"+{unchecked(Now(world) - began)}ms step={siege.Step} injected={injected} lifted={lifted} push={raid.Push} denied={raid.PushDeniedStep} atWp={raid.AtWaypoint} x={leader.X - Origin.X:F1} rej={PlayerbotMotion.ConsecutiveRejections(leader)}");
            if (!injected && raid.PushInForce != PlayerbotSiegePush.None && !raid.AtWaypoint && siege.Step == stepAtPush && YardsFrom(leader, waypoint) > 20f
                && PlayerbotMotion.TryGetState(leader, out PlayerbotMotion.MotionState? state))
            {
                // Lifting one place leaves the rejection count alone (a successful route start resets it, so this is checked on the call itself, not later).
                var probe = new Vector3(Origin.X + 400f, Origin.Y + 400f, Origin.Z);
                state.Blacklist.Add((probe, Now(world) + 600_000));
                state.ConsecutiveRejections = 2;
                state.LastRejectedDestination = probe;
                PlayerbotMotion.Unblacklist(leader, probe);
                counterKept = state.ConsecutiveRejections == 2 && !PlayerbotMotion.IsBlacklisted(leader, probe);
                state.ConsecutiveRejections = 0;
                state.LastRejectedDestination = default;
                // The push's own walk is refused (as an A, B, A pattern would), another place is refused too, and a rejection count is running.
                state.Blacklist.Add((waypoint, Now(world) + 600_000));
                state.Blacklist.Add((decoy, Now(world) + 600_000));
                state.ConsecutiveRejections = 2;
                state.LastRejectedDestination = decoy;
                injected = true;
                injectedAt = Now(world);
                xAtInject = leader.X;
            }

            if (injected)
            {
                lifted |= !PlayerbotMotion.IsBlacklisted(leader, waypoint);
                // ForgetLoops on every think would clear this entry as well.
                if (unchecked(Now(world) - injectedAt) < 20_000) decoyKept &= PlayerbotMotion.IsBlacklisted(leader, decoy);
                walked |= leader.X - xAtInject > 10f;
            }

            return injected && (siege.Step > stepAtPush || raid.PushDeniedStep == stepAtPush || raid.Retreats > 0);
        });

        async Task<string> Story() => $"done={done} injected={injected} lifted={lifted} decoyKept={decoyKept} counterKept={counterKept} walked={walked} step={siege.Step}/{stepAtPush} denied={raid.PushDeniedStep} retreats={raid.Retreats} {raid.LastFacts}"
            + Environment.NewLine + string.Join(Environment.NewLine, trail) + Environment.NewLine + await world.TraceAsync();
        if (!injected) Assert.Fail("the probe never fired: " + await Story());
        if (!lifted) Assert.Fail("the push's own waypoint stayed refused: " + await Story());
        if (!walked) Assert.Fail("the leader did not walk on: " + await Story());
        if (raid.PushDeniedStep == stepAtPush) Assert.Fail("the push was denied: " + await Story());
        if (!counterKept) Assert.Fail("lifting one refused place reset the rejection count: " + await Story());
        if (!decoyKept) Assert.Fail("the other refused place was forgotten as well (ForgetLoops on every think): " + await Story());
        if (!(done && siege.Step > stepAtPush)) Assert.Fail("the step did not move: " + await Story());
    }

    [Fact]
    public async Task AStepThatAdvancesWhileTheLeaderAnswers_IsNotReadAtTheNextWaypoint_TheLeaderReachesItBeforeTheStepMovesOn()
    {
        // Waypoints at 4, 150, 188 and 238 yards: waypoint 2 is 38 yards past waypoint 1, outside the answer leash (the leader is not in its disk while it answers, so the furthest-waypoint credit has no waypoint-2 visit to give; a credit after the answer's bound is spent and the lag waived is the credit's own rule, which the test exempts),
        // and about 10 yards from where the leader stands when it answers an attacker 29 yards past waypoint 1.
        (GroupTestWorld world, PlayerbotSiege siege, PlayerbotSiegeRaid raid) = await StalemateFixture(4f, 150f, 188f, 238f);
        await using GroupTestWorld _ = world;
        world.Options.Party.TeleportToLeader = false;
        Guid leaderBot = raid.Group!.LeaderBotId;
        var held = raid.Group.Members.First(m => !m.Real && m.BotId != leaderBot);
        void Hold()
        {
            Player far = world.World.Host.World.FindOnlinePlayer(held.Guid)!;
            far.Relocate(Origin.X + 38f, Origin.Y, far.Z, 0, Now(world));
        }

        int stepAtPush = -1;
        Creature? attacker = null;
        uint sentAt = 0, lastLine = 0;
        bool readAtNext = false, sawDiskAtNext = false, skipped = false, advanced = false;
        float closestToNext = float.MaxValue;
        bool movedWhileOut = false;
        string stepChange = string.Empty;
        var trail = new List<string>();
        uint began = Now(world);
        bool done = await world.RunUntilAsync(PlayerbotSiegeCommander.StalemateMs + 500_000, () =>
        {
            TopUpGuards(world, raid);
            TopUpMembers(world, raid);
            Player leader = Leader(world, raid);
            if (raid.Push == PlayerbotSiegePush.Stalemate && stepAtPush < 0) stepAtPush = siege.Step;
            if (stepAtPush < 0) return false;
            Hold();
            if (attacker is null && SendRangedAttacker(siege, raid, leader, stepAtPush, Now(world), 29f) is { } sent)
            {
                attacker = sent;
                sentAt = Now(world);
            }

            if (attacker is null) return false;
            if (attacker.IsAlive && !ReferenceEquals(attacker.Combat.Victim, leader)) attacker.Combat.Threat.AddThreat(leader, 100_000f); // it keeps shooting the leader
            if (unchecked(Now(world) - lastLine) >= 1_000) { lastLine = Now(world); trail.Add(AnswerLine(world, raid, leaderBot, leader, siege.Waypoints[stepAtPush], attacker, began) + $" step={siege.Step}"); }
            if (siege.Step == stepAtPush + 1)
            {
                float toNext = YardsFrom(leader, siege.Waypoints[stepAtPush + 1]);
                if (!advanced)
                {
                    // The lockstep moves the step only with the leader really in the old waypoint's disk (level-aware); a little slack for the leader's move within the tick.
                    Vector3 old = siege.Waypoints[stepAtPush];
                    float oldFlat = FlatYards(leader, old), oldLevel = Math.Abs(leader.Z - old.Z);
                    // The lockstep is meant here. A step moved by the credit after the answer's bound was spent (LagHoldMs out of the disk) and the lag was waived is the credit's rule (tested apart).
                    bool credited = world.Coordinator.Events.Any(e => e.Contains($"step {stepAtPush} -> {stepAtPush + 1}", StringComparison.Ordinal));
                    movedWhileOut = !credited && (oldFlat > PlayerbotSiegeCommander.WaypointYards + 1f || oldLevel > PlayerbotSiegeCommander.WaypointLevelYards);
                    stepChange = $"the step moved at +{unchecked(Now(world) - began)}ms with the leader {oldFlat:F1} yards (level {oldLevel:F1}) from the old waypoint and {toNext:F1} from the next, atWp={raid.AtWaypoint} real={raid.AtWaypointReal} answerStep={raid.AnsweringStep}";
                }

                advanced = true;
                closestToNext = Math.Min(closestToNext, toNext);
                // At the next waypoint while the leader is still out where it fought: it is not at it.
                if (toNext > PlayerbotSiegeCommander.WaypointYards + 2f) readAtNext |= raid.AtWaypoint;
                else sawDiskAtNext = true;
            }

            if (siege.Step > stepAtPush + 1 && !sawDiskAtNext) skipped = true;
            return sawDiskAtNext || siege.Step > stepAtPush + 1 || raid.PushDeniedStep == stepAtPush || raid.Retreats > 0 || unchecked(Now(world) - sentAt) > 450_000;
        });

        async Task<string> Story() => $"{stepChange} done={done} advanced={advanced} readAtNext={readAtNext} sawDiskAtNext={sawDiskAtNext} skipped={skipped} closestToNext={closestToNext:F1} step={siege.Step}/{stepAtPush} denied={raid.PushDeniedStep} retreats={raid.Retreats} {raid.LastFacts}"
            + Environment.NewLine + string.Join(Environment.NewLine, trail) + Environment.NewLine + await world.TraceAsync();
        if (attacker is null) Assert.Fail("the probe never fired: " + await Story());
        if (!advanced) Assert.Fail("the step never moved on while the leader answered: " + await Story());
        if (movedWhileOut) Assert.Fail("the lockstep moved the step while the leader was out of the old waypoint's disk, answering: " + await Story());
        if (readAtNext) Assert.Fail("the raid read the leader at the next waypoint while it was still out where it fought: " + await Story());
        if (skipped) Assert.Fail("the step moved past the next waypoint before the leader reached it: " + await Story());
        if (!sawDiskAtNext) Assert.Fail("the leader never reached the next waypoint: " + await Story());
        if (raid.PushDeniedStep == stepAtPush) Assert.Fail("the push was denied: " + await Story());
    }

    [Fact]
    public async Task AKiterAtTheLeashEdge_SteppingInAndOutSeveralTimes_NeverResetsTheLagHold_NorBlacklistsTheWaypoint_NorTripsTheRejectionGuard()
    {
        (GroupTestWorld world, PlayerbotSiege siege, PlayerbotSiegeRaid raid) = await StalemateFixture();
        await using GroupTestWorld _ = world;
        world.Options.Party.TeleportToLeader = false;
        Guid leaderBot = raid.Group!.LeaderBotId;
        var held = raid.Group.Members.First(m => !m.Real && m.BotId != leaderBot);
        void Hold()
        {
            Player far = world.World.Host.World.FindOnlinePlayer(held.Guid)!;
            far.Relocate(Origin.X + 38f, Origin.Y, far.Z, 0, Now(world));
        }

        int stepAtPush = -1, maxRejections = 0;
        Creature? attacker = null;
        uint sentAt = 0, lagBefore = 0, lastLine = 0;
        bool lagReset = false, refused = false, chased = false, flipped = false, noPath = false;
        uint blacklistedSince = 0;
        float leaderOff = 0f;
        var trail = new List<string>();
        uint began = Now(world);
        bool done = await world.RunUntilAsync(PlayerbotSiegeCommander.StalemateMs + 300_000, () =>
        {
            TopUpGuards(world, raid);
            TopUpMembers(world, raid);
            Player leader = Leader(world, raid);
            if (raid.Push == PlayerbotSiegePush.Stalemate && stepAtPush < 0) stepAtPush = siege.Step;
            if (stepAtPush < 0) return false;
            Hold();
            Vector3 waypoint = siege.Waypoints[stepAtPush];
            if (attacker is null && SendRangedAttacker(siege, raid, leader, stepAtPush, Now(world), 28f) is { } sent)
            {
                attacker = sent;
                sentAt = Now(world);
                lagBefore = raid.LagSinceMs;
            }

            if (attacker is null) return false;
            if (attacker.IsAlive && !ReferenceEquals(attacker.Combat.Victim, leader)) attacker.Combat.Threat.AddThreat(leader, 100_000f);
            if (unchecked(Now(world) - lastLine) >= 1_000) { lastLine = Now(world); trail.Add(AnswerLine(world, raid, leaderBot, leader, waypoint, attacker, began) + $" rej={PlayerbotMotion.ConsecutiveRejections(leader)}"); }
            // The kiter: 3.5 s inside the leash (28 yards: the leader chases it out about 24 yards), then 4.5 s outside it (33 yards: the walk back takes longer than the 3 s a request stays fresh), three times over.
            uint t = unchecked(Now(world) - sentAt);
            bool inside = t < 24_000 && t % 8_000 < 3_500;
            attacker.Relocate(waypoint.X + (inside ? 28f : 33f), waypoint.Y, attacker.Z, 0, Now(world));
            float off = FlatYards(leader, waypoint);
            if (siege.Step == stepAtPush)
            {
                leaderOff = Math.Max(leaderOff, off);
                chased |= off > PlayerbotSiegeCommander.WaypointYards + 4f;
                lagReset |= raid.LagSinceMs != lagBefore;
                flipped |= raid.PushingThrough && !raid.AtWaypoint && off > PlayerbotSiegeCommander.WaypointYards;
                // The loop watch may refuse the waypoint for the moment between a walk back and the next think of the leader, which lifts it (LeadSiege); a refusal that stands, or a walk that finds no path, is the failure.
                if (PlayerbotMotion.IsBlacklisted(leader, waypoint)) { if (blacklistedSince == 0) blacklistedSince = Math.Max(1u, Now(world)); refused |= unchecked(Now(world) - blacklistedSince) > 2_000; }
                else blacklistedSince = 0;
                // The leader's own walk back to the waypoint (a chase of the kiter's place may be refused by the loop watch for a while, which only keeps the leader at the waypoint).
                string aiStep = raid.Group.Members.First(m => m.BotId == leaderBot).AI?.Step ?? string.Empty;
                noPath |= aiStep.StartsWith("lead:", StringComparison.Ordinal) && aiStep.EndsWith(":no-path", StringComparison.Ordinal);
                maxRejections = Math.Max(maxRejections, PlayerbotMotion.ConsecutiveRejections(leader));
            }

            return t > 25_000 || raid.PushDeniedStep == stepAtPush || raid.Retreats > 0 || siege.Step > stepAtPush;
        });

        async Task<string> Story() => $"done={done} chased={chased} lagReset={lagReset} flipped={flipped} refused={refused} noPath={noPath} maxRejections={maxRejections} step={siege.Step}/{stepAtPush} denied={raid.PushDeniedStep} retreats={raid.Retreats} leaderOff={leaderOff:F1} {raid.LastFacts}"
            + Environment.NewLine + string.Join(Environment.NewLine, trail) + Environment.NewLine + await world.TraceAsync();
        if (attacker is null) Assert.Fail("the probe never fired: " + await Story());
        if (!chased) Assert.Fail("the leader never left the disk to answer the kiter: " + await Story());
        if (siege.Step > stepAtPush) Assert.Fail("the step moved on during the kiting, the probe proves nothing: " + await Story());
        if (lagReset) Assert.Fail("the kiter reset the lag hold: " + await Story());
        if (flipped) Assert.Fail("the raid read the leader as away from its waypoint while it answered or walked back: " + await Story());
        if (refused) Assert.Fail("the waypoint stayed blacklisted: " + await Story());
        if (noPath) Assert.Fail("the leader's walk to the waypoint found no path: " + await Story());
        if (maxRejections >= PlayerbotMotion.MaxRejectionsBeforeLoop) Assert.Fail("the rejection guard was tripped: " + await Story());
        if (raid.PushDeniedStep == stepAtPush) Assert.Fail("the push was denied: " + await Story());
    }

    [Fact]
    public async Task QueuedLootWaits_WhileAPushingLeaderWalksOffItsWaypoint()
    {
        (GroupTestWorld world, PlayerbotSiege siege, PlayerbotSiegeRaid raid) = await StalemateFixture();
        await using GroupTestWorld _ = world;
        Guid leaderBot = raid.Group!.LeaderBotId;
        int stepAtPush = -1;
        bool queued = false, drained = false;
        int walkTicks = 0;
        bool done = await world.RunUntilAsync(PlayerbotSiegeCommander.StalemateMs + 250_000, () =>
        {
            TopUpGuards(world, raid);
            TopUpMembers(world, raid);
            Player leader = Leader(world, raid);
            if (raid.Push == PlayerbotSiegePush.Stalemate && stepAtPush < 0) stepAtPush = siege.Step;
            if (stepAtPush < 0) return false;
            PlayerbotGroupAI ai = raid.Group.Members.First(m => m.BotId == leaderBot).AI!;
            bool walking = siege.Step == stepAtPush && raid.PushingThrough && !raid.AtWaypoint && YardsFrom(leader, siege.Waypoints[stepAtPush]) > 20f;
            if (!queued && walking)
            {
                // A corpse looted before the push began left its packets queued.
                ai.QueueLootAction(WorldOpcode.CmsgLootMoney, []);
                queued = true;
            }

            if (queued && walking)
            {
                walkTicks++;
                drained |= ai.QueuedLootActions == 0;
            }

            return queued && (!walking && walkTicks > 0 || siege.Step > stepAtPush);
        });

        async Task<string> Story() => $"done={done} queued={queued} drained={drained} walkTicks={walkTicks} step={siege.Step}/{stepAtPush} {raid.LastFacts}" + Environment.NewLine + await world.TraceAsync();
        if (!queued) Assert.Fail("the probe never fired: " + await Story());
        if (walkTicks < 3) Assert.Fail("the leader was not seen walking long enough: " + await Story());
        if (drained) Assert.Fail("the queued loot was sent while the pushing leader was walking: " + await Story());
    }

    [Fact]
    public async Task APushingLeaderOffItsWaypoint_HurtAndOutOfCombat_WalksOnInsteadOfResting_NoDenial()
    {
        GroupTestWorld world = await GroupTestWorld.StartAsync([], null,
            services: c =>
            {
                if (c.LastOrDefault(d => d.ServiceType == typeof(ArcaneCore.Kernel.Items.IItemTemplateSource))?.ImplementationInstance is ArcaneCore.World.Tests.Items.InMemoryItemTemplateSource items)
                    items.Templates.Add(new ArcaneCore.Kernel.Items.ItemTemplate { Entry = WeakFoodItem, Name = "weak food", Class = 0, InventoryType = 0, Stackable = 20,
                        Spells = [new ArcaneCore.Kernel.Items.ItemSpell(WeakFoodSpell, 0, -1, 0, 0, 0, 0)] });
            },
            extraCreatures: [Guard(GuardA, 45f, 2f, health: 600), Guard(GuardB, 46f, -2f, health: 600), Guard(Objective, 600f, 0f, health: 120)]);
        await using GroupTestWorld _ = world;
        await world.OnWorldAsync(() =>
        {
            // A food whose regeneration is far too weak to clear the rest threshold in the 20 s a rest lasts.
            var spells = world.World.Services.GetRequiredService<ArcaneCore.World.Spells.SpellFeature>();
            spells.System.Store = new ArcaneCore.Game.Spells.SpellStore([.. spells.System.Store.All, new ArcaneCore.Game.Spells.SpellInfo
            {
                Id = WeakFoodSpell, Name = "playerbot consumable", Duration = new ArcaneCore.Game.Spells.SpellDuration(60_000, 0, 60_000),
                Attributes = ArcaneCore.Game.Spells.SpellAttributes.AllowWhileSitting, AuraInterruptFlags = ArcaneCore.Game.Spells.SpellAuraInterruptFlags.StandingCancels,
                Effects = [new ArcaneCore.Game.Spells.SpellEffectInfo { Effect = ArcaneCore.Game.Spells.SpellEffectName.ApplyAura, AuraType = ArcaneCore.Game.Spells.AuraType.ModRegen,
                    BasePoints = 0, BaseDice = 1, DieSides = 1, TargetA = ArcaneCore.Game.Spells.SpellImplicitTarget.UnitCaster }, new(), new()],
            }], [], []);
            return true;
        });
        (Guid Id, string Name)[] bots = await Enlist(world, 4f, 150f, 200f, 250f);
        PlayerbotSiege siege = await Start(world, bots, raids: 1);
        PlayerbotSiegeRaid raid = siege.Raids[0];
        bool food = await world.OnWorldAsync(() => Leader(world, raid).Inventory.AddItem(WeakFoodItem, 10, out ArcaneCore.Game.Items.Item? _) == ArcaneCore.Game.Items.InventoryResult.Ok);
        Assert.True(food, "the food was not added");

        int stepAtPush = -1;
        bool hurt = false, sat = false, outOfCombat = false;
        var trail = new List<string>();
        uint began = Now(world);
        bool done = await world.RunUntilAsync(PlayerbotSiegeCommander.StalemateMs + 200_000, () =>
        {
            TopUpGuards(world, raid);
            Player leader = Leader(world, raid);
            // The raid is kept in combat and healthy; the leader is left as it is once it is hurt.
            foreach (Player member in Living(world, raid).Where(p => p.Guid != leader.Guid || !hurt)) member.Health = member.MaxHealth;
            if (hurt) foreach (Player member in Living(world, raid).Where(p => p.Guid != leader.Guid)) member.Map!.Combat.SetInCombatState(member, 5_500); // the raid fights on, the leader does not
            if (raid.Push == PlayerbotSiegePush.Stalemate && stepAtPush < 0) stepAtPush = siege.Step;
            if (stepAtPush < 0) return false;
            if (hurt && unchecked(Now(world) - began) / 1_000 >= trail.Count)
                trail.Add($"+{unchecked(Now(world) - began)}ms step={siege.Step} push={raid.Push} denied={raid.PushDeniedStep} atWp={raid.AtWaypoint} x={leader.X - Origin.X:F1} hp={100 * leader.Health / Math.Max(1, leader.MaxHealth)}% sit={leader.StandState == StandState.Sit} combat={leader.Combat.IsInCombat}");
            if (!hurt && !raid.AtWaypoint && leader.X - Origin.X > 80f && leader.Map?.FindUpdater<CreatureMapSystem>() is { } system)
            {
                // Mid-walk its attackers are gone (they evade) and it is left at 30 % health, out of combat, while the raid fights on.
                foreach (Creature guard in system.Creatures.Where(g => g.Entry is GuardA or GuardB && ReferenceEquals(g.Combat.Victim, leader))) system.EnterEvadeMode(guard);
                leader.Map.Combat.CombatStop(leader);
                leader.Health = leader.MaxHealth * 30 / 100;
                hurt = true;
            }

            if (hurt)
            {
                sat |= leader.StandState == StandState.Sit && !raid.AtWaypoint; // at the waypoint it waits and may rest
                outOfCombat |= !leader.Combat.IsInCombat;
            }

            return siege.Step > stepAtPush || raid.PushDeniedStep == stepAtPush || raid.Retreats > 0;
        });

        string story = $"done={done} hurt={hurt} outOfCombat={outOfCombat} sat={sat} step={siege.Step}/{stepAtPush} denied={raid.PushDeniedStep} retreats={raid.Retreats}" + Environment.NewLine + string.Join(Environment.NewLine, trail);
        Assert.True(hurt, "the leader was never hurt mid-walk: " + story);
        Assert.False(sat, "the pushing leader sat down to rest away from its waypoint (it may rest at the waypoint, where it waits): " + story);
        Assert.True(done && siege.Step > stepAtPush, "the step did not move: " + story);
        Assert.NotEqual(stepAtPush, raid.PushDeniedStep);
    }

    [Fact]
    public async Task TheNoProgressBound_InTheWorld_WithdrawsTheStandingPush_AndGrantsNoOtherForTheStep()
    {
        (GroupTestWorld world, PlayerbotSiege siege, PlayerbotSiegeRaid raid) = await StalemateFixture();
        await using GroupTestWorld _ = world;
        Assert.True(await world.RunUntilAsync(PlayerbotSiegeCommander.StalemateMs + 60_000, () =>
        {
            TopUpGuards(world, raid);
            TopUpMembers(world, raid);
            return raid.Push == PlayerbotSiegePush.Stalemate;
        }), "the raid never pushed through: " + raid.LastFacts + Environment.NewLine + await world.TraceAsync());

        // The leader can no longer plan a way (the maps the bots may enter exclude this one): it stands, and the push gets it nowhere.
        int step = siege.Step;
        uint[] allowed = world.Options.AllowedMaps;
        world.Options.AllowedMaps = [1];
        float movedFrom = Leader(world, raid).X;
        bool withdrawn = await world.RunUntilAsync(PlayerbotSiegeCommander.PushStallMs + 10_000, () =>
        {
            TopUpGuards(world, raid);
            TopUpMembers(world, raid);
            return raid.PushDeniedStep == step && raid.Push == PlayerbotSiegePush.None;
        });
        Assert.True(withdrawn, $"the push was not withdrawn (step {siege.Step}, push {raid.Push}, denied {raid.PushDeniedStep}, best {raid.PushBestYards:F1}, leader moved {Leader(world, raid).X - movedFrom:F1})" + Environment.NewLine + await world.TraceAsync());
        Assert.Equal(PlayerbotSiegePush.None, raid.PushInForce);

        // Back at the baseline (Assault), and no push is granted again at this step, though the fight has lasted longer than StalemateMs.
        var regrants = new List<string>();
        bool assaulted = false;
        await world.RunUntilAsync(20_000, () =>
        {
            TopUpGuards(world, raid);
            TopUpMembers(world, raid);
            assaulted |= raid.Order == PlayerbotSiegeOrder.Assault;
            if (raid.Push != PlayerbotSiegePush.None || raid.PushInForce != PlayerbotSiegePush.None || siege.Step != step)
                regrants.Add($"at {Now(world)}: push {raid.Push}, in force {raid.PushInForce}, step {siege.Step}");
            return false;
        });
        Assert.True(assaulted, "the raid did not fall back to Assault: " + raid.LastFacts);
        Assert.Empty(regrants);
        Assert.True(raid.LastFacts.MsInCombatAtStep >= PlayerbotSiegeCommander.StalemateMs, "the fight has lasted longer than a stalemate: " + raid.LastFacts);
        Assert.Contains("push=denied", raid.Detail);
        world.Options.AllowedMaps = allowed;
    }
}
