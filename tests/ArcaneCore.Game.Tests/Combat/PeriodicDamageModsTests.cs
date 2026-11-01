using System.Buffers.Binary;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Game.Tests.SpellRules;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Combat;

/// <summary>
/// A periodic damage tick runs DealDamageMods after the absorb step (vmangos Aura::PeriodicTick, SpellAuras.cpp:5900): a victim on a taxi
/// flight takes nothing and SMSG_PERIODICAURALOG shows the whole tick absorbed.
/// </summary>
public sealed class PeriodicDamageModsTests
{
    private const uint FireDot = 980_901;

    private static SpellTestKit Kit() => new(
        RuleTestKit.Grant(FireDot, AuraType.PeriodicDamage, 20) with
        {
            School = SpellSchool.Fire,
            Attributes = SpellAttributes.AuraIsDebuff,
            Effects = [SpellTestKit.Effect(SpellEffectName.ApplyAura, 20, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 3000)],
            Duration = new SpellDuration(30_000, 0, 30_000),
        });

    private static (uint Amount, uint Absorbed) TickLog(SpellTestKit kit, bool taxiFlight)
    {
        (Player caster, _) = kit.AddPlayer(1);
        (Player victim, FakeSession session) = kit.AddPlayer(2, 2);
        victim.Health = victim.MaxHealth = 1000;
        kit.System.Relations = new FakeRelations { Hostile = { victim.Guid } };
        kit.System.CastSpell(caster, FireDot, SpellCastTargets.ForUnit(victim.Guid), triggered: true);
        if (taxiFlight)
        {
            victim.UnitFlags |= UnitFlags.TaxiFlight;
        }

        session.Clear();
        kit.Advance(3100);

        Assert.Equal(taxiFlight ? 1000u : 980u, victim.Health);
        byte[] log = Assert.Single(SpellTestKit.Packets(session, WorldOpcode.SmsgPeriodicauralog));
        int tail = log.Length - 16; // amount, school, absorbed, resisted
        return (BinaryPrimitives.ReadUInt32LittleEndian(log.AsSpan(tail)), BinaryPrimitives.ReadUInt32LittleEndian(log.AsSpan(tail + 8)));
    }

    [Fact]
    public void ADotTick_OnATaxiFlyingVictim_DealsNothingAndLogsItAbsorbed()
    {
        using SpellTestKit kit = Kit();

        (uint amount, uint absorbed) = TickLog(kit, taxiFlight: true);

        Assert.Equal(0u, amount);
        Assert.Equal(20u, absorbed);
    }

    [Fact]
    public void ADotTick_OnAnOrdinaryVictim_IsUnchanged()
    {
        using SpellTestKit kit = Kit();

        (uint amount, uint absorbed) = TickLog(kit, taxiFlight: false);

        Assert.Equal(20u, amount);
        Assert.Equal(0u, absorbed);
    }
}
