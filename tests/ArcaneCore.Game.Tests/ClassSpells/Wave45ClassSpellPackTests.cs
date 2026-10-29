using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Pets.PetAuras;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.ClassScripts;
using ArcaneCore.Game.Spells.Hunter;
using ArcaneCore.Game.Spells.Paladin;
using ArcaneCore.Game.Spells.Priest;
using ArcaneCore.Game.Spells.Procs;
using ArcaneCore.Game.Spells.Rogue;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Spells.Utility.Targets;
using ArcaneCore.Game.Spells.Warlock;
using ArcaneCore.Game.Tests.Pets;
using ArcaneCore.Game.Tests.Procs;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.ClassSpells;

/// <summary>
/// Wave 45 Retail 1.12.1 Class Spell, Proc &amp; Pet-Aura Completeness Pack:
/// Shaman Lightning Shield proc, Priest Touch of Weakness / Power Word: Shield / Holy Nova,
/// Hunter Wyvern Sting wake-up DoT / Mongoose Bite / Refocus / Frost Trap Aura Entrapment,
/// Rogue Eviscerate 1.12.0 attack power scaling, Warrior Shield Slam / Intimidating Shout / Sweeping Strikes stance retention,
/// Warlock Devour Magic self-heal / demon temporary unsummon pet-aura restore, and Frost stun/root AuraState.Frozen.
/// </summary>
public sealed class Wave45ClassSpellPackTests
{
    /// <summary>
    /// With spell batching on (<c>ARCANECORE_TEST_SPELL_BATCH_MS</c>) a cast that goes off is held until the next window boundary: advance
    /// the clock to it so the held go resolves. A no-op with the window at 0.
    /// </summary>
    private static void FlushSpellBatch(SpellTestKit kit)
    {
        uint window = kit.System.SpellBatchWindowMs;
        if (window != 0)
        {
            kit.Advance(window - (kit.Now % window));
        }
    }

    private static SpellInfo Instant(SpellInfo spell) => spell with { StartRecoveryCategory = 0, StartRecoveryTime = 0, SpellVisual = 1 };

    private static SpellInfo AtEnemy(SpellInfo spell) => Instant(spell with { RangeIndex = 4, Range = new SpellRange(0, 30) });

    // --- 1. Shaman — Lightning Shield (UnitAuraProcHandler.cpp:1504-1536) -------------------------------------------

    [Theory]
    [InlineData(324u, 26364u, 13)]
    [InlineData(325u, 26365u, 29)]
    [InlineData(905u, 26366u, 51)]
    [InlineData(945u, 26367u, 80)]
    [InlineData(8134u, 26369u, 114)]
    [InlineData(10431u, 26370u, 154)]
    [InlineData(10432u, 26363u, 198)]
    public void LightningShield_ProcsTheRanksDamageSpellAtTheAttacker_WithTheAurasAmount_AndSpendsACharge(uint auraSpellId, uint damageSpellId, int amount)
    {
        const uint dbcTrigger = 995_450;
        using var rig = new TalentProcRig(
            TalentProcRig.Talent(
                auraSpellId,
                AuraType.ProcTriggerSpell,
                amount,
                ProcFlags.TakeMeleeSwing,
                family: 11, // SPELLFAMILY_SHAMAN
                charges: 3,
                trigger: dbcTrigger) with
            {
                SpellFamilyFlags = 1UL << 10, // CF_SHAMAN_LIGHTNING_SHIELD
            },
            TalentProcRig.Probe(dbcTrigger, atEnemy: true, value: 1),
            TalentProcRig.Probe(damageSpellId, atEnemy: true, value: 1));

        Player shaman = rig.AddPlayer(1, 0, 0);
        Player attacker = rig.AddPlayer(2, 2, 0);
        rig.Apply(shaman, auraSpellId);

        rig.System.ProcDamageAndSpell(attacker, new ProcEvent
        {
            Victim = shaman,
            AttackerFlags = ProcFlags.DealMeleeSwing,
            VictimFlags = ProcFlags.TakeMeleeSwing | ProcFlags.TakenAnyDamage,
            Extra = ProcFlagsEx.NormalHit,
            Amount = 50,
            OriginalAmount = 50,
        });

        TalentProcRig.ProbeHit hit = Assert.Single(rig.Probes);
        Assert.Equal(damageSpellId, hit.SpellId);
        Assert.Same(shaman, hit.Caster);
        Assert.Same(attacker, hit.Target);
        Assert.Equal(amount, hit.Value);
        Assert.Equal(2, rig.System.GetAuras(shaman).Single(h => h.Spell.Id == auraSpellId).Charges);
    }

