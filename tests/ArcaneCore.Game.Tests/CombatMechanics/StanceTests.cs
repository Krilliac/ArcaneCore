using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.CombatMechanics;

/// <summary>
/// S06 warrior stances against vmangos: HandleAuraModShapeshift (SpellAuras.cpp:2420-2575), HandleShapeshiftBoosts
/// (:5433-5597), the strict shapeshift gate (Spell.cpp:5340-5343) and GetErrorAtShapeshiftedCast
/// (SpellEntry.cpp:1032-1074). Spell attribute words are the classic-db 1.12.1 values of the real stance spells.
/// </summary>
public sealed class StanceTests
{
    private const uint BattleStance = 2457;
    private const uint DefensiveStance = 71;
    private const uint BerserkerStance = 2458;
    private const uint BattleBoost = 21156;
    private const uint DefensiveBoost = 7376;
    private const uint BerserkerBoost = 7381;

    private const uint Overpower = 7384;          // Stances 65536 (Battle)
    private const uint Revenge = 6572;            // Stances 131072 (Defensive)
    private const uint Hamstring = 1715;          // Stances 327680 (Battle | Berserker)
    private const uint Retaliation = 20230;       // Stances 65536, self buff
    private const uint BattleCastTime = 900401;   // Battle only, 2 s cast
    private const uint BattleSwing = 900402;      // Battle only, next-swing
    private const uint BattlePassive = 900403;    // passive bound to Battle Stance
    private const uint ThirdFormCat = 900404;     // an (unsupported) cat form spell
    private const uint UnknownForm = 900405;      // form 25: not in the catalog
    private const uint TacticalMasteryBase = 900410;

