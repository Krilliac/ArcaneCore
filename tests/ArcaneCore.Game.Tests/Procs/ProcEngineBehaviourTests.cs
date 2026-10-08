using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.SpellRules;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Procs;

/// <summary>
/// What the proc engine changes, observed only through the hit paths (white swings, spell hits, periodic ticks): the auras that proc, the
/// spells they trigger, the damage they deal, the charges they spend and the reflected spells. vmangos SpellCaster::ProcDamageAndSpell,
/// Unit::ProcDamageAndSpellFor, Unit::HandleTriggers and the AuraProcHandler table (UnitAuraProcHandler.cpp). These tests use only APIs that
/// existed before the engine, so they fail on the engine-less base (RED evidence in D:/ArcaneCore-lanes/_logs/w2-proc-engine/).
/// </summary>
public sealed class ProcEngineBehaviourTests
{
    private const uint OnSwingAura = 991_001;
    private const uint OnSwingBuff = 991_002;
    private const uint ChargedVictimAura = 991_003;
    private const uint ThornsLikeProc = 991_004;
    private const uint ExtraAttackAura = 991_005;
    private const uint ExtraAttackSpell = 991_006;
    private const uint ReflectAll = 991_007;
    private const uint FrostBolt = 991_008;
    private const uint FrostReflect = 991_009;
    private const uint FireBolt = 991_010;
    private const uint ProcSleep = 991_011;
    private const uint Hit = 991_012;
    private const uint PeriodicCharge = 991_013;
    private const uint Dot = 991_014;
    private const uint TargetTriggerAura = 991_015;
    private const uint TargetTriggerDebuff = 991_016;
    private const uint FamilySpell = 991_017;
    private const uint WyvernStingRank1 = 19386; // the real ids the old exemption keyed on
    private const uint ProwlRank1 = 5215;

