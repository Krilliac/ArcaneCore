using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Risk;

/// <summary>
/// Risk against reward on the real world (<see cref="RiskTestWorld"/>: manual clock, flat ground, the server's own creature aggro,
/// assistance, chase and leash), one think at a time. Before <see cref="PlayerbotRisk"/>, a bot took the nearest creature at most one
/// level above it whatever stood beside it, and fought every fight to the end.
/// </summary>
public sealed class PlayerbotRiskWorldTests
{
    /// <summary>
    /// Three same-level creatures stand together 20 yards east, one alone 30 yards north. The nearest is a pack member (the old
    /// choice); the bot weighs it as a fight against three (the others answer its assistance call and stand inside their aggro
    /// radius of where the bot would fight), passes, and pulls the lone one, which it then fights without the pack joining.
    /// </summary>
    [Fact]
    public async Task ABot_DeclinesAPullIntoAPackOfThree_AndTakesTheLoneOne()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        CreatureTemplate template = RiskTestWorld.Template(991001);
        (Creature[] pack, Creature lone) = await world.OnWorldAsync(() => (
            new[] { world.Spawn(template, 20, 0), world.Spawn(template, 23, 3), world.Spawn(template, 23, -3) },
            world.Spawn(template, 0, 30)));
        await world.SeeAsync([.. pack, lone]);

        await world.ThinkAsync();
        Assert.Same(lone, await world.OnWorldAsync(() => world.Brain.InspectionTarget));
        PlayerbotEngagement packVerdict = await world.OnWorldAsync(() => world.Brain.Risk.Assess(world.Player, pack[0], false, out _));
        Assert.Equal(PlayerbotEngageDecision.Avoid, packVerdict.Decision);
        Assert.Equal("pack-of-3", packVerdict.Reason);
        Assert.Contains("decision=engage", await world.OnWorldAsync(() => world.Brain.RiskReport));

