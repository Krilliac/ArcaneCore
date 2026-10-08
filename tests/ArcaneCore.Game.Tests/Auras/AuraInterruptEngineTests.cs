using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Auras;

/// <summary>
/// The damage break of vmangos Unit::RemoveAurasWithInterruptFlags(AURA_INTERRUPT_DAMAGE_CANCELS, damaging spell id, checkProcFlags)
/// (Unit.cpp:3735-3751; callers :735-745 and :895-906): an aura whose spell has procFlags is skipped and the aura of the
/// spell that dealt the damage is excepted.
/// </summary>
public sealed class AuraInterruptEngineTests
{
    private const uint Sleep = 944001;
    private const uint ProcSleep = 944002;
    private const uint SelfBreakingDot = 944003;
    private const uint Hit = 944004;
    private const uint StealthLike = 944005;
    private const uint WyvernStingRank1 = 19386; // the real ids, which the replaced id exemption keyed on
    private const uint ProwlRank1 = 5215;
    private const AuraType Probe = SpellHandlerModuleTests.FixtureAura;

    private static SpellInfo Aura(uint id, ProcFlags proc = ProcFlags.None, uint dispel = 0) => Spell(
        id, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, Probe)) with
    {
        AuraInterruptFlags = SpellAuraInterruptFlags.Damage,
        ProcFlags = proc,
        Dispel = dispel,
        Duration = new SpellDuration(60000, 0, 60000),
        SpellVisual = 1,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellTestKit NewKit() => new(
        Aura(Sleep),
        Aura(ProcSleep, proc: ProcFlags.TakenAnyDamage),
        Aura(StealthLike, dispel: 5),
        Aura(WyvernStingRank1, proc: ProcFlags.TakenAnyDamage),
        Aura(ProwlRank1, proc: ProcFlags.TakenAnyDamage, dispel: 5),
        Spell(SelfBreakingDot, Effect(SpellEffectName.ApplyAura, 4, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 3000)) with
        {
            AuraInterruptFlags = SpellAuraInterruptFlags.Damage,
            Attributes = SpellAttributes.AuraIsDebuff,
            Duration = new SpellDuration(15000, 0, 15000),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        },
        Spell(Hit, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)) with
        {
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        });

    [Fact]
    public void DamageBreak_RemovesTheAuraWithoutProcFlags_AndSkipsOneThatHasThem()
    {
        using SpellTestKit kit = NewKit();
        kit.System.AuraOptions = new AuraOptions { ProcEngineBreaksDamageAuras = true };
        (Player attacker, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2);
        kit.System.CastSpell(victim, Sleep, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(victim, ProcSleep, SpellCastTargets.ForSelf(), triggered: true);

        kit.System.OnDamageTaken(victim, attacker, 10, periodic: false);

        Assert.False(kit.System.HasAura(victim, Sleep));
        Assert.True(kit.System.HasAura(victim, ProcSleep)); // HEAD removed every Damage-flag aura: Wyvern Sting/Prowl broke on their own hit
    }

    [Fact]
    public void DamageBreak_WithTheProcEngineSwitchedOff_AlsoBreaksProcFlagAuras()
    {
        // Auras:ProcEngineBreaksDamageAuras = false is the engine's behaviour before the proc engine: the interrupt path ignores procFlags.
        using SpellTestKit kit = NewKit();
        kit.System.AuraOptions = new AuraOptions { ProcEngineBreaksDamageAuras = false };
        (Player attacker, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2);
        kit.System.CastSpell(victim, ProcSleep, SpellCastTargets.ForSelf(), triggered: true);

        kit.System.OnDamageTaken(victim, attacker, 10, periodic: false);

        Assert.False(kit.System.HasAura(victim, ProcSleep));
    }

    [Fact]
    public void DamageBreak_AlsoSkipsProcFlagAuras_WhenTheHitWasFullyAbsorbed()
    {
        using SpellTestKit kit = NewKit();
        kit.System.AuraOptions = new AuraOptions { ProcEngineBreaksDamageAuras = true };
        (Player attacker, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2);
        kit.System.CastSpell(victim, Sleep, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(victim, ProcSleep, SpellCastTargets.ForSelf(), triggered: true);

        kit.System.OnDamageTaken(victim, attacker, 0, periodic: false, absorbed: 10);

        Assert.False(kit.System.HasAura(victim, Sleep));
        Assert.True(kit.System.HasAura(victim, ProcSleep));
    }

    [Fact]
    public void DamageBreak_ExceptsTheAuraOfTheSpellThatDealtTheDamage()
    {
        using SpellTestKit kit = NewKit();
        (Player attacker, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2);
        kit.System.CastSpell(victim, Sleep, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(attacker, SelfBreakingDot, SpellCastTargets.ForUnit(victim.Guid), triggered: true);

        kit.Advance(3000); // the DoT ticks: its own aura carries the Damage flag but is the damaging spell

        Assert.True(victim.Health < victim.MaxHealth);
        Assert.True(kit.System.HasAura(victim, SelfBreakingDot));
        Assert.False(kit.System.HasAura(victim, Sleep));
    }

    [Fact]
    public void DamageBreak_FromADirectSpell_ExceptsThatSpellsOwnAuraToo()
    {
        using SpellTestKit kit = NewKit();
        (Player attacker, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2);
        kit.System.CastSpell(victim, Sleep, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(victim, SelfBreakingDot, SpellCastTargets.ForSelf(), triggered: true);

        kit.System.OnDamageTaken(victim, attacker, 10, periodic: false, sourceSpellId: SelfBreakingDot);

        Assert.True(kit.System.HasAura(victim, SelfBreakingDot));
        Assert.False(kit.System.HasAura(victim, Sleep));
    }

    [Fact]
    public void DamageBreak_StillBreaksStealthAuras_BecauseSkipStealthIsFalseAboveBuild1_6_1()
    {
        using SpellTestKit kit = NewKit();
        (Player attacker, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2);
        kit.System.CastSpell(victim, StealthLike, SpellCastTargets.ForSelf(), triggered: true);

        kit.System.OnDamageTaken(victim, attacker, 10, periodic: false);

        Assert.False(kit.System.HasAura(victim, StealthLike));
    }

    [Fact]
    public void RemoveAurasWithInterruptFlags_WithCheckProcFlags_LeavesProcFlagAurasOnly()
    {
        using SpellTestKit kit = NewKit();
        (Player victim, _) = kit.AddPlayer(2, 2);
        kit.System.CastSpell(victim, Sleep, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(victim, ProcSleep, SpellCastTargets.ForSelf(), triggered: true);

        int removed = kit.System.RemoveAurasWithInterruptFlags(victim, (uint)SpellAuraInterruptFlags.Damage, checkProcFlags: true);

        Assert.Equal(1, removed);
        Assert.Equal(1, kit.System.RemoveAurasWithInterruptFlags(victim, (uint)SpellAuraInterruptFlags.Damage));
    }

    [Fact]
    public void DamageBreak_ByDefault_IsVmangosCheckProcFlags_ForEveryProcFlagAura_NotAnIdList()
    {
        // The proc engine exists, so the damage break skips every procFlags aura (Unit.cpp:735-745, 895-906) and breaks every aura without
        // them; no spell is special-cased by id any more (Wyvern Sting ends through its damage proc, ProcEngineBehaviourTests).
        using SpellTestKit kit = NewKit();
        Assert.True(kit.System.AuraOptions.ProcEngineBreaksDamageAuras);
        (Player attacker, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2);
        kit.System.CastSpell(victim, WyvernStingRank1, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(victim, ProwlRank1, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(victim, ProcSleep, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(victim, Sleep, SpellCastTargets.ForSelf(), triggered: true);

        kit.System.OnDamageTaken(victim, attacker, 10, periodic: false);

        Assert.True(kit.System.HasAura(victim, WyvernStingRank1));
        Assert.True(kit.System.HasAura(victim, ProwlRank1)); // this fixture gives it procFlags; the real druid Prowl has none
        Assert.True(kit.System.HasAura(victim, ProcSleep));
        Assert.False(kit.System.HasAura(victim, Sleep));
    }
}
