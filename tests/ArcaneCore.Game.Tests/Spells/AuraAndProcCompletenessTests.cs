using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.ClassScripts;
using ArcaneCore.Game.Spells.Procs;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Tests.CombatCore;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

public sealed class AuraAndProcCompletenessTests
{
    [Fact]
    public void ModResistanceOfStatPercent_AddsIntellectPercentToArmor_RecomputesOnIntellectChange_AndRestoresOnRemove()
    {
        SpellInfo intellectArmorAura = SpellTestKit.Spell(
            30001,
            SpellTestKit.Effect(SpellEffectName.ApplyAura, 50, aura: AuraType.ModResistanceOfStatPercent, misc: 1)) with
        {
            Duration = new SpellDuration(60_000, 0, 60_000),
        };
        SpellInfo intellectBuff = SpellTestKit.Spell(
            30002,
            SpellTestKit.Effect(SpellEffectName.ApplyAura, 40, aura: AuraType.ModStat, misc: 3)) with
        {
            Duration = new SpellDuration(60_000, 0, 60_000),
        };

        using var kit = new SpellTestKit(intellectArmorAura, intellectBuff);
        (Player player, _) = kit.AddPlayer(1);
        player.SetInt32(UpdateFields.UnitFieldStat0 + 3, 100);
        player.SetInt32(UpdateFields.UnitFieldResistances, 200);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 30001, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(250, player.GetInt32(UpdateFields.UnitFieldResistances));

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 30002, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(140, player.GetInt32(UpdateFields.UnitFieldStat0 + 3));
        Assert.Equal(270, player.GetInt32(UpdateFields.UnitFieldResistances));

        kit.System.RemoveAuras(player, 30001);
        Assert.Equal(200, player.GetInt32(UpdateFields.UnitFieldResistances));

        AuraSupportEntry row = Assert.Single(AuraSupport.Entries, r => r.Type == AuraType.ModResistanceOfStatPercent);
        Assert.Equal(AuraSupportLevel.Handler, row.Level);
    }

