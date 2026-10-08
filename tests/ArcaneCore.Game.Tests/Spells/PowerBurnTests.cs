using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// Mana burn and the health funnel (docs/areas/unit-control.md): vmangos Spell::EffectPowerBurn (SpellEffects.cpp:1778-1809),
/// Aura::PeriodicTick SPELL_AURA_POWER_BURN_MANA (SpellAuras.cpp:6300-6352) and SPELL_AURA_PERIODIC_HEALTH_FUNNEL, which shares the leech
/// block (SpellAuras.cpp:5927-6010). Spell shapes from classic-db: Mana Burn R1 8129 (effect 62, misc 0 = mana, multiple 0.5), Ignite Mana 19659
/// (aura 162, 400 per 3 s, multiple 1), Blood Funnel 24617 (aura 62, 500 per second, multiple 10).
/// </summary>
public sealed class PowerBurnTests
{
    private const uint ManaBurn = 930_001;
    private const uint IgniteMana = 930_002;
    private const uint BloodFunnel = 930_003;

    private sealed class RecordingSink : IDamageSink
    {
        public List<(uint Spell, uint Amount, bool Periodic)> Damage { get; } = [];

        public List<uint> Healing { get; } = [];

        public uint DealSpellDamage(Unit caster, Unit victim, SpellInfo spell, uint damage, bool periodic)
        {
            Damage.Add((spell.Id, damage, periodic));
            return damage;
        }

        public uint Heal(Unit caster, Unit target, SpellInfo spell, uint amount)
        {
            Healing.Add(amount);
            return amount;
        }
    }

    private static SpellInfo Ranged(SpellInfo spell) => spell with
    {
        RangeIndex = 4,
        Range = new SpellRange(0, 30),
        School = SpellSchool.Arcane,
        SpellVisual = 1,
    };

    private static SpellTestKit NewKit() => new(
        Ranged(SpellTestKit.Spell(ManaBurn, SpellTestKit.Effect(SpellEffectName.PowerBurn, 100, SpellImplicitTarget.UnitEnemy, misc: 0) with { MultipleValue = 0.5f })),
        Ranged(SpellTestKit.Spell(IgniteMana, SpellTestKit.Effect(SpellEffectName.ApplyAura, 400, SpellImplicitTarget.UnitEnemy, AuraType.PowerBurnMana, amplitude: 3000, misc: 0)
            with { MultipleValue = 1.0f }) with { Duration = new SpellDuration(9000, 0, 9000) }),
        Ranged(SpellTestKit.Spell(BloodFunnel, SpellTestKit.Effect(SpellEffectName.ApplyAura, 50, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicHealthFunnel, amplitude: 1000)
            with { MultipleValue = 10.0f }) with { Duration = new SpellDuration(3000, 0, 3000), School = SpellSchool.Shadow }));

    private static (Player Caster, Player Target, RecordingSink Sink) Setup(SpellTestKit kit, uint targetMana)
    {
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 10, 0);
        if (targetMana > 0)
        {
            target.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
            target.SetUInt32(UpdateFields.UnitFieldMaxpower1, 1000);
            SpellSystem.SetPower(target, PowerType.Mana, targetMana);
        }