        Assert.True(await world.ThinkUntilAsync(30_000, () => ReferenceEquals(world.Player.Combat.Victim, lone)), "the bot never attacked the lone creature");
        Assert.All(pack, member => Assert.False(member.Combat.IsInCombat, "a pack member joined"));
    }

    /// <summary>
    /// An elite three levels above the bot is passed over as an ordinary target. As the objective of a quest it is taken when the
    /// estimate says the bot can solo it, and still passed over when it cannot; the old choice never looked above level + 1.
    /// </summary>
    [Fact]
    public async Task AnEliteThreeLevelsAbove_IsAvoided_UnlessItIsAQuestObjectiveTheBotCanSolo()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        Creature weak = await world.OnWorldAsync(() => world.Spawn(RiskTestWorld.Template(991011, level: 4, health: 20, minDamage: 1, maxDamage: 1, rank: 1), 25, 0));
        Creature strong = await world.OnWorldAsync(() => world.Spawn(RiskTestWorld.Template(991012, level: 4, health: 4000, minDamage: 30, maxDamage: 40, rank: 1), -25, 0));
        await world.SeeAsync(weak, strong);

        await world.OnWorldAsync(() =>
        {
            PlayerbotRisk risk = world.Brain.Risk;
            Assert.Null(risk.ChooseTarget(world.Player, 0, _ => false, questObjective: false));
            Assert.Equal("elite-above", risk.LastEngagement?.Reason);

            // The old choice never looked above level + 1, objective or not.
            Assert.Null(PlayerbotBrain.FindTarget(world.Player, weak.Entry));
            Creature? chosen = risk.ChooseTarget(world.Player, weak.Entry, _ => false, questObjective: true);
            Assert.True(ReferenceEquals(weak, chosen), $"{risk.LastEngagement} level {weak.Level} health {weak.Health} bot {world.Player.Level} {world.Player.Health}");
            Assert.Equal(PlayerbotEngageDecision.Engage, risk.LastEngagement?.Decision);

            Assert.Null(risk.ChooseTarget(world.Player, strong.Entry, _ => false, questObjective: true));
            Assert.Equal("elite", risk.LastEngagement?.Reason);
            return true;
        });
    }

    /// <summary>
    /// The bot walks 80 yards to a lone creature and fights it; two more of its kind come to its call and the fight turns. The bot
    /// retreats back the way it came, out past the creatures' leash (vmangos IsOutOfThreatArea: 50 yards from where the fight began),
    /// the creatures evade home, and the bot recovers before it pulls again. It does not pull the creature it fled from while it
    /// remembers it (<see cref="PlayerbotRiskOptions.DangerMemorySeconds"/>), and pulls it again afterwards.
    /// </summary>
    [Fact]
    public async Task ABotLosingAFight_RetreatsPastTheLeash_TheCreaturesEvade_ItRecovers_AndLaterReengages()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync(options => options.Risk.DangerMemorySeconds = 20);
        // Slow runners (4 yards a second against the bot's 7), so the chase cannot end the bot before the leash does.
        CreatureTemplate template = RiskTestWorld.Template(991021, health: 60, minDamage: 3, maxDamage: 4, runSpeed: 0.6f);
        Vector3 start = await world.OnWorldAsync(() => new Vector3(world.Player.X, world.Player.Y, world.Player.Z));
        Creature first = await world.OnWorldAsync(() => world.Spawn(template, 0, -80));
        await world.SeeAsync(first);

        Assert.True(await world.ThinkUntilAsync(30_000, () => first.Combat.IsInCombat), "the bot never pulled the creature: "
            + await world.OnWorldAsync(() => $"{world.Brain.RiskReport} goal {world.Brain.Goal} target {world.Brain.InspectionTarget?.Entry} at {RiskTestWorld.Distance(world.Player, new Vector3(first.X, first.Y, first.Z)):F1}"));
        Creature[] adds = await world.OnWorldAsync(() =>
        {
            // Two more of its kind join (as an assistance call or a patrol would bring them).
            // They come from the creature's side, beyond it.
            Creature[] spawned = [world.Creatures.SpawnTemporary(template, first.X + 3, first.Y - 3, first.Z, 0),
                world.Creatures.SpawnTemporary(template, first.X - 3, first.Y - 3, first.Z, 0)];
            foreach (Creature add in spawned) world.Engage(add);
            return spawned;
        });

        Vector3 fightStart = await world.OnWorldAsync(() => new Vector3(first.Home.X, first.Home.Y, first.Home.Z));
        bool retreated = await world.ThinkUntilAsync(30_000, () => world.Brain.Risk.Retreat.Active);
        Assert.True(retreated, "the bot fought on against three: " + await world.OnWorldAsync(() => world.Brain.RiskReport));
        Assert.Equal(PlayerbotGoalKind.Retreat, await world.OnWorldAsync(() => world.Brain.Goal));
        string retreatStart = await world.OnWorldAsync(() => $"goal {world.Brain.Risk.Retreat.Goal} crumbs {world.Brain.Risk.Breadcrumbs.Points.Count} first {(world.Brain.Risk.Breadcrumbs.Points.Count > 0 ? world.Brain.Risk.Breadcrumbs.Points[0] : default)} bot {world.Player.Y:F1}");
        Assert.StartsWith("retreat reason=losing", await world.OnWorldAsync(() => world.Brain.RiskReport));

        bool evaded = false;
        Assert.True(await world.ThinkUntilAsync(60_000, () => !world.Brain.Risk.Retreat.Active,
            () => evaded |= first.IsInEvadeMode || adds.Any(a => a.IsInEvadeMode)), "the retreat did not end");
        Assert.True("safe" == await world.OnWorldAsync(() => world.Brain.Risk.Retreat.Outcome), await world.OnWorldAsync(() =>
            $"{world.Brain.Risk.Retreat.Outcome} at {RiskTestWorld.Distance(world.Player, fightStart):F0} from the fight; "
            + string.Join("; ", new[] { first }.Concat(adds).Select(c => $"{c.Guid.Low}: alive {c.IsAlive} combat {c.Combat.IsInCombat} evade {c.IsInEvadeMode} victim {c.Combat.Victim?.Guid} at {RiskTestWorld.Distance(c, fightStart):F0}/{RiskTestWorld.Distance(world.Player, new Vector3(c.X, c.Y, c.Z)):F0}"))
            + $"; attackers {world.Player.Combat.Attackers.Count()} threatened {world.Player.Combat.ThreatenedBy.Count()} outOfArea {world.Creatures.IsOutOfThreatArea(first, world.Player)}"));
        Assert.True(world.Player.IsAlive, "the bot died retreating");
        Assert.True(evaded, "no creature evaded");
        float fromFight = await world.OnWorldAsync(() => RiskTestWorld.Distance(world.Player, fightStart));
        Assert.True(fromFight > 50f, $"the bot stopped {fromFight:F0} yards from the fight, inside the leash");
        // Back the way it came: from the fight, the bot now stands on the side it walked in from.
        Vector3 here = await world.OnWorldAsync(() => new Vector3(world.Player.X, world.Player.Y, world.Player.Z));
        Assert.True(Vector3.Dot(Vector3.Normalize(here - fightStart), Vector3.Normalize(start - fightStart)) > 0.8f,
            $"the bot ran off to {here}, not back towards {start}; {retreatStart}; {world.Brain.Risk.Retreat.Trace}");

        // Recovering: it waits (no food here) rather than pulling again while hurt.
        Assert.True(await world.ThinkUntilAsync(10_000, () => world.Brain.Risk.Waiting && !world.Player.Combat.IsInCombat));
        await world.OnWorldAsync(() =>
        {
            foreach (Creature add in adds) world.Player.Map!.Combat.DealDamage(world.Player, add, add.Health, direct: false);
            world.Player.Health = world.Player.MaxHealth; // recovered
            return true;
        });
        Assert.False(await world.ThinkUntilAsync(5_000, () => first.Combat.IsInCombat), "the bot pulled the creature it fled from at once");
        Assert.True(await world.OnWorldAsync(() => world.Brain.Risk.IsRemembered(first)));
        // The memory runs out while the bot stands where its retreat ended (no thinks: it would wander off exploring meanwhile).
        await world.Host.World.AdvanceClockAsync(20_000);
        Assert.False(await world.OnWorldAsync(() => world.Brain.Risk.IsRemembered(first)));
        // The retreat ended well out of sight of the creature; the bot comes back to where it started from (as its goals would bring it).
        await world.OnWorldAsync(() =>
        {
            PlayerbotMotion.Reset(world.Player);
            world.Player.Relocate(start.X, start.Y, start.Z, world.Player.Orientation, world.Host.World.NowMs);
            return true;
        });
        await world.SeeAsync(first);
        Assert.True(await world.ThinkUntilAsync(60_000, () => first.Combat.IsInCombat && ReferenceEquals(first.Combat.Victim, world.Player)),
            "the bot never pulled the creature again after forgetting it: " + await world.OnWorldAsync(() =>
                $"{world.Brain.RiskReport} goal {world.Brain.Goal} remembered {world.Brain.Risk.IsRemembered(first)} distance {RiskTestWorld.Distance(world.Player, new Vector3(first.X, first.Y, first.Z)):F0} visible {world.Player.VisibleObjects.Contains(first.Guid)} first {first.Health}/{first.MaxHealth} evade {first.IsInEvadeMode} bot {world.Player.Health}/{world.Player.MaxHealth} dps {world.Brain.Risk.Tracker.ObservedDps}"));
    }

    /// <summary>
    /// A fight the bot is about to win is finished, though the bot is hurt below its retreat threshold: the creature at 10% health.
    /// The same bot, as hurt, against the same creature at full health, retreats (so the first half is not vacuous).
    /// </summary>
    [Fact]
    public async Task ANearlyWonFight_IsNotAbandoned_AFreshOneAtTheSameHealthIs()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        CreatureTemplate template = RiskTestWorld.Template(991031, health: 400, minDamage: 1, maxDamage: 2);
        Creature nearlyDead = await world.OnWorldAsync(() => world.Spawn(template, 2, 0));
        await world.SeeAsync(nearlyDead);
        await world.OnWorldAsync(() =>
        {
            world.Engage(nearlyDead);
            nearlyDead.Health = 40;
            world.Player.Health = world.Player.MaxHealth * 30 / 100;
            return true;
        });
        Assert.True(await world.ThinkUntilAsync(30_000, () => !nearlyDead.IsAlive || !world.Player.IsAlive || world.Brain.Risk.Retreat.Active),
            await world.OnWorldAsync(() => $"creature {nearlyDead.Health} combat {nearlyDead.Combat.IsInCombat} victim {nearlyDead.Combat.Victim?.Guid} evade {nearlyDead.IsInEvadeMode}; bot {world.Player.Health} victim {world.Player.Combat.Victim?.Guid} goal {world.Brain.Goal} {world.Brain.RiskReport}"));
        Assert.False(await world.OnWorldAsync(() => world.Brain.Risk.Retreat.Active), "the bot ran from a creature at 10%: "
            + await world.OnWorldAsync(() => $"{world.Brain.Risk.Retreat.Reason} {world.Brain.Risk.Tracker.LastVerdict} creature {nearlyDead.Health} bot {world.Player.Health}/{world.Player.MaxHealth}"));
        Assert.False(await world.OnWorldAsync(() => nearlyDead.IsAlive), "the creature survived");

        Creature fresh = await world.OnWorldAsync(() => world.Spawn(template, 2, 0));
        await world.SeeAsync(fresh);
        await world.OnWorldAsync(() =>
        {
            world.Engage(fresh);
            world.Player.Health = world.Player.MaxHealth * 30 / 100;
            return true;
        });
        Assert.True(await world.ThinkUntilAsync(30_000, () => world.Brain.Risk.Retreat.Active || !world.Player.IsAlive),
            "the hurt bot fought a fresh creature to the end");
        Assert.True(world.Player.IsAlive);
    }

    /// <summary>
    /// A bot killed on its way to a trainer sets that errand aside rather than walking the same way into the same creatures after
    /// its revive (the live replay's Dawnrover, five deaths in ten minutes to the Scourge invasion's Skeletal Soldiers on the road
    /// to Arthur the Faithful). With the risk estimate off it walks there again.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ABotKilledOnAnErrand_SetsItAside(bool riskEnabled)
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync(options => options.Risk.Enabled = riskEnabled);
        Creature killer = await world.OnWorldAsync(() => world.Spawn(RiskTestWorld.Template(991061, minDamage: 1, maxDamage: 1), 2, 0));
        await world.SeeAsync(killer);
        await world.OnWorldAsync(() =>
        {
            world.Brain.NoteErrand(PlayerbotGoalKind.Train, 5491, 0);
            world.Player.Map!.Combat.DealDamage(killer, world.Player, world.Player.Health, direct: false);
            Assert.False(world.Player.IsAlive);
            return true;
        });
        await world.ThinkAsync();
        Assert.Equal(riskEnabled, await world.OnWorldAsync(() => world.Brain.Suspensions.IsEntrySuspended(5491, world.Host.World.NowMs)));
    }
}
