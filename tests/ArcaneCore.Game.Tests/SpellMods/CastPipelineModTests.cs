using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;
using static ArcaneCore.Game.Tests.SpellMods.ModTestSupport;

namespace ArcaneCore.Game.Tests.SpellMods;

/// <summary>
/// The modifier engine wired into the cast pipeline in vmangos order: cost (Spell.cpp:7008-7040: the cost mod runs before the
/// creature scaling and the school percent multiplier), global cooldown before the haste scaling (Player.cpp:22101-22111),
/// cooldown on the spell time else the category time (:22200-22206), range before the leeway (Spell.cpp:6915-6917) and the
/// melee range quirk (:6890-6899), radius and jump targets (:2058-2062), and the chain multiplier (:1766, :1920). Synthetic
/// spells only; each test also pins the unmodified value so a no-op engine fails it.
/// </summary>
public sealed class CastPipelineModTests
{
    private const uint Costly = 942001;
    private const uint CostFlat = 942002;
    private const uint CostPct = 942003;
    private const uint GcdSpell = 942004;
    private const uint GcdMod = 942005;
    private const uint CdSpell = 942006;
    private const uint CdMod = 942007;
    private const uint CategorySpell = 942008;
    private const uint RangedBolt = 942009;
    private const uint RangeMod = 942010;
    private const uint MeleeStrike = 942011;
    private const uint MeleeRangeMod = 942012;
    private const uint Blast = 942013;
    private const uint RadiusMod = 942014;
    private const uint ChainBolt = 942015;
    private const uint JumpMod = 942016;
    private const uint PastFirstMod = 942017;
    private const uint DotOfFamily = 942018;
    private const uint DurationMod = 942019;
    private const uint CategoryCdMod = 942020;
    private const uint IgnoredSpell = 942021;
    private const uint HealOfFamily = 942022;
    private const uint AllEffectsMod = 942023;

