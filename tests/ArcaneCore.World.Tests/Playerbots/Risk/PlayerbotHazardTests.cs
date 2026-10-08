using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Risk;

/// <summary>
/// Creatures that kill outright and remembered danger, on the real world (<see cref="RiskTestWorld"/>): the live replay's Dawnrover
/// walked to a trainer past the Scourge invasion's Skeletal Soldiers (EventAI: cast Scourge Strike 28265, SPELL_EFFECT_INSTAKILL)
/// and died five times in ten minutes, reviving at its body beside them. A creature with such a spell is never pulled, every walk
/// of a living bot keeps out of its reach (round it when a way round exists, else not at all) and out of the places it died, and
/// a ghost does not revive within its reach.
/// </summary>
public sealed class PlayerbotHazardTests
{
    private const uint SoldierEntry = 991101;
    private const uint ScourgeStrike = 991_128;

    private static CreatureAiContent SoldierAi => new(
    [
        new CreatureAiEvent
        {
            Id = 99110101, CreatureId = SoldierEntry, EventType = 0, Param3 = 5000, Param4 = 10000,
            Action1 = new CreatureAiAction(PlayerbotCreatureSpells.ActionCast, (int)ScourgeStrike, 1, 0),
        },
    ], []);

    private static async Task<(RiskTestWorld World, CreatureTemplate Soldier)> StartAsync(Action<PlayerbotOptions>? configure = null)
    {
        RiskTestWorld world = await RiskTestWorld.StartAsync(configure, SoldierAi);
        await world.OnWorldAsync(() =>
        {
            SpellFeature feature = world.Session.Services.GetRequiredService<SpellFeature>();
            feature.System.Store = new SpellStore([.. feature.System.Store.All, new SpellInfo
            {
                Id = ScourgeStrike, Name = "Scourge Strike",
                Effects = [new SpellEffectInfo { Effect = SpellEffectName.Instakill, TargetA = SpellImplicitTarget.UnitEnemy }],
            }], [], []);
            return true;
        });
        return (world, RiskTestWorld.Template(SoldierEntry) with { AIName = "EventAI" });
    }

    [Fact]
    public async Task ACreatureThatKillsOutright_IsNotPulled_ALoneOrdinaryOneIs()
    {
        (RiskTestWorld world, CreatureTemplate soldierTemplate) = await StartAsync();
        await using RiskTestWorld owned = world;
        (Creature soldier, Creature wolf) = await world.OnWorldAsync(() =>
            (world.Spawn(soldierTemplate, 25, 0), world.Spawn(RiskTestWorld.Template(991102), 0, 35)));
        await world.SeeAsync(soldier, wolf);
        await world.OnWorldAsync(() =>
        {
            Assert.True(PlayerbotCreatureSpells.Of(soldier, world.Session.Services.GetRequiredService<SpellFeature>().System.Store).Instakill,
                $"ai {soldier.AI?.GetType().Name} events {(soldier.AI as CreatureEventAI)?.EventCount} store {world.Session.Services.GetRequiredService<SpellFeature>().System.Store.Get(ScourgeStrike)?.Name}");
            Assert.Same(soldier, PlayerbotBrain.FindTarget(world.Player)); // the old choice: the nearest
            PlayerbotRisk risk = world.Brain.Risk;
            PlayerbotEngagement verdict = risk.Assess(world.Player, soldier, false, out _);
            Assert.Equal("lethal", verdict.Reason);
            Assert.Equal("lethal", risk.Assess(world.Player, soldier, true, out _).Reason); // not even for a quest
            Assert.Same(wolf, risk.ChooseTarget(world.Player, 0, _ => false, questObjective: false));
            return true;
        });
    }

