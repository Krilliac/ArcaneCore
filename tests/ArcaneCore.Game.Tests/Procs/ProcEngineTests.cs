using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Procs;
using ArcaneCore.Game.Tests.SpellRules;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.Procs;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Procs;

/// <summary>
/// The proc engine's own rules: spell_proc_event conditions (SpellMgr::IsSpellProcEventCanTriggeredBy, PPM, custom chance, cooldown, family masks),
/// charges and PROC_FAILURE_BURNS_CHARGE (Unit::HandleTriggers), the per-type handlers (UnitAuraProcHandler.cpp), damage shields
/// (Unit::TriggerDamageShields), kills, reflect charges and the registry seams other lanes extend.
/// </summary>
public sealed class ProcEngineTests
{
    private const uint SwingProc = 992_001;
    private const uint SwingBuff = 992_002;
    private const uint Thorns = 992_003;
    private const uint KillProc = 992_004;
    private const uint KillBuff = 992_005;
    private const uint FrostReflect = 992_006;
    private const uint FrostBolt = 992_007;
    private const uint FireBolt = 992_008;
    private const uint FamilyProc = 992_009;
    private const uint FamilyBolt = 992_010;
    private const uint OtherFamilyBolt = 992_011;
    private const uint Root = 992_012;
    private const uint Hit = 992_013;
    private const uint BurnCharge = 992_014;
    private const uint CritOnly = 992_015;

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

    private static SpellInfo NoGcd(SpellInfo spell) => spell with { StartRecoveryCategory = 0, StartRecoveryTime = 0 };

