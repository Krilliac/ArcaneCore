using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.CombatMechanics;

/// <summary>
/// S07 aura states and reactives against vmangos: Unit::ModifyAuraState / HasAuraState (Unit.cpp:4682-4745),
/// ProcSkillsAndReactives (Unit.cpp:8834-8915), StartReactiveTimer / UpdateReactives / ClearAllReactives
/// (UnitDefines.h:746-754, Unit.cpp:9424-9486), the caster and 20% target state checks (Spell.cpp:5392, 5733-5742).
/// </summary>
public sealed class ReactiveTests
{
    private const uint Revenge = 950001;        // CasterAuraState 1 (Defense), classic-db 6572 shape
    private const uint Execute = 950002;        // TargetAuraState 2 (Healthless20Percent), classic-db 5308 shape
    private const uint DefensePassive = 950003; // a passive that needs the Defense state
    private const uint Overpower = 950004;      // finishing move on the marker, classic-db 7384 shape
    private const uint MeleeAbility = 950005;   // a melee-class ability
    private const uint FireBolt = 950006;       // a magic-class spell

    private static SpellInfo NoGcd(SpellInfo spell) => spell with { StartRecoveryCategory = 0, StartRecoveryTime = 0 };

    private static IEnumerable<SpellInfo> Spells()
    {
        yield return NoGcd(Spell(Revenge, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)) with
        {
            CasterAuraState = AuraState.Defense,
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
        });
        yield return NoGcd(Spell(Execute, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)) with
        {
            TargetAuraState = AuraState.Healthless20Percent,
            PowerType = (int)PowerType.Rage,
            ManaCost = 100,
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
        });
        yield return NoGcd(Spell(DefensePassive, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with
        {
            Attributes = SpellAttributes.Passive,
            CasterAuraState = AuraState.Defense,
            Duration = new SpellDuration(-1, 0, -1),
        });
        yield return NoGcd(Spell(Overpower, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)) with
        {
            Attributes = (SpellAttributes)0x00250010u,   // classic-db 7384: ability + NO_ACTIVE_DEFENSE
            AttributesEx = (SpellAttributesEx)0x48100000u,
            DamageClass = SpellDamageClass.Melee,
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
        });
        yield return NoGcd(Spell(MeleeAbility, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)) with
        {
            DamageClass = SpellDamageClass.Melee,
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
        });
        yield return NoGcd(Spell(FireBolt, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)) with
        {
            DamageClass = SpellDamageClass.Magic,
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
        });
    }

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Kit = new SpellTestKit([.. Spells()]);
            (Warrior, _) = Kit.AddPlayer(1);
            Warrior.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Warrior);
            (Rogue, _) = Kit.AddPlayer(2, 0, 3);
            Rogue.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Rogue);
            (Hunter, _) = Kit.AddPlayer(3, 0, -3);
            Hunter.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
            (Enemy, _) = Kit.AddPlayer(4, 3, 0);
            Enemy.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Warrior);
            Combos = new ComboPointService(Kit.System, (_, guid) => Kit.World.FindOnlinePlayer(guid));
            Combos.Install();
            States = new AuraStateService(Kit.System, p => KnownSpells.TryGetValue(p.Guid, out List<uint>? list) ? list : []);
            AuraStateCastChecks.Install(Kit.System, States);
            Reactives = new ReactiveService(States, () => Combos);
            Kit.System.RegisterObserver(new ReactiveSpellObserver(Reactives));
            Warrior.Map!.AddUpdater(new ReactiveUpdater(Reactives));
            Reactives.Observe(Warrior.Map!.Combat);
            Kit.World.RunTick(0);
        }

        public SpellTestKit Kit { get; }

        public Player Warrior { get; }

        public Player Rogue { get; }

        public Player Hunter { get; }

        public Player Enemy { get; }

        public ComboPointService Combos { get; }

        public AuraStateService States { get; }

        public ReactiveService Reactives { get; }

        public Dictionary<ObjectGuid, List<uint>> KnownSpells { get; } = [];

        public SpellSystem System => Kit.System;

        public SpellCastResult Cast(Player caster, uint spell, Unit? target = null, bool triggered = false)
            => System.CastSpell(caster, spell, target is null ? SpellCastTargets.ForSelf() : SpellCastTargets.ForUnit(target.Guid), triggered);

        public void Dispose() => Kit.Dispose();
    }

    // --- aura states ------------------------------------------------------------------------------------------

    [Fact]
    public void ModifyAuraState_SetsAndClearsBit_StateMinusOne_InUnitFieldAurastate()
    {
        using var rig = new Rig();

        rig.States.ModifyAuraState(rig.Warrior, AuraState.Defense, true);
        Assert.Equal(0b1u, rig.Warrior.GetUInt32(UpdateFields.UnitFieldAurastate));

        rig.States.ModifyAuraState(rig.Warrior, AuraState.Healthless20Percent, true);
        Assert.Equal(0b11u, rig.Warrior.GetUInt32(UpdateFields.UnitFieldAurastate));
        Assert.True(rig.States.HasAuraState(rig.Warrior, AuraState.Defense));
        Assert.True(rig.States.HasAuraState(rig.Warrior, AuraState.Healthless20Percent));
        Assert.False(rig.States.HasAuraState(rig.Warrior, AuraState.Judgement));

        rig.States.ModifyAuraState(rig.Warrior, AuraState.Defense, false);
        Assert.Equal(0b10u, rig.Warrior.GetUInt32(UpdateFields.UnitFieldAurastate));
        Assert.False(rig.States.HasAuraState(rig.Warrior, AuraState.None));
    }

    [Fact]
    public void SettingAState_CastsTheKnownPassivesThatNeedIt_AndClearingItRemovesTheAurasThatNeedIt()
    {
        using var rig = new Rig();
        rig.KnownSpells[rig.Warrior.Guid] = [DefensePassive];

        rig.States.ModifyAuraState(rig.Warrior, AuraState.Defense, true);
        Assert.True(rig.System.HasAura(rig.Warrior, DefensePassive));

        rig.States.ModifyAuraState(rig.Warrior, AuraState.Defense, true);   // already set: nothing is cast twice
        Assert.Single(rig.System.GetAuras(rig.Warrior), h => h.Spell.Id == DefensePassive);

        rig.States.ModifyAuraState(rig.Warrior, AuraState.Defense, false);
        Assert.False(rig.System.HasAura(rig.Warrior, DefensePassive));
    }

    [Theory]
    [InlineData(60u, 12u, false)]    // exactly 20%: not below it
    [InlineData(60u, 11u, true)]
    [InlineData(60u, 59u, false)]
    public void HealthState_IsSetBelowTwentyPercent_Strictly(uint max, uint health, bool expected)
    {
        using var rig = new Rig();
        rig.Enemy.SetUInt32(UpdateFields.UnitFieldMaxhealth, max);
        rig.Enemy.Health = health;

        rig.States.UpdateHealthState(rig.Enemy);

        Assert.Equal(expected, rig.States.HasAuraState(rig.Enemy, AuraState.Healthless20Percent));
    }

    [Fact]
    public void HealthState_FollowsTheHealth_EveryTick()
    {
        using var rig = new Rig();
        rig.Enemy.Health = 5;
        rig.Kit.World.RunTick(50);
        Assert.True(rig.States.HasAuraState(rig.Enemy, AuraState.Healthless20Percent));

        rig.Enemy.Health = 50;
        rig.Kit.World.RunTick(50);
        Assert.False(rig.States.HasAuraState(rig.Enemy, AuraState.Healthless20Percent));
    }

    // --- the cast checks --------------------------------------------------------------------------------------

    [Fact]
    public void CasterAuraState_IsRequired_ForRevenge_EvenWhenTriggered()
    {
        using var rig = new Rig();
        Assert.Equal(SpellCastResult.CasterAurastate, rig.Cast(rig.Warrior, Revenge, rig.Enemy));
        Assert.Equal(SpellCastResult.CasterAurastate, rig.Cast(rig.Warrior, Revenge, rig.Enemy, triggered: true));

        rig.States.ModifyAuraState(rig.Warrior, AuraState.Defense, true);
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(rig.Warrior, Revenge, rig.Enemy));
    }

    [Fact]
    public void Execute_NeedsATargetBelowTwentyPercent()
    {
        using var rig = new Rig();
        SpellSystem.SetPower(rig.Warrior, PowerType.Rage, 200);
        rig.Warrior.SetUInt32(UpdateFields.UnitFieldMaxpower1 + 1, 1000);
        SpellSystem.SetPower(rig.Warrior, PowerType.Rage, 200);

        Assert.Equal(SpellCastResult.BadTargets, rig.Cast(rig.Warrior, Execute, rig.Enemy));

        rig.Enemy.Health = 5;
        rig.States.UpdateHealthState(rig.Enemy);
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(rig.Warrior, Execute, rig.Enemy));
    }

    [Fact]
    public void Execute_WithoutAnExplicitTarget_IsBadImplicitTargets_AndAlsoChecksTriggeredCasts()
    {
        using var rig = new Rig();
        var check = new TargetHealthStateCheck(rig.States);
        var context = new SpellCastCheckContext(rig.System, rig.Warrior, rig.Kit.Store.Get(Execute)!, SpellCastTargets.ForSelf(), null, true, false);

        Assert.Equal(SpellCastResult.BadImplicitTargets, check.Check(context));
        Assert.Equal(SpellCastResult.BadTargets, check.Check(context with { Target = rig.Enemy }));
        Assert.Equal(SpellCastResult.CastOk, check.Check(context with { Spell = rig.Kit.Store.Get(Revenge)! }));   // no TargetAuraState
    }

    [Fact]
    public void TheTargetStateCheck_RunsAfterPower_SoMissingRageIsReportedFirst()
    {
        using var rig = new Rig();
        SpellSystem.SetPower(rig.Warrior, PowerType.Rage, 0);

        Assert.Equal(SpellCastResult.NoPower, rig.Cast(rig.Warrior, Execute, rig.Enemy));
    }

    // --- reactions --------------------------------------------------------------------------------------------

    [Fact]
    public void Dodge_PutsTheVictimInDefense_ExceptARogue_AndOpensAFourSecondWindow()
    {
        using var rig = new Rig();

        rig.Reactives.OnAttackAvoided(rig.Rogue, rig.Warrior, AttackAvoidance.Dodge);
        Assert.True(rig.States.HasAuraState(rig.Warrior, AuraState.Defense));
        Assert.True(rig.Reactives.IsReactiveOpen(rig.Warrior, ReactiveType.Defense));
        Assert.Equal(rig.Rogue.Guid, rig.Reactives.GetReactiveTarget(rig.Warrior, ReactiveType.Defense));
        Assert.Equal(4000u, ReactiveService.ReactiveTimerStartMs);

        rig.Reactives.OnAttackAvoided(rig.Warrior, rig.Rogue, AttackAvoidance.Dodge);   // a rogue's dodge opens nothing (skips Riposte)
        Assert.False(rig.States.HasAuraState(rig.Rogue, AuraState.Defense));
        Assert.False(rig.Reactives.IsReactiveOpen(rig.Rogue, ReactiveType.Defense));
    }

    [Fact]
    public void ParryAndBlock_PutTheVictimInDefense()
    {
        using var rig = new Rig();

        rig.Reactives.OnAttackAvoided(rig.Rogue, rig.Warrior, AttackAvoidance.Parry);
        Assert.True(rig.States.HasAuraState(rig.Warrior, AuraState.Defense));
        rig.Reactives.ClearAllReactives(rig.Warrior);
        Assert.False(rig.States.HasAuraState(rig.Warrior, AuraState.Defense));

        rig.Reactives.OnAttackAvoided(rig.Rogue, rig.Warrior, AttackAvoidance.Block);
        Assert.True(rig.States.HasAuraState(rig.Warrior, AuraState.Defense));

        rig.Reactives.ClearAllReactives(rig.Rogue);
        rig.Reactives.OnAttackAvoided(rig.Warrior, rig.Rogue, AttackAvoidance.Parry);   // a rogue parrying still gets Defense (1.12 branch)
        Assert.True(rig.States.HasAuraState(rig.Rogue, AuraState.Defense));
    }

    [Fact]
    public void AHuntersParry_GivesHunterParryAndACombo_NotDefense()
    {
        using var rig = new Rig();

        rig.Reactives.OnAttackAvoided(rig.Enemy, rig.Hunter, AttackAvoidance.Parry);

        Assert.True(rig.States.HasAuraState(rig.Hunter, AuraState.HunterParry));
        Assert.False(rig.States.HasAuraState(rig.Hunter, AuraState.Defense));
        Assert.Equal(1, rig.Combos.GetComboPoints(rig.Hunter));
        Assert.Equal(rig.Enemy.Guid, rig.Combos.GetComboTarget(rig.Hunter));
        Assert.True(rig.Reactives.IsReactiveOpen(rig.Hunter, ReactiveType.HunterParry));
    }

    [Fact]
    public void AWarriorWhoseAttackWasDodged_GetsTheOverpowerMarker()
    {
        using var rig = new Rig();

        rig.Reactives.OnAttackAvoided(rig.Warrior, rig.Enemy, AttackAvoidance.Dodge);

        Assert.Equal(1, rig.Combos.GetComboPoints(rig.Warrior));
        Assert.Equal(rig.Enemy.Guid, rig.Combos.GetComboTarget(rig.Warrior));
        Assert.True(rig.Reactives.IsReactiveOpen(rig.Warrior, ReactiveType.Overpower));

        rig.Reactives.OnAttackAvoided(rig.Rogue, rig.Enemy, AttackAvoidance.Dodge);   // other classes get no marker
        Assert.Equal(0, rig.Combos.GetComboPoints(rig.Rogue));
        rig.Reactives.OnAttackAvoided(rig.Warrior, rig.Enemy, AttackAvoidance.Parry);  // only a dodge opens Overpower
        Assert.Equal(1, rig.Combos.GetComboPoints(rig.Warrior));
    }

    [Fact]
    public void NothingAvoided_OpensNothing()
    {
        using var rig = new Rig();

        rig.Reactives.OnAttackAvoided(rig.Warrior, rig.Enemy, AttackAvoidance.None);

        Assert.Equal(0, rig.Combos.GetComboPoints(rig.Warrior));
        Assert.Empty(rig.Reactives.ActiveUnits);
    }

    // --- the windows ------------------------------------------------------------------------------------------

    [Fact]
    public void TheWindow_EndsAfterFourSeconds_RemovingTheStateOrTheMarker()
    {
        using var rig = new Rig();
        rig.Reactives.OnAttackAvoided(rig.Warrior, rig.Enemy, AttackAvoidance.Dodge);   // Overpower marker on the warrior, Defense on the enemy

        rig.Reactives.Update(3999);
        Assert.Equal(1, rig.Combos.GetComboPoints(rig.Warrior));
        Assert.True(rig.States.HasAuraState(rig.Enemy, AuraState.Defense));

        rig.Reactives.Update(1);
        Assert.Equal(0, rig.Combos.GetComboPoints(rig.Warrior));
        Assert.False(rig.States.HasAuraState(rig.Enemy, AuraState.Defense));
        Assert.False(rig.Reactives.IsReactiveOpen(rig.Warrior, ReactiveType.Overpower));
        Assert.Empty(rig.Reactives.ActiveUnits);
    }

    [Fact]
    public void ANewAvoidance_RestartsTheWindow()
    {
        using var rig = new Rig();
        rig.Reactives.OnAttackAvoided(rig.Rogue, rig.Warrior, AttackAvoidance.Block);
        rig.Reactives.Update(3000);

        rig.Reactives.OnAttackAvoided(rig.Rogue, rig.Warrior, AttackAvoidance.Block);
        rig.Reactives.Update(3000);

        Assert.True(rig.States.HasAuraState(rig.Warrior, AuraState.Defense));
        rig.Reactives.Update(1000);
        Assert.False(rig.States.HasAuraState(rig.Warrior, AuraState.Defense));
    }

    [Fact]
    public void ClearAllReactives_ClosesEveryWindow_AndTheWarriorsMarker()
    {
        using var rig = new Rig();
        rig.Reactives.OnAttackAvoided(rig.Warrior, rig.Enemy, AttackAvoidance.Dodge);
        rig.Reactives.OnAttackAvoided(rig.Rogue, rig.Warrior, AttackAvoidance.Parry);

        rig.Reactives.ClearAllReactives(rig.Warrior);

        Assert.False(rig.States.HasAuraState(rig.Warrior, AuraState.Defense));
        Assert.Equal(0, rig.Combos.GetComboPoints(rig.Warrior));
        Assert.False(rig.Reactives.IsReactiveOpen(rig.Warrior, ReactiveType.Overpower));
        Assert.False(rig.Reactives.IsReactiveOpen(rig.Warrior, ReactiveType.Defense));
    }

    [Fact]
    public void TheMapTick_RunsTheWindowsDown()
    {
        using var rig = new Rig();
        rig.Reactives.OnAttackAvoided(rig.Rogue, rig.Warrior, AttackAvoidance.Block);

        rig.Kit.World.RunTick(3950);
        Assert.True(rig.States.HasAuraState(rig.Warrior, AuraState.Defense));

        rig.Kit.World.RunTick(50);
        Assert.False(rig.States.HasAuraState(rig.Warrior, AuraState.Defense));
    }

    [Fact]
    public void ADeadUnit_LosesItsStatesAndWindows()
    {
        using var rig = new Rig();
        rig.Enemy.Health = 5;
        rig.States.UpdateHealthState(rig.Enemy);
        rig.Reactives.OnAttackAvoided(rig.Warrior, rig.Enemy, AttackAvoidance.Dodge);
        Assert.True(rig.States.HasAuraState(rig.Enemy, AuraState.Healthless20Percent));

        rig.Warrior.Map!.Combat.Kill(rig.Rogue, rig.Enemy);
        rig.Warrior.Map!.Combat.Kill(rig.Rogue, rig.Warrior);

        Assert.False(rig.States.HasAuraState(rig.Enemy, AuraState.Healthless20Percent));
        Assert.False(rig.States.HasAuraState(rig.Enemy, AuraState.Defense));
        Assert.Equal(0, rig.Combos.GetComboPoints(rig.Warrior));
        Assert.False(rig.Reactives.IsReactiveOpen(rig.Warrior, ReactiveType.Overpower));
    }

    // --- abilities and swings ---------------------------------------------------------------------------------

    [Fact]
    public void ADodgedMeleeAbility_OpensTheWindows_ButMagicAndHitsDoNot()
    {
        using var rig = new Rig();
        var rules = new FixedRules { Miss = SpellMissInfo.Dodge };
        rig.System.CombatRules = rules;

        rig.Cast(rig.Warrior, FireBolt, rig.Enemy);
        Assert.Equal(0, rig.Combos.GetComboPoints(rig.Warrior));

        rules.Miss = SpellMissInfo.None;
        rig.Cast(rig.Warrior, MeleeAbility, rig.Enemy);
        Assert.Equal(0, rig.Combos.GetComboPoints(rig.Warrior));

        rules.Miss = SpellMissInfo.Dodge;
        rig.Cast(rig.Warrior, MeleeAbility, rig.Enemy);
        Assert.Equal(1, rig.Combos.GetComboPoints(rig.Warrior));
        Assert.True(rig.States.HasAuraState(rig.Enemy, AuraState.Defense));
    }

    [Fact]
    public void ADodgedWhiteSwing_OpensTheWindows_ThroughTheMeleeSwingEvent()
    {
        using var rig = new Rig();
        MapCombat combat = rig.Warrior.Map!.Combat;
        var random = new ScriptedRandom { DefaultInt = 600 };   // inside the dodge band of a 5% dodger (cf. CombatHitTableTests)
        combat.Random = random;
        rig.Enemy.SetFloat(UpdateFields.PlayerDodgePercentage, 5f);
        rig.Enemy.Relocate(rig.Enemy.X, rig.Enemy.Y, rig.Enemy.Z, MathF.PI, 1);   // facing the attacker: players cannot dodge from behind

        MeleeDamageInfo? info = combat.AttackerStateUpdate(rig.Warrior, rig.Enemy, WeaponAttackType.BaseAttack);

        Assert.Equal(MeleeHitOutcome.Dodge, info!.Outcome);
        Assert.Equal(1, rig.Combos.GetComboPoints(rig.Warrior));
        Assert.True(rig.States.HasAuraState(rig.Enemy, AuraState.Defense));
    }

    [Fact]
    public void Overpower_FollowsTheMarker_AndSpendsIt()
    {
        using var rig = new Rig();
        Assert.Equal(SpellCastResult.BadTargets, rig.Cast(rig.Warrior, Overpower, rig.Enemy));   // no dodge yet: the warrior's NO_COMBO_POINTS

        rig.Reactives.OnAttackAvoided(rig.Warrior, rig.Enemy, AttackAvoidance.Dodge);
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(rig.Warrior, Overpower, rig.Enemy));
        Assert.Equal(0, rig.Combos.GetComboPoints(rig.Warrior));                                      // spent by the finishing move

        rig.Reactives.OnAttackAvoided(rig.Warrior, rig.Enemy, AttackAvoidance.Dodge);
        rig.Reactives.Update(4000);
        Assert.Equal(SpellCastResult.BadTargets, rig.Cast(rig.Warrior, Overpower, rig.Enemy));   // the window closed
    }

    [Fact]
    public void Install_RegistersTheTwoChecks()
    {
        using var rig = new Rig();

        Assert.Single(rig.System.CastChecks.OfType<CasterAuraStateCheck>());
        Assert.Single(rig.System.CastChecks.OfType<TargetHealthStateCheck>());
        Assert.Equal(SpellCheckPhase.Final, rig.System.CastChecks.OfType<TargetHealthStateCheck>().Single().Phase);
    }
}
