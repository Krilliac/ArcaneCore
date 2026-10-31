using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Instances;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Groups;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// A wiped run regrouping outside the exit before the retry. Split from the former <c>PlayerbotRunClearWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotRunClearWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotRunClearRetryRegroupWorldTests : PlayerbotRunClearWorldTestBase
{
    /// <summary>
    /// After a wipe the released bots are revived at the instance landing at about half health while the surviving trash is still a few yards
    /// away, and the group then regroups for up to GatherCatchUpMs. Holding inside for that time left the revived bots to be pulled and fought
    /// at half health with no wipe check running; on the Retry step the leader must walk back out through the exit instead (the members follow),
    /// so the group re-enters at full strength with the single wipe it already had.
    /// </summary>
    [Fact]
    public async Task AWipedRunWhoseLeaderRevivesInside_RegroupsOutsideTheExit_BeforeTheRetry()
    {
        await using GroupTestWorld world = await StartAsync();
        ((Guid Id, string Name)[] bots, PlayerbotGroupCoordinator.BotGroup group, PlayerbotRunMachine run) = await WipeIntoTheRetryRegroup(world);

        // The group regroups on the Retry step with the leader revived back inside: the moment the run's own state says so, the surviving trash
        // hits one of the revived members, as it does when they come back within its reach. Each creature then attacks that member (vmangos
        // Creature::OnEnterCombat -> AttackedBy) and keeps at it, as dungeon trash does not leash: the members in combat walk out through the
        // exit with the leader instead of fighting it there.
        await world.OnWorldAsync(() =>
        {
            Player member = world.Player(bots[1].Id);
            foreach (Creature c in Pack(world, bots[0].Id).Where(c => c.IsAlive)) member.Map!.Combat.DealDamage(c, member, 1, direct: false);
            return true;
        });

        // Well inside GatherCatchUpMs (60 s): everyone is outside the instance, alive and out of combat, and the run has not re-entered yet.
        // While the leader is still inside the group is not gathered (even once the members alive have come to it), so it never travels or re-enters from there.
        bool leftEarly = false, stepOk = true;
        var steps = new HashSet<string>();
        Assert.True(await world.RunUntilAsync(20_000, () =>
        {
            foreach ((Guid id, string _) in bots) steps.Add(world.Coordinator.FindAI(id)!.Step);
            if (world.Player(bots[0].Id).MapId == GroupRfcContent.Map) leftEarly |= group.State != PlayerbotGroupState.Gathering;
            // The last member out completes the gathering, so the run may already have stepped on to Enter in that same tick; either way it has not cleared.
            stepOk &= run.Step is PlayerbotRunStep.Retry or PlayerbotRunStep.Enter;
            return bots.All(b => world.Player(b.Id) is { IsAlive: true, MapId: KalimdorMap } p && !p.Combat.IsInCombat);
        }), await world.TraceAsync());
        Assert.False(leftEarly, "the group left Gathering while its leader was still inside the instance");
        Assert.True(stepOk, "the run moved past Retry/Enter before the group was outside");
        Assert.Contains("retry:run-exit", steps); // the engaged member walked out through the retry regroup's exit walk, not by winning the fight
        Assert.True(await world.RunUntilAsync(180_000, () => run.Step == PlayerbotRunStep.Clear
            && bots.All(b => world.Player(b.Id).MapId == GroupRfcContent.Map && world.Player(b.Id).IsAlive)), await world.TraceAsync());
        Assert.Equal(1, run.Wipes);
    }

    /// <summary>
    /// A member in the retry regroup that the trash engages while it stands on an exit whose trigger does not fire fails its exit walk on every
    /// think (its step stays retry:run-exit, as the walk plans while it stands on the point; the stand counts as the failure): past WipeExitGiveUp
    /// it goes on with the regroup's own think (it fights back, or with no target follows its leader) for the rest of the regroup, instead of
    /// turning back to the exit walk every other think (the give-up count is not reset while the regroup lasts; a death would reset it, so the
    /// member starts at full health and must not die here).
    /// </summary>
    [Fact]
    public async Task ARetryRegroupMemberWhoseExitNeverFires_GivesUpOnTheExitWalk_WithoutTurningBackToIt()
    {
        await using GroupTestWorld world = await StartAsync();
        ((Guid Id, string Name)[] bots, PlayerbotGroupCoordinator.BotGroup group, PlayerbotRunMachine run) = await WipeIntoTheRetryRegroup(world);
        (Guid Id, string Name) member = bots[1];
        await StandEngagedOnTheStuckExit(world, member.Id);
        (List<string> steps, bool died) = await RegroupSteps(world, group, run, member.Id, 10_000);
        string seen = string.Join(" > ", steps);
        Assert.False(died, "the member died in the window: " + seen);
        int firstOther = steps.FindIndex(s => !s.StartsWith("retry:", StringComparison.Ordinal));
        Assert.True(steps.Count > 0 && steps[0].StartsWith("retry:", StringComparison.Ordinal), seen + await world.TraceAsync());
        Assert.True(firstOther > 0, "the member never gave up on the exit: " + seen + await world.TraceAsync());
        Assert.DoesNotContain(steps.Skip(firstOther), s => s.StartsWith("retry:", StringComparison.Ordinal));
    }
}
