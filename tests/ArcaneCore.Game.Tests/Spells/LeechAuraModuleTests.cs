using System.Buffers.Binary;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// SPELL_AURA_PERIODIC_LEECH (Drain Life, Siphon Life) and PERIODIC_MANA_LEECH (Drain Mana) after vmangos
/// Aura::PeriodicTick (SpellAuras.cpp:5927-6014 and :6116-6190).
/// </summary>
public sealed class LeechAuraModuleTests
{
    private const uint DrainLife = 930401;
    private const uint DrainLifeDefaultMultiplier = 930402;
    private const uint DrainMana = 930403;
    private const uint Sleep = 930404;

    [Fact]
    public void ModuleIsDiscovered_AndTheAuraTypesHaveHandlers()
    {
        using var kit = Kit();

        Assert.Contains(typeof(LeechAuras), kit.System.Modules);
        Assert.True(kit.System.HasAuraHandler(AuraType.PeriodicLeech));
        Assert.True(kit.System.HasAuraHandler(AuraType.PeriodicManaLeech));
    }

    [Fact]
    public void PeriodicLeech_EachTick_DamagesTheTarget_AndHealsTheCasterByTheDamageTimesTheMultiple()
    {
        using var kit = Kit();
        (Player caster, FakeSession casterSession) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        caster.Health = 30;
        casterSession.Clear();

        kit.System.CastSpell(caster, DrainLife, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        Assert.Equal(60u, target.Health);

        kit.Advance(3000);

        Assert.Equal(50u, target.Health);
        Assert.Equal(45u, caster.Health); // 10 * 1.5
        byte[] damageLog = Assert.Single(Packets(casterSession, WorldOpcode.SmsgSpellnonmeleedamagelog));
        Assert.Equal(1, PeriodicFlag(damageLog)); // Unit::SendSpellNonMeleeDamageLog(..., isPeriodic = true)
        Assert.Empty(Packets(casterSession, WorldOpcode.SmsgPeriodicauralog)); // leech has no periodic aura log (Unit.cpp SendPeriodicAuraLog)
        Assert.Single(Packets(casterSession, WorldOpcode.SmsgSpellheallog));

        kit.Advance(3000);
        Assert.Equal(40u, target.Health);
        Assert.Equal(60u, caster.Health); // capped at the maximum
    }

    [Fact]
    public void PeriodicLeech_NoMultiple_MeansOne_AndTheDamageIsCappedByTheTargetsHealth()
    {
        using var kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        caster.Health = 10;
        target.Health = 4;

        kit.System.CastSpell(caster, DrainLifeDefaultMultiplier, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        kit.Advance(3000);

        // vmangos :5997: pdamage = min(pdamage, target health); multiplier is 1 when EffectMultipleValue is not positive.
        Assert.Equal(0u, target.Health);
        Assert.Equal(14u, caster.Health);
    }

    [Fact]
    public void PeriodicLeech_StopsWhenTheCasterIsDead()
    {
        using var kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        kit.System.CastSpell(caster, DrainLife, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        caster.Health = 0;

        kit.Advance(3000);

        Assert.Equal(60u, target.Health); // vmangos :5952: the caster must be alive
    }

    [Fact]
    public void PeriodicManaLeech_DrainsTheTargetsMana_AndGivesTheCasterTheMultiple()
    {
        using var kit = Kit();
        Player caster = TestPlayers.Add(kit, 3, Class.Mage, PowerType.Mana);
        Player target = TestPlayers.Add(kit, 4, Class.Priest, PowerType.Mana);
        caster.SetUInt32(UpdateFields.UnitFieldPower1, 20);
        target.SetUInt32(UpdateFields.UnitFieldPower1, 100);
        var session = (FakeSession)caster.Session;

        kit.System.CastSpell(caster, DrainMana, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        kit.Advance(1000);

        Assert.Equal(70u, target.GetUInt32(UpdateFields.UnitFieldPower1));
        Assert.Equal(35u, caster.GetUInt32(UpdateFields.UnitFieldPower1)); // 30 * 0.5

        // SMSG_PERIODICAURALOG: packed target, packed caster, spell, count 1, aura type, power, amount, float multiplier.
        byte[] log = Assert.Single(Packets(session, WorldOpcode.SmsgPeriodicauralog));
        int tail = log.Length - 16;
        Assert.Equal((uint)AuraType.PeriodicManaLeech, BinaryPrimitives.ReadUInt32LittleEndian(log.AsSpan(tail)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(log.AsSpan(tail + 4)));
        Assert.Equal(30u, BinaryPrimitives.ReadUInt32LittleEndian(log.AsSpan(tail + 8)));
        Assert.Equal(0.5f, BinaryPrimitives.ReadSingleLittleEndian(log.AsSpan(tail + 12)));

        kit.Advance(3000);
        Assert.Equal(0u, target.GetUInt32(UpdateFields.UnitFieldPower1)); // the last tick drains what is left
    }

    [Fact]
    public void PeriodicManaLeech_DoesNothingToATargetWhosePowerTypeDiffers()
    {
        using var kit = Kit();
        Player caster = TestPlayers.Add(kit, 3, Class.Mage, PowerType.Mana);
        (Player warrior, _) = kit.AddPlayer(2, 2); // rage user
        warrior.SetUInt32(UpdateFields.UnitFieldPower1, 90);

        kit.System.CastSpell(caster, DrainMana, SpellCastTargets.ForUnit(warrior.Guid), triggered: true);
        kit.Advance(1000);

        Assert.Equal(90u, warrior.GetUInt32(UpdateFields.UnitFieldPower1));
        Assert.Equal(100u, caster.GetUInt32(UpdateFields.UnitFieldPower1));
    }

    [Fact]
    public void PeriodicManaLeech_CasterWithoutManaGainsNothing_AndDamageCancelsAurasBreak()
    {
        using var kit = Kit();
        Player caster = TestPlayers.Add(kit, 3, Class.Mage, PowerType.Mana);
        Player target = TestPlayers.Add(kit, 4, Class.Priest, PowerType.Mana);
        caster.SetUInt32(UpdateFields.UnitFieldMaxpower1, 0);
        caster.SetUInt32(UpdateFields.UnitFieldPower1, 0);
        kit.System.CastSpell(caster, Sleep, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        Assert.True(kit.System.HasAura(target, Sleep));

        kit.System.CastSpell(caster, DrainMana, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        kit.Advance(1000);

        // vmangos :6136-6143: no gain without a mana pool; :6182: the drain removes AURA_INTERRUPT_DAMAGE_CANCELS auras.
        Assert.Equal(0u, caster.GetUInt32(UpdateFields.UnitFieldPower1));
        Assert.Equal(70u, target.GetUInt32(UpdateFields.UnitFieldPower1));
        Assert.False(kit.System.HasAura(target, Sleep));
    }

    private static int PeriodicFlag(byte[] log)
    {
        // packed target, packed caster, u32 spell, u32 damage, u8 school, u32 absorbed, u32 resisted, u8 periodic
        int offset = 0;
        for (int guids = 0; guids < 2; guids++)
        {
            byte mask = log[offset++];
            offset += System.Numerics.BitOperations.PopCount(mask);
        }

        offset += 4 + 4 + 1 + 4 + 4;
        return log[offset];
    }

    private static SpellTestKit Kit()
    {
        SpellInfo Channel(uint id, SpellEffectInfo effect) => Spell(id, effect) with
        {
            Duration = new SpellDuration(6000, 0, 6000),
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            SpellVisual = 1,
        };

        var kit = new SpellTestKit(
            Channel(DrainLife, Effect(SpellEffectName.ApplyAura, 10, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicLeech, amplitude: 3000) with { MultipleValue = 1.5f }),
            Channel(DrainLifeDefaultMultiplier, Effect(SpellEffectName.ApplyAura, 10, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicLeech, amplitude: 3000)),
            Channel(DrainMana, Effect(SpellEffectName.ApplyAura, 30, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicManaLeech, amplitude: 1000, misc: (int)PowerType.Mana) with { MultipleValue = 0.5f }),
            Channel(Sleep, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.Dummy)) with
            {
                AuraInterruptFlags = SpellAuraInterruptFlags.Damage,
                Attributes = SpellAttributes.AuraIsDebuff,
            });
        kit.System.CombatRules = SpellCombatRules.Neutral;
        return kit;
    }
}
