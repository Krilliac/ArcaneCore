using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Tests.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.SpellRules;

/// <summary>
/// Fully absorbed damage still breaks damage-cancels auras and interrupts a player's damage-cancels cast
/// (vmangos Unit::DealDamage damage==0 branch with cleanDamage->absorb, Unit.cpp:719-746: "interrupt spells like
/// trying to mount even through absorb shields"); it never pushes back or delays (that needs real damage, Unit.cpp:900-947).
/// </summary>
public sealed class AbsorbBreaksControlTests
{
    private const uint FireBolt = 981_001;
    private const uint Shield = 981_002;
    private const uint Sleep = 981_003;
    private const uint MountLike = 981_004;
    private const uint PushbackBolt = 981_005;

    private static SpellInfo Cast(uint id, SpellInterruptFlags interrupt) =>
        SpellTestKit.Spell(id, SpellTestKit.Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy))
        with
        {
            School = SpellSchool.Fire,
            DamageClass = SpellDamageClass.Magic,
            CastTime = new SpellCastTime(3000, 0, 0),
            InterruptFlags = interrupt,
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };

    private static SpellTestKit Kit() => new(
        SpellTestKit.Spell(FireBolt, SpellTestKit.Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)) with { School = SpellSchool.Fire, DamageClass = SpellDamageClass.Magic },
        RuleTestSupport.Grant(Shield, AuraType.SchoolAbsorb, 1000, (int)SpellSchoolMasks.All),
        RuleTestSupport.Grant(Sleep, AuraType.ModStun, 0) with { AuraInterruptFlags = SpellAuraInterruptFlags.Damage },
        Cast(MountLike, SpellInterruptFlags.DamageCancels),
        Cast(PushbackBolt, SpellInterruptFlags.DamagePushback));

    private static (Player Attacker, Player Victim) Pair(SpellTestKit kit)
    {
        (Player attacker, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2);
        victim.Health = victim.MaxHealth = 1000;
        return (attacker, victim);
    }

    [Fact]
    public void AFullyAbsorbedHit_BreaksADamageCancelsAura()
    {
        using SpellTestKit kit = Kit();
        (Player attacker, Player victim) = Pair(kit);
        RuleTestSupport.Apply(kit, victim, Sleep);
        RuleTestSupport.Apply(kit, victim, Shield);

        SpellDamageResult result = kit.System.DealDirectDamage(attacker, victim, kit.Store.Get(FireBolt)!, 60, allowCrit: false);

        Assert.Equal((0u, 60u), (result.Dealt, result.Absorbed));
        Assert.False(kit.System.HasAura(victim, Sleep));
        Assert.True(kit.System.HasAura(victim, Shield));
    }

    [Fact]
    public void ADamageCancelsAura_SurvivesWhenNothingWasAbsorbedOrDealt()
    {
        using SpellTestKit kit = Kit();
        (Player attacker, Player victim) = Pair(kit);
        RuleTestSupport.Apply(kit, victim, Sleep);

        kit.System.OnDamageTaken(victim, attacker, 0, periodic: false);

        Assert.True(kit.System.HasAura(victim, Sleep));
    }

    [Fact]
    public void AFullyAbsorbedDamageOverTimeTick_StillBreaksTheAura()
    {
        using SpellTestKit kit = Kit();
        (Player attacker, Player victim) = Pair(kit);
        RuleTestSupport.Apply(kit, victim, Sleep);

        kit.System.OnDamageTaken(victim, attacker, 0, periodic: true, absorbed: 20); // Unit.cpp:741 does not look at the damage type

        Assert.False(kit.System.HasAura(victim, Sleep));
    }

    [Fact]
    public void AFullyAbsorbedHit_InterruptsAPlayersDamageCancelsCast()
    {
        using SpellTestKit kit = Kit();
        (Player attacker, Player victim) = Pair(kit);
        RuleTestSupport.Apply(kit, victim, Shield);
        kit.Spellbook.Teach(victim, MountLike);
        kit.System.HandleCastRequest(victim, MountLike, SpellCastTargets.ForUnit(attacker.Guid));
        Assert.NotNull(kit.System.GetState(victim.Guid)!.CurrentCast);

        kit.System.DealDirectDamage(attacker, victim, kit.Store.Get(FireBolt)!, 60, allowCrit: false);

        Assert.Null(kit.System.GetState(victim.Guid)!.CurrentCast);
    }

    [Fact]
    public void AFullyAbsorbedDamageOverTimeTick_DoesNotInterruptACast()
    {
        using SpellTestKit kit = Kit();
        (Player attacker, Player victim) = Pair(kit);
        kit.Spellbook.Teach(victim, MountLike);
        kit.System.HandleCastRequest(victim, MountLike, SpellCastTargets.ForUnit(attacker.Guid));

        kit.System.OnDamageTaken(victim, attacker, 0, periodic: true, absorbed: 20); // Unit.cpp:744 "damagetype != DOT"

        Assert.NotNull(kit.System.GetState(victim.Guid)!.CurrentCast);
    }

    [Fact]
    public void AFullyAbsorbedHit_NeverPushesBackACast()
    {
        using SpellTestKit kit = Kit();
        (Player attacker, Player victim) = Pair(kit);
        RuleTestSupport.Apply(kit, victim, Shield);
        kit.Spellbook.Teach(victim, PushbackBolt);
        kit.System.HandleCastRequest(victim, PushbackBolt, SpellCastTargets.ForUnit(attacker.Guid));
        SpellCast cast = kit.System.GetState(victim.Guid)!.CurrentCast!;
        cast.Timer = 1;

        kit.System.DealDirectDamage(attacker, victim, kit.Store.Get(FireBolt)!, 60, allowCrit: false);

        Assert.Same(cast, kit.System.GetState(victim.Guid)!.CurrentCast);
        Assert.Equal(1, cast.Timer);
    }
}