    private static SpellTestKit Kit() => new(
        // cost: base 100 rage, fire school
        InFamily(Spell(Costly, Effect(SpellEffectName.Dummy, 0)) with { PowerType = (int)PowerType.Rage, ManaCost = 100, School = SpellSchool.Fire }),
        Flat(CostFlat, SpellModOp.Cost, -20),
        Pct(CostPct, SpellModOp.Cost, -50),
        // gcd and cooldowns
        InFamily(Spell(GcdSpell, Effect(SpellEffectName.Dummy, 0))),
        Flat(GcdMod, SpellModOp.GlobalCooldown, -500),
        InFamily(Spell(CdSpell, Effect(SpellEffectName.Dummy, 0)) with { RecoveryTime = 10_000, StartRecoveryCategory = 0, StartRecoveryTime = 0 }),
        Pct(CdMod, SpellModOp.Cooldown, -10),
        InFamily(Spell(CategorySpell, Effect(SpellEffectName.Dummy, 0)) with { Category = 8, CategoryRecoveryTime = 8_000, StartRecoveryCategory = 0, StartRecoveryTime = 0 }),
        // range
        InFamily(Spell(RangedBolt, Effect(SpellEffectName.SchoolDamage, 5, SpellImplicitTarget.UnitEnemy)) with { RangeIndex = 4, Range = new SpellRange(0, 30) }),
        Flat(RangeMod, SpellModOp.Range, 5),
        InFamily(Spell(MeleeStrike, Effect(SpellEffectName.SchoolDamage, 5, SpellImplicitTarget.UnitEnemy)) with { RangeIndex = SpellConstants.RangeIndexCombat, Range = new SpellRange(0, 5) }),
        Flat(MeleeRangeMod, SpellModOp.Range, 10),
        // radius
        InFamily(Spell(Blast, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.LocationCasterSrc, targetB: SpellImplicitTarget.EnumUnitsEnemyAoeAtSrcLoc) with { Radius = 8 })
            with { StartRecoveryCategory = 0, StartRecoveryTime = 0 }),
        Pct(RadiusMod, SpellModOp.Radius, 25),
        // chain
        InFamily(Spell(ChainBolt, Effect(SpellEffectName.SchoolDamage, 20, SpellImplicitTarget.UnitEnemy) with { ChainTarget = 3, DamageMultiplier = 0.5f })
            with { RangeIndex = 4, Range = new SpellRange(0, 30), StartRecoveryCategory = 0, StartRecoveryTime = 0 }),
        Flat(JumpMod, SpellModOp.JumpTargets, 1),
        Pct(PastFirstMod, SpellModOp.EffectPastFirst, -50),
        // duration
        InFamily(Spell(DotOfFamily, Effect(SpellEffectName.ApplyAura, 4, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 3000)) with
        {
            Duration = new SpellDuration(12000, 0, 12000),
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            SpellVisual = 1,
        }),
        Pct(DurationMod, SpellModOp.Duration, 25),
        Flat(CategoryCdMod, SpellModOp.Cooldown, -2_000),
        InFamily(Spell(HealOfFamily, Effect(SpellEffectName.Heal, 20))),
        Pct(AllEffectsMod, SpellModOp.AllEffects, 50),
        // a spell the mods do not cover (other mask)
        InFamily(Spell(IgnoredSpell, Effect(SpellEffectName.Dummy, 0)) with { RecoveryTime = 10_000, StartRecoveryCategory = 0, StartRecoveryTime = 0 }, flags: 0x40));

    private static (SpellTestKit Kit, Player Caster) Setup(params uint[] learn)
    {
        SpellTestKit kit = Kit();
        (Player caster, _) = CasterWithRage(kit);
        kit.Spellbook.Teach(caster, Costly, GcdSpell, CdSpell, CategorySpell, RangedBolt, MeleeStrike, Blast, ChainBolt, DotOfFamily, IgnoredSpell);
        foreach (uint spell in learn)
        {
            kit.System.LearnSpell(caster, spell);
        }

        return (kit, caster);
    }

    private static uint Rage(Player player) => SpellSystem.GetPower(player, PowerType.Rage);

    private static Player Enemy(SpellTestKit kit, FakeRelations relations, uint guid, float x, float y = 0)
    {
        (Player player, _) = kit.AddPlayer(guid, x, y);
        relations.Hostile.Add(player.Guid);
        return player;
    }

    // --- power cost -------------------------------------------------------------------------------------------

    [Fact]
    public void Cost_ModsRunBeforeTheSchoolMultiplier()
    {
        (SpellTestKit kit, Player caster) = Setup(CostFlat, CostPct);
        using (kit)
        {
            caster.SetFloat(UpdateFields.UnitFieldPowerCostMultiplier + (int)SpellSchool.Fire, 0.1f);

            Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, Costly, SpellCastTargets.ForSelf()));

            // vmangos: ((100 - 20) * 50% off) = 40, then x1.1 = 44. The old order gave 100 * 1.1 = 110 and refused the cast.
            Assert.Equal(100u - 44u, Rage(caster));
        }
    }

    [Fact]
    public void Cost_WithoutMods_IsTheSchoolMultipliedBase()
    {
        (SpellTestKit kit, Player caster) = Setup();
        using (kit)
        {
            caster.SetFloat(UpdateFields.UnitFieldPowerCostMultiplier + (int)SpellSchool.Fire, -0.1f);

            Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, Costly, SpellCastTargets.ForSelf()));

            Assert.Equal(100u - 90u, Rage(caster));
        }
    }

    // --- global cooldown and cooldowns ---------------------------------------------------------------------------

    [Fact]
    public void GlobalCooldown_ModRunsBeforeTheHasteScaling()
    {
        (SpellTestKit kit, Player caster) = Setup(GcdMod);
        using (kit)
        {
            caster.SetFloat(UpdateFields.UnitModCastSpeed, 0.8f);
            kit.System.HandleCastRequest(caster, GcdSpell, SpellCastTargets.ForSelf());

            // 1500 - 500 = 1000; the haste scaling only applies to exactly 1500, so 0.8 does not touch it.
            Assert.Equal(1000u, kit.System.GetState(caster.Guid)!.GlobalCooldowns[SpellConstants.GlobalCooldownCategory] - kit.Now);
        }
    }

    [Fact]
    public void GlobalCooldown_WithoutMods_IsHasteScaled()
    {
        (SpellTestKit kit, Player caster) = Setup();
        using (kit)
        {
            caster.SetFloat(UpdateFields.UnitModCastSpeed, 0.8f);
            kit.System.HandleCastRequest(caster, GcdSpell, SpellCastTargets.ForSelf());

            Assert.Equal(1200u, kit.System.GetState(caster.Guid)!.GlobalCooldowns[SpellConstants.GlobalCooldownCategory] - kit.Now);
        }
    }

    [Fact]
    public void Cooldown_ModAppliesToTheSpellTime()
    {
        (SpellTestKit kit, Player caster) = Setup(CdMod);
        using (kit)
        {
            kit.System.HandleCastRequest(caster, CdSpell, SpellCastTargets.ForSelf());

            Assert.Equal(9000u, kit.System.GetState(caster.Guid)!.SpellCooldowns[CdSpell] - kit.Now);
        }
    }

    [Fact]
    public void Cooldown_ModAppliesToTheCategoryTimeWhenTheSpellHasNone()
    {
        (SpellTestKit kit, Player caster) = Setup(CategoryCdMod);
        using (kit)
        {
            kit.System.HandleCastRequest(caster, CategorySpell, SpellCastTargets.ForSelf());

            UnitSpellState state = kit.System.GetState(caster.Guid)!;
            Assert.Equal(6000u, state.CategoryCooldowns[8] - kit.Now);
            Assert.False(state.SpellCooldowns.ContainsKey(CategorySpell));
        }
    }

    [Fact]
    public void Cooldown_ModOnlyTouchesSpellsOfItsMask()
    {
        (SpellTestKit kit, Player caster) = Setup(CdMod);
        using (kit)
        {
            kit.System.HandleCastRequest(caster, IgnoredSpell, SpellCastTargets.ForSelf());

            Assert.Equal(10_000u, kit.System.GetState(caster.Guid)!.SpellCooldowns[IgnoredSpell] - kit.Now);
        }
    }

    // --- effect value ----------------------------------------------------------------------------------------------

    [Fact]
    public void AllEffects_PctModScalesTheEffectValue()
    {
        (SpellTestKit kit, Player caster) = Setup();
        using (kit)
        {
            caster.Health = 10;
            kit.System.CastSpell(caster, HealOfFamily, SpellCastTargets.ForSelf(), triggered: true);
            Assert.Equal(30u, caster.Health);   // 10 + 20

            kit.System.LearnSpell(caster, AllEffectsMod);
            caster.Health = 10;
            kit.System.CastSpell(caster, HealOfFamily, SpellCastTargets.ForSelf(), triggered: true);
            Assert.Equal(40u, caster.Health);   // 10 + 20 * 1.5
        }
    }

    // --- duration -----------------------------------------------------------------------------------------------

    [Fact]
    public void Duration_ModScalesTheAuraDuration()
    {
        (SpellTestKit kit, Player caster) = Setup(DurationMod);
        using (kit)
        {
            (Player target, _) = kit.AddPlayer(2, 3, 0);
            kit.System.HandleCastRequest(caster, DotOfFamily, SpellCastTargets.ForUnit(target.Guid));

            Assert.Equal(15000, kit.System.GetState(target.Guid)!.AuraHolders.Single(h => h.Spell.Id == DotOfFamily).MaxDuration);
        }
    }

    // --- range --------------------------------------------------------------------------------------------------

    [Fact]
    public void Range_FlatModExtendsTheMaximum_BeforeTheLeeway()
    {
        (SpellTestKit kit, Player caster) = Setup(RangeMod);
        using (kit)
        {
            (Player near, _) = kit.AddPlayer(2, 35, 0);
            (Player far, _) = kit.AddPlayer(3, 45, 0);

            // 30 + 5 = 35 max (+ 1.25 leeway) against the combat distance (distance minus both combat reaches).
            Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, RangedBolt, SpellCastTargets.ForUnit(near.Guid)));
            kit.Advance(1500);
            Assert.Equal(SpellCastResult.OutOfRange, kit.System.HandleCastRequest(caster, RangedBolt, SpellCastTargets.ForUnit(far.Guid)));
        }
    }

    [Fact]
    public void Range_WithoutMods_RefusesTheSameTarget()
    {
        (SpellTestKit kit, Player caster) = Setup();
        using (kit)
        {
            (Player near, _) = kit.AddPlayer(2, 38, 0);

            Assert.Equal(SpellCastResult.OutOfRange, kit.System.HandleCastRequest(caster, RangedBolt, SpellCastTargets.ForUnit(near.Guid)));
        }
    }

    [Fact]
    public void MeleeRange_ModIsAddedToRangeMod1_AsVmangosDoes()
    {
        (SpellTestKit kit, Player caster) = Setup(MeleeRangeMod);
        using (kit)
        {
            (Player target, _) = kit.AddPlayer(2, 12, 0);
            target.Orientation = 0;
            caster.Orientation = 0;

            // The unmodified melee reach (both combat reaches + 1 + 4/3, at least 5) cannot reach 12 yd; range_mod = 1 + 10 can.
            Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, MeleeStrike, SpellCastTargets.ForUnit(target.Guid)));
        }
    }

    [Fact]
    public void MeleeRange_WithoutMods_CannotReachTwelveYards()
    {
        (SpellTestKit kit, Player caster) = Setup();
        using (kit)
        {
            (Player target, _) = kit.AddPlayer(2, 12, 0);

            Assert.Equal(SpellCastResult.OutOfRange, kit.System.HandleCastRequest(caster, MeleeStrike, SpellCastTargets.ForUnit(target.Guid)));
        }
    }

    // --- radius, jump targets, chain multiplier ----------------------------------------------------------------------

    private static (SpellTestKit Kit, Player Caster, FakeRelations Relations) AreaSetup(params uint[] learn)
    {
        (SpellTestKit kit, Player caster) = Setup(learn);
        var relations = new FakeRelations();
        kit.System.Relations = relations;
        WorldCollision.Of(kit.World).Install(new FakeLineOfSight());
        return (kit, caster, relations);
    }

    [Fact]
    public void Radius_PctModWidensAnAreaSpell()
    {
        (SpellTestKit kit, Player caster, FakeRelations relations) = AreaSetup(RadiusMod);
        using (kit)
        {
            Player inside = Enemy(kit, relations, 2, 9.5f);   // radius 8 * 1.25 = 10
            kit.World.RunTick(0);

            kit.System.CastSpell(caster, Blast, SpellCastTargets.ForSelf(), triggered: true);

            Assert.True(inside.Health < inside.MaxHealth);
        }
    }

    [Fact]
    public void Radius_WithoutMods_MissesTheSameUnit()
    {
        (SpellTestKit kit, Player caster, FakeRelations relations) = AreaSetup();
        using (kit)
        {
            Player outside = Enemy(kit, relations, 2, 9.5f);
            kit.World.RunTick(0);

            kit.System.CastSpell(caster, Blast, SpellCastTargets.ForSelf(), triggered: true);

            Assert.Equal(outside.MaxHealth, outside.Health);
        }
    }

    private static (Player A, Player B, Player C, Player D) Line(SpellTestKit kit, FakeRelations relations)
    {
        Player a = Enemy(kit, relations, 2, 10);
        Player b = Enemy(kit, relations, 3, 16);
        Player c = Enemy(kit, relations, 4, 22);
        Player d = Enemy(kit, relations, 5, 28);
        kit.World.RunTick(0);
        return (a, b, c, d);
    }

    [Fact]
    public void JumpTargets_FlatModAddsAChainLink()
    {
        (SpellTestKit kit, Player caster, FakeRelations relations) = AreaSetup(JumpMod);
        using (kit)
        {
            (Player a, Player b, Player c, Player d) = Line(kit, relations);

            kit.System.CastSpell(caster, ChainBolt, SpellCastTargets.ForUnit(a.Guid), triggered: true);

            Assert.All(new[] { a, b, c, d }, p => Assert.True(p.Health < p.MaxHealth));
        }
    }

    [Fact]
    public void JumpTargets_WithoutMods_StopAtThree()
    {
        (SpellTestKit kit, Player caster, FakeRelations relations) = AreaSetup();
        using (kit)
        {
            (Player a, Player b, Player c, Player d) = Line(kit, relations);

            kit.System.CastSpell(caster, ChainBolt, SpellCastTargets.ForUnit(a.Guid), triggered: true);

            Assert.True(c.Health < c.MaxHealth);
            Assert.Equal(d.MaxHealth, d.Health);
        }
    }

    [Fact]
    public void EffectPastFirst_PctModScalesThePerJumpMultiplier()
    {
        (SpellTestKit plain, Player plainCaster, FakeRelations plainRelations) = AreaSetup();
        (SpellTestKit modded, Player moddedCaster, FakeRelations moddedRelations) = AreaSetup(PastFirstMod);
        using (plain)
        using (modded)
        {
            (Player pa, Player pb, _, _) = Line(plain, plainRelations);
            (Player ma, Player mb, _, _) = Line(modded, moddedRelations);

            plain.System.CastSpell(plainCaster, ChainBolt, SpellCastTargets.ForUnit(pa.Guid), triggered: true);
            modded.System.CastSpell(moddedCaster, ChainBolt, SpellCastTargets.ForUnit(ma.Guid), triggered: true);

            uint plainSecond = pb.MaxHealth - pb.Health;
            uint moddedSecond = mb.MaxHealth - mb.Health;
            Assert.True(plainSecond > 0);
            Assert.Equal(pa.MaxHealth - pa.Health, ma.MaxHealth - ma.Health);   // the first target is untouched
            Assert.Equal(plainSecond / 2, moddedSecond);                        // multiplier 0.5 -> 0.25: half the second link's damage
        }
    }
}