    // --- 2. Priest — Touch of Weakness, Power Word: Shield -> Weakened Soul, Holy Nova (spell_priest.cpp:19-131) ---

    [Theory]
    [InlineData(2652u, 2943u)]
    [InlineData(19261u, 19249u)]
    [InlineData(19262u, 19251u)]
    [InlineData(19264u, 19252u)]
    [InlineData(19265u, 19253u)]
    [InlineData(19266u, 19254u)]
    public void TouchOfWeakness_CastsTheHexRankMatchingTheProcAuraRank(uint auraSpellId, uint hexSpellId)
    {
        using var kit = new SpellTestKit(
            Instant(Spell(auraSpellId, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ProcTriggerSpell, trigger: TouchOfWeaknessScript.TouchOfWeakness)) with
            {
                ProcFlags = ProcFlags.TakeMeleeSwing,
                ProcChance = 100,
                ProcCharges = 1,
                Duration = new SpellDuration(120_000, 0, 120_000),
            }),
            AtEnemy(Spell(TouchOfWeaknessScript.TouchOfWeakness, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy))),
            AtEnemy(Spell(hexSpellId, Effect(SpellEffectName.ApplyAura, -5, SpellImplicitTarget.UnitEnemy, AuraType.ModDamageDone)) with
            {
                Attributes = SpellAttributes.AuraIsDebuff,
                Duration = new SpellDuration(120_000, 0, 120_000),
            }));
        (Player priest, _) = kit.AddPlayer(1);
        (Player attacker, _) = kit.AddPlayer(2, 2, 0);
        kit.System.Relations = new FakeRelations { Hostile = { attacker.Guid } };
        kit.System.CombatRules = new NoCritNoResistRules();
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));

        kit.System.CastSpell(priest, auraSpellId, SpellCastTargets.ForSelf(), triggered: true);
        kit.Now++;

        kit.System.ProcDamageAndSpell(attacker, new ProcEvent
        {
            Victim = priest,
            AttackerFlags = ProcFlags.DealMeleeSwing,
            VictimFlags = ProcFlags.TakeMeleeSwing | ProcFlags.TakenAnyDamage,
            Extra = ProcFlagsEx.NormalHit,
            Amount = 25,
            OriginalAmount = 25,
        });

        Assert.True(kit.System.HasAura(attacker, hexSpellId));
        Assert.False(kit.System.HasAura(priest, auraSpellId));
    }

    [Fact]
    public void PowerWordShield_AppliesWeakenedSoul_AndWeakenedSoulBlocksAnotherShield()
    {
        const uint pwsRank1 = 17;
        using var kit = new SpellTestKit(
            Instant(Spell(pwsRank1, Effect(SpellEffectName.ApplyAura, 44, SpellImplicitTarget.UnitFriend, AuraType.SchoolAbsorb, misc: 127)) with
            {
                School = SpellSchool.Holy,
                SpellFamilyName = 6,
                Mechanic = (uint)SpellMechanic.Shield,
                Duration = new SpellDuration(30_000, 0, 30_000),
                RangeIndex = 4,
                Range = new SpellRange(0, 40),
            }),
            Instant(Spell(PriestCastObserver.WeakenedSoul, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitFriend, AuraType.MechanicImmunity,
                misc: (int)SpellMechanic.Shield)) with
            {
                School = SpellSchool.Holy,
                Attributes = (SpellAttributes)0x24000000, // AURA_IS_DEBUFF | NO_IMMUNITIES
                Duration = new SpellDuration(15_000, 0, 15_000),
                RangeIndex = 4,
                Range = new SpellRange(0, 40),
            }));
        (Player priest, _) = kit.AddPlayer(1);
        (Player ally, _) = kit.AddPlayer(2, 2, 0);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(priest, pwsRank1, SpellCastTargets.ForUnit(ally.Guid), triggered: false));
        FlushSpellBatch(kit);
        Assert.True(kit.System.HasAura(ally, pwsRank1));
        Assert.True(kit.System.HasAura(ally, PriestCastObserver.WeakenedSoul));

        // A second Power Word: Shield while Weakened Soul is active is refused by PositiveSpellImmunityCheck (TargetAurastate).
        Assert.Equal(SpellCastResult.TargetAurastate, kit.System.CastSpell(priest, pwsRank1, SpellCastTargets.ForUnit(ally.Guid), triggered: false));
    }

    [Theory]
    [InlineData(15237u, 23455u)]
    [InlineData(27801u, 27805u)]
    public void HolyNova_CastsItsCorrespondingHealRankOnFinish(uint damageRankId, uint healRankId)
    {
        using var kit = new SpellTestKit(
            Instant(Spell(damageRankId, Effect(SpellEffectName.SchoolDamage, 30, SpellImplicitTarget.LocationCasterSrc, targetB: SpellImplicitTarget.EnumUnitsEnemyAoeAtSrcLoc) with { Radius = 10f }) with
            {
                School = SpellSchool.Holy,
                SpellFamilyName = 6,
            }),
            Instant(Spell(healRankId, Effect(SpellEffectName.Heal, 55, SpellImplicitTarget.UnitCaster)) with
            {
                School = SpellSchool.Holy,
                SpellFamilyName = 6,
            }));
        (Player priest, _) = kit.AddPlayer(1);
        priest.MaxHealth = 500;
        priest.Health = 200;

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(priest, damageRankId, SpellCastTargets.ForSelf(), triggered: false));
        FlushSpellBatch(kit);
        Assert.Equal(255u, priest.Health);
    }

    // --- 3. Hunter — Wyvern Sting, Mongoose Bite, Refocus, Frost Trap Aura (spell_hunter.cpp:19-154) ---------------

    [Theory]
    [InlineData(19386u, 24131u)]
    [InlineData(24132u, 24134u)]
    [InlineData(24133u, 24135u)]
    public void WyvernSting_AppliesWakeUpDot_WhenSleepHolderIsRemoved(uint sleepSpellId, uint dotSpellId)
    {
        using var kit = new SpellTestKit(
            AtEnemy(Spell(sleepSpellId, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.ModStun)) with
            {
                School = SpellSchool.Nature,
                Attributes = SpellAttributes.AuraIsDebuff,
                Duration = new SpellDuration(12_000, 0, 12_000),
            }),
            AtEnemy(Spell(dotSpellId, Effect(SpellEffectName.ApplyAura, 50, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 2000)) with
            {
                School = SpellSchool.Nature,
                Attributes = SpellAttributes.AuraIsDebuff,
                Duration = new SpellDuration(12_000, 0, 12_000),
            }));
        (Player hunter, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 3, 0);
        kit.System.Relations = new FakeRelations { Hostile = { victim.Guid } };

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(hunter, sleepSpellId, SpellCastTargets.ForUnit(victim.Guid), triggered: false));
        FlushSpellBatch(kit);
        Assert.True(kit.System.HasAura(victim, sleepSpellId));
        Assert.False(kit.System.HasAura(victim, dotSpellId));

        kit.System.RemoveAuras(victim, sleepSpellId);

        Assert.False(kit.System.HasAura(victim, sleepSpellId));
        Assert.True(kit.System.HasAura(victim, dotSpellId));
    }

    [Fact]
    public void MongooseBite_HitsOnlyTheDodgedAttackerDuringTheDefenseWindow()
    {
        const uint mongooseBite = 1495;
        using var kit = new SpellTestKit(
            Spell(mongooseBite, Effect(SpellEffectName.SchoolDamage, 35, SpellImplicitTarget.UnitEnemy)) with
            {
                CasterAuraState = AuraState.Defense,
                DamageClass = SpellDamageClass.Melee,
                SpellFamilyName = 9,
                RangeIndex = 2,
                Range = new SpellRange(0, 5),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            });
        (Player hunter, _) = kit.AddPlayer(1);
        hunter.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        (Player attacker, _) = kit.AddPlayer(2, 2, 0);
        (Player other, _) = kit.AddPlayer(3, 0, 2);
        kit.System.Relations = new FakeRelations { Hostile = { attacker.Guid, other.Guid } };
        var combos = new ComboPointService(kit.System, (_, guid) => kit.World.FindOnlinePlayer(guid));
        combos.Install();
        var states = new AuraStateService(kit.System, _ => []);
        AuraStateCastChecks.Install(kit.System, states);
        var reactives = new ReactiveService(states, () => combos);
        kit.System.CombatRules = new NoCritNoResistRules();
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));

        Assert.NotEqual(SpellCastResult.CastOk, kit.System.CastSpell(hunter, mongooseBite, SpellCastTargets.ForUnit(attacker.Guid), triggered: false));

        reactives.OnAttackAvoided(attacker, hunter, AttackAvoidance.Dodge);

        Assert.Equal(SpellCastResult.BadTargets, kit.System.CastSpell(hunter, mongooseBite, SpellCastTargets.ForUnit(other.Guid), triggered: false));
        uint before = attacker.Health;
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(hunter, mongooseBite, SpellCastTargets.ForUnit(attacker.Guid), triggered: false));
        FlushSpellBatch(kit);
        Assert.Equal(before - 35u, attacker.Health);

        reactives.Update(ReactiveService.ReactiveTimerStartMs);
        Assert.Equal(SpellCastResult.CasterAurastate, kit.System.CastSpell(hunter, mongooseBite, SpellCastTargets.ForUnit(attacker.Guid), triggered: false));
    }

    [Fact]
    public void Refocus_FinishesCooldownsOnAimedShotMultiShotArcaneShotAndVolley_LeavingOtherHunterCooldownsIntact()
    {
        const uint aimedShot = 19434;
        const uint multiShot = 2643;
        const uint arcaneShot = 3044;
        const uint volley = 1510;
        const uint rapidFire = 3045;

        static SpellInfo HunterShot(uint id, ulong flag) => Instant(Spell(id, Effect(SpellEffectName.Dummy, 0)) with
        {
            SpellFamilyName = 9,
            SpellFamilyFlags = flag,
            RecoveryTime = 10_000,
        });

        using var kit = new SpellTestKit(
            Instant(Spell(RefocusScript.Refocus, Effect(SpellEffectName.Dummy, 0)) with { SpellFamilyName = 9 }),
            HunterShot(arcaneShot, 1UL << 11),
            HunterShot(multiShot, 1UL << 12),
            HunterShot(volley, 1UL << 13),
            HunterShot(aimedShot, 1UL << 17),
            HunterShot(rapidFire, 1UL << 5));
        (Player hunter, _) = kit.AddPlayer(1);
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));

        foreach (uint shot in new[] { arcaneShot, multiShot, volley, aimedShot, rapidFire })
        {
            Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(hunter, shot, SpellCastTargets.ForSelf(), triggered: false));
            FlushSpellBatch(kit);
            Assert.Contains(kit.System.GetActiveCooldowns(hunter), c => c.SpellId == shot);
        }

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(hunter, RefocusScript.Refocus, SpellCastTargets.ForSelf(), triggered: true));

        Assert.DoesNotContain(kit.System.GetActiveCooldowns(hunter), c => c.SpellId == arcaneShot);
        Assert.DoesNotContain(kit.System.GetActiveCooldowns(hunter), c => c.SpellId == multiShot);
        Assert.DoesNotContain(kit.System.GetActiveCooldowns(hunter), c => c.SpellId == volley);
        Assert.DoesNotContain(kit.System.GetActiveCooldowns(hunter), c => c.SpellId == aimedShot);
        Assert.Contains(kit.System.GetActiveCooldowns(hunter), c => c.SpellId == rapidFire);
    }

    [Fact]
    public void FrostTrapAura_PeriodicTickFiresOnTrapActivationProc_OnTheHunterForEntrapment()
    {
        const uint entrapmentTalent = 19184;
        const uint entrapmentRoot = 19185;
        const uint frostTrapTrigger = 13809;

        using var rig = new TalentProcRig(
            TalentProcRig.Talent(entrapmentTalent, AuraType.ProcTriggerSpell, 0, ProcFlags.OnTrapActivation, family: 9, trigger: entrapmentRoot),
            TalentProcRig.Probe(entrapmentRoot, atEnemy: true, value: 1),
            TalentProcRig.Probe(frostTrapTrigger, atEnemy: false, value: 1),
            Spell(HunterScriptsModule.FrostTrapAura,
                Effect(SpellEffectName.ApplyAura, -60, SpellImplicitTarget.UnitEnemy, AuraType.ModDecreaseSpeed),
                Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.PeriodicTriggerSpell, amplitude: 1000, trigger: frostTrapTrigger)) with
            {
                Duration = new SpellDuration(30_000, 0, 30_000),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            });

        Player hunter = rig.AddPlayer(1, 0, 0);
        rig.Apply(hunter, entrapmentTalent);
        rig.Apply(hunter, HunterScriptsModule.FrostTrapAura);
        rig.Probes.Clear();

        rig.Kit.Advance(1000);

        Assert.Contains(rig.Probes, p => p.SpellId == entrapmentRoot && ReferenceEquals(p.Caster, hunter));
    }

    // --- 4. Rogue — Eviscerate 1.12.0 Attack Power Scaling (spell_rogue.cpp:19-46) ----------------------------------

    [Theory]
    [InlineData(1, 30)]  // 1000 * 1 * 0.03 = 30
    [InlineData(3, 90)]  // 1000 * 3 * 0.03 = 90
    [InlineData(5, 150)] // 1000 * 5 * 0.03 = 150
    public void Eviscerate_AddsThreePercentOfAttackPowerPerComboPoint(int comboPoints, int expectedApBonus)
    {
        const uint eviscerateRank1 = 2098;
        const uint finishingDamage = 0x00100000;
        using var kit = new SpellTestKit(
            AtEnemy(Spell(eviscerateRank1, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy) with { PointsPerComboPoint = 15f }) with
            {
                AttributesEx = (SpellAttributesEx)finishingDamage,
                DamageClass = SpellDamageClass.Melee,
                SpellFamilyName = RogueBleedScripts.RogueFamily,
                SpellFamilyFlags = 1UL << RogueBleedScripts.EviscerateFlagBit,
            }));
        (Player rogue, _) = kit.AddPlayer(1);
        (Player enemy, _) = kit.AddPlayer(2, 2, 0);
        enemy.MaxHealth = 5000;
        enemy.Health = 5000;
        kit.System.Relations = new FakeRelations { Hostile = { enemy.Guid } };
        kit.System.CombatRules = new NoCritNoResistRules();
        var combos = new ComboPointService(kit.System, (_, guid) => kit.World.FindOnlinePlayer(guid));
        combos.Install();
        RogueBleedScripts.Install(kit.System, combos);
        rogue.SetInt32(UpdateFields.UnitFieldAttackPower, 1000);

        combos.AddComboPoints(rogue, enemy, comboPoints);
        uint before = enemy.Health;

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(rogue, eviscerateRank1, SpellCastTargets.ForUnit(enemy.Guid), triggered: false));
        FlushSpellBatch(kit);

        uint expectedDamage = (uint)(10 + (15 * comboPoints) + expectedApBonus);
        Assert.Equal(before - expectedDamage, enemy.Health);
    }

    // --- 5. Warrior — Shield Slam, Intimidating Shout, Sweeping Strikes (spell_warrior.cpp:19-74, 149-162) ----------

    [Fact]
    public void ShieldSlam_AddsShieldBlockValueToEffect1Damage()
    {
        const uint shieldSlamRank1 = 23922;
        using var kit = new SpellTestKit(
            AtEnemy(Spell(shieldSlamRank1,
                Effect(SpellEffectName.Dispel, 1, SpellImplicitTarget.UnitEnemy, misc: (int)DispelType.Magic),
                Effect(SpellEffectName.SchoolDamage, 225, SpellImplicitTarget.UnitEnemy)) with
            {
                DamageClass = SpellDamageClass.Melee,
                SpellFamilyName = 4,
            }));
        (Player warrior, _) = kit.AddPlayer(1);
        (Player enemy, _) = kit.AddPlayer(2, 2, 0);
        enemy.MaxHealth = 5000;
        enemy.Health = 5000;
        kit.System.Relations = new FakeRelations { Hostile = { enemy.Guid } };
        kit.System.CombatRules = new NoCritNoResistRules();

        // Give the warrior 100 flat shield block and 40 strength (+1 block value from str/20 - 1 = 101 total).
        warrior.StatState.ShieldBlockFlat = 100;
        warrior.SetUInt32(UpdateFields.UnitFieldStat0, 40);
        uint expectedBlock = ShieldSlamDamage.GetShieldBlockValue(warrior);
        Assert.Equal(101u, expectedBlock);

        uint before = enemy.Health;
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(warrior, shieldSlamRank1, SpellCastTargets.ForUnit(enemy.Guid), triggered: false));
        FlushSpellBatch(kit);
        Assert.Equal(before - (225u + expectedBlock), enemy.Health);
    }

    [Fact]
    public void IntimidatingShout_ExcludesPrimaryTargetFromAoEFearAndThreatEffects()
    {
        using var kit = new SpellTestKit(
            AtEnemy(Spell(WarriorScriptsModule.IntimidatingShout,
                Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.ModFear),
                Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.LocationCasterSrc, aura: AuraType.ModFear, targetB: SpellImplicitTarget.EnumUnitsEnemyAoeAtSrcLoc) with { Radius = 10f },
                Effect(SpellEffectName.Threat, 50, SpellImplicitTarget.LocationCasterSrc, targetB: SpellImplicitTarget.EnumUnitsEnemyAoeAtSrcLoc) with { Radius = 10f }) with
            {
                SpellFamilyName = 4,
                Duration = new SpellDuration(8_000, 0, 8_000),
            }));
        (Player warrior, _) = kit.AddPlayer(1, 0, 0);
        (Player primary, _) = kit.AddPlayer(2, 2, 0);
        (Player bystander, _) = kit.AddPlayer(3, 3, 0);
        kit.System.Relations = new FakeRelations { Hostile = { primary.Guid, bystander.Guid } };
        kit.System.CombatRules = new NoCritNoResistRules();

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(warrior, WarriorScriptsModule.IntimidatingShout, SpellCastTargets.ForUnit(primary.Guid), triggered: false));
        FlushSpellBatch(kit);

        SpellAuraHolder primaryHolder = Assert.Single(kit.System.GetAuras(primary), h => h.Spell.Id == WarriorScriptsModule.IntimidatingShout);
        Assert.NotNull(primaryHolder.Auras[0]); // Effect 0 (cower on primary target) landed
        Assert.Null(primaryHolder.Auras[1]);    // Effect 1 (AoE fear) filtered out on primary target

        SpellAuraHolder bystanderHolder = Assert.Single(kit.System.GetAuras(bystander), h => h.Spell.Id == WarriorScriptsModule.IntimidatingShout);
        Assert.Null(bystanderHolder.Auras[0]);
        Assert.NotNull(bystanderHolder.Auras[1]); // Effect 1 (AoE fear) landed on bystander
    }

    [Fact]
    public void SweepingStrikes_IsRetainedAcrossWarriorStanceChanges_WhileOtherStanceBoundAurasAreRemoved()
    {
        const uint battleStance = 2457;
        const uint berserkerStance = 2458;
        const uint sweepingStrikes = 12292;
        const uint retaliation = 20230;
        const uint battleStanceMask = 1u << ((int)ShapeshiftForm.BattleStance - 1);

        using var kit = new SpellTestKit(
            Instant(Spell(battleStance, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ModShapeshift, misc: (int)ShapeshiftForm.BattleStance)) with
            {
                Duration = new SpellDuration(-1, 0, -1),
            }),
            Instant(Spell(berserkerStance, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ModShapeshift, misc: (int)ShapeshiftForm.BerserkerStance)) with
            {
                Duration = new SpellDuration(-1, 0, -1),
            }),
            Instant(Spell(sweepingStrikes, Effect(SpellEffectName.ApplyAura, 1, aura: AuraType.Dummy)) with
            {
                Stances = battleStanceMask,
                Duration = new SpellDuration(20_000, 0, 20_000),
            }),
            Instant(Spell(retaliation, Effect(SpellEffectName.ApplyAura, 1, aura: AuraType.Dummy)) with
            {
                Stances = battleStanceMask,
                Duration = new SpellDuration(15_000, 0, 15_000),
            }));
        (Player warrior, _) = kit.AddPlayer(1);
        warrior.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Warrior);
        var shapeshift = new ShapeshiftService(kit.System, ShapeshiftFormCatalog.WarriorStances, new CombatOptions(), _ => []);
        shapeshift.Install();

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(warrior, battleStance, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(warrior, sweepingStrikes, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(warrior, retaliation, SpellCastTargets.ForSelf(), triggered: true));
        Assert.True(kit.System.HasAura(warrior, sweepingStrikes));
        Assert.True(kit.System.HasAura(warrior, retaliation));

        // Switch to Berserker Stance: Retaliation is removed on shape lost, Sweeping Strikes stays!
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(warrior, berserkerStance, SpellCastTargets.ForSelf(), triggered: true));
        Assert.True(kit.System.HasAura(warrior, sweepingStrikes));
        Assert.False(kit.System.HasAura(warrior, retaliation));
    }

    // --- 6. Warlock Devour Magic, Demon Temporary Unsummon Pet Aura Restore, Frost AuraState.Frozen -----------------

    [Theory]
    [InlineData(19505u, 19658u)]
    [InlineData(19731u, 19732u)]
    [InlineData(19734u, 19733u)]
    [InlineData(19736u, 19735u)]
    public void DevourMagic_CastsRankSelfHeal_OnlyWhenItDispelsAtLeastOneMagicAura(uint devourSpellId, uint healSpellId)
    {
        const uint magicBuff = 995_460;
        using var kit = new SpellTestKit(
            AtEnemy(Spell(devourSpellId, Effect(SpellEffectName.Dispel, 1, SpellImplicitTarget.UnitEnemy, misc: (int)DispelType.Magic)) with
            {
                School = SpellSchool.Shadow,
            }),
            Instant(Spell(healSpellId, Effect(SpellEffectName.Heal, 95, SpellImplicitTarget.UnitCaster)) with
            {
                School = SpellSchool.Shadow,
            }),
            Instant(Spell(magicBuff, Effect(SpellEffectName.ApplyAura, 10, SpellImplicitTarget.UnitFriend, AuraType.ModResistance)) with
            {
                School = SpellSchool.Arcane,
                Dispel = (uint)DispelType.Magic,
                Duration = new SpellDuration(60_000, 0, 60_000),
            }));
        (Player felhunter, _) = kit.AddPlayer(1);
        (Player enemy, _) = kit.AddPlayer(2, 2, 0);
        felhunter.MaxHealth = 500;
        felhunter.Health = 200;
        kit.System.Relations = new FakeRelations { Hostile = { enemy.Guid } };
        kit.System.CombatRules = new NoCritNoResistRules();
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));

        // No magic buff on enemy -> NothingToDispel and no heal.
        Assert.Equal(SpellCastResult.NothingToDispel, kit.System.CastSpell(felhunter, devourSpellId, SpellCastTargets.ForUnit(enemy.Guid), triggered: false));
        Assert.Equal(200u, felhunter.Health);

        // Apply magic buff -> dispelled -> self-heal cast!
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(enemy, magicBuff, SpellCastTargets.ForSelf(), triggered: true));
        Assert.True(kit.System.HasAura(enemy, magicBuff));

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(felhunter, devourSpellId, SpellCastTargets.ForUnit(enemy.Guid), triggered: false));
        FlushSpellBatch(kit);
        Assert.False(kit.System.HasAura(enemy, magicBuff));
        Assert.Equal(295u, felhunter.Health);
    }

    [Fact]
    public void TemporaryUnsummon_RestoresOwnerPetAurasAndSoulLinkOnWarlockDemon()
    {
        const uint impEntry = PetAuraTable.Imp;
        const uint summonImp = 688;
        const uint soulLink = PetAuraTable.SoulLink;
        const uint soulLinkAura = 25228;
        const uint masterDemonologist = PetAuraTable.MasterDemonologist1;
        const uint impThreat = 23759;

        static SpellEffectInfo PetArea(AuraType aura, int value, int misc = 0) =>
            Effect(SpellEffectName.ApplyAreaAuraPet, value, aura: aura, misc: misc) with { Radius = 100f };

        using var spells = new SpellTestKit(
            Instant(Spell(summonImp, Effect(SpellEffectName.SummonPet, 0, (SpellImplicitTarget)32, misc: (int)impEntry))),
            Instant(Spell(soulLink, Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.UnitCasterPet))),
            Instant(Spell(soulLinkAura, PetArea(AuraType.ModDamagePercentDone, 3, misc: 127), PetArea(AuraType.SplitDamagePct, 30, misc: 127))) with
            {
                Duration = new SpellDuration(-1, 0, -1),
            },
            Instant(Spell(masterDemonologist, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy))) with
            {
                Attributes = SpellAttributes.Passive,
                Duration = new SpellDuration(-1, 0, -1),
            },
            Instant(Spell(impThreat, PetArea(AuraType.ModThreat, -4, misc: 127))) with
            {
                Duration = new SpellDuration(-1, 0, -1),
            });

        Map map = spells.World.GetMap(0);
        CreatureContent content = CreatureTestKit.Content(
            [
                CreatureTestKit.Template(impEntry, b =>
                {
                    b.Name = "Imp";
                    b.Faction = 14;
                    b.MinLevel = 5;
                    b.MaxLevel = 5;
                    b.MinLevelHealth = 100;
                    b.MaxLevelHealth = 100;
                }) with { CreatureType = 3 },
            ],
            []);
        var creatures = new CreatureMapSystem(map, content, random: new Random(1));
        map.AddUpdater(creatures);
        var service = new SummonService(systems: m => ReferenceEquals(m, map) ? creatures : null, random: new Random(3));
        spells.System.Units = new MapObjectResolver();
        service.Install(spells.System);
        service.InstallDemons(spells.System);
        spells.System.Summons = service;
        PetTargets.Install(spells.System);
        SpellScriptDispatcher.Install(spells.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));

        (Player warlock, _) = spells.AddPlayer(1);
        warlock.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Warlock);
        warlock.Level = 40;

        spells.System.CastSpell(warlock, masterDemonologist, SpellCastTargets.ForSelf(), triggered: true);
        spells.System.CastSpell(warlock, summonImp, SpellCastTargets.ForSelf(), triggered: true);
        Creature initialImp = Assert.IsType<Creature>(warlock.GetPet());
        spells.System.CastSpell(warlock, soulLink, SpellCastTargets.ForUnit(initialImp.Guid), triggered: true);

        Assert.True(spells.System.HasAura(initialImp, impThreat));
        Assert.True(spells.System.HasAura(initialImp, soulLinkAura));

        // Temporarily unsummon (e.g., mounting or boarding transport) and restore.
        Assert.True(service.UnsummonPetTemporarily(warlock));
        Assert.Null(warlock.GetPet());

        Creature restoredImp = Assert.IsType<Creature>(service.ResummonTemporarilyUnsummonedPet(warlock));
        Assert.True(spells.System.HasAura(restoredImp, impThreat));
        Assert.True(spells.System.HasAura(restoredImp, soulLinkAura));
        Assert.True(spells.System.HasAura(warlock, impThreat));
        Assert.True(spells.System.HasAura(warlock, soulLinkAura));
    }

    [Fact]
    public void FrostRootAndStun_SetAuraStateFrozen_KeepItWhileAnotherFrostControlRemains_AndClearItWhenAllLeave()
    {
        const uint frostNova = 122;
        const uint frostbite = 12494;
        const uint natureRoot = 339;
        const uint frozenBit = 1u << ((int)AuraState.Frozen - 1);

        using var kit = new SpellTestKit(
            AtEnemy(Spell(frostNova, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.ModRoot)) with
            {
                School = SpellSchool.Frost,
                Attributes = SpellAttributes.AuraIsDebuff,
                Duration = new SpellDuration(8_000, 0, 8_000),
            }),
            AtEnemy(Spell(frostbite, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.ModStun)) with
            {
                School = SpellSchool.Frost,
                Attributes = SpellAttributes.AuraIsDebuff,
                Duration = new SpellDuration(5_000, 0, 5_000),
            }),
            AtEnemy(Spell(natureRoot, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.ModRoot)) with
            {
                School = SpellSchool.Nature,
                Attributes = SpellAttributes.AuraIsDebuff,
                Duration = new SpellDuration(8_000, 0, 8_000),
            }));
        (Player mage, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 3, 0);
        kit.System.Relations = new FakeRelations { Hostile = { target.Guid } };

        // Non-Frost root does not set AURA_STATE_FROZEN.
        kit.System.CastSpell(mage, natureRoot, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        Assert.Equal(0u, target.GetUInt32(UpdateFields.UnitFieldAurastate) & frozenBit);

        // Frost Nova sets AURA_STATE_FROZEN.
        kit.System.CastSpell(mage, frostNova, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        Assert.NotEqual(0u, target.GetUInt32(UpdateFields.UnitFieldAurastate) & frozenBit);

        // Overlapping Frost stun keeps AURA_STATE_FROZEN when Frost Nova is removed.
        kit.System.CastSpell(mage, frostbite, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        kit.System.RemoveAuras(target, frostNova);
        Assert.NotEqual(0u, target.GetUInt32(UpdateFields.UnitFieldAurastate) & frozenBit);

        // Removing the last Frost root/stun clears AURA_STATE_FROZEN.
        kit.System.RemoveAuras(target, frostbite);
        Assert.Equal(0u, target.GetUInt32(UpdateFields.UnitFieldAurastate) & frozenBit);
    }
}