    /// <summary>classic-db stance spells: Attributes 151322640 (0x09050010, includes NOT_SHAPESHIFT), AttributesEx3 1048576 (ALLOW_AURA_WHILE_DEAD).</summary>
    private static SpellInfo Stance(uint id, int form) => Spell(id, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.ModShapeshift, misc: form)) with
    {
        Attributes = (SpellAttributes)0x09050010u,
        AttributesEx3 = 0x00100000,
        Duration = new SpellDuration(-1, 0, -1),
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellInfo Passive(uint id, uint stances = 0, AuraType aura = AuraType.Dummy, int misc = 0, int duration = -1) => Spell(id, Effect(SpellEffectName.ApplyAura, 0, aura: aura, misc: misc)) with
    {
        Attributes = SpellAttributes.Passive,
        Stances = stances,
        Duration = new SpellDuration(duration, 0, duration),
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellInfo Strike(uint id, uint stances) => Spell(id, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)) with
    {
        Stances = stances,
        RangeIndex = 4,
        Range = new SpellRange(0, 30),
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static IEnumerable<SpellInfo> Spells()
    {
        yield return Stance(BattleStance, 17);
        yield return Stance(DefensiveStance, 18);
        yield return Stance(BerserkerStance, 19);
        yield return Passive(BattleBoost);
        yield return Passive(DefensiveBoost);
        yield return Passive(BerserkerBoost);
        yield return Strike(Overpower, 1u << 16);
        yield return Strike(Revenge, 1u << 17);
        yield return Strike(Hamstring, (1u << 16) | (1u << 18));
        yield return Spell(Retaliation, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
        {
            Stances = 1u << 16,
            Duration = new SpellDuration(15000, 0, 15000),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };
        yield return Strike(BattleCastTime, 1u << 16) with { CastTime = new SpellCastTime(2000, 0, 0) };
        yield return Spell(BattleSwing, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)) with
        {
            Attributes = (SpellAttributes)0x4u | SpellAttributes.IsAbility,
            Stances = 1u << 16,
            RangeIndex = SpellConstants.RangeIndexCombat,
            Range = new SpellRange(0, 5),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };
        yield return Passive(BattlePassive, stances: 1u << 16);
        yield return Stance(ThirdFormCat, (int)ShapeshiftForm.Cat);
        yield return Stance(UnknownForm, 25);
        for (int i = 0; i < 5; i++)
        {
            yield return Passive(TacticalMasteryBase + (uint)i, aura: AuraType.OverrideClassScripts, misc: 831 + i);
        }
    }

    private sealed class Rig : IDisposable
    {
        public Rig(CombatOptions? options = null, ShapeshiftFormCatalog? forms = null)
        {
            Kit = new SpellTestKit([.. Spells()]);
            (Player, _) = Kit.AddPlayer(1);
            (Enemy, _) = Kit.AddPlayer(2, 3, 0);
            Player.SetUInt32(UpdateFields.UnitFieldMaxpower1 + 1, 1000);
            Service = new ShapeshiftService(Kit.System, forms ?? ShapeshiftFormCatalog.WarriorStances, options ?? new CombatOptions(),
                p => KnownSpells.TryGetValue(p.Guid, out List<uint>? list) ? list : []);
            Service.Install();
            Kit.World.RunTick(0);
        }

        public SpellTestKit Kit { get; }

        public Player Player { get; }

        public Player Enemy { get; }

        public ShapeshiftService Service { get; }

        public Dictionary<ObjectGuid, List<uint>> KnownSpells { get; } = [];

        public SpellSystem System => Kit.System;

        public SpellCastResult Cast(uint spell, bool triggered = false)
        {
            Kit.Advance(1500);
            return System.CastSpell(Player, spell, SpellCastTargets.ForSelf(), triggered);
        }

        public SpellCastResult CastAtEnemy(uint spell)
        {
            Kit.Advance(1500);
            return System.CastSpell(Player, spell, SpellCastTargets.ForUnit(Enemy.Guid), triggered: false);
        }

        public bool Has(uint spell) => System.HasAura(Player, spell);

        public ShapeshiftForm Form => ShapeshiftService.GetForm(Player);

        public void Dispose() => Kit.Dispose();
    }

    // --- the aura handler -------------------------------------------------------------------------------------

    [Fact]
    public void BattleStance_Cast_WritesFormByte17_InUnitBytes1Byte2_AndAddsTheBoostAura()
    {
        using var rig = new Rig();

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(BattleStance));

        Assert.Equal(17, rig.Player.GetByte(UpdateFields.UnitFieldBytes1, 2));
        Assert.Equal(ShapeshiftForm.BattleStance, rig.Form);
        Assert.True(rig.Has(BattleStance));
        Assert.True(rig.Has(BattleBoost));
        Assert.Equal(PowerType.Rage, rig.Player.PowerType);
    }

    [Theory]
    [InlineData(BattleStance, ShapeshiftForm.BattleStance, BattleBoost)]
    [InlineData(DefensiveStance, ShapeshiftForm.DefensiveStance, DefensiveBoost)]
    [InlineData(BerserkerStance, ShapeshiftForm.BerserkerStance, BerserkerBoost)]
    public void EachStance_HasItsOwnFormAndBoostPassive(uint spell, ShapeshiftForm form, uint boost)
    {
        using var rig = new Rig();

        rig.Cast(spell);

        Assert.Equal(form, rig.Form);
        Assert.True(rig.Has(boost));
        Assert.Equal(boost, ShapeshiftService.GetBoostSpell(form));
    }

    [Fact]
    public void SwitchStance_RemovesThePreviousStanceAndItsBoost()
    {
        using var rig = new Rig();
        rig.Cast(BattleStance);

        rig.Cast(DefensiveStance);

        Assert.Equal(ShapeshiftForm.DefensiveStance, rig.Form);
        Assert.False(rig.Has(BattleStance));
        Assert.False(rig.Has(BattleBoost));
        Assert.True(rig.Has(DefensiveStance));
        Assert.True(rig.Has(DefensiveBoost));
    }

    [Fact]
    public void RecastingTheSameStance_KeepsItsForm()
    {
        using var rig = new Rig();
        rig.Cast(BattleStance);

        rig.Cast(BattleStance);

        Assert.Equal(ShapeshiftForm.BattleStance, rig.Form);
        Assert.True(rig.Has(BattleBoost));
    }

    [Fact]
    public void CancellingTheStance_ClearsTheFormAndTheBoost()
    {
        using var rig = new Rig();
        rig.Cast(BattleStance);

        rig.System.RemoveAuras(rig.Player, BattleStance);

        Assert.Equal(ShapeshiftForm.None, rig.Form);
        Assert.False(rig.Has(BattleBoost));
    }

    [Theory]
    [InlineData(-1, 0u)]
    [InlineData(831, 50u)]
    [InlineData(832, 100u)]
    [InlineData(833, 150u)]
    [InlineData(834, 200u)]
    [InlineData(835, 250u)]
    public void SwitchStance_RageIsCappedToTacticalMastery_0_50_100_150_200_250_Raw(int script, uint expectedRage)
    {
        using var rig = new Rig();
        if (script > 0)
        {
            rig.Cast(TacticalMasteryBase + (uint)(script - 831), triggered: true);
        }

        SpellSystem.SetPower(rig.Player, PowerType.Rage, 400);
        rig.Cast(BattleStance);

        Assert.Equal(expectedRage, SpellSystem.GetPower(rig.Player, PowerType.Rage));
    }

    [Fact]
    public void SwitchStance_KeepsRageBelowTheTacticalMasteryCap()
    {
        using var rig = new Rig();
        rig.Cast(TacticalMasteryBase + 2, triggered: true);   // 833 -> keeps up to 150
        SpellSystem.SetPower(rig.Player, PowerType.Rage, 90);

        rig.Cast(BerserkerStance);

        Assert.Equal(90u, SpellSystem.GetPower(rig.Player, PowerType.Rage));
    }

    [Fact]
    public void StanceFormsThatAreNotWarriorStances_AreLeftAlone()
    {
        using var rig = new Rig();

        rig.Cast(ThirdFormCat);

        Assert.Equal(ShapeshiftForm.None, rig.Form);
    }

    [Fact]
    public void AnUnknownForm_ChangesNothing()
    {
        using var rig = new Rig();
        rig.Cast(BattleStance);

        rig.Cast(UnknownForm, triggered: true);

        Assert.Equal(ShapeshiftForm.BattleStance, rig.Form);
    }

    // --- stance-bound spells ----------------------------------------------------------------------------------

    [Fact]
    public void Overpower_FailsOnlyShapeshift_InDefensiveStance_AndWithoutAStance()
    {
        using var rig = new Rig();
        Assert.Equal(SpellCastResult.OnlyShapeshift, rig.CastAtEnemy(Overpower));

        rig.Cast(DefensiveStance);
        Assert.Equal(SpellCastResult.OnlyShapeshift, rig.CastAtEnemy(Overpower));

        rig.Cast(BattleStance);
        Assert.Equal(SpellCastResult.CastOk, rig.CastAtEnemy(Overpower));
    }

    [Fact]
    public void Revenge_OnlyInDefensive_AndHamstringInBattleOrBerserker()
    {
        using var rig = new Rig();
        rig.Cast(BattleStance);
        Assert.Equal(SpellCastResult.OnlyShapeshift, rig.CastAtEnemy(Revenge));
        Assert.Equal(SpellCastResult.CastOk, rig.CastAtEnemy(Hamstring));

        rig.Cast(DefensiveStance);
        Assert.Equal(SpellCastResult.CastOk, rig.CastAtEnemy(Revenge));
        Assert.Equal(SpellCastResult.OnlyShapeshift, rig.CastAtEnemy(Hamstring));

        rig.Cast(BerserkerStance);
        Assert.Equal(SpellCastResult.CastOk, rig.CastAtEnemy(Hamstring));
    }

    [Fact]
    public void StanceSpells_CannotBeCastWhileInANonStanceForm()
    {
        // Battle Stance carries NOT_SHAPESHIFT (0x10000): in a non-stance form (flags1 without the stance bit) it fails.
        var forms = new ShapeshiftFormCatalog([new ShapeshiftFormInfo(1, 0, 0), .. ShapeshiftFormCatalog.WarriorStances.Forms]);
        using var rig = new Rig(forms: forms);
        rig.Player.SetByte(UpdateFields.UnitFieldBytes1, 2, (byte)ShapeshiftForm.Cat);

        Assert.Equal(SpellCastResult.NotShapeshift, rig.Cast(BattleStance));

        rig.Player.SetByte(UpdateFields.UnitFieldBytes1, 2, (byte)ShapeshiftForm.DefensiveStance);
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(BattleStance));
    }

    [Fact]
    public void TriggeredCasts_BypassTheStanceGate()
    {
        using var rig = new Rig();

        Assert.Equal(SpellCastResult.CastOk, rig.System.CastSpell(rig.Player, Overpower, SpellCastTargets.ForUnit(rig.Enemy.Guid), triggered: true));
    }

    [Fact]
    public void TheGateIsStrictOnly_ACastThatLandsAfterAStanceChangeStillLands()
    {
        // vmangos GetErrorAtShapeshiftedCast runs under `if (strict ...)` (Spell.cpp:5340): the landing re-check skips it.
        using var rig = new Rig();
        rig.Cast(BattleStance);
        Assert.Equal(SpellCastResult.CastOk, rig.CastAtEnemy(BattleCastTime));

        rig.Cast(DefensiveStance);
        rig.Kit.Advance(2000);

        Assert.True(rig.Enemy.Health < 60u);
    }

    [Fact]
    public void LosingTheStance_RemovesSelfBuffsBoundToIt_ButNotOtherCastersBuffs()
    {
        using var rig = new Rig();
        rig.Cast(BattleStance);
        rig.Cast(Retaliation, triggered: true);
        Assert.True(rig.Has(Retaliation));

        rig.Cast(DefensiveStance);

        Assert.False(rig.Has(Retaliation));
    }

    [Fact]
    public void Combat_StanceShiftKeepsSelfBuffs_KeepsThemWhenSwitchingStances_ButNotWhenTheStanceIsCancelled()
    {
        using var rig = new Rig(new CombatOptions { StanceShiftKeepsSelfBuffs = true });
        rig.Cast(BattleStance);
        rig.Cast(Retaliation, triggered: true);

        rig.Cast(DefensiveStance);
        Assert.True(rig.Has(Retaliation));

        rig.Cast(BattleStance);
        rig.System.RemoveAuras(rig.Player, BattleStance);   // no replacement: the form is really lost
        Assert.False(rig.Has(Retaliation));
    }

    [Fact]
    public void LosingTheStance_DropsPassivesBoundToIt_AndCastsThemAgainWhenTheFormIsApplied()
    {
        using var rig = new Rig();
        rig.KnownSpells[rig.Player.Guid] = [BattlePassive];

        rig.Cast(BattleStance);
        Assert.True(rig.Has(BattlePassive));   // IsNeedCastSpellAtFormApply

        rig.Cast(DefensiveStance);
        Assert.False(rig.Has(BattlePassive));  // IsRemovedOnShapeLost

        rig.Cast(BattleStance);
        Assert.True(rig.Has(BattlePassive));
    }

    [Fact]
    public void LosingTheStance_InterruptsAQueuedNextSwingSpellBoundToIt()
    {
        using var rig = new Rig();
        rig.Cast(BattleStance);
        Assert.Equal(SpellCastResult.CastOk, rig.CastAtEnemy(BattleSwing));
        Assert.NotNull(rig.System.GetState(rig.Player.Guid)!.MeleeCast);

        rig.Cast(DefensiveStance);

        Assert.Null(rig.System.GetState(rig.Player.Guid)?.MeleeCast);
    }

    // --- persistence and death --------------------------------------------------------------------------------

    [Fact]
    public void Stance_SurvivesRelog_RestoresTheFormByteAndBoostAura()
    {
        using var rig = new Rig();
        rig.Cast(DefensiveStance);
        SpellStateSnapshot saved = rig.System.CaptureState(rig.Player, nowUnixMs: 1_000_000);
        Assert.Contains(saved.Auras, a => a.SpellId == DefensiveStance);

        rig.System.RemoveUnit(rig.Player);
        rig.Player.SetByte(UpdateFields.UnitFieldBytes1, 2, 0);   // a freshly loaded player has no form
        rig.System.RestoreAuras(rig.Player, saved.Auras, nowUnixMs: 1_000_500);

        Assert.Equal(ShapeshiftForm.DefensiveStance, rig.Form);
        Assert.True(rig.Has(DefensiveStance));
        Assert.True(rig.Has(DefensiveBoost));
    }

    [Fact]
    public void Death_FollowsTheVmangosRule_StancesCarryAllowAuraWhileDead_SoTheyStay()
    {
        // classic-db stance spells have AttributesEx3 0x100000 (ALLOW_AURA_WHILE_DEAD) and vmangos RemoveAuraTypeOnDeath
        // (Unit.cpp:3955-3967) spares death-persistent holders, so the stance outlives the death.
        using var rig = new Rig();
        rig.Cast(BattleStance);

        rig.Player.Health = 0;
        rig.System.OnUnitDied(rig.Player);

        Assert.True(rig.Has(BattleStance));
        Assert.Equal(ShapeshiftForm.BattleStance, rig.Form);
    }

    [Fact]
    public void Death_RemovesAStanceWithoutTheDeathPersistentBit_AndClearsTheForm()
    {
        var plain = Stance(BattleStance, 17) with { AttributesEx3 = 0 };
        using var kit = new SpellTestKit(plain, Passive(BattleBoost));
        (Player player, _) = kit.AddPlayer(1);
        new ShapeshiftService(kit.System, ShapeshiftFormCatalog.WarriorStances, new CombatOptions(), _ => []).Install();
        kit.World.RunTick(0);
        kit.System.CastSpell(player, BattleStance, SpellCastTargets.ForSelf(), triggered: false);
        Assert.Equal(ShapeshiftForm.BattleStance, ShapeshiftService.GetForm(player));

        player.Health = 0;
        kit.System.OnUnitDied(player);

        Assert.Equal(ShapeshiftForm.None, ShapeshiftService.GetForm(player));
        Assert.False(kit.System.HasAura(player, BattleBoost));
    }

    // --- classification ---------------------------------------------------------------------------------------

    [Fact]
    public void StanceSpells_AreClassifiedPositive_AndTheClientMayCancelThem()
    {
        using var rig = new Rig();
        Assert.True(rig.Kit.Store.Get(BattleStance)!.IsPositive);
        rig.Cast(BattleStance);
        rig.Kit.Spellbook.Teach(rig.Player, BattleStance);

        rig.System.CancelAura(rig.Player, BattleStance);

        Assert.Equal(ShapeshiftForm.None, rig.Form);
    }

    [Fact]
    public void Install_RegistersTheHandlerAndTheGate()
    {
        using var rig = new Rig();

        Assert.True(rig.System.HasAuraHandler(AuraType.ModShapeshift));
        Assert.Single(rig.System.CastChecks.OfType<StanceCastCheck>());
        Assert.Equal(SpellCheckPhase.Caster, rig.System.CastChecks.OfType<StanceCastCheck>().Single().Phase);
        Assert.Equal(SpellCastCheckOrder.Shapeshift, rig.System.CastChecks.OfType<StanceCastCheck>().Single().Order);
    }
}
