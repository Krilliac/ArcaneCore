using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>Source damage flags must survive the spell/combat seam (vmangos SpellEffects.cpp:285, Unit.cpp:2140,2179).</summary>
public sealed class DamageDurabilityRoutingTests
{
    [Fact]
    public void InstantKill_ExplicitlySuppressesDeathDurability()
    {
        SpellInfo spell = SpellTestKit.Spell(963800, SpellTestKit.Effect(SpellEffectName.Instakill, 0));
        using var kit = new SpellTestKit(spell);
        (Player caster, _) = kit.AddPlayer(1);
        var sink = new RecordingSink();
        kit.System.Damage = sink;

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, spell.Id, SpellCastTargets.ForSelf(), triggered: true));

        Delivery delivery = Assert.Single(sink.Deliveries);
        Assert.False(delivery.DurabilityLoss);
        Assert.False(delivery.Periodic);
        Assert.True(delivery.StartsCombat);
    }

    [Theory]
    [InlineData(AuraType.SplitDamageFlat, 20u)]
    [InlineData(AuraType.SplitDamagePct, 10u)]
    public void RedirectedDamage_ExplicitlySuppressesDeathDurability(AuraType type, uint expectedDamage)
    {
        SpellInfo spell = SpellTestKit.Spell(963801, SpellTestKit.Effect(SpellEffectName.ApplyAura, 20,
            SpellImplicitTarget.Unit, type, misc: (int)SpellSchoolMasks.All)) with
        {
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            Duration = new SpellDuration(60_000, 0, 60_000),
        };
        using var kit = new SpellTestKit(spell);
        (Player recipient, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        (Player attacker, _) = kit.AddPlayer(3, 3);
        var sink = new RecordingSink();
        kit.System.Damage = sink;
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(recipient, spell.Id,
            SpellCastTargets.ForUnit(target.Guid), triggered: true));
        Assert.True(kit.System.HasAura(target, spell.Id));

        Assert.Equal(expectedDamage, kit.System.AbsorbDamage(attacker, target, SpellSchoolMasks.All, 50, null));

        Delivery delivery = Assert.Single(sink.Deliveries);
        Assert.Same(recipient, delivery.Target);
        Assert.Equal(expectedDamage, delivery.Damage);
        Assert.False(delivery.DurabilityLoss);
        Assert.True(delivery.Periodic);
        Assert.True(delivery.StartsCombat);
    }

    private sealed record Delivery(Unit Target, uint Damage, bool Periodic, bool StartsCombat, bool DurabilityLoss);

    private sealed class RecordingSink : IDamageSink
    {
        private readonly HealthOnlyDamageSink _health = new();
        public List<Delivery> Deliveries { get; } = [];

        public uint DealSpellDamage(Unit caster, Unit victim, SpellInfo spell, uint damage, bool periodic)
            => DealSpellDamage(caster, victim, spell, damage, periodic, startsCombat: true, durabilityLoss: true);

        public uint DealSpellDamage(Unit caster, Unit victim, SpellInfo spell, uint damage, bool periodic, bool startsCombat)
            => DealSpellDamage(caster, victim, spell, damage, periodic, startsCombat, durabilityLoss: true);

        public uint DealSpellDamage(Unit caster, Unit victim, SpellInfo spell, uint damage, bool periodic, bool startsCombat, bool durabilityLoss)
        {
            Deliveries.Add(new Delivery(victim, damage, periodic, startsCombat, durabilityLoss));
            return _health.DealSpellDamage(caster, victim, spell, damage, periodic);
        }

        public uint Heal(Unit caster, Unit target, SpellInfo spell, uint amount) => _health.Heal(caster, target, spell, amount);
    }
}
