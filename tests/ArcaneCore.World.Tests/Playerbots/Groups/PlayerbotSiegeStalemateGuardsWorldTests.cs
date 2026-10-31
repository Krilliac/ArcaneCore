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
/// The stalemate-with-guards run (one theory over many rows). Split from the former <c>PlayerbotSiegeWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotSiegeWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotSiegeStalemateGuardsWorldTests(ITestOutputHelper output) : PlayerbotSiegeWorldTestBase
{
    [Theory]
    [MemberData(nameof(StalemateRuns))]
    public async Task AStalemateWithGuardsTheRaidCannotKill_TheLeaderWalksOnAndTheStepMoves_OrTheRaidRetreatsAsDesigned_AndNoLeaderDies(int run)
    {
        (GroupTestWorld world, PlayerbotSiege siege, PlayerbotSiegeRaid raid) = await StalemateFixture();
        await using GroupTestWorld _ = world;
        Guid leaderBot = raid.Group!.LeaderBotId;

        // Nothing is killed or dealt by the test: the guards stay at full health for the whole run, and nobody is topped up once the push has begun
        // (the members and the leader are left to the guards; two members may die, and the raid then retreats as designed).
        bool pushed = false;
        int stepAtPush = -1;
        uint pushSince = 0;
        float maxX = float.MinValue;
        var trail = new List<string>();
        uint began = Now(world);
        var problems = new List<string>();
        bool done = await world.RunUntilAsync(PlayerbotSiegeCommander.StalemateMs + 200_000, () =>
        {
            if (unchecked(Now(world) - began) / 5_000 >= trail.Count)
                trail.Add($"+{unchecked(Now(world) - began)}ms step={siege.Step} order={raid.Order} push={raid.Push} force={raid.PushInForce} denied={raid.PushDeniedStep} atWp={raid.AtWaypoint} x={Leader(world, raid).X - Origin.X:F1} lag={MaxLag(world, raid):F0} alive={Living(world, raid).Count} retreats={raid.Retreats}");
            TopUpGuards(world, raid);
            if (!pushed) TopUpMembers(world, raid);
            Player leader = Leader(world, raid);
            if (raid.Group.LeaderBotId != leaderBot) problems.Add($"the leader changed at {Now(world)}");
            if (!leader.IsAlive) problems.Add($"the leader died at {Now(world)} (push {raid.Push}, step {siege.Step})");
            maxX = Math.Max(maxX, leader.X - Origin.X);
            if (raid.Push == PlayerbotSiegePush.Stalemate)
            {
                if (!pushed) { pushed = true; stepAtPush = siege.Step; pushSince = Now(world); }
                if (unchecked(Now(world) - pushSince) >= 1_000 && !raid.AtWaypoint
                    && raid.Group.Members.First(m => m.BotId == leaderBot).AI?.TargetUnit is { } target)
                    problems.Add($"the leader fights {target.Guid} {unchecked(Now(world) - pushSince)} ms into the push, away from its waypoint");
            }

            return pushed && (siege.Step > stepAtPush || raid.PushDeniedStep == stepAtPush || raid.Retreats > 0);
        });

        // The story (with the world trace) is built only when something fails: the trace is a live buffer.
        async Task<string> Story() => $"run {run}: step {siege.Step} (at the push {stepAtPush}), denied {raid.PushDeniedStep}, push {raid.Push}, retreats {raid.Retreats}, leader reached x+{maxX:F1}, {raid.LastFacts}"
            + Environment.NewLine + string.Join(Environment.NewLine, trail) + Environment.NewLine + await world.TraceAsync();
        bool walkedOn = maxX > 60f && siege.Step > stepAtPush;
        bool retreated = raid.Retreats > 0;
        output.WriteLine($"STALEMATE-ROW run={run} outcome={(walkedOn ? "walked" : retreated ? "retreat" : "neither")} step={siege.Step}/{stepAtPush} maxX={maxX:F1} retreats={raid.Retreats} denied={raid.PushDeniedStep}");
        int rows = Interlocked.Increment(ref _stalemateRows);
        int retreatRows = retreated && !walkedOn ? Interlocked.Increment(ref _stalemateRetreatRows) : Volatile.Read(ref _stalemateRetreatRows);
        if (!pushed) Assert.Fail("the raid never pushed through: " + await Story());
        if (!done) Assert.Fail("the push neither moved the step nor ended in a retreat: " + await Story());
        Assert.Empty(problems);
        // Never a denial: the leader walks on (it passed 60 yards from the start and the step moved), or the raid lost members and retreated, as designed.
        Assert.NotEqual(stepAtPush, raid.PushDeniedStep);
        if (!(walkedOn || retreated)) Assert.Fail(await Story());
        // The retreat is a designed outcome of a row, not of the test: if every row of the run retreated the leader never walked on and the test proves nothing about it.
        if (rows == StalemateRunCount() && retreatRows == rows) Assert.Fail($"every one of the {rows} rows retreated, none walked on: " + await Story());
    }
}