    [Fact]
    public void ModCastingSpeedNotStack_UpdatesUnitModCastSpeed_ScalesCastTime_AndRestoresOnRemove()
    {
        SpellInfo hasteAura = SpellTestKit.Spell(
            30010,
            SpellTestKit.Effect(SpellEffectName.ApplyAura, 25, aura: AuraType.ModCastingSpeedNotStack)) with
        {
            Duration = new SpellDuration(60_000, 0, 60_000),
        };
        SpellInfo slowAura = SpellTestKit.Spell(
            30011,
            SpellTestKit.Effect(SpellEffectName.ApplyAura, -20, aura: AuraType.ModCastingSpeedNotStack)) with
        {
            Duration = new SpellDuration(60_000, 0, 60_000),
        };

        using var kit = new SpellTestKit(hasteAura, slowAura);
        (Player caster, _) = kit.AddPlayer(1);
        SpellTestKit.FlagForPvp(caster);
        (Player target, _) = kit.AddHostilePlayer(2, x: 5);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, 30010, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(0.8f, caster.GetFloat(UpdateFields.UnitModCastSpeed), 4);
        caster.SetUInt32(UpdateFields.UnitFieldPower2, 500);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, SpellTestKit.CastBolt, SpellCastTargets.ForUnit(target.Guid), triggered: false));
        SpellCast? running = kit.System.GetState(caster.Guid)?.CurrentCast;
        Assert.NotNull(running);
        Assert.Equal(1600, running!.Timer);
        kit.System.CancelCast(caster, 0);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, 30011, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(0.8f * 1.2f, caster.GetFloat(UpdateFields.UnitModCastSpeed), 4);

        kit.System.RemoveAuras(caster, 30010);
        kit.System.RemoveAuras(caster, 30011);
        Assert.Equal(1.0f, caster.GetFloat(UpdateFields.UnitModCastSpeed), 4);
    }

    [Fact]
    public void ModDetectedRange_ModifiesCreatureAttackDistance()
    {
        SpellInfo mindSoothe = SpellTestKit.Spell(
            30020,
            SpellTestKit.Effect(SpellEffectName.ApplyAura, -10, SpellImplicitTarget.Unit, aura: AuraType.ModDetectedRange)) with
        {
            Duration = new SpellDuration(30_000, 0, 30_000),
        };

        using var kit = new SpellTestKit(mindSoothe);
        (Player player, _) = kit.AddPlayer(1);
        player.Level = 60;

        Map map = kit.World.GetMap(0);
        var template = new CreatureTemplate
        {
            Entry = 100,
            Name = "Guard",
            MinLevel = 60,
            MaxLevel = 60,
            MinLevelHealth = 1000,
            MaxLevelHealth = 1000,
            Faction = 35,
            Detection = 20f,
        };
        var creatures = new CreatureMapSystem(
            map,
            new CreatureContent([template], [], [], [], []),
            new CreatureOptions(),
            aiServices: new CreatureAiServices { UnitSpells = new SpellSystemUnitSpellQueries(kit.System) });
        Creature creature = creatures.SpawnTemporary(template, 0, 0, 0, 0);

        float baseDist = creatures.GetAttackDistance(creature, player);
        Assert.Equal(20f, baseDist, 3);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 30020, SpellCastTargets.ForUnit(player.Guid), triggered: true));
        Assert.Equal(-10f, new SpellSystemUnitSpellQueries(kit.System).GetDetectedRangeModifier(player), 3);
        Assert.Equal(10f, creatures.GetAttackDistance(creature, player), 3);

        kit.System.RemoveAuras(player, 30020);
        Assert.Equal(20f, creatures.GetAttackDistance(creature, player), 3);
    }

    [Fact]
    public void GhostAura_OnNightElfOrWispSpirit_AppliesWispDisplayId_AndRestoresNativeDisplayId()
    {
        SpellInfo wispGhost = SpellTestKit.Spell(
            GhostAuras.WispSpiritSpellId,
            SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Ghost)) with
        {
            AttributesEx3 = 0x00100000u,
            Duration = new SpellDuration(-1, 0, -1),
        };

        using var kit = new SpellTestKit(wispGhost);
        (Player nightElf, _) = kit.AddPlayer(1, race: Race.NightElf);
        nightElf.NativeDisplayId = 55;
        nightElf.DisplayId = 55;

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(nightElf, GhostAuras.WispSpiritSpellId, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(GhostAuras.WispDisplayId, nightElf.DisplayId);
        Assert.NotEqual(0u, (uint)(nightElf.Flags & PlayerFlags.Ghost));

        kit.System.RemoveAuras(nightElf, GhostAuras.WispSpiritSpellId);
        Assert.Equal(55u, nightElf.DisplayId);
        Assert.Equal(0u, (uint)(nightElf.Flags & PlayerFlags.Ghost));
    }

    [Theory]
    [InlineData(5118u)]
    [InlineData(13159u)]
    public void AspectOfTheCheetahAndPack_RequireDamagingHitToTriggerDaze(uint aspectSpellId)
    {
        SpellInfo daze = SpellTestKit.Spell(
            15571,
            SpellTestKit.Effect(SpellEffectName.ApplyAura, -50, SpellImplicitTarget.UnitCaster, aura: AuraType.ModDecreaseSpeed)) with
        {
            Duration = new SpellDuration(4_000, 0, 4_000),
        };
        SpellInfo aspect = SpellTestKit.Spell(
            aspectSpellId,
            SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ProcTriggerSpell, trigger: 15571)) with
        {
            SpellFamilyName = 9,
            ProcFlags = ProcFlags.TakeMeleeSwing,
            ProcChance = 100,
            Duration = new SpellDuration(60_000, 0, 60_000),
        };

        using var kit = new SpellTestKit(daze, aspect);
        (Player hunter, _) = kit.AddPlayer(1);
        (Player attacker, _) = kit.AddPlayer(2, x: 2);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(hunter, aspectSpellId, SpellCastTargets.ForSelf(), triggered: true));

        // Non-damaging or absorbed hits (Amount = 0 or 1) must not trigger Daze (15571).
        kit.System.ProcDamageAndSpell(attacker, new ProcEvent
        {
            Victim = hunter,
            VictimFlags = ProcFlags.TakeMeleeSwing,
            Extra = ProcFlagsEx.NormalHit,
            Amount = 0,
        });
        Assert.False(kit.System.HasAura(hunter, 15571));

        kit.System.ProcDamageAndSpell(attacker, new ProcEvent
        {
            Victim = hunter,
            VictimFlags = ProcFlags.TakeMeleeSwing,
            Extra = ProcFlagsEx.NormalHit,
            Amount = 1,
        });
        Assert.False(kit.System.HasAura(hunter, 15571));

        // Damaging hit (Amount > 1) triggers Daze (15571).
        kit.System.ProcDamageAndSpell(attacker, new ProcEvent
        {
            Victim = hunter,
            VictimFlags = ProcFlags.TakeMeleeSwing,
            Extra = ProcFlagsEx.NormalHit,
            Amount = 25,
        });
        Assert.True(kit.System.HasAura(hunter, 15571));
    }

    [Fact]
    public void BloodFuryAndCannibalize_TriggerExpectedBuffDebuffAndHeal()
    {
        SpellInfo bloodFury = SpellTestKit.Spell(
            BloodFuryScript.BloodFury,
            SpellTestKit.Effect(SpellEffectName.Dummy, 0));
        SpellInfo bloodFuryBuff = SpellTestKit.Spell(
            BloodFuryScript.BloodFuryBuff,
            SpellTestKit.Effect(SpellEffectName.ApplyAura, 25, aura: AuraType.ModAttackPowerPct)) with
        {
            Duration = new SpellDuration(15_000, 0, 15_000),
        };
        SpellInfo bloodFuryDebuff = SpellTestKit.Spell(
            BloodFuryScript.BloodFuryDebuff,
            SpellTestKit.Effect(SpellEffectName.ApplyAura, -50, aura: AuraType.ModHealingPct)) with
        {
            Duration = new SpellDuration(25_000, 0, 25_000),
        };
        SpellInfo cannibalize = SpellTestKit.Spell(
            CannibalizeScript.Cannibalize,
            SpellTestKit.Effect(SpellEffectName.Dummy, 7));
        SpellInfo cannibalizeHeal = SpellTestKit.Spell(
            CannibalizeScript.CannibalizeHeal,
            SpellTestKit.Effect(SpellEffectName.ApplyAura, 7, aura: AuraType.ObsModHealth, amplitude: 2_000)) with
        {
            Duration = new SpellDuration(10_000, 0, 10_000),
        };

        using var kit = new SpellTestKit(bloodFury, bloodFuryBuff, bloodFuryDebuff, cannibalize, cannibalizeHeal);
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(BloodFuryScript).Assembly));
        (Player player, _) = kit.AddPlayer(1, race: Race.Orc);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, BloodFuryScript.BloodFury, SpellCastTargets.ForSelf(), triggered: true));
        Assert.True(kit.System.HasAura(player, BloodFuryScript.BloodFuryBuff));
        Assert.False(kit.System.HasAura(player, BloodFuryScript.BloodFuryDebuff));

        kit.System.RemoveAuras(player, BloodFuryScript.BloodFuryBuff);
        Assert.False(kit.System.HasAura(player, BloodFuryScript.BloodFuryBuff));
        Assert.True(kit.System.HasAura(player, BloodFuryScript.BloodFuryDebuff));

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, CannibalizeScript.Cannibalize, SpellCastTargets.ForSelf(), triggered: true));
        Assert.True(kit.System.HasAura(player, CannibalizeScript.CannibalizeHeal));
    }

    [Fact]
    public void ApplyAuraWithAuraTypeNone_IsACleanNoOp()
    {
        SpellInfo commandRank1 = SpellTestKit.Spell(
            21563,
            SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.None)) with
        {
            Duration = new SpellDuration(60_000, 0, 60_000),
        };

        using var kit = new SpellTestKit(commandRank1);
        (Player player, _) = kit.AddPlayer(1);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 21563, SpellCastTargets.ForSelf(), triggered: true));
        Assert.False(kit.System.HasAura(player, 21563));
    }

    [Fact]
    public void AttackSpawnDetectAndTransDoorEffects_AreRegistered_AndAttackStartsMeleeAutoAttack()
    {
        SpellInfo attackSpell = SpellTestKit.Spell(
            8376,
            SpellTestKit.Effect(SpellEffectName.Attack, 0, SpellImplicitTarget.UnitEnemy)) with
        {
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
        };

        using var kit = new SpellTestKit(attackSpell);
        Assert.True(kit.System.HasEffectHandler(SpellEffectName.Attack));
        Assert.True(kit.System.HasEffectHandler(SpellEffectName.Spawn));
        Assert.True(kit.System.HasEffectHandler(SpellEffectName.Detect));
        Assert.True(kit.System.HasEffectHandler(SpellEffectName.TransDoor));

        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, x: 2);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, 8376, SpellCastTargets.ForUnit(target.Guid), triggered: true));
        Assert.Same(target, caster.Combat.Victim);
        Assert.True(caster.Combat.IsMeleeAttacking);
    }

    [Fact]
    public void All35PromotedCombatAndSightAuras_HaveRegisteredHandlersAndHandlerSupportLevel()
    {
        AuraType[] promoted =
        [
            AuraType.ModSpellCritChance,
            AuraType.SchoolAbsorb,
            AuraType.ModSpellCritChanceSchool,
            AuraType.ModLanguage,
            AuraType.SplitDamagePct,
            AuraType.ModDetectRange,
            AuraType.PreventsFleeing,
            AuraType.InterruptRegen,
            AuraType.ManaShield,
            AuraType.ModPowerRegenPercent,
            AuraType.OverrideClassScripts,
            AuraType.ModHealing,
            AuraType.ModMechanicResistance,
            AuraType.ModHealingPct,
            AuraType.ModTargetResistance,
            AuraType.ModHealingDone,
            AuraType.ModHealingDonePercent,
            AuraType.RetainComboPoints,
            AuraType.ResistPushback,
            AuraType.ModDetectedRange,
            AuraType.SplitDamageFlat,
            AuraType.ModAoeAvoidance,
            AuraType.ModCritPercentVersus,
            AuraType.ModSpellHealingOfStatPercent,
            AuraType.ModDebuffResistance,
            AuraType.ModAttackerSpellCritChance,
            AuraType.ModCriticalThreat,
            AuraType.ExtraAttacks,
            AuraType.FarSight,
            AuraType.AddCasterHitTrigger,
            AuraType.PetDamageMulti,
            AuraType.ModCritDamageBonus,
            AuraType.DetectAmore,
            AuraType.ModFlatSpellCritDamageVersus,
            AuraType.AuraSpell,
        ];

        using var kit = new SpellTestKit();
        foreach (AuraType type in promoted)
        {
            Assert.True(kit.System.HasAuraHandler(type), $"{type} should have a registered aura handler");
            Assert.Equal(AuraSupportLevel.Handler, AuraSupport.Get(type).Level);
        }
    }

    [Fact]
    public void ModCritDamageBonus_IncreasesMeleeCriticalStrikeDamage()
    {
        SpellInfo physicalCritBonus = SpellTestKit.Spell(
            30050,
            SpellTestKit.Effect(SpellEffectName.ApplyAura, 10, aura: AuraType.ModCritDamageBonus, misc: 1)) with
        {
            Duration = new SpellDuration(60_000, 0, 60_000),
        };
        SpellInfo fireCritBonus = SpellTestKit.Spell(
            30051,
            SpellTestKit.Effect(SpellEffectName.ApplyAura, 10, aura: AuraType.ModCritDamageBonus, misc: 4)) with
        {
            Duration = new SpellDuration(60_000, 0, 60_000),
        };

        using var kit = new SpellTestKit(physicalCritBonus, fireCritBonus);
        (Player attacker, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, x: 2);
        attacker.Level = 60;
        victim.Level = 60;
        MapCombat combat = attacker.Map!.Combat;
        combat.SpellMitigation = kit.System;
        attacker.SetFloat(UpdateFields.UnitFieldMindamage, 100);
        attacker.SetFloat(UpdateFields.UnitFieldMaxdamage, 100);
        attacker.SetFloat(UpdateFields.PlayerCritPercentage, 200f);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(attacker, 30050, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(attacker, 30051, SpellCastTargets.ForSelf(), triggered: true));

        // The hit table draws from the default Random.Shared-backed roll unless scripted, so a miss, glancing blow or dodge
        // could land instead of the crit. Script the roll: 5000 is past the 5% miss range and inside the (clamped) crit range.
        var random = new ScriptedRandom();
        random.Ints.Enqueue(5000);
        combat.Random = random;

        MeleeDamageInfo hit = combat.CalculateMeleeDamage(attacker, victim, WeaponAttackType.BaseAttack);
        Assert.Equal(MeleeHitOutcome.Crit, hit.Outcome);
        Assert.Equal(210u, hit.TotalDamage);
    }

    [Fact]
    public void ModFlatSpellCritDamageVersus_IncreasesSpellCriticalDamageAgainstMatchingCreatureType()
    {
        SpellInfo flatCritVersusHumanoid = SpellTestKit.Spell(
            30060,
            SpellTestKit.Effect(SpellEffectName.ApplyAura, 25, aura: AuraType.ModFlatSpellCritDamageVersus, misc: 64)) with
        {
            Duration = new SpellDuration(60_000, 0, 60_000),
        };

        using var kit = new SpellTestKit(flatCritVersusHumanoid);
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, x: 5);

        var rules = new VanillaSpellCombatRules();
        SpellInfo bolt = kit.Store.Get(SpellTestKit.CastBolt)!;

        Assert.Equal(150u, rules.CriticalDamage(kit.System, caster, target, bolt, 100u));

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, 30060, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(175u, rules.CriticalDamage(kit.System, caster, target, bolt, 100u));
    }
}
