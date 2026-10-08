using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Casters;
using ArcaneCore.Game.Spells.Procs;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Tests.SpellRules;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.Procs;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Procs;

/// <summary>
/// Proc-engine fidelity points from the lane review: the leech and Improved Drain Mana ticks (vmangos Aura::PeriodicTick, SpellAuras.cpp:5917,
/// 5998, 6200-6206), the apply-time guard (Unit.cpp:8958, <c>&gt;=</c>), PROC_EX_REFLECT on reflected damage (CreateProcExtendMask, Unit.cpp:8776),
/// heal procs before the heal (Spell.cpp:1335-1352), CAST_END alone when the main target is not a target (Spell.cpp:3729-3740) and the damage-proc
/// cancel only on a successful proc.
/// </summary>
public sealed class ProcEngineFidelityTests
{
    private const uint PeriodicCharge = 993_001;
    private const uint SiphonLike = 993_002;
    private const uint ManaLeech = 993_003;
    private const uint ImprovedDrainManaRank1 = 17864; // DrainAuras keys the talent on its real id
    private const uint ReflectAll = 993_004;
    private const uint MarkThenHit = 993_005;
    private const uint FreshMark = 993_006;
    private const uint ReflectOnly = 993_007;
    private const uint FrostBolt = 993_008;
    private const uint SelfHeal = 993_009;
    private const uint SelfBuff = 993_010;
    private const uint SleepLike = 993_011;
    private const uint Hit = 993_012;
    private const uint RefreshMark = 993_013;
    private const uint RefreshThenHit = 993_014;