    /// <summary>
    /// A walk 60 yards north passes a soldier standing on the straight line: with the risk on, the route goes round its reach; a
    /// destination inside its reach is refused (the errand is set aside, another trainer taken); without the guard the route goes
    /// straight through.
    /// </summary>
    [Fact]
    public async Task EveryWalk_KeepsOutOfALethalCreaturesReach_RoundIt_OrNotAtAll()
    {
        (RiskTestWorld world, CreatureTemplate soldierTemplate) = await StartAsync();
        await using RiskTestWorld owned = world;
        Creature soldier = await world.OnWorldAsync(() => world.Spawn(soldierTemplate, 0, 30));
        await world.SeeAsync(soldier);
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            var far = new Vector3(player.X, player.Y + 60, player.Z);
            var soldierAt = new Vector3(soldier.X, soldier.Y, soldier.Z);

            PlayerbotNavigation.Guard(player, null);
            Assert.True(PlayerbotNavigation.TryPlan(player, far, world.Options, out PlayerbotRoute? straight));
            Assert.Contains(Sample(straight!.Points), p => Vector3.Distance(p, soldierAt) < 10f);

            PlayerbotNavigation.Guard(player, world.Brain.Risk);
            world.Brain.Risk.ScanHazards(player);
            PlayerbotHazard hazard = Assert.Single(world.Brain.Risk.Hazards.Active(player.MapId, world.Host.World.NowMs));
            Assert.Equal("lethal", hazard.Kind);
            Assert.True(PlayerbotNavigation.TryPlan(player, far, world.Options, out PlayerbotRoute? round), "no way round the soldier");
            Assert.All(Sample(round!.Points), p => Assert.False(hazard.Covers(p), $"the route passes {p}, inside the soldier's reach"));
            Assert.True(Vector3.Distance(round.Points[^1], far) < 1f);

            Assert.False(PlayerbotNavigation.TryPlan(player, soldierAt + new Vector3(3, 0, 0), world.Options, out _),
                "a destination beside the soldier was not refused");
            PlayerbotNavigation.Guard(player, null);
            return true;
        });
    }

    /// <summary>A bot that died somewhere keeps out of that place on its later walks (and walks out of one it stands in).</summary>
    [Fact]
    public async Task APlaceWhereTheBotDied_IsWalkedRound()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            var deathSpot = new Vector3(player.X + 40, player.Y, player.Z);
            world.Brain.Risk.Hazards.Add(player.MapId, deathSpot, PlayerbotHazards.DeathYards, world.Host.World.NowMs, 300, "death");
            PlayerbotNavigation.Guard(player, world.Brain.Risk);
            Assert.True(PlayerbotNavigation.TryPlan(player, new Vector3(player.X + 80, player.Y, player.Z), world.Options, out PlayerbotRoute? route));
            Assert.All(Sample(route!.Points), p => Assert.True(Vector3.Distance(p, deathSpot) >= PlayerbotHazards.DeathYards - 0.5f, $"through the death place at {p}"));

            // Standing inside it, the bot may walk out.
            player.Relocate(deathSpot.X, deathSpot.Y, deathSpot.Z, 0, world.Host.World.NowMs);
            Assert.True(PlayerbotNavigation.TryPlan(player, new Vector3(deathSpot.X + 40, deathSpot.Y, deathSpot.Z), world.Options, out _));
            PlayerbotNavigation.Guard(player, null);
            return true;
        });
    }

    /// <summary>
    /// A revive 22 yards from a soldier is outside its aggro radius (18) and was not camped; it is within its reach as a hazard
    /// (radius plus 8), so the ghost looks for a spot further off, or waits for the spirit healer.
    /// </summary>
    [Fact]
    public async Task AReviveInALethalCreaturesReach_IsCamped()
    {
        (RiskTestWorld world, CreatureTemplate soldierTemplate) = await StartAsync();
        await using RiskTestWorld owned = world;
        Creature soldier = await world.OnWorldAsync(() => world.Spawn(soldierTemplate, 22, 0));
        await world.SeeAsync(soldier);
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            Assert.False(PlayerbotRecovery.Camped(player));
            world.Brain.Risk.ScanHazards(player);
            Assert.True(PlayerbotRecovery.Camped(player, world.Brain.Risk.HazardThreats(player)));
            return true;
        });
    }

    /// <summary>
    /// A route planned before the soldier was known is given up at the next step once it is: the bot does not walk on into it.
    /// </summary>
    [Fact]
    public async Task ARouteIntoAHazardThatAppearedLater_IsGivenUp()
    {
        (RiskTestWorld world, CreatureTemplate soldierTemplate) = await StartAsync();
        await using RiskTestWorld owned = world;
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            PlayerbotNavigation.Guard(player, world.Brain.Risk);
            Assert.True(PlayerbotNavigation.TryPlan(player, new Vector3(player.X, player.Y + 60, player.Z), world.Options, out PlayerbotRoute? route));
            world.Session.ManagedBudget = new ArcaneCore.World.Net.ManagedActionBudget(4);
            Assert.True(PlayerbotNavigation.TryAdvance(world.Session, route!, world.Options, 100, world.Host.World.NowMs));
            Creature soldier = world.Spawn(soldierTemplate, 0, 30);
            world.Brain.Risk.Hazards.Add(player.MapId, new Vector3(soldier.X, soldier.Y, soldier.Z), 26f, world.Host.World.NowMs, 300, "lethal", soldier.Guid);
            Assert.False(PlayerbotNavigation.TryAdvance(world.Session, route!, world.Options, 100, world.Host.World.NowMs));
            PlayerbotNavigation.Guard(player, null);
            return true;
        });
    }

    private static IEnumerable<Vector3> Sample(IReadOnlyList<Vector3> points)
    {
        for (int i = 1; i < points.Count; i++)
            for (int step = 0; step <= 10; step++)
                yield return Vector3.Lerp(points[i - 1], points[i], step / 10f);
    }
}
