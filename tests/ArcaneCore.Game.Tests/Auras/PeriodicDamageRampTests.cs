using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Auras;

/// <summary>
/// The tick-index ramp of Curse of Agony and Starshards (vmangos Aura::PeriodicTick, SpellAuras.cpp:5867-5872):
/// Curse of Agony adds (-1 + (tick - 1) / 4) * SimpleValue(0) / 2 and Starshards (-1 + (tick - 1) / 2) * SimpleValue(0) / 3
/// to the snapshotted amount, with integer division on the tick term. Synthetic spells with the retail family masks
/// (CF_WARLOCK_CURSE_OF_AGONY bit 10, CF_PRIEST_STARSHARDS bit 21).
/// </summary>
public sealed class PeriodicDamageRampTests
{
    private const uint Agony = 947001;
    private const uint Shards = 947002;
    private const uint PlainDot = 947003;

    private const uint WarlockFamily = 5;
    private const uint PriestFamily = 6;

    private sealed class RecordingSink : IDamageSink
    {
        public List<uint> Damage { get; } = [];

        public uint DealSpellDamage(Unit caster, Unit victim, SpellInfo spell, uint damage, bool periodic)
        {
            Damage.Add(damage);
            return damage;
        }

        public uint Heal(Unit caster, Unit target, SpellInfo spell, uint amount) => amount;
    }

    private static SpellInfo Dot(uint id, int amount, uint amplitude, int durationMs) =>
        Spell(id, Effect(SpellEffectName.ApplyAura, amount, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: amplitude)) with
        {
            School = SpellSchool.Shadow,
            Duration = new SpellDuration(durationMs, 0, durationMs),
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };

    private static (SpellTestKit Kit, RecordingSink Sink, Player Caster, Player Victim) Setup()
    {
        var kit = new SpellTestKit(
            Dot(Agony, 12, 2000, 24000) with { SpellFamilyName = WarlockFamily, SpellFamilyFlags = 1UL << 10 },
            Dot(Shards, 12, 1000, 6000) with { SpellFamilyName = PriestFamily, SpellFamilyFlags = 1UL << 21 },
            Dot(PlainDot, 12, 2000, 8000) with { SpellFamilyName = WarlockFamily, SpellFamilyFlags = 1UL << 1 });
        (Player caster, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 5, 0);
        kit.System.Relations = new FakeRelations { Hostile = { victim.Guid } };
        var sink = new RecordingSink();
        kit.System.Damage = sink;
        return (kit, sink, caster, victim);
    }

    [Fact]
    public void CurseOfAgony_RampsFromHalfToOneAndAHalfTimesTheBaseOverTwelveTicks()
    {
        (SpellTestKit kit, RecordingSink sink, Player caster, Player victim) = Setup();
        using (kit)
        {
            kit.System.CastSpell(caster, Agony, SpellCastTargets.ForUnit(victim.Guid), triggered: true);
            kit.Advance(24000);

            // 12 + (-1 + (t - 1) / 4) * 12 / 2: ticks 1-4 -> 6, 5-8 -> 12, 9-12 -> 18 (total 144 = 12 * 12).
            Assert.Equal([6u, 6u, 6u, 6u, 12u, 12u, 12u, 12u, 18u, 18u, 18u, 18u], sink.Damage);
        }
    }

    [Fact]
    public void Starshards_RampsOverSixTicks()
    {
        (SpellTestKit kit, RecordingSink sink, Player caster, Player victim) = Setup();
        using (kit)
        {
            kit.System.CastSpell(caster, Shards, SpellCastTargets.ForUnit(victim.Guid), triggered: true);
            kit.Advance(6000);

            // 12 + (-1 + (t - 1) / 2) * 12 / 3: ticks 1-2 -> 8, 3-4 -> 12, 5-6 -> 16.
            Assert.Equal([8u, 8u, 12u, 12u, 16u, 16u], sink.Damage);
        }
    }

    [Fact]
    public void AnotherDotOfTheSameFamily_DoesNotRamp()
    {
        (SpellTestKit kit, RecordingSink sink, Player caster, Player victim) = Setup();
        using (kit)
        {
            kit.System.CastSpell(caster, PlainDot, SpellCastTargets.ForUnit(victim.Guid), triggered: true);
            kit.Advance(8000);

            Assert.Equal([12u, 12u, 12u, 12u], sink.Damage);
        }
    }
}