    private static SpellInfo Permanent(uint id, SpellEffectInfo effect) => Spell(id, effect) with
    {
        Duration = new SpellDuration(-1, 0, -1),
        SpellVisual = 1,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    /// <summary>
    /// Apply a self aura a millisecond before the event under test: an aura the event's actor applied at the event's own time does not proc
    /// from it (vmangos Unit.cpp:8958, <c>GetAuraApplyTime() &gt;= procTime</c>), and the test clock does not move by itself.
    /// </summary>
    private static void ApplyEarlier(SpellTestKit kit, Unit unit, uint id)
    {
        RuleTestSupport.Apply(kit, unit, id);
        kit.Now++;
    }

    private static SpellInfo DamageCancelled(uint id, ProcFlags procFlags, uint dispel = 0) => Spell(id,
        Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, SpellHandlerModuleTests.FixtureAura)) with
    {
        AuraInterruptFlags = SpellAuraInterruptFlags.Damage,
        ProcFlags = procFlags,
        ProcChance = procFlags == ProcFlags.None ? 101u : 100u, // Spell.dbc: 101 for no proc, 100 for Wyvern Sting
        Dispel = dispel,
        Duration = new SpellDuration(60000, 0, 60000),
        SpellVisual = 1,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellTestKit NewKit() => new(
        Permanent(OnSwingAura, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ProcTriggerSpell, trigger: OnSwingBuff)) with
        {
            ProcFlags = ProcFlags.DealMeleeSwing,
            ProcChance = 100,
        },
        RuleTestSupport.Grant(OnSwingBuff, AuraType.ModResistance, 5, misc: 1) with { Duration = new SpellDuration(10000, 0, 10000) },
        Permanent(ChargedVictimAura, Effect(SpellEffectName.ApplyAura, 5, aura: AuraType.ModResistance, misc: 1)) with
        {
            ProcFlags = ProcFlags.TakeMeleeSwing,
            ProcChance = 100,
            ProcCharges = 2,
        },
        Permanent(ThornsLikeProc, Effect(SpellEffectName.ApplyAura, 20, aura: AuraType.ProcTriggerDamage)) with
        {
            ProcFlags = ProcFlags.TakeMeleeSwing,
            ProcChance = 100,
            School = SpellSchool.Holy,
        },
        Permanent(ExtraAttackAura, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ProcTriggerSpell, trigger: ExtraAttackSpell)) with
        {
            ProcFlags = ProcFlags.DealMeleeSwing,
            ProcChance = 100,
        },
        Spell(ExtraAttackSpell, Effect(SpellEffectName.AddExtraAttacks, 1)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 },
        RuleTestSupport.Grant(ReflectAll, AuraType.ReflectSpells, 100),
        RuleTestSupport.Grant(FrostReflect, AuraType.ReflectSpellsSchool, 100, misc: (int)(1u << (int)SpellSchool.Frost)),
        RuleTestSupport.Magic(FrostBolt, SpellSchool.Frost) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 },
        RuleTestSupport.Magic(FireBolt, SpellSchool.Fire) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 },
        DamageCancelled(ProcSleep, ProcFlags.TakenAnyDamage),
        DamageCancelled(WyvernStingRank1, ProcFlags.TakenAnyDamage),
        DamageCancelled(ProwlRank1, ProcFlags.None, dispel: 5), // build 5875 Spell.dbc: druid Prowl has procFlags 0
        Spell(Hit, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 },
        Permanent(PeriodicCharge, Effect(SpellEffectName.ApplyAura, 5, aura: AuraType.ModResistance, misc: 1)) with
        {
            ProcFlags = ProcFlags.TakeHarmfulPeriodic,
            ProcChance = 100,
            ProcCharges = 1,
        },
        Spell(Dot, Effect(SpellEffectName.ApplyAura, 4, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 3000)) with
        {
            Attributes = SpellAttributes.AuraIsDebuff,
            Duration = new SpellDuration(15000, 0, 15000),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        },
        Permanent(TargetTriggerAura, Effect(SpellEffectName.ApplyAura, 100, aura: AuraType.AddTargetTrigger, trigger: TargetTriggerDebuff) with { ItemType = 0x10 })
            with { SpellFamilyName = 3 },
        Spell(TargetTriggerDebuff, Effect(SpellEffectName.ApplyAura, -5, SpellImplicitTarget.UnitEnemy, AuraType.ModResistance, misc: 1)) with
        {
            Attributes = SpellAttributes.AuraIsDebuff,
            Duration = new SpellDuration(10000, 0, 10000),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        },
        Spell(FamilySpell, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)) with
        {
            SpellFamilyName = 3,
            SpellFamilyFlags = 0x10,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        });

    private static (SpellTestKit Kit, Player Attacker, Player Victim, MapCombat Combat) MeleeKit()
    {
        SpellTestKit kit = NewKit();
        (Player attacker, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2);
        MapCombat combat = attacker.Map!.Combat;
        combat.SpellMitigation = kit.System;
        combat.Random = new ScriptedRandom { DefaultInt = 9999 }; // every swing an ordinary hit
        attacker.SetFloat(UpdateFields.UnitFieldMindamage, 10);
        attacker.SetFloat(UpdateFields.UnitFieldMaxdamage, 10);
        attacker.Health = attacker.MaxHealth = 1000;
        victim.Health = victim.MaxHealth = 1000;
        return (kit, attacker, victim, combat);
    }

    [Fact]
    public void AWhiteSwing_ProcsTheAttackersProcTriggerSpellAura_AndCastsItsPositiveTriggerOnTheAttacker()
    {
        (SpellTestKit kit, Player attacker, Player victim, MapCombat combat) = MeleeKit();
        using SpellTestKit _ = kit;
        ApplyEarlier(kit, attacker, OnSwingAura);

        combat.AttackerStateUpdate(attacker, victim, WeaponAttackType.BaseAttack);

        Assert.True(kit.System.HasAura(attacker, OnSwingBuff));
        Assert.False(kit.System.HasAura(victim, OnSwingBuff));
    }

    [Fact]
    public void ATakenSwing_SpendsTheVictimAurasCharges_AndRemovesItWithTheLastOne()
    {
        (SpellTestKit kit, Player attacker, Player victim, MapCombat combat) = MeleeKit();
        using SpellTestKit _ = kit;
        ApplyEarlier(kit, victim, ChargedVictimAura);

        combat.AttackerStateUpdate(attacker, victim, WeaponAttackType.BaseAttack);
        Assert.Equal(1, kit.System.GetAuras(victim).Single(h => h.Spell.Id == ChargedVictimAura).Charges);

        combat.AttackerStateUpdate(attacker, victim, WeaponAttackType.BaseAttack);
        Assert.False(kit.System.HasAura(victim, ChargedVictimAura));
    }

    [Fact]
    public void ProcTriggerDamage_HurtsTheAttackerOfTheAuraOwner()
    {
        (SpellTestKit kit, Player attacker, Player victim, MapCombat combat) = MeleeKit();
        using SpellTestKit _ = kit;
        ApplyEarlier(kit, victim, ThornsLikeProc);

        combat.AttackerStateUpdate(attacker, victim, WeaponAttackType.BaseAttack);

        Assert.Equal(990u, victim.Health); // the swing
        Assert.Equal(980u, attacker.Health); // the 20 holy damage the proc dealt back
    }

    [Fact]
    public void AnExtraAttackProc_QueuesOneExtraAttack_AndCannotProcAgainWhileItIsPending()
    {
        (SpellTestKit kit, Player attacker, Player victim, MapCombat combat) = MeleeKit();
        using SpellTestKit _ = kit;
        ApplyEarlier(kit, attacker, ExtraAttackAura);

        combat.AttackerStateUpdate(attacker, victim, WeaponAttackType.BaseAttack);
        Assert.Equal(1u, attacker.Combat.ExtraAttacks);

        combat.AttackerStateUpdate(attacker, victim, WeaponAttackType.BaseAttack); // "not allow proc extra attack spell at extra attack"
        Assert.Equal(1u, attacker.Combat.ExtraAttacks);
    }

    [Fact]
    public void ReflectSpells_TurnsAHarmfulMagicSpellBackOnItsCaster()
    {
        using SpellTestKit kit = NewKit();
        kit.System.CombatRules = new VanillaSpellCombatRules();
        (Player caster, FakeSession session) = kit.AddPlayer(1);
        (Player reflector, _) = kit.AddPlayer(2, 2);
        caster.Health = caster.MaxHealth = 1000;
        reflector.Health = reflector.MaxHealth = 1000;
        ApplyEarlier(kit, reflector, ReflectAll);
        session.Clear();

        kit.System.CastSpell(caster, FrostBolt, SpellCastTargets.ForUnit(reflector.Guid), triggered: true);

        Assert.Equal(1000u, reflector.Health);
        Assert.True(caster.Health < 1000u, "the reflected bolt hits its caster");
        var go = new PacketReader(Assert.Single(session.Sent, p => p.Opcode == WorldOpcode.SmsgSpellGo).Payload);
        go.ReadPackedGuid();
        go.ReadPackedGuid();
        Assert.Equal(FrostBolt, go.ReadUInt32());
        go.ReadUInt16(); // cast flags
        Assert.Equal(0, go.ReadByte()); // no hit targets
        Assert.Equal(1, go.ReadByte()); // one miss target
        Assert.Equal(reflector.Guid.Value, go.ReadUInt64());
        Assert.Equal((byte)SpellMissInfo.Reflect, go.ReadByte());
    }

    [Fact]
    public void WyvernStingsSleep_EndsOnTheDamageThatProcsIt_NotOnTheInterruptPath()
    {
        using SpellTestKit kit = NewKit();
        (Player attacker, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2);
        ApplyEarlier(kit, victim, WyvernStingRank1);

        kit.System.OnDamageTaken(victim, attacker, 10, periodic: false); // a bare damage break: procFlags auras are the proc engine's
        Assert.True(kit.System.HasAura(victim, WyvernStingRank1));

        kit.System.CastSpell(attacker, Hit, SpellCastTargets.ForUnit(victim.Guid), triggered: true); // "Any damage will cancel the effect"
        Assert.False(kit.System.HasAura(victim, WyvernStingRank1));
    }

    [Fact]
    public void DruidProwl_HasNoProcFlags_SoDamageOverTimeBreaksIt()
    {
        using SpellTestKit kit = NewKit();
        (Player attacker, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2);
        ApplyEarlier(kit, victim, ProwlRank1);
        kit.System.CastSpell(attacker, Dot, SpellCastTargets.ForUnit(victim.Guid), triggered: true);

        kit.Advance(3000);

        Assert.True(victim.Health < victim.MaxHealth);
        Assert.False(kit.System.HasAura(victim, ProwlRank1)); // the old id exemption kept Prowl through damage
    }

    [Fact]
    public void APeriodicTick_ProcsTakeHarmfulPeriodicAuras()
    {
        using SpellTestKit kit = NewKit();
        (Player attacker, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2);
        ApplyEarlier(kit, victim, PeriodicCharge);
        kit.System.CastSpell(attacker, Dot, SpellCastTargets.ForUnit(victim.Guid), triggered: true);
        Assert.True(kit.System.HasAura(victim, PeriodicCharge)); // the cast itself is no periodic event

        kit.Advance(3000);

        Assert.False(kit.System.HasAura(victim, PeriodicCharge));
    }

    [Fact]
    public void AddTargetTrigger_CastsItsTriggerSpellOnTheTargetOfAnAffectedSpell()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        ApplyEarlier(kit, caster, TargetTriggerAura);

        kit.System.CastSpell(caster, Hit, SpellCastTargets.ForUnit(target.Guid), triggered: true); // not in the aura's class mask
        Assert.False(kit.System.HasAura(target, TargetTriggerDebuff));

        kit.System.CastSpell(caster, FamilySpell, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        Assert.True(kit.System.HasAura(target, TargetTriggerDebuff));
    }
}