        var sink = new RecordingSink();
        kit.System.Damage = sink;
        return (caster, target, sink);
    }

    [Fact]
    public void ManaBurn_TakesTheTargetsMana_AndDealsTheBurnTimesTheMultipleAsDamage()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, Player target, RecordingSink sink) = Setup(kit, targetMana: 500);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, ManaBurn, SpellCastTargets.ForUnit(target.Guid), triggered: true));

        Assert.Equal(400u, SpellSystem.GetPower(target, PowerType.Mana));
        Assert.Equal([(ManaBurn, 50u, false)], sink.Damage); // 100 burned * 0.5
    }

    [Fact]
    public void ManaBurn_BurnsNoMoreThanTheTargetHas_AndLeavesARageUserAlone()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, Player target, RecordingSink sink) = Setup(kit, targetMana: 30);

        kit.System.CastSpell(caster, ManaBurn, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        Assert.Equal(0u, SpellSystem.GetPower(target, PowerType.Mana));
        Assert.Equal([(ManaBurn, 15u, false)], sink.Damage);

        (Player warrior, _) = kit.AddPlayer(3, 12, 0); // rage: not the effect's power type
        sink.Damage.Clear();
        kit.System.CastSpell(caster, ManaBurn, SpellCastTargets.ForUnit(warrior.Guid), triggered: true);
        Assert.Empty(sink.Damage);
    }

    [Fact]
    public void IgniteMana_BurnsManaEachTick_AsPeriodicDamage()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, Player target, RecordingSink sink) = Setup(kit, targetMana: 1000);
        FakeSession observer = kit.AddPlayer(4, 11, 0).Session;

        kit.System.CastSpell(caster, IgniteMana, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        observer.Clear();
        kit.Advance(9000);

        Assert.Equal(0u, SpellSystem.GetPower(target, PowerType.Mana)); // three ticks of 400: the last one burns the 200 that are left
        Assert.Equal([(IgniteMana, 400u, true), (IgniteMana, 400u, true), (IgniteMana, 200u, true)], sink.Damage);

        // the periodic log flag of SMSG_SPELLNONMELEEDAMAGELOG (vmangos damageInfo.periodicLog = true)
        byte[] log = SpellTestKit.Packets(observer, WorldOpcode.SmsgSpellnonmeleedamagelog).First();
        var r = new PacketReader(log);
        r.ReadPackedGuid();
        r.ReadPackedGuid();
        Assert.Equal(IgniteMana, r.ReadUInt32());
        Assert.Equal(400u, r.ReadUInt32());
        r.ReadByte();
        r.ReadUInt32();
        r.ReadUInt32();
        Assert.Equal(1, r.ReadByte());
    }

    [Fact]
    public void IgniteMana_DoesNothingToATargetWithoutMana()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, Player target, RecordingSink sink) = Setup(kit, targetMana: 0);

        kit.System.CastSpell(caster, IgniteMana, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        kit.Advance(9000);

        Assert.Empty(sink.Damage);
    }

    private sealed class AlwaysCritRules : ISpellCombatRules
    {
        public int CritRolls { get; private set; }

        public SpellMissInfo RollHit(SpellSystem system, Unit caster, Unit target, SpellInfo spell) => SpellMissInfo.None;

        public bool RollCrit(SpellSystem system, Unit caster, Unit target, SpellInfo spell)
        {
            CritRolls++;
            return true;
        }

        public float CritMultiplier(SpellInfo spell) => 1.5f;

        public uint RollPartialResist(SpellSystem system, Unit caster, Unit target, SpellInfo spell, uint damage) => 0;

        public uint ApplyArmor(Unit caster, Unit target, SpellInfo spell, uint damage) => damage;
    }

    [Fact]
    public void IgniteMana_RollsTheCritEveryTick_EvenWhenNothingIsLeftToBurn()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, Player target, RecordingSink sink) = Setup(kit, targetMana: 1000);
        SpellSystem.SetPower(target, PowerType.Mana, 0); // a mana user with an empty pool
        var rules = new AlwaysCritRules();
        kit.System.CombatRules = rules;
        FakeSession observer = kit.AddPlayer(4, 11, 0).Session;

        kit.System.CastSpell(caster, IgniteMana, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        observer.Clear();
        int before = rules.CritRolls;
        kit.Advance(9000);

        // vmangos (SpellAuras.cpp:6335-6336): IsSpellCrit is rolled before CalculateSpellDamage on every tick, whatever was burned, and a
        // crit marks the zero-damage log too.
        Assert.Equal(3, rules.CritRolls - before);
        Assert.Equal([(IgniteMana, 0u, true), (IgniteMana, 0u, true), (IgniteMana, 0u, true)], sink.Damage);
        byte[] log = SpellTestKit.Packets(observer, WorldOpcode.SmsgSpellnonmeleedamagelog).First();
        var r = new PacketReader(log);
        r.ReadPackedGuid();
        r.ReadPackedGuid();
        Assert.Equal(IgniteMana, r.ReadUInt32());
        Assert.Equal(0u, r.ReadUInt32());
        r.ReadByte();
        r.ReadUInt32();
        r.ReadUInt32();
        r.ReadByte();
        r.ReadByte();
        r.ReadUInt32();
        Assert.NotEqual(0u, r.ReadUInt32() & 0x2u); // SPELL_HIT_TYPE_CRIT in the hit info
    }

    [Fact]
    public void HealthFunnel_DamagesTheTarget_AndHealsTheCasterTheDamageTimesTheMultiple()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, Player target, RecordingSink sink) = Setup(kit, targetMana: 0);

        kit.System.CastSpell(caster, BloodFunnel, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        kit.Advance(3000);

        Assert.Equal([(BloodFunnel, 50u, true), (BloodFunnel, 50u, true), (BloodFunnel, 50u, true)], sink.Damage);
        Assert.Equal([500u, 500u, 500u], sink.Healing);
    }
}
