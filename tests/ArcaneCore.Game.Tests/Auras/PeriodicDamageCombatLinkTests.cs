using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Auras;

/// <summary>
/// vmangos Unit::DealDamage ShouldEnterCombat (Objects/Unit.cpp:696-714): damage of type DOT links no combat unless the spell is a channel.
/// Live: a creature whose poison kept ticking on a player after it evaded was put back in combat by every tick with an empty threat list
/// (creature/stuck-in-combat, creature/evade-loop on Venomtail Scorpids and other poisoners).
/// </summary>
public sealed class PeriodicDamageCombatLinkTests
{
    private const uint PlainDot = 947101;
    private const uint ChanneledDot = 947102;

    private static SpellInfo Dot(uint id, bool channeled) =>
        Spell(id, Effect(SpellEffectName.ApplyAura, 5, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 1000)) with
        {
            School = SpellSchool.Nature,
            Duration = new SpellDuration(10_000, 0, 10_000),
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
            AttributesEx = channeled ? SpellAttributesEx.IsChanneled : 0,
        };

    private static (SpellTestKit Kit, Player Caster, Player Victim) Setup()
    {
        var kit = new SpellTestKit(Dot(PlainDot, channeled: false), Dot(ChanneledDot, channeled: true));
        (Player caster, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 5, 0);
        victim.MaxHealth = 1_000_000;
        victim.Health = 1_000_000;
        kit.System.Relations = new FakeRelations { Hostile = { victim.Guid } };
        kit.System.Damage = new MapCombatDamageSink();
        return (kit, caster, victim);
    }

    [Fact]
    public void ADotTick_DoesNotPutItsCasterBackInCombat()
    {
        (SpellTestKit kit, Player caster, Player victim) = Setup();
        using (kit)
        {
            kit.System.CastSpell(caster, PlainDot, SpellCastTargets.ForUnit(victim.Guid), triggered: true);
            Assert.True(kit.System.HasAura(victim, PlainDot));

            // The fight ended (an evade or a combat timer); the poison is still on the victim.
            MapCombat.ClearInCombat(caster);
            MapCombat.ClearInCombat(victim);
            uint before = victim.Health;

            kit.Advance(3_000);
            Assert.True(victim.Health < before); // the poison ticked
            Assert.False(caster.Combat.IsInCombat);
            Assert.False(victim.Combat.IsInCombat);
        }
    }

    /// <summary>
    /// vmangos Aura::PeriodicTick deals the tick through Unit::DealDamage alone (SpellAuras.cpp:5921), which pulses no PvP flag for
    /// DOT damage: the out-of-combat flag (SetOutOfCombatWithVictim) belongs to the spell's hit (Spell.cpp:1260, 1684, 1692).
    /// </summary>
    [Fact]
    public void ADotTick_DoesNotFlagItsCasterForPvp()
    {
        (SpellTestKit kit, Player caster, Player victim) = Setup();
        using (kit)
        {
            victim.UnitFlags |= UnitFlags.Pvp;
            kit.System.CastSpell(caster, PlainDot, SpellCastTargets.ForUnit(victim.Guid), triggered: true);
            Assert.True(kit.System.HasAura(victim, PlainDot));

            // The fight ended and the caster's PvP flag ran out; the poison is still on the flagged victim.
            MapCombat.ClearInCombat(caster);
            MapCombat.ClearInCombat(victim);
            caster.UnitFlags &= ~UnitFlags.Pvp;
            caster.Combat.PvpFlagTimer = 0;
            MapCombat.UpdatePvpContested(caster, false, overriding: true);
            uint before = victim.Health;

            kit.Advance(3_000);
            Assert.True(victim.Health < before); // the poison ticked
            Assert.Equal(0u, (uint)(caster.UnitFlags & UnitFlags.Pvp));
            Assert.Equal(0u, (uint)(caster.Flags & PlayerFlags.ContestedPvp));
        }
    }

    [Fact]
    public void AChanneledDotTick_StillLinksCombat()
    {
        (SpellTestKit kit, Player caster, Player victim) = Setup();
        using (kit)
        {
            kit.System.CastSpell(caster, ChanneledDot, SpellCastTargets.ForUnit(victim.Guid), triggered: true);
            Assert.True(kit.System.HasAura(victim, ChanneledDot));
            MapCombat.ClearInCombat(caster);
            MapCombat.ClearInCombat(victim);
            uint before = victim.Health;

            kit.Advance(3_000);
            Assert.True(victim.Health < before);
            Assert.True(caster.Combat.IsInCombat);
            Assert.True(victim.Combat.IsInCombat);
        }
    }
}