    private static SpellInfo Permanent(uint id, SpellEffectInfo effect) => Spell(id, effect) with
    {
        Duration = new SpellDuration(-1, 0, -1),
        SpellVisual = 1,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellInfo NoGcd(SpellInfo spell) => spell with { StartRecoveryCategory = 0, StartRecoveryTime = 0 };

    private static SpellInfo Debuff(uint id, SpellEffectInfo effect) => NoGcd(Spell(id, effect)) with
    {
        Attributes = SpellAttributes.AuraIsDebuff,
        Duration = new SpellDuration(5000, 0, 5000),
        School = SpellSchool.Shadow,
        DamageClass = SpellDamageClass.Magic,
        SpellVisual = 1,
    };

    private static SpellTestKit NewKit() => new(
        Permanent(PeriodicCharge, Effect(SpellEffectName.ApplyAura, 5, aura: AuraType.ModResistance, misc: 1)) with
        {
            ProcFlags = ProcFlags.TakeHarmfulPeriodic,
            ProcChance = 100,
            ProcCharges = 1,
        },
        // Siphon Life's shape: a PERIODIC_LEECH debuff that is not channelled (so it can be reflected and tick on its caster).
        Debuff(SiphonLike, Effect(SpellEffectName.ApplyAura, 9, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicLeech, amplitude: 1000)),
        Debuff(ManaLeech, Effect(SpellEffectName.ApplyAura, 10, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicManaLeech, amplitude: 1000, misc: 0)
            with { MultipleValue = 1.0f }),
        Spell(ImprovedDrainManaRank1, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
        {
            Attributes = SpellAttributes.Passive,
            Duration = new SpellDuration(-1, 0, -1),
            School = SpellSchool.Shadow,
        },
        RuleTestSupport.Grant(ReflectAll, AuraType.ReflectSpells, 100),
        // Effect 0 puts another spell's aura on the target, effect 1 then hits it: the fresh aura must not proc from that hit.
        NoGcd(Spell(MarkThenHit,
            Effect(SpellEffectName.TriggerSpell, 0, SpellImplicitTarget.UnitEnemy, trigger: FreshMark),
            Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)) with { DamageClass = SpellDamageClass.Magic }),
        NoGcd(Spell(FreshMark, Effect(SpellEffectName.ApplyAura, -5, SpellImplicitTarget.UnitEnemy, AuraType.ModResistance, misc: 1))) with
        {
            Attributes = SpellAttributes.AuraIsDebuff,
            Duration = new SpellDuration(60000, 0, 60000),
            SpellVisual = 1,
            ProcFlags = ProcFlags.TakeHarmfulSpell,
            ProcChance = 100,
            ProcCharges = 1,
        },
        Permanent(ReflectOnly, Effect(SpellEffectName.ApplyAura, 5, aura: AuraType.ModResistance, misc: 1)) with
        {
            ProcFlags = ProcFlags.DealHarmfulSpell,
            ProcChance = 100,
            ProcCharges = 2,
        },
        NoGcd(RuleTestSupport.Magic(FrostBolt, SpellSchool.Frost)),
        NoGcd(Spell(SelfHeal, Effect(SpellEffectName.Heal, 50, SpellImplicitTarget.UnitCaster))),
        Permanent(SelfBuff, Effect(SpellEffectName.ApplyAura, 5, SpellImplicitTarget.UnitCaster, AuraType.ModResistance, misc: 1)),
        NoGcd(Spell(SleepLike, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, SpellHandlerModuleTests.FixtureAura))) with
        {
            AuraInterruptFlags = SpellAuraInterruptFlags.Damage,
            ProcFlags = ProcFlags.TakenAnyDamage,
            ProcChance = 100,
            Duration = new SpellDuration(60000, 0, 60000),
            SpellVisual = 1,
        },
        NoGcd(Spell(Hit, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)) with { DamageClass = SpellDamageClass.Magic }),
        // A mark with no charges and no stacks, so the same caster's second application refreshes the holder in place (CanBeRefreshedBy).
        NoGcd(Spell(RefreshMark, Effect(SpellEffectName.ApplyAura, -5, SpellImplicitTarget.UnitEnemy, AuraType.ModResistance, misc: 1))) with
        {
            Attributes = SpellAttributes.AuraIsDebuff,
            Duration = new SpellDuration(60000, 0, 60000),
            SpellVisual = 1,
            ProcFlags = ProcFlags.TakeHarmfulSpell,
            ProcChance = 100,
        },
        NoGcd(Spell(RefreshThenHit,
            Effect(SpellEffectName.TriggerSpell, 0, SpellImplicitTarget.UnitEnemy, trigger: RefreshMark),
            Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)) with { DamageClass = SpellDamageClass.Magic }));

    private static (SpellTestKit Kit, Player Caster, Player Target) Kit()
    {
        SpellTestKit kit = NewKit();
        CasterSpellModules.Register(kit.System);
        kit.System.CombatRules = new AlwaysHitRules();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        caster.Health = caster.MaxHealth = 1000;
        target.Health = target.MaxHealth = 1000;
        return (kit, caster, target);
    }

    [Fact]
    public void ALeechTick_ProcsTakeHarmfulPeriodic_AndSpendsTheCharge()
    {
        (SpellTestKit kit, Player caster, Player target) = Kit();
        using SpellTestKit _ = kit;
        RuleTestSupport.Apply(kit, target, PeriodicCharge);
        kit.System.CastSpell(caster, SiphonLike, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        Assert.True(kit.System.HasAura(target, PeriodicCharge)); // the cast itself is no periodic event

        kit.Advance(1000);

        Assert.True(target.Health < 1000u, "the leech ticked");
        Assert.False(kit.System.HasAura(target, PeriodicCharge)); // vmangos SpellAuras.cpp:5998
    }

    [Fact]
    public void AReflectedLeech_DamagesItsOwnCasterAsReflectedDamage()
    {
        (SpellTestKit kit, Player caster, Player target) = Kit();
        using SpellTestKit _ = kit;
        var sink = new RecordingSink();
        kit.System.Damage = sink;
        RuleTestSupport.Apply(kit, target, ReflectAll);
        kit.System.CastSpell(caster, SiphonLike, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        Assert.True(kit.System.GetAuras(caster).Single(h => h.Spell.Id == SiphonLike).IsReflected);

        kit.Advance(1000);

        // vmangos SpellAuras.cpp:5999: DealDamage(..., GetHolder()->IsReflected()) — a reflected leech cannot kill its caster in a duel.
        (Unit victim, uint spell, bool reflected) = Assert.Single(sink.Damage);
        Assert.Same(caster, victim);
        Assert.Equal(SiphonLike, spell);
        Assert.True(reflected);
    }

    [Fact]
    public void ALeechTick_DealsItsDamageWithoutDurabilityLoss()
    {
        (SpellTestKit kit, Player caster, Player target) = Kit();
        using SpellTestKit _ = kit;
        var sink = new RecordingSink();
        kit.System.Damage = sink;
        kit.System.CastSpell(caster, SiphonLike, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        kit.Advance(1000);

        // vmangos SpellAuras.cpp:5999: DealDamage(target, pdamage, &cleanDamage, DOT, school, spellProto, durabilityLoss = false, ...): a player
        // killed by Drain Life or Siphon Life keeps their durability.
        Assert.Equal([false], sink.DurabilityLoss);
    }

    [Fact]
    public void AnImprovedDrainManaTick_IsAPeriodicDamageTick_ThatProcsTakeHarmfulPeriodic()
    {
        (SpellTestKit kit, Player caster, Player target) = Kit();
        using SpellTestKit _ = kit;
        GiveMana(caster, target);
        RuleTestSupport.Apply(kit, target, PeriodicCharge);
        kit.System.CastSpell(caster, ImprovedDrainManaRank1, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(caster, ManaLeech, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        kit.Advance(1000);

        Assert.Equal(40u, SpellSystem.GetPower(target, PowerType.Mana)); // 10 drained
        Assert.Equal(999u, target.Health); // 10 * 0.15 = 1 shadow damage under the talent spell
        Assert.False(kit.System.HasAura(target, PeriodicCharge)); // PeriodicTick(talent, PERIODIC_DAMAGE): SpellAuras.cpp:6203-6206 -> :5917
    }

    [Fact]
    public void AManaLeechTickWithoutTheTalent_ProcsNothing()
    {
        (SpellTestKit kit, Player caster, Player target) = Kit();
        using SpellTestKit _ = kit;
        GiveMana(caster, target);
        RuleTestSupport.Apply(kit, target, PeriodicCharge);
        kit.System.CastSpell(caster, ManaLeech, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        kit.Advance(1000);

        Assert.Equal(40u, SpellSystem.GetPower(target, PowerType.Mana));
        Assert.True(kit.System.HasAura(target, PeriodicCharge)); // vmangos' PERIODIC_MANA_LEECH block calls no ProcDamageAndSpell
    }

    [Fact]
    public void AnAuraTheActorAppliedDuringTheSameEvent_DoesNotProcFromIt()
    {
        (SpellTestKit kit, Player caster, Player target) = Kit();
        using SpellTestKit _ = kit;

        kit.System.CastSpell(caster, MarkThenHit, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.True(target.Health < 1000u, "effect 1 hit");
        // Unit.cpp:8958: GetAuraApplyTime() >= procTime and the aura's caster is the event's actor: skipped, so the charge is kept.
        Assert.Equal(1, kit.System.GetAuras(target).Single(h => h.Spell.Id == FreshMark).Charges);

        kit.Advance(1);
        kit.System.CastSpell(caster, Hit, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        Assert.False(kit.System.HasAura(target, FreshMark)); // a later hit spends it
    }

    [Fact]
    public void AnAuraAppliedBeforeTheMillisecondClockWrapped_StillProcs()
    {
        (SpellTestKit kit, Player caster, Player target) = Kit();
        using SpellTestKit _ = kit;
        kit.Now = uint.MaxValue - 50; // getMSTime wraps after ~49.7 days of uptime
        RuleTestSupport.Apply(kit, caster, ReflectOnly);
        kit.Now = 50;

        kit.System.CastSpell(caster, Hit, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.Equal(1, kit.System.GetAuras(caster).Single(h => h.Spell.Id == ReflectOnly).Charges); // 101 ms old, not "applied after the event"
    }

    [Fact]
    public void ASelfProcAuraOlderThanHalfTheMillisecondClock_StillProcs()
    {
        (SpellTestKit kit, Player caster, Player target) = Kit();
        using SpellTestKit _ = kit;
        kit.Now = 0;
        RuleTestSupport.Apply(kit, caster, ReflectOnly);
        kit.Now = (1u << 31) + 10; // ~24.9 days later: a signed 32-bit difference reads the aura as applied in the future

        kit.System.CastSpell(caster, Hit, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.Equal(1, kit.System.GetAuras(caster).Single(h => h.Spell.Id == ReflectOnly).Charges); // DEAL_HARMFUL_SPELL procs: 2 -> 1
    }

    [Fact]
    public void AnAuraTheSameEventApplied_DoesNotProcFromIt_EvenWhenTheClockMovesInsideTheEvent()
    {
        (SpellTestKit kit, Player caster, Player target) = Kit();
        using SpellTestKit _ = kit;
        // The live world clock keeps running while one hit is handled: the nested cast stamps the mark, then the damage effect procs later.
        kit.System.HolderAdded += holder =>
        {
            if (holder.Spell.Id == FreshMark)
            {
                kit.Now += 7;
            }
        };

        kit.System.CastSpell(caster, MarkThenHit, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.True(target.Health < 1000u, "effect 1 hit");
        Assert.Equal(1, kit.System.GetAuras(target).SingleOrDefault(h => h.Spell.Id == FreshMark)?.Charges); // not procced, charge kept
    }

    [Fact]
    public void AnAuraTheSameEventRefreshed_DoesNotProcFromIt()
    {
        (SpellTestKit kit, Player caster, Player target) = Kit();
        using SpellTestKit _ = kit;
        var script = new CountingProcScript();
        kit.System.RegisterProcScript(RefreshMark, script);
        kit.System.CastSpell(caster, RefreshMark, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        SpellAuraHolder mark = kit.System.GetAuras(target).Single(h => h.Spell.Id == RefreshMark);
        kit.Advance(1000);

        kit.System.CastSpell(caster, RefreshThenHit, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.True(target.Health < 1000u, "effect 1 hit");
        Assert.Same(mark, kit.System.GetAuras(target).Single(h => h.Spell.Id == RefreshMark)); // refreshed in place, not replaced
        Assert.Equal(0, script.Procs); // SpellAuras.cpp:368: the refresh resets the apply time, so the refreshing hit does not proc it

        kit.Advance(1);
        kit.System.CastSpell(caster, Hit, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        Assert.True(script.Procs > 0, "a later hit procs it");
    }

    [Fact]
    public void AReflectedDamagingSpell_ProcsWithProcExReflect()
    {
        (SpellTestKit kit, Player caster, Player reflector) = Kit();
        using SpellTestKit _ = kit;
        RuleTestSupport.Apply(kit, reflector, ReflectAll);
        RuleTestSupport.Apply(kit, caster, ReflectOnly);
        kit.System.ProcEvents = new Catalog(new SpellProcEventRecord(ReflectOnly, 0, 0, 0, 0, 0, 0, (uint)ProcFlagsEx.Reflect, 0, 0, 0));
        kit.Advance(1);

        kit.System.CastSpell(caster, FrostBolt, SpellCastTargets.ForUnit(reflector.Guid), triggered: true);

        Assert.True(caster.Health < 1000u, "the bolt came back");
        // CreateProcExtendMask(REFLECT) falls through to the hit bits (Unit.cpp:8811-8828): the reflected hit carries PROC_EX_REFLECT.
        Assert.Equal(1, kit.System.GetAuras(caster).Single(h => h.Spell.Id == ReflectOnly).Charges);
    }

    [Fact]
    public void AHealsProcs_FireBeforeTheHeal_WithTheWholeHealAsTheAmount()
    {
        (SpellTestKit kit, Player caster, _) = Kit();
        using SpellTestKit scope = kit;
        caster.Health = 980;
        var seen = new List<(uint Health, uint Amount)>();
        kit.System.ProcEventProcessed += (actor, e) =>
        {
            if (e.ProcSpell?.Id == SelfHeal && (e.Extra & ProcFlagsEx.CastEnd) == 0)
            {
                seen.Add((e.Victim!.Health, e.Amount));
            }
        };

        kit.System.CastSpell(caster, SelfHeal, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(1000u, caster.Health);
        Assert.Equal([(980u, 50u)], seen); // Spell.cpp:1335-1352: ProcDamageAndSpell(addhealth, addhealth) then DealHeal
    }

    [Fact]
    public void CastEndProcs_AreCastEndOnly_WhenTheMainTargetIsNotATargetOfTheSpell()
    {
        (SpellTestKit kit, Player caster, Player other) = Kit();
        using SpellTestKit _ = kit;
        var castEnd = new List<ProcFlagsEx>();
        kit.System.ProcEventProcessed += (actor, e) =>
        {
            if (e.ProcSpell?.Id == SelfBuff && (e.Extra & ProcFlagsEx.CastEnd) != 0)
            {
                castEnd.Add(e.Extra);
            }
        };

        kit.System.CastSpell(caster, SelfBuff, SpellCastTargets.ForUnit(other.Guid), triggered: true); // the unit target is not hit
        kit.System.CastSpell(caster, SelfBuff, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal([ProcFlagsEx.CastEnd, ProcFlagsEx.CastEnd | ProcFlagsEx.NormalHit], castEnd); // Spell.cpp:3729-3740
    }

    [Fact]
    public void TheDamageProcCancel_LeavesTheAuraWhenItsProcFailed()
    {
        (SpellTestKit kit, Player attacker, Player victim) = Kit();
        using SpellTestKit _ = kit;
        RuleTestSupport.Apply(kit, victim, SleepLike);
        kit.System.RegisterProcHandler(SpellHandlerModuleTests.FixtureAura, (in AuraProcContext context) => AuraProcResult.Failed);

        kit.System.CastSpell(attacker, Hit, SpellCastTargets.ForUnit(victim.Guid), triggered: true);

        Assert.True(victim.Health < 1000u);
        Assert.True(kit.System.HasAura(victim, SleepLike));
    }

    private static void GiveMana(Player caster, Player target)
    {
        target.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
        target.SetUInt32(UpdateFields.UnitFieldMaxpower1, 100);
        target.SetUInt32(UpdateFields.UnitFieldPower1, 50);
        caster.SetUInt32(UpdateFields.UnitFieldMaxpower1, 100);
    }

    private sealed class CountingProcScript : IProcScript
    {
        public int Procs { get; private set; }

        public AuraProcResult? OnProc(in AuraProcContext context)
        {
            Procs++;
            return null;
        }
    }

    private sealed class RecordingSink : IDamageSink
    {
        public List<(Unit Victim, uint Spell, bool Reflected)> Damage { get; } = [];

        public List<bool> DurabilityLoss { get; } = [];

        public uint DealSpellDamage(Unit caster, Unit victim, SpellInfo spell, uint damage, bool periodic)
            => ((IDamageSink)this).DealSpellDamage(caster, victim, spell, damage, periodic, true, false, true, false);

        public uint DealSpellDamage(Unit caster, Unit victim, SpellInfo spell, uint damage, bool periodic, bool startsCombat, bool critical, bool durabilityLoss,
            bool reflected)
        {
            Damage.Add((victim, spell.Id, reflected));
            DurabilityLoss.Add(durabilityLoss);
            uint dealt = Math.Min(damage, victim.Health);
            victim.Health -= dealt;
            return dealt;
        }

        public uint Heal(Unit caster, Unit target, SpellInfo spell, uint amount) => 0;
    }

    private sealed class Catalog(params SpellProcEventRecord[] rows) : ISpellProcEventCatalog
    {
        public SpellProcEventRecord? Find(uint spellId) => rows.FirstOrDefault(r => r.Entry == spellId);
    }

    /// <summary>Every spell lands and nothing crits, but the reflect step of the vanilla rules still runs.</summary>
    private sealed class AlwaysHitRules : VanillaSpellCombatRules
    {
        public override SpellMissInfo RollHit(SpellSystem system, Unit caster, Unit target, SpellInfo spell)
            => system.RollSpellReflect(caster, target, spell) ? SpellMissInfo.Reflect : SpellMissInfo.None;

        public override bool RollCrit(SpellSystem system, Unit caster, Unit target, SpellInfo spell) => false;
    }
}
