using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Casters;
using ArcaneCore.Game.Spells.Casters.Drain;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells.Casters;

/// <summary>
/// Drain Life / Devouring Plague (PERIODIC_LEECH) and Drain Mana (PERIODIC_MANA_LEECH).
/// vmangos SpellAuras.cpp:5927-6000, 6116-6200; shapes of classic-db spells 689 (Drain Life R1: base 9, multiplier 1,
/// amplitude 1 s, TargetA 6) and 5138 (Drain Mana R1: base 41, misc 0 = mana, multiplier 1).
/// </summary>
public sealed class DrainAuraTests
{
    private const uint DrainLife = 5001;
    private const uint DrainLifeBig = 5002;
    private const uint DrainMana = 5003;
    private const uint ImprovedDrain = 17864;
    private const uint ImprovedDrainSpell = 17864;

    private sealed class RecordingSink : IDamageSink
    {
        public List<uint> Damage { get; } = [];

        public List<uint> Healing { get; } = [];

        public List<uint> DamageSpells { get; } = [];

        public uint DealSpellDamage(Unit caster, Unit victim, SpellInfo spell, uint damage, bool periodic)
        {
            Damage.Add(damage);
            DamageSpells.Add(spell.Id);
            return damage;
        }

        public uint Heal(Unit caster, Unit target, SpellInfo spell, uint amount)
        {
            Healing.Add(amount);
            return amount;
        }
    }

    private static SpellInfo Drain(uint id, AuraType aura, int amount, float multiplier, int misc = 0) => SpellTestKit.Spell(
        id, SpellTestKit.Effect(SpellEffectName.ApplyAura, amount, SpellImplicitTarget.UnitEnemy, aura, amplitude: 1000, misc: misc)
            with { MultipleValue = multiplier }) with
    {
        AttributesEx = SpellAttributesEx.IsChanneled,
        Duration = new SpellDuration(5000, 0, 5000),
        School = SpellSchool.Shadow,
        RangeIndex = 4,
        Range = new SpellRange(0, 30),
        SpellVisual = 1,
    };

    private static SpellTestKit NewKit() => new(
        Drain(DrainLife, AuraType.PeriodicLeech, 9, 1.0f),
        Drain(DrainLifeBig, AuraType.PeriodicLeech, 9, 1.5f),
        Drain(DrainMana, AuraType.PeriodicManaLeech, 10, 1.0f),
        SpellTestKit.Spell(ImprovedDrainSpell, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
        {
            Attributes = SpellAttributes.Passive,
            Duration = new SpellDuration(-1, 0, -1),
            School = SpellSchool.Shadow,
        });

    private static (Player Caster, Player Target, RecordingSink Sink) Setup(SpellTestKit kit)
    {
        CasterSpellModules.Register(kit.System);
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 10, 0);
        kit.Spellbook.Teach(caster, DrainLife, DrainLifeBig, DrainMana);
        var sink = new RecordingSink();
        kit.System.Damage = sink;
        return (caster, target, sink);
    }

