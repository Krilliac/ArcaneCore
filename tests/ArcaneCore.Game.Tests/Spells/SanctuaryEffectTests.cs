using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Stealth;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

public sealed class SanctuaryEffectTests
{
    private static SpellInfo Vanish() => Spell(1856, Effect(SpellEffectName.Sanctuary, 0)) with
    { SpellFamilyName = 8, SpellFamilyFlags = 1UL << 11 };

    private static Creature Creature(SpellTestKit kit, uint guid, float x)
    {
        var template = Template(guid, b => b.Faction = 14);
        var creature = new Creature(guid, template, null, Content([template], []), new Random(1));
        creature.MapId = 0;
        creature.Relocate(x, 0, 83.5f, 0, kit.Now);
        var map = kit.World.GetMap(0);
        map.AddObject(creature);
        map.Combat.Track(creature);
        return creature;
    }

    [Fact]
    public void Vanish_StopsOrdinaryAttackersAndRemovesTheirThreatReferences()
    {
        using var kit = new SpellTestKit(Vanish());
        (Player rogue, _) = kit.AddPlayer(1);
        Creature attacker = Creature(kit, 900001, 2);
        var combat = kit.World.GetMap(0).Combat;
        Assert.True(combat.Attack(attacker, rogue));
        attacker.Combat.Threat.AddThreat(rogue, 100);
        Assert.True(attacker.Combat.Threat.Contains(rogue));

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(rogue, 1856, SpellCastTargets.ForSelf(), triggered: true));

        Assert.Null(attacker.Combat.Victim);
        Assert.False(attacker.Combat.Threat.Contains(rogue));
        Assert.False(rogue.Combat.IsInCombat);
    }

    [Fact]
    public void NonVanishSanctuary_StopsAttackersAndZerosItsOwnThreatWithoutDeletingEntries()
    {
        const uint petrification = 17624;
        using var kit = new SpellTestKit(Spell(petrification,
            Effect(SpellEffectName.Sanctuary, 0, SpellImplicitTarget.UnitCaster)));
        (Player player, _) = kit.AddPlayer(1);
        Creature protectedCreature = Creature(kit, 900002, 2);
        Creature attacker = Creature(kit, 900003, 4);
        var combat = kit.World.GetMap(0).Combat;
        Assert.True(combat.Attack(protectedCreature, player));
        Assert.True(combat.Attack(attacker, protectedCreature));
        protectedCreature.Combat.Threat.AddThreat(player, 100);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(protectedCreature, petrification,
            SpellCastTargets.ForSelf(), triggered: true));

        Assert.Null(attacker.Combat.Victim);
        Assert.Same(player, protectedCreature.Combat.Victim); // non-Vanish does not CombatStop the target
        Assert.True(protectedCreature.Combat.Threat.Contains(player));
        Assert.Equal(0, protectedCreature.Combat.Threat.GetThreat(player));
        Assert.Equal(kit.Now, protectedCreature.Combat.LastSanctuaryMs);
    }

    [Fact]
    public void Vanish_PreservesContestedGuardThreatButDropsOtherThreat()
    {
        using var kit = new SpellTestKit(Vanish());
        (Player rogue, _) = kit.AddPlayer(1);
        Creature guard = Creature(kit, 900004, 2);
        Creature ordinary = Creature(kit, 900005, 3);
        kit.System.IsContestedGuard = unit => ReferenceEquals(unit, guard);
        var combat = kit.World.GetMap(0).Combat;
        Assert.True(combat.Attack(guard, rogue));
        Assert.True(combat.Attack(ordinary, rogue));
        guard.Combat.Threat.AddThreat(rogue, 100);
        ordinary.Combat.Threat.AddThreat(rogue, 100);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(rogue, 1856, SpellCastTargets.ForSelf(), triggered: true));

        Assert.True(guard.Combat.Threat.Contains(rogue));
        Assert.False(ordinary.Combat.Threat.Contains(rogue));
        Assert.False(kit.System.IsCreatureDetectionSuppressed(rogue));
    }

    [Fact]
    public void Vanish_SuppressesCreatureDetectionForOneSecondWhenNoGuardRemains()
    {
        using var kit = new SpellTestKit(Vanish());
        (Player rogue, _) = kit.AddPlayer(1);
        Creature creature = Creature(kit, 900006, 2);
        var visibility = new StealthServices(kit.System, new StealthRegistry(), StealthOptions.Default);
        Assert.True(visibility.CanCreatureSee(creature, rogue, out _));

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(rogue, 1856, SpellCastTargets.ForSelf(), triggered: true));
        Assert.False(visibility.CanCreatureSee(creature, rogue, out _));
        kit.Now += 999;
        Assert.False(visibility.CanCreatureSee(creature, rogue, out _));
        kit.Now++;
        Assert.True(visibility.CanCreatureSee(creature, rogue, out _));
    }
}
