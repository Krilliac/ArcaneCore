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
/// A wipe survivor giving up on, or retrying, the exit walk. Split from the former <c>PlayerbotRunClearWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotRunClearWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotRunClearWipeSurvivorWorldTests : PlayerbotRunClearWorldTestBase
{
    /// <summary>
    /// A death restarts the exit give-up: a wipe survivor that gave up on the exit (it stood on an exit that did not fire), dies and is revived
    /// while the group is still wiped (another survivor keeps the wipe going) stands somewhere new, so when the trash engages it again it walks to
    /// the exit again (wipe:run-exit) instead of keeping the give-up from before its death.
    /// </summary>
    [Fact]
    public async Task AWipeSurvivorThatGaveUpOnTheExit_DiesAndIsRevivedStillWiped_TriesTheExitAgain()
    {
        await using GroupTestWorld world = await StartAsync();
        (Guid Id, string Name)[] bots = await AddParty(world, resurrection: false);
        PlayerbotGroupCoordinator.BotGroup group = await StartRun(world, bots);
        PlayerbotRunMachine run = group.Run!;
        Assert.True(await world.RunUntilAsync(180_000, () => bots.All(b => world.Player(b.Id).MapId == GroupRfcContent.Map)), await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(120_000, () =>
        {
            Creature[] trash = Pack(world, bots[0].Id);
            return trash.Length == 3 && trash.Count(c => !c.IsAlive) >= 1 && trash.Any(c => c.IsAlive);
        }), await world.TraceAsync());
        (Guid Id, string Name) member = bots[4], holder = bots[3];
        PlayerbotGroupAI ai = world.Coordinator.FindAI(member.Id)!;
        // Both survivors stand engaged on the stuck exit: the holder gives up too and stays in combat, so the wipe does not settle meanwhile.
        // The member takes the harder hits, so the trash goes for it (it must stay engaged until it gives up).
        await StandEngagedOnTheStuckExit(world, holder.Id);
        await StandEngagedOnTheStuckExit(world, member.Id, damage: 5);
        await world.OnWorldAsync(() =>
        {
            Creature attacker = Pack(world, bots[0].Id).First(c => c.IsAlive);
            foreach ((Guid id, string _) in bots.Take(3))
            {
                Player victim = world.Player(id);
                victim.Map!.Combat.DealDamage(attacker, victim, victim.Health, direct: false);
                Assert.False(victim.IsAlive);
            }

            return true;
        });
        Assert.True(await world.RunUntilAsync(5_000, () => run.Step == PlayerbotRunStep.Wiped), await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(10_000, () => group.State == PlayerbotGroupState.Wiped && ai.WipeExitFails >= PlayerbotGroupAI.WipeExitGiveUp),
            ai.Step + await world.TraceAsync());
        // Its next think is past the exit walk, so a wipe:run-exit step from here on is a fresh one.
        Assert.True(await world.RunUntilAsync(5_000, () => !ai.Step.StartsWith("wipe:", StringComparison.Ordinal)), ai.Step + await world.TraceAsync());

        await world.OnWorldAsync(() =>
        {
            Player dying = world.Player(member.Id);
            dying.Map!.Combat.DealDamage(Pack(world, member.Id).First(c => c.IsAlive), dying, dying.Health, direct: false);
            Assert.False(dying.IsAlive);
            return true;
        });
        // One tick for the bot to see its death (the group AI's update runs every tick), too short for the think that releases its spirit.
        await world.RunAsync(1);
        string notWiped = await world.OnWorldAsync(() =>
        {
            if (group.State != PlayerbotGroupState.Wiped) return $"the wipe settled while the member was dead: {group.State}";
            Player revived = world.Player(member.Id);
            if (revived.IsAlive || revived.MapId != GroupRfcContent.Map) return $"the member is not dead inside: alive={revived.IsAlive}, map {revived.MapId}, step {ai.Step}";
            revived.Map!.Combat.ResurrectPlayer(revived, 1f, applySickness: false);
            Assert.True(revived.IsAlive);
            // Revived on the exit point whose trigger still does not fire, and engaged again (in the same tick, before the bot moves).
            StandEngagedOnTheStuckExitNow(world, member.Id, damage: 5);
            return "";
        });
        Assert.True(notWiped.Length == 0, notWiped + await world.TraceAsync());

        var steps = new List<string>();
        await world.RunUntilAsync(5_000, () =>
        {
            Player p = world.Player(member.Id);
            string step = $"{ai.Step} ({group.State}, map {p.MapId}, alive={p.IsAlive}, combat={p.Combat.IsInCombat}, fails={ai.WipeExitFails})";
            if (steps.Count == 0 || steps[^1] != step) steps.Add(step);
            return false;
        });
        Assert.True(steps.Any(s => s.StartsWith("wipe:run-exit ", StringComparison.Ordinal)), string.Join(" > ", steps) + await world.TraceAsync());
    }

    /// <summary>
    /// A give-up holds for the phase it was reached in: a survivor that gave up on the exit while the group was wiped (it stood on an exit that
    /// did not fire) and lived through the wipe walks to the exit again (retry:run-exit) when the trash engages it once the retry regroup is on,
    /// instead of keeping the wipe's give-up.
    /// </summary>
    [Fact]
    public async Task AWipeSurvivorThatGaveUpOnTheExit_TriesItAgainInTheRetryRegroup()
    {
        await using GroupTestWorld world = await StartAsync();
        (Guid Id, string Name)[] bots = await AddParty(world, resurrection: false);
        PlayerbotGroupCoordinator.BotGroup group = await StartRun(world, bots);
        PlayerbotRunMachine run = group.Run!;
        Assert.True(await world.RunUntilAsync(180_000, () => bots.All(b => world.Player(b.Id).MapId == GroupRfcContent.Map)), await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(120_000, () =>
        {
            Creature[] trash = Pack(world, bots[0].Id);
            return trash.Length == 3 && trash.Count(c => !c.IsAlive) >= 1 && trash.Any(c => c.IsAlive);
        }), await world.TraceAsync());
        (Guid Id, string Name) survivor = bots[4];
        PlayerbotGroupAI ai = world.Coordinator.FindAI(survivor.Id)!;
        await StandEngagedOnTheStuckExit(world, survivor.Id);
        await world.OnWorldAsync(() =>
        {
            Creature attacker = Pack(world, bots[0].Id).First(c => c.IsAlive);
            foreach ((Guid id, string _) in bots.Take(4))
            {
                Player victim = world.Player(id);
                victim.Map!.Combat.DealDamage(attacker, victim, victim.Health, direct: false);
                Assert.False(victim.IsAlive);
            }

            return true;
        });
        Assert.True(await world.RunUntilAsync(5_000, () => run.Step == PlayerbotRunStep.Wiped), await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(10_000, () => group.State == PlayerbotGroupState.Wiped && ai.WipeExitFails >= PlayerbotGroupAI.WipeExitGiveUp),
            ai.Step + await world.TraceAsync());

        // The wipe ends: the survivor finishes the pack (nothing is left to hold it in combat), and it waits at the landing for the others.
        await world.OnWorldAsync(() =>
        {
            Player alive = world.Player(survivor.Id);
            MapCombat combat = alive.Map!.Combat;
            foreach (Creature c in Pack(world, survivor.Id).Where(c => c.IsAlive)) combat.DealDamage(alive, c, c.Health, direct: false);
            Assert.All(Pack(world, survivor.Id), c => Assert.False(c.IsAlive));
            combat.CombatStop(alive);
            alive.Relocate(0.797643f, -8.23429f, -15.5288f, 0f, world.World.Host.World.NowMs);
            alive.Health = alive.MaxHealth;
            return true;
        });
        var path = new List<string>();
        bool died = false;
        Assert.True(await world.RunUntilAsync(180_000, () =>
        {
            died |= !world.Player(survivor.Id).IsAlive;
            string at = $"{group.State}/{ai.Step}/map{world.Player(survivor.Id).MapId}";
            if (path.Count == 0 || path[^1] != at) path.Add(at);
            return group.State == PlayerbotGroupState.Gathering && run.Step == PlayerbotRunStep.Retry;
        }), string.Join(" > ", path) + await world.TraceAsync());
        Assert.False(died, "the survivor died before the retry regroup (a death resets the give-up too): " + string.Join(" > ", path));
        // A creature of the instance that is still up engages it inside (the pack is dead, so the nearest one left, a boss in this test
        // content, hits it from wherever it is).
        string left = await world.OnWorldAsync(() =>
        {
            Player alive = world.Player(survivor.Id);
            if (!alive.IsAlive || alive.MapId != GroupRfcContent.Map) return "the survivor is not alive inside: " + string.Join(" > ", path);
            Creature? other = alive.Map!.FindUpdater<CreatureMapSystem>()?.Creatures.Where(c => c.IsAlive)
                .OrderBy(c => Vector3.Distance(new Vector3(c.X, c.Y, c.Z), new Vector3(alive.X, alive.Y, alive.Z))).FirstOrDefault();
            if (other is null) return "no live creature left in the instance";
            alive.Map!.Combat.DealDamage(other, alive, 1, direct: false);
            return alive.Combat.IsInCombat ? "" : $"the hit from {other.Entry} did not put the survivor in combat";
        });
        Assert.True(left.Length == 0, left);

        var steps = new List<string>();
        await world.RunUntilAsync(5_000, () =>
        {
            Player p = world.Player(survivor.Id);
            string step = $"{ai.Step} ({group.State}/{run.Step}, map {p.MapId}, alive={p.IsAlive}, combat={p.Combat.IsInCombat}, fails={ai.WipeExitFails})";
            if (steps.Count == 0 || steps[^1] != step) steps.Add(step);
            return false;
        });
        Assert.True(steps.Any(s => s.StartsWith("retry:run-exit ", StringComparison.Ordinal)), string.Join(" > ", steps) + await world.TraceAsync());
    }

    /// <summary>
    /// Dungeon trash never leashes (CreatureMapSystem.IsOutOfThreatArea is false in an instanceable map), so a wiped group's leash-based retreat
    /// can run in a line that misses the exit trigger and be chased for good. The survivors here start south of the exit box, where the way
    /// straight away from the pack does not cross it: they must walk out through the exit instead.
    /// </summary>
    [Fact]
    public async Task AWipeInsideTheInstance_SurvivorsLeaveThroughTheExit_EvenWhenTheWayAwayMissesIt()
    {
        await using GroupTestWorld world = await StartAsync();
        (Guid Id, string Name)[] bots = await AddParty(world, resurrection: true);
        PlayerbotGroupCoordinator.BotGroup group = await StartRun(world, bots);
        PlayerbotRunMachine run = group.Run!;
        Assert.True(await world.RunUntilAsync(180_000, () => bots.All(b => world.Player(b.Id).MapId == GroupRfcContent.Map)), await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(120_000, () =>
        {
            Creature[] trash = Pack(world, bots[0].Id);
            return trash.Length == 3 && trash.Count(c => !c.IsAlive) >= 1 && trash.Any(c => c.IsAlive);
        }), await world.TraceAsync());
        (Guid Id, string Name)[] survivors = [.. bots.Skip(3)];
        await world.OnWorldAsync(() =>
        {
            Creature[] live = [.. Pack(world, bots[0].Id).Where(c => c.IsAlive)];
            foreach ((Guid id, string _) in survivors)
            {
                Player survivor = world.Player(id);
                survivor.Relocate(-2f, -16f, -15.5288f, 0f, world.World.Host.World.NowMs);
                foreach (Creature c in live) survivor.Map!.Combat.DealDamage(c, survivor, 1, direct: false);
                Assert.True(survivor.IsAlive);
            }

            foreach ((Guid id, string _) in bots.Take(3))
            {
                Player victim = world.Player(id);
                victim.Map!.Combat.DealDamage(live[0], victim, victim.Health, direct: false);
                Assert.False(victim.IsAlive);
            }

            return true;
        });
        Assert.True(await world.RunUntilAsync(5_000, () => run.Step == PlayerbotRunStep.Wiped), await world.TraceAsync());

        // The exit is a few yards away and the trash hits for 2-3 every 2 s: both survivors are out, alive and out of combat, long before they
        // could die (a survivor chased east by the leash retreat is still inside, in combat, at this point).
        Assert.True(await world.RunUntilPacedAsync(() => survivors.All(b => world.Player(b.Id) is { IsAlive: true, MapId: 1 } p && !p.Combat.IsInCombat), 30_000), await world.TraceAsync());
        // Retry is a transient step (it lasts through the regroup's gathering, about 1.3 s of game time here, then Enter and Clear): when the survivors take a few seconds to get out, the run has
        // already passed through it by the time this wait starts, so wait for the run to leave Wiped (Retry is the only way out of it) and require it
        // to have done so on the single wipe rather than by giving up.
        Assert.True(await world.RunUntilPacedAsync(() => run.Step != PlayerbotRunStep.Wiped, 180_000, TimeSpan.FromSeconds(240)), await world.TraceAsync());
        Assert.True(await world.OnWorldAsync(() => run is { Wipes: 1, Step: not PlayerbotRunStep.GiveUp }), await world.TraceAsync());
    }

    /// <summary>
    /// A survivor that stands on the exit point while its trigger does not fire (the bot never reports a move onto it) is within WalkTo's arrival
    /// distance, which used to reset the give-up count on every think and hold it there for good: it must give up on the exit and retreat.
    /// </summary>
    [Trait("Category", "Timing")]
    [Fact]
    public async Task AWipeSurvivorStandingOnAnExitThatNeverFires_GivesUpOnTheExitAndRetreats()
    {
        await using GroupTestWorld world = await StartAsync();
        (Guid Id, string Name)[] bots = await AddParty(world, resurrection: true);
        PlayerbotGroupCoordinator.BotGroup group = await StartRun(world, bots);
        PlayerbotRunMachine run = group.Run!;
        Assert.True(await world.RunUntilAsync(180_000, () => bots.All(b => world.Player(b.Id).MapId == GroupRfcContent.Map)), await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(120_000, () =>
        {
            Creature[] trash = Pack(world, bots[0].Id);
            return trash.Length == 3 && trash.Count(c => !c.IsAlive) >= 1 && trash.Any(c => c.IsAlive);
        }), await world.TraceAsync());
        (Guid Id, string Name) survivor = bots[4];
        await world.OnWorldAsync(() =>
        {
            Creature[] live = [.. Pack(world, bots[0].Id).Where(c => c.IsAlive)];
            Player standing = world.Player(survivor.Id);
            standing.Relocate(2.58019f, -0.013587f, -13.3668f, 0f, world.World.Host.World.NowMs);
            // The bot's tracker has already seen it enter the exit volume (without sending the trigger), so standing still fires nothing.
            PlayerbotTriggerTracker tracker = PlayerbotAreaTriggers.TrackerOf(standing);
            var centre = new Vector3(standing.X, standing.Y, standing.Z);
            tracker.Observe(standing.Map!, standing.MapId, centre, tracker.Near(world.World.Host.World, standing.Map!, centre), fire: false);
            Assert.Contains(GroupRfcContent.Exit, tracker.Inside);
            foreach (Creature c in live) standing.Map!.Combat.DealDamage(c, standing, 1, direct: false);
            Assert.True(standing.IsAlive);
            foreach ((Guid id, string _) in bots.Take(3))
            {
                Player victim = world.Player(id);
                victim.Map!.Combat.DealDamage(live[0], victim, victim.Health, direct: false);
                Assert.False(victim.IsAlive);
            }

            return true;
        });
        Assert.True(await world.RunUntilAsync(5_000, () => run.Step == PlayerbotRunStep.Wiped), await world.TraceAsync());

        var steps = new List<string>();
        Assert.True(await world.RunUntilAsync(30_000, () =>
        {
            string step = world.Coordinator.FindAI(survivor.Id)!.Step;
            if (steps.Count == 0 || steps[^1] != step) steps.Add(step);
            return step == "retreat";
        }), string.Join(" > ", steps) + await world.TraceAsync());
    }
}