    [Fact]
    public void DrainLife_DamagesTheTargetEachSecond_AndHealsTheCasterTheSameAmount()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, Player target, RecordingSink sink) = Setup(kit);

        kit.System.HandleCastRequest(caster, DrainLife, SpellCastTargets.ForUnit(target.Guid));
        kit.Advance(5000);

        Assert.Equal([9u, 9u, 9u, 9u, 9u], sink.Damage);
        Assert.Equal([9u, 9u, 9u, 9u, 9u], sink.Healing);
    }

    [Fact]
    public void LeechMultiplier_ScalesTheHeal()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, Player target, RecordingSink sink) = Setup(kit);

        kit.System.HandleCastRequest(caster, DrainLifeBig, SpellCastTargets.ForUnit(target.Guid));
        kit.Advance(1000);

        Assert.Equal([9u], sink.Damage);
        Assert.Equal([13u], sink.Healing); // int(9 * 1.5)
    }

    [Fact]
    public void LowHealthTarget_TakesAndGivesOnlyWhatIsLeft_AndTheChannelEnds()
    {
        using SpellTestKit kit = NewKit();
        CasterSpellModules.Register(kit.System);
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 10, 0);
        kit.Spellbook.Teach(caster, DrainLife);
        caster.Health = 10;
        target.Health = 4;

        kit.System.HandleCastRequest(caster, DrainLife, SpellCastTargets.ForUnit(target.Guid));
        kit.Advance(1000);

        Assert.Equal(0u, target.Health);
        Assert.Equal(14u, caster.Health);
        Assert.Null(kit.System.GetState(caster.Guid)?.CurrentCast);
        Assert.Equal(0u, caster.GetUInt32(UpdateFields.UnitChannelSpell));
        kit.Advance(3000);
        Assert.Equal(14u, caster.Health);
    }

    [Fact]
    public void ADeadCaster_DrainsNothing()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, Player target, RecordingSink sink) = Setup(kit);
        kit.System.HandleCastRequest(caster, DrainLife, SpellCastTargets.ForUnit(target.Guid));
        kit.Advance(1000);
        Assert.Single(sink.Damage);

        caster.Health = 0;
        kit.Advance(2000);

        Assert.Single(sink.Damage);
    }

    [Fact]
    public void LeechDamage_IsLoggedAsPeriodic()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, Player target, _) = Setup(kit);
        var casterSession = (FakeSession)caster.Session;
        casterSession.Clear();

        kit.System.HandleCastRequest(caster, DrainLife, SpellCastTargets.ForUnit(target.Guid));
        kit.Advance(1000);

        byte[] log = SpellTestKit.Packets(casterSession, WorldOpcode.SmsgSpellnonmeleedamagelog).Single();
        // packed target (2 bytes for guid 2), packed caster (2), spell u32, damage u32, school u8, absorbed u32, resisted u32, periodic u8
        Assert.Equal((byte)1, log[2 + 2 + 4 + 4 + 1 + 4 + 4]);
    }

    [Fact]
    public void DrainMana_MovesManaFromAManaTargetToTheCaster()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, Player target, _) = Setup(kit);
        target.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
        target.SetUInt32(UpdateFields.UnitFieldMaxpower1, 100);
        target.SetUInt32(UpdateFields.UnitFieldPower1, 25);
        caster.SetUInt32(UpdateFields.UnitFieldMaxpower1, 100);
        caster.SetUInt32(UpdateFields.UnitFieldPower1, 5);

        kit.System.HandleCastRequest(caster, DrainMana, SpellCastTargets.ForUnit(target.Guid));
        kit.Advance(3000);

        // 10 + 10 + the last 5, the caster gains the same.
        Assert.Equal(0u, SpellSystem.GetPower(target, PowerType.Mana));
        Assert.Equal(30u, SpellSystem.GetPower(caster, PowerType.Mana));
    }

    [Fact]
    public void DrainMana_DoesNothingToATargetOfAnotherPowerType()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, Player target, _) = Setup(kit);
        target.SetUInt32(UpdateFields.UnitFieldPower1, 25); // a warrior: rage
        caster.SetUInt32(UpdateFields.UnitFieldMaxpower1, 100);

        kit.System.HandleCastRequest(caster, DrainMana, SpellCastTargets.ForUnit(target.Guid));
        kit.Advance(2000);

        Assert.Equal(25u, SpellSystem.GetPower(target, PowerType.Mana));
        Assert.Equal(0u, SpellSystem.GetPower(caster, PowerType.Mana));
    }

    [Fact]
    public void DrainMana_GivesNothingToACasterWithoutThatPower()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, Player target, _) = Setup(kit);
        target.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
        target.SetUInt32(UpdateFields.UnitFieldMaxpower1, 100);
        target.SetUInt32(UpdateFields.UnitFieldPower1, 25);

        kit.System.HandleCastRequest(caster, DrainMana, SpellCastTargets.ForUnit(target.Guid));
        kit.Advance(1000);

        Assert.Equal(15u, SpellSystem.GetPower(target, PowerType.Mana));
        Assert.Equal(0u, SpellSystem.GetPower(caster, PowerType.Mana));
    }

    [Fact]
    public void ImprovedDrainMana_AddsAFractionOfTheDrainedManaAsShadowDamage()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, Player target, RecordingSink sink) = Setup(kit);
        target.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
        target.SetUInt32(UpdateFields.UnitFieldMaxpower1, 100);
        target.SetUInt32(UpdateFields.UnitFieldPower1, 50);
        caster.SetUInt32(UpdateFields.UnitFieldMaxpower1, 100);
        kit.System.CastSpell(caster, ImprovedDrain, SpellCastTargets.ForSelf(), triggered: true);

        kit.System.HandleCastRequest(caster, DrainMana, SpellCastTargets.ForUnit(target.Guid));
        kit.Advance(1000);

        // 10 drained * 0.15 = 1 shadow damage under the talent spell.
        Assert.Equal([1u], sink.Damage);
        Assert.Equal([ImprovedDrainSpell], sink.DamageSpells);
    }

    [Fact]
    public void ManaLeechLog_FollowsTheWowMessagesLayout()
    {
        byte[] log = CasterPeriodicPackets.BuildManaLeechLog(new ObjectGuid(2), new ObjectGuid(1), 5138, 0, 41, 1.5f);

        var expected = new List<byte> { 0x01, 0x02, 0x01, 0x01 };
        expected.AddRange(BitConverter.GetBytes(5138u));
        expected.AddRange(BitConverter.GetBytes(1u));
        expected.AddRange(BitConverter.GetBytes(64u));
        expected.AddRange(BitConverter.GetBytes(0u));
        expected.AddRange(BitConverter.GetBytes(41u));
        expected.AddRange(BitConverter.GetBytes(1.5f));
        Assert.Equal([.. expected], log);
    }

    [Fact]
    public void HealthLeechEffect_HealsTheCasterForTheDamageDealt()
    {
        using SpellTestKit kit = new(SpellTestKit.Spell(
            5010, SpellTestKit.Effect(SpellEffectName.HealthLeech, 20, SpellImplicitTarget.UnitEnemy) with { MultipleValue = 2.0f }) with
        {
            School = SpellSchool.Shadow,
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
        });
        (Player caster, Player target, RecordingSink sink) = Setup(kit);

        kit.System.CastSpell(caster, 5010, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.Equal([20u], sink.Damage);
        Assert.Equal([40u], sink.Healing);
    }

    /// <summary>Rules whose direct-hit resist and periodic resist differ, so a test sees which one a tick asked for.</summary>
    private sealed class SplitResistRules(int periodicRoll) : ISpellCombatRules, ISpellResistRoll
    {
        public List<bool> PeriodicFlags { get; } = [];

        public SpellMissInfo RollHit(SpellSystem system, Unit caster, Unit target, SpellInfo spell) => SpellMissInfo.None;

        public bool RollCrit(SpellSystem system, Unit caster, Unit target, SpellInfo spell) => false;

        public float CritMultiplier(SpellInfo spell) => 1.0f;

        public uint RollPartialResist(SpellSystem system, Unit caster, Unit target, SpellInfo spell, uint damage) => 3; // the direct-hit roll

        public uint ApplyArmor(Unit caster, Unit target, SpellInfo spell, uint damage) => damage;

        public int RollResist(SpellSystem system, Unit caster, Unit target, SpellInfo spell, uint damage, bool periodic)
        {
            PeriodicFlags.Add(periodic);
            return periodic ? periodicRoll : 3;
        }
    }

    [Fact]
    public void DrainLife_RollsThePeriodicResist_AndAVulnerabilityAddsDamage()
    {
        // vmangos Aura::PeriodicTick PERIODIC_LEECH (SpellAuras.cpp:5962): CalculateDamageAbsorbAndResist(..., DOT, ...), a signed resist:
        // a negative resist (Curse of Shadow) is bonus damage (:5975-5978).
        using SpellTestKit kit = NewKit();
        (Player caster, Player target, RecordingSink sink) = Setup(kit);
        var rules = new SplitResistRules(periodicRoll: -4);
        kit.System.CombatRules = rules;

        kit.System.HandleCastRequest(caster, DrainLife, SpellCastTargets.ForUnit(target.Guid));
        kit.Advance(1000);

        Assert.Equal([13u], sink.Damage);   // 9 + 4 (the direct-hit roll would have resisted 3: 6)
        Assert.Equal([13u], sink.Healing);
        Assert.Equal([true], rules.PeriodicFlags);
    }

    [Fact]
    public void DrainLife_APositivePeriodicResistStillComesOff()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, Player target, RecordingSink sink) = Setup(kit);
        kit.System.CombatRules = new SplitResistRules(periodicRoll: 2);

        kit.System.HandleCastRequest(caster, DrainLife, SpellCastTargets.ForUnit(target.Guid));
        kit.Advance(1000);

        Assert.Equal([7u], sink.Damage);
    }

    [Fact]
    public void ImprovedDrainMana_RollsThePeriodicResist()
    {
        // vmangos: the talent damage is PeriodicTick(talent, PERIODIC_DAMAGE, ...) (SpellAuras.cpp:6192-6200), the DOT resist path.
        using SpellTestKit kit = NewKit();
        (Player caster, Player target, RecordingSink sink) = Setup(kit);
        var rules = new SplitResistRules(periodicRoll: -4);
        kit.System.CombatRules = rules;
        target.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
        target.SetUInt32(UpdateFields.UnitFieldMaxpower1, 100);
        target.SetUInt32(UpdateFields.UnitFieldPower1, 50);
        caster.SetUInt32(UpdateFields.UnitFieldMaxpower1, 100);
        kit.System.CastSpell(caster, ImprovedDrain, SpellCastTargets.ForSelf(), triggered: true);

        kit.System.HandleCastRequest(caster, DrainMana, SpellCastTargets.ForUnit(target.Guid));
        kit.Advance(1000);

        Assert.Equal([5u], sink.Damage);   // 1 + 4 (the direct-hit roll would have resisted all of it)
        Assert.Equal([true], rules.PeriodicFlags);
    }
}