    private static SpellTestKit NewKit() => new(
        Permanent(SwingProc, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ProcTriggerSpell, trigger: SwingBuff)) with
        {
            ProcFlags = ProcFlags.DealMeleeSwing,
            ProcChance = 100,
        },
        RuleTestSupport.Grant(SwingBuff, AuraType.ModResistance, 5, misc: 1) with { Duration = new SpellDuration(10000, 0, 10000) },
        Permanent(Thorns, Effect(SpellEffectName.ApplyAura, 25, aura: AuraType.DamageShield)) with { School = SpellSchool.Nature },
        Permanent(KillProc, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ProcTriggerSpell, trigger: KillBuff)) with
        {
            ProcFlags = ProcFlags.Kill,
            ProcChance = 100,
        },
        RuleTestSupport.Grant(KillBuff, AuraType.ModResistance, 5, misc: 1) with { Duration = new SpellDuration(10000, 0, 10000) },
        RuleTestSupport.Grant(FrostReflect, AuraType.ReflectSpellsSchool, 100, misc: (int)(1u << (int)SpellSchool.Frost)) with
        {
            ProcFlags = ProcFlags.TakeHarmfulSpell,
            ProcChance = 100,
            ProcCharges = 1,
        },
        NoGcd(RuleTestSupport.Magic(FrostBolt, SpellSchool.Frost)),
        NoGcd(RuleTestSupport.Magic(FireBolt, SpellSchool.Fire)),
        Permanent(FamilyProc, Effect(SpellEffectName.ApplyAura, 5, aura: AuraType.ModResistance, misc: 1)) with
        {
            ProcFlags = ProcFlags.DealHarmfulSpell,
            ProcChance = 100,
            ProcCharges = 3,
        },
        NoGcd(RuleTestSupport.Magic(FamilyBolt) with { SpellFamilyName = 3, SpellFamilyFlags = 0x20 }),
        NoGcd(RuleTestSupport.Magic(OtherFamilyBolt) with { SpellFamilyName = 3, SpellFamilyFlags = 0x40 }),
        Spell(Root, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.ModRoot)) with
        {
            Attributes = SpellAttributes.AuraIsDebuff,
            ProcFlags = ProcFlags.TakenAnyDamage,
            ProcChance = 100,
            Duration = new SpellDuration(60000, 0, 60000),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        },
        NoGcd(Spell(Hit, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy))),
        Permanent(BurnCharge, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ProcTriggerSpell, trigger: 999_999)) with
        {
            Attributes = (SpellAttributes)ProcAttributes.ProcFailureBurnsCharge,
            ProcFlags = ProcFlags.DealMeleeSwing,
            ProcChance = 100,
            ProcCharges = 2,
        },
        Permanent(CritOnly, Effect(SpellEffectName.ApplyAura, 5, aura: AuraType.ModResistance, misc: 1)) with
        {
            ProcFlags = ProcFlags.DealMeleeSwing,
            ProcChance = 100,
            ProcCharges = 1,
        });

    private static (SpellTestKit Kit, Player Attacker, Player Victim) Kit()
    {
        SpellTestKit kit = NewKit();
        (Player attacker, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2);
        attacker.Health = attacker.MaxHealth = 1000;
        victim.Health = victim.MaxHealth = 1000;
        return (kit, attacker, victim);
    }

    private static MeleeDamageInfo Swing(Unit attacker, Unit victim, MeleeHitOutcome outcome = MeleeHitOutcome.Normal, uint damage = 10) => new()
    {
        Attacker = attacker,
        Target = victim,
        AttackType = WeaponAttackType.BaseAttack,
        Outcome = outcome,
        HitInfo = HitInfo.AffectsVictim,
        TargetState = outcome switch
        {
            MeleeHitOutcome.Parry => VictimState.Parry,
            MeleeHitOutcome.Dodge => VictimState.Dodge,
            _ => VictimState.Normal,
        },
        TotalDamage = outcome is MeleeHitOutcome.Parry or MeleeHitOutcome.Dodge or MeleeHitOutcome.Miss ? 0 : damage,
    };

    [Fact]
    public void ThePpmRate_TurnsTheWeaponSpeedIntoTheChance()
    {
        Assert.Equal(10f, SpellSystem.PpmChance(3000, 2f)); // 3.0 s weapon, 2 procs a minute: 10% per swing
        Assert.Equal(4f, SpellSystem.PpmChance(1200, 2f));
    }

    [Fact]
    public void SpellFlags_FollowTheDamageClass_LikePrepareMasksForProcSystem()
    {
        SpellInfo autoShot = Spell(75, Effect(SpellEffectName.WeaponDamage, 0)) with
        {
            DamageClass = SpellDamageClass.Ranged,
            AttributesEx2 = (SpellAttributesEx2)ProcAttributes.Ex2AutoRepeat,
        };
        SpellInfo aimed = autoShot with { Id = 19434, AttributesEx2 = SpellAttributesEx2.None };
        SpellInfo strike = Spell(78, Effect(SpellEffectName.WeaponDamage, 0)) with { DamageClass = SpellDamageClass.Melee };
        SpellInfo bolt = RuleTestSupport.Magic(116);

        Assert.Equal((ProcFlags.DealRangedAttack, ProcFlags.TakeRangedAttack), ProcFlagRules.SpellFlags(autoShot, WeaponAttackType.RangedAttack));
        Assert.Equal((ProcFlags.DealRangedAbility, ProcFlags.TakeRangedAbility), ProcFlagRules.SpellFlags(aimed, WeaponAttackType.RangedAttack));
        Assert.Equal((ProcFlags.DealMeleeAbility | ProcFlags.MainHandWeaponSwing, ProcFlags.TakeMeleeAbility),
            ProcFlagRules.SpellFlags(strike, WeaponAttackType.BaseAttack));
        Assert.Equal((ProcFlags.DealHarmfulSpell, ProcFlags.TakeHarmfulSpell), ProcFlagRules.SpellFlags(bolt, WeaponAttackType.BaseAttack));
        Assert.Equal((ProcFlags.None, ProcFlags.None), ProcFlagRules.SpellFlags(aimed with { Id = 2094 }, WeaponAttackType.RangedAttack)); // Blind
    }

    [Fact]
    public void ASpellProcEventRow_ReplacesTheChance_AndItsPpmRateFollowsTheAttackTime()
    {
        (SpellTestKit kit, Player attacker, Player victim) = Kit();
        using SpellTestKit _ = kit;
        ApplyEarlier(kit, attacker, SwingProc);
        kit.System.ProcEvents = new Catalog(new SpellProcEventRecord(SwingProc, 0, 0, 0, 0, 0, 0, 0, PpmRate: 0.0001f, 0, 0));
        kit.System.Random = new SpellRules.ScriptedRandom(500); // 5%: above the PPM chance of a 2 s weapon at 0.0001 PPM

        kit.System.OnMeleeSwingResolved(Swing(attacker, victim));
        Assert.False(kit.System.HasAura(attacker, SwingBuff));

        kit.System.ProcEvents = new Catalog(new SpellProcEventRecord(SwingProc, 0, 0, 0, 0, 0, 0, 0, 0, CustomChance: 6f, 0));
        kit.System.Random = new SpellRules.ScriptedRandom(500);
        kit.System.OnMeleeSwingResolved(Swing(attacker, victim));
        Assert.True(kit.System.HasAura(attacker, SwingBuff));
    }

    [Fact]
    public void TheHiddenCooldown_KeepsTheTriggeredSpellFromProccingAgainUntilItRunsOut()
    {
        (SpellTestKit kit, Player attacker, Player victim) = Kit();
        using SpellTestKit _ = kit;
        ApplyEarlier(kit, attacker, SwingProc);
        kit.System.ProcEvents = new Catalog(new SpellProcEventRecord(SwingProc, 0, 0, 0, 0, 0, 0, 0, 0, 0, Cooldown: 5000));

        kit.System.OnMeleeSwingResolved(Swing(attacker, victim));
        Assert.True(kit.System.HasAura(attacker, SwingBuff));
        kit.System.RemoveAuras(attacker, SwingBuff);

        kit.System.OnMeleeSwingResolved(Swing(attacker, victim));
        Assert.False(kit.System.HasAura(attacker, SwingBuff));

        kit.Advance(5000);
        kit.System.OnMeleeSwingResolved(Swing(attacker, victim));
        Assert.True(kit.System.HasAura(attacker, SwingBuff));
    }

    [Fact]
    public void AProcExRequirement_FiltersTheOutcome()
    {
        (SpellTestKit kit, Player attacker, Player victim) = Kit();
        using SpellTestKit _ = kit;
        ApplyEarlier(kit, attacker, CritOnly);
        kit.System.ProcEvents = new Catalog(new SpellProcEventRecord(CritOnly, 0, 0, 0, 0, 0, 0, (uint)ProcFlagsEx.CriticalHit, 0, 0, 0));

        kit.System.OnMeleeSwingResolved(Swing(attacker, victim));
        Assert.True(kit.System.HasAura(attacker, CritOnly)); // a normal hit does not spend the crit-only charge

        kit.System.OnMeleeSwingResolved(Swing(attacker, victim, MeleeHitOutcome.Crit));
        Assert.False(kit.System.HasAura(attacker, CritOnly));
    }

    [Fact]
    public void AFamilyMask_LetsOnlyTheMatchingSpellsSpendTheCharges()
    {
        (SpellTestKit kit, Player caster, Player target) = Kit();
        using SpellTestKit _ = kit;
        ApplyEarlier(kit, caster, FamilyProc);
        kit.System.ProcEvents = new Catalog(new SpellProcEventRecord(FamilyProc, 0, 3, SpellFamilyMask0: 0x20, 0, 0, 0, 0, 0, 0, 0));

        kit.System.CastSpell(caster, OtherFamilyBolt, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        Assert.Equal(3, kit.System.GetAuras(caster).Single(h => h.Spell.Id == FamilyProc).Charges);

        kit.System.CastSpell(caster, FamilyBolt, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        Assert.Equal(2, kit.System.GetAuras(caster).Single(h => h.Spell.Id == FamilyProc).Charges);
    }

    [Fact]
    public void ProcFailureBurnsCharge_SpendsAChargeOnlyWhenTheHandlerFails()
    {
        (SpellTestKit kit, Player attacker, Player victim) = Kit();
        using SpellTestKit _ = kit;
        ApplyEarlier(kit, attacker, BurnCharge); // its trigger spell does not exist: the handler fails every time

        kit.System.OnMeleeSwingResolved(Swing(attacker, victim));

        Assert.Equal(1, kit.System.GetAuras(attacker).Single(h => h.Spell.Id == BurnCharge).Charges);
    }

    [Fact]
    public void DamageShields_HitTheAttackerOfAnAffectedSwing_ButNotAParriedOrDodgedOne()
    {
        (SpellTestKit kit, Player attacker, Player victim) = Kit();
        using SpellTestKit _ = kit;
        (Player observer, FakeSession session) = kit.AddPlayer(3, 1);
        ApplyEarlier(kit, victim, Thorns);
        session.Clear();

        kit.System.OnMeleeWeaponHit(Swing(attacker, victim, MeleeHitOutcome.Parry));
        kit.System.OnMeleeWeaponHit(Swing(attacker, victim, MeleeHitOutcome.Dodge));
        Assert.Equal(1000u, attacker.Health);

        kit.System.OnMeleeWeaponHit(Swing(attacker, victim));

        Assert.Equal(975u, attacker.Health);
        var shield = new PacketReader(Assert.Single(session.Sent, p => p.Opcode == WorldOpcode.SmsgSpelldamageshield).Payload);
        Assert.Equal(victim.Guid.Value, shield.ReadUInt64());
        Assert.Equal(attacker.Guid.Value, shield.ReadUInt64());
        Assert.Equal(25u, shield.ReadUInt32());
        Assert.Equal((uint)SpellSchool.Nature, shield.ReadUInt32());
        Assert.NotNull(observer);
    }

    [Fact]
    public void AKill_ProcsTheKillersKillAuras()
    {
        (SpellTestKit kit, Player killer, Player victim) = Kit();
        using SpellTestKit _ = kit;
        ApplyEarlier(kit, killer, KillProc);
        killer.Level = 1;
        victim.Level = 1;

        kit.System.OnUnitKilled(killer, victim);

        Assert.True(kit.System.HasAura(killer, KillBuff));
    }

    [Fact]
    public void AReflect_SpendsTheChargeOfTheReflectAura_ThroughItsSpellProcEventReflectRequirement()
    {
        (SpellTestKit kit, Player caster, Player reflector) = Kit();
        using SpellTestKit _ = kit;
        kit.System.CombatRules = new AlwaysHitRules();
        ApplyEarlier(kit, reflector, FrostReflect);
        kit.System.ProcEvents = new Catalog(new SpellProcEventRecord(FrostReflect, 0, 0, 0, 0, 0, 0, (uint)ProcFlagsEx.Reflect, 0, 0, 0));

        kit.System.CastSpell(caster, FireBolt, SpellCastTargets.ForUnit(reflector.Guid), triggered: true);
        Assert.True(reflector.Health < 1000u, "fire is not in the reflect mask");
        Assert.True(kit.System.HasAura(reflector, FrostReflect));
        uint afterFire = reflector.Health;

        kit.System.CastSpell(caster, FrostBolt, SpellCastTargets.ForUnit(reflector.Guid), triggered: true);

        Assert.Equal(afterFire, reflector.Health);
        Assert.True(caster.Health < 1000u);
        Assert.False(kit.System.HasAura(reflector, FrostReflect)); // its one charge went on the reflect
    }

    [Fact]
    public void RootsWithProcFlags_BreakByTheDamageChance_OfTheRemoveByDamageChanceHandler()
    {
        (SpellTestKit kit, Player attacker, Player victim) = Kit();
        using SpellTestKit _ = kit;
        victim.Level = 1; // max damage 50 below level 9: a 10 damage hit is a 20% chance
        kit.System.CastSpell(attacker, Root, SpellCastTargets.ForUnit(victim.Guid), triggered: true);
        kit.Now++; // the root is the attacker's own aura: the hits below come a millisecond later (Unit.cpp:8958)
        Assert.True(kit.System.HasAura(victim, Root));

        kit.System.Random = new FixedDoubleRandom(0.25); // the proc roll passes (100%), the break roll 25% misses the 20% chance
        kit.System.CastSpell(attacker, Hit, SpellCastTargets.ForUnit(victim.Guid), triggered: true);
        Assert.True(kit.System.HasAura(victim, Root));

        kit.System.Random = new FixedDoubleRandom(0.15); // 15% < 20%: the root breaks
        kit.System.CastSpell(attacker, Hit, SpellCastTargets.ForUnit(victim.Guid), triggered: true);
        Assert.False(kit.System.HasAura(victim, Root));
    }

    [Fact]
    public void AProcScript_DecidesTheCheckAndTheProc_AndASecondScriptForTheSameSpellIsRefused()
    {
        (SpellTestKit kit, Player attacker, Player victim) = Kit();
        using SpellTestKit _ = kit;
        ApplyEarlier(kit, attacker, SwingProc);
        var script = new CountingScript();
        kit.System.RegisterProcScript(SwingProc, script);

        kit.System.OnMeleeSwingResolved(Swing(attacker, victim));

        Assert.Equal((1, 1), (script.Checks, script.Procs));
        Assert.False(kit.System.HasAura(attacker, SwingBuff)); // the script handled the proc instead of the trigger-spell handler
        Assert.Throws<InvalidOperationException>(() => kit.System.RegisterProcScript(SwingProc, new CountingScript()));
    }

    [Fact]
    public void AProcHandler_CanBeInstalledForAnAuraType()
    {
        (SpellTestKit kit, Player attacker, Player victim) = Kit();
        using SpellTestKit _ = kit;
        ApplyEarlier(kit, attacker, CritOnly);
        int calls = 0;
        kit.System.RegisterProcHandler(AuraType.ModResistance, (in AuraProcContext context) =>
        {
            calls++;
            return AuraProcResult.CantTrigger; // the effect is skipped: no charge is spent
        });

        kit.System.OnMeleeSwingResolved(Swing(attacker, victim));

        Assert.Equal(1, calls);
        Assert.True(kit.System.HasAura(attacker, CritOnly));
    }

    private sealed class FixedDoubleRandom(double value) : Random(3)
    {
        public override double NextDouble() => value;
    }

    private sealed class Catalog(params SpellProcEventRecord[] rows) : ISpellProcEventCatalog
    {
        public SpellProcEventRecord? Find(uint spellId) => rows.FirstOrDefault(r => r.Entry == spellId);
    }

    private sealed class CountingScript : IProcScript
    {
        public int Checks { get; private set; }

        public int Procs { get; private set; }

        public ProcTriggerCheck? CheckProc(in ProcCheckContext context)
        {
            Checks++;
            return ProcTriggerCheck.Ok;
        }

        public AuraProcResult? OnProc(in AuraProcContext context)
        {
            Procs++;
            return AuraProcResult.Ok;
        }
    }

    /// <summary>Every spell lands and nothing is mitigated, but the reflect step of the vanilla rules still runs.</summary>
    private sealed class AlwaysHitRules : VanillaSpellCombatRules
    {
        public override SpellMissInfo RollHit(SpellSystem system, Unit caster, Unit target, SpellInfo spell)
            => system.RollSpellReflect(caster, target, spell) ? SpellMissInfo.Reflect : SpellMissInfo.None;

        public override bool RollCrit(SpellSystem system, Unit caster, Unit target, SpellInfo spell) => false;
    }
}
