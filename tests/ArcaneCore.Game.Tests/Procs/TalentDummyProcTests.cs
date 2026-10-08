using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Procs;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.Procs;
using Xunit;
using static ArcaneCore.Game.Tests.Procs.TalentProcRig;

namespace ArcaneCore.Game.Tests.Procs;

/// <summary>
/// The talents whose proc is a DUMMY aura with a scripted effect (vmangos <c>Unit::HandleDummyAuraProc</c>, UnitAuraProcHandler.cpp:550-1145):
/// Eye for an Eye, Sweeping Strikes, Retaliation, Magic Absorption, Master of Elements, Vampiric Embrace and Blade Flurry. Each test drives the
/// proc engine with one event and reads the triggered spell's base points, recipient and charges against the vmangos formula.
/// </summary>
public sealed class TalentDummyProcTests
{
    private const uint EyeForAnEye = 9799;
    private const uint EyeForAnEyeDamage = 25997;
    private const uint SweepingStrikes = 12292;
    private const uint SweepingStrikesDamage = 12723;
    private const uint Retaliation = 20230;
    private const uint RetaliationStrike = 22858;
    private const uint MagicAbsorption = 29441;
    private const uint MagicAbsorptionMana = 29442;
    private const uint MasterOfElements = 29076;
    private const uint MasterOfElementsMana = 29077;
    private const uint VampiricEmbrace = 15286;
    private const uint VampiricEmbraceHeal = 15290;
    private const uint BladeFlurry = 13877;
    private const uint BladeFlurryDamage = 22482;
    private const uint Fireball = 995_001;
    private const uint Frostbolt = 995_002;
    private const uint ArcaneMissile = 995_003;
    private const uint ShadowBolt = 995_004;
    private const uint Strike = 995_005;

    private const uint FamilyMage = 3;
    private const uint FamilyPriest = 6;
    private const uint FamilyRogue = 8;
    private const uint FamilyWarrior = 4;

    private static TalentProcRig NewRig() => new(
        Talent(EyeForAnEye, AuraType.Dummy, 30, ProcFlags.TakeHarmfulSpell),
        Probe(EyeForAnEyeDamage, atEnemy: true),
        Talent(SweepingStrikes, AuraType.Dummy, 0, ProcFlags.DealMeleeSwing | ProcFlags.DealMeleeAbility, FamilyWarrior, charges: 5),
        Probe(SweepingStrikesDamage, atEnemy: true),
        Talent(Retaliation, AuraType.Dummy, 0, ProcFlags.TakeMeleeSwing, FamilyWarrior, charges: 30),
        Probe(RetaliationStrike, atEnemy: true),
        Talent(MagicAbsorption, AuraType.Dummy, 2, ProcFlags.TakeHarmfulSpell, FamilyMage, icon: 459),
        Probe(MagicAbsorptionMana, atEnemy: false),
        Talent(MasterOfElements, AuraType.Dummy, 30, ProcFlags.DealHarmfulSpell, FamilyMage, icon: 1920),
        Probe(MasterOfElementsMana, atEnemy: false),
        Talent(VampiricEmbrace, AuraType.Dummy, 20, ProcFlags.TakeHarmfulSpell, FamilyPriest) with
        {
            Attributes = SpellAttributes.AuraIsDebuff,
            Effects = [SpellTestKit.Effect(SpellEffectName.ApplyAura, 20, SpellImplicitTarget.UnitEnemy, AuraType.Dummy)],
            Duration = new SpellDuration(60_000, 0, 60_000),
            School = SpellSchool.Shadow,
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
        },
        Probe(VampiricEmbraceHeal, atEnemy: false),
        Talent(BladeFlurry, AuraType.Dummy, 0, ProcFlags.DealMeleeSwing | ProcFlags.DealMeleeAbility, FamilyRogue),
        Probe(BladeFlurryDamage, atEnemy: true),
        Bolt(Fireball, SpellSchool.Fire, FamilyMage, 0x1, manaCost: 200, manaCostPct: 10),
        Bolt(Frostbolt, SpellSchool.Frost, FamilyMage, 0x20, manaCost: 100),
        Bolt(ArcaneMissile, SpellSchool.Arcane, FamilyMage, 0x800, manaCost: 100),
        Bolt(ShadowBolt, SpellSchool.Shadow, FamilyPriest, 0x2000, manaCost: 100),
        SpellTestKit.Spell(Strike, SpellTestKit.Effect(SpellEffectName.WeaponDamage, 0, SpellImplicitTarget.UnitEnemy)) with
        {
            DamageClass = SpellDamageClass.Melee,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        });

    private static SpellAuraHolder Holder(SpellSystem system, Unit unit, uint spellId) => system.GetAuras(unit).Single(h => h.Spell.Id == spellId);

    // --- Eye for an Eye (UnitAuraProcHandler.cpp:577-593) ---------------------------------------------------------------------------------

    [Fact]
    public void EyeForAnEye_ReturnsItsPercentOfTheCriticalSpellsDamage_ToTheCaster()
    {
        using TalentProcRig rig = NewRig();
        Player paladin = rig.AddPlayer(1, 0, 0);
        Player mage = rig.AddPlayer(2, 5, 0, Race.Orc);
        rig.Apply(paladin, EyeForAnEye);

        rig.System.ProcDamageAndSpell(mage, SpellHit(paladin, rig.Kit.Store.Get(Fireball)!, ProcFlagsEx.CriticalHit, amount: 180, original: 200));

        // basepoints = rand_dither(30% x originalAmount 200) = 60, cast by the paladin at the mage.
        TalentProcRig.ProbeHit hit = Assert.Single(rig.Probes);
        Assert.Equal((EyeForAnEyeDamage, paladin, mage, 60), (hit.SpellId, hit.Caster, hit.Target, hit.Value));
    }

    [Fact]
    public void EyeForAnEye_IsCappedAtHalfTheOwnersMaximumHealth()
    {
        using TalentProcRig rig = NewRig();
        Player paladin = rig.AddPlayer(1, 0, 0);
        Player mage = rig.AddPlayer(2, 5, 0, Race.Orc);
        rig.Apply(paladin, EyeForAnEye);

        rig.System.ProcDamageAndSpell(mage, SpellHit(paladin, rig.Kit.Store.Get(Fireball)!, ProcFlagsEx.CriticalHit, amount: 4000, original: 4000));

        Assert.Equal(500, Assert.Single(rig.Probes).Value); // 30% of 4000 = 1200 > 1000 / 2
    }

    [Fact]
    public void EyeForAnEye_IgnoresWeaponSpecialAttacks()
    {
        using TalentProcRig rig = NewRig();
        Player paladin = rig.AddPlayer(1, 0, 0);
        Player warrior = rig.AddPlayer(2, 5, 0, Race.Orc);
        rig.Apply(paladin, EyeForAnEye);

        // "prevent damage back from weapon special attacks": a critical melee ability is not a magic spell.
        rig.System.ProcDamageAndSpell(warrior, new ProcEvent
        {
            Victim = paladin,
            VictimFlags = ProcFlags.TakeHarmfulSpell | ProcFlags.TakenAnyDamage,
            Extra = ProcFlagsEx.CriticalHit,
            Amount = 200,
            OriginalAmount = 200,
            ProcSpell = rig.Kit.Store.Get(Strike),
        });

        Assert.Empty(rig.Probes);
    }

    // --- Sweeping Strikes (UnitAuraProcHandler.cpp:594-656) -------------------------------------------------------------------------------

    [Fact]
    public void SweepingStrikes_HitsAnotherNearbyEnemy_WithTheDamageBeforeTheVictimsArmor_AndSpendsACharge()
    {
        using TalentProcRig rig = NewRig();
        Player warrior = rig.AddPlayer(1, 0, 0);
        Player victim = rig.AddPlayer(2, 2, 0, Race.Orc);
        Player second = rig.AddPlayer(3, 0, 2, Race.Orc);
        rig.AddPlayer(4, -2, 0); // a friendly player in reach is never the second target
        rig.AddPlayer(5, 0, 40, Race.Orc); // an enemy out of reach neither
        victim.SetInt32(UpdateFields.UnitFieldResistances, 2750); // level 60: 2750 / (8.5 x 60 + 40) x 0.1 = 0.5 -> 33.3% reduction
        rig.Apply(warrior, SweepingStrikes);

        rig.System.ProcDamageAndSpell(warrior, Swing(victim, 66));

        // rand_ditheru(66 x 100 / CalcArmorReducedDamage(victim, 100) = 66) = 100, on the other enemy.
        TalentProcRig.ProbeHit hit = Assert.Single(rig.Probes);
        Assert.Equal((SweepingStrikesDamage, warrior, second, 100), (hit.SpellId, hit.Caster, hit.Target, hit.Value));
        Assert.Equal(4, Holder(rig.System, warrior, SweepingStrikes).Charges);
    }

    [Fact]
    public void SweepingStrikes_DoesNotFireFromNonDamagingHits_OrWithoutASecondTarget_AndKeepsItsCharges()
    {
        using TalentProcRig rig = NewRig();
        Player warrior = rig.AddPlayer(1, 0, 0);
        Player victim = rig.AddPlayer(2, 2, 0, Race.Orc);
        rig.Apply(warrior, SweepingStrikes);

        rig.System.ProcDamageAndSpell(warrior, Swing(victim, 50)); // nobody else in reach
        Player second = rig.AddPlayer(3, 0, 2, Race.Orc);
        rig.System.ProcDamageAndSpell(warrior, Swing(victim, 1));  // "amount is 1 for non damaging spells if they hit"

        Assert.Empty(rig.Probes);
        Assert.Equal(5, Holder(rig.System, warrior, SweepingStrikes).Charges);
        Assert.True(second.IsAlive);
    }

    [Fact]
    public void SweepingStrikes_SkipsPvpFlaggedPlayers_WhenTheWarriorIsNotFlagged()
    {
        using TalentProcRig rig = NewRig();
        Player warrior = rig.AddPlayer(1, 0, 0, pvp: false);
        Player victim = rig.AddPlayer(2, 2, 0, Race.Orc);
        rig.AddPlayer(3, 0, 2, Race.Orc);
        rig.Apply(warrior, SweepingStrikes);

        // 1.7.0: "will ignore PvP enabled targets if you are not PvP enabled" (CanAttackWithoutEnablingPvP).
        rig.System.ProcDamageAndSpell(warrior, Swing(victim, 50));

        Assert.Empty(rig.Probes);
    }

    // --- Retaliation (UnitAuraProcHandler.cpp:660-675) ------------------------------------------------------------------------------------

    [Fact]
    public void Retaliation_StrikesAnAttackerInFront_AndSpendsACharge()
    {
        using TalentProcRig rig = NewRig();
        Player warrior = rig.AddPlayer(1, 0, 0, orientation: 0f); // facing +x
        Player attacker = rig.AddPlayer(2, 2, 0, Race.Orc);
        rig.Apply(warrior, Retaliation);

        rig.System.ProcDamageAndSpell(attacker, Struck(warrior));

        TalentProcRig.ProbeHit hit = Assert.Single(rig.Probes);
        Assert.Equal((RetaliationStrike, warrior, attacker), (hit.SpellId, hit.Caster, hit.Target));
        Assert.Equal(29, Holder(rig.System, warrior, Retaliation).Charges);
    }

    [Fact]
    public void Retaliation_IgnoresAttacksFromBehind_AndWhileStunned()
    {
        using TalentProcRig rig = NewRig();
        Player warrior = rig.AddPlayer(1, 0, 0, orientation: 0f);
        Player behind = rig.AddPlayer(2, -2, 0, Race.Orc);
        Player front = rig.AddPlayer(3, 2, 0, Race.Orc);
        rig.Apply(warrior, Retaliation);

        rig.System.ProcDamageAndSpell(behind, Struck(warrior));
        warrior.UnitFlags |= UnitFlags.Stunned; // UNIT_STATE_CAN_NOT_REACT (1.7.0: "not possible while stunned")
        rig.System.ProcDamageAndSpell(front, Struck(warrior));

        Assert.Empty(rig.Probes);
        Assert.Equal(30, Holder(rig.System, warrior, Retaliation).Charges);
    }

    // --- Magic Absorption (UnitAuraProcHandler.cpp:832-845) --------------------------------------------------------------------------------

    [Fact]
    public void MagicAbsorption_RestoresItsPercentOfMaximumMana_OnAResist()
    {
        using TalentProcRig rig = NewRig();
        Player mage = rig.AddPlayer(1, 0, 0);
        Player priest = rig.AddPlayer(2, 5, 0, Race.Orc);
        GiveMana(mage, maxMana: 3000);
        // vmangos spell_proc_event: the talent procs on a resisted spell (PROC_EX_RESIST); Spell.dbc alone would demand a hit.
        rig.UseRows(new SpellProcEventRecord(MagicAbsorption, 0, 0, 0, 0, 0, 0, (uint)ProcFlagsEx.Resist, 0, 0, 0));
        rig.Apply(mage, MagicAbsorption);

        rig.System.ProcDamageAndSpell(priest, new ProcEvent
        {
            Victim = mage,
            VictimFlags = ProcFlags.TakeHarmfulSpell,
            Extra = ProcFlagsEx.Resist,
            ProcSpell = rig.Kit.Store.Get(ShadowBolt),
        });

        // basepoints = rand_dither(2% x 3000) = 60, on the mage.
        TalentProcRig.ProbeHit hit = Assert.Single(rig.Probes);
        Assert.Equal((MagicAbsorptionMana, mage, mage, 60), (hit.SpellId, hit.Caster, hit.Target, hit.Value));
    }

    [Fact]
    public void MagicAbsorption_NeedsAManaUser()
    {
        using TalentProcRig rig = NewRig();
        Player warrior = rig.AddPlayer(1, 0, 0); // rage
        Player priest = rig.AddPlayer(2, 5, 0, Race.Orc);
        rig.UseRows(new SpellProcEventRecord(MagicAbsorption, 0, 0, 0, 0, 0, 0, (uint)ProcFlagsEx.Resist, 0, 0, 0));
        rig.Apply(warrior, MagicAbsorption);

        rig.System.ProcDamageAndSpell(priest, new ProcEvent
        {
            Victim = warrior,
            VictimFlags = ProcFlags.TakeHarmfulSpell,
            Extra = ProcFlagsEx.Resist,
            ProcSpell = rig.Kit.Store.Get(ShadowBolt),
        });

        Assert.Empty(rig.Probes);
    }

    // --- Master of Elements (UnitAuraProcHandler.cpp:849-862) ------------------------------------------------------------------------------

    [Fact]
    public void MasterOfElements_RefundsItsPercentOfTheBaseManaCost_OnAFireCrit()
    {
        using TalentProcRig rig = NewRig();
        Player mage = rig.AddPlayer(1, 0, 0);
        Player target = rig.AddPlayer(2, 5, 0, Race.Orc);
        GiveMana(mage, maxMana: 3000, createMana: 1000);
        rig.Apply(mage, MasterOfElements);

        rig.System.ProcDamageAndSpell(mage, SpellHit(target, rig.Kit.Store.Get(Fireball)!, ProcFlagsEx.CriticalHit, amount: 500, original: 500, attacker: true));

        // cost = manaCost 200 + ManaCostPercentage 10 x create mana 1000 / 100 = 300; basepoints = rand_dither(300 x 30 / 100) = 90, on the mage.
        TalentProcRig.ProbeHit hit = Assert.Single(rig.Probes);
        Assert.Equal((MasterOfElementsMana, mage, mage, 90), (hit.SpellId, hit.Caster, hit.Target, hit.Value));
    }

    [Fact]
    public void MasterOfElements_WithoutARow_StillNeedsAFireOrFrostCrit()
    {
        using TalentProcRig rig = NewRig();
        Player mage = rig.AddPlayer(1, 0, 0);
        Player target = rig.AddPlayer(2, 5, 0, Race.Orc);
        GiveMana(mage, maxMana: 3000, createMana: 1000);
        rig.Apply(mage, MasterOfElements);

        rig.System.ProcDamageAndSpell(mage, SpellHit(target, rig.Kit.Store.Get(Fireball)!, ProcFlagsEx.NormalHit, 500, 500, attacker: true));
        rig.System.ProcDamageAndSpell(mage, SpellHit(target, rig.Kit.Store.Get(ArcaneMissile)!, ProcFlagsEx.CriticalHit, 500, 500, attacker: true));
        Assert.Empty(rig.Probes);

        rig.System.ProcDamageAndSpell(mage, SpellHit(target, rig.Kit.Store.Get(Frostbolt)!, ProcFlagsEx.CriticalHit, 500, 500, attacker: true));
        Assert.Equal(30, Assert.Single(rig.Probes).Value); // 100 x 30%
    }

    // --- Vampiric Embrace (UnitAuraProcHandler.cpp:875-893) --------------------------------------------------------------------------------

    [Fact]
    public void VampiricEmbrace_HealsThePriestsParty_ForItsPercentOfTheShadowDamage()
    {
        using TalentProcRig rig = NewRig();
        Player priest = rig.AddPlayer(1, 0, 0);
        Player enemy = rig.AddPlayer(2, 5, 0, Race.Orc);
        rig.System.CastSpell(priest, VampiricEmbrace, SpellCastTargets.ForUnit(enemy.Guid), triggered: true);
        rig.Kit.Now++;
        Assert.True(rig.System.HasAura(enemy, VampiricEmbrace));

        rig.System.ProcDamageAndSpell(priest, SpellHit(enemy, rig.Kit.Store.Get(ShadowBolt)!, ProcFlagsEx.NormalHit, amount: 150, original: 150));

        // basepoints = rand_dither(20% x 150) = 30; the priest casts 15290 on itself (its party heal).
        TalentProcRig.ProbeHit hit = Assert.Single(rig.Probes);
        Assert.Equal((VampiricEmbraceHeal, priest, priest, 30), (hit.SpellId, hit.Caster, hit.Target, hit.Value));
    }

    [Fact]
    public void VampiricEmbrace_OnlyItsCastersShadowDamage_AndNeverForZero()
    {
        using TalentProcRig rig = NewRig();
        Player priest = rig.AddPlayer(1, 0, 0);
        Player otherPriest = rig.AddPlayer(3, 0, 3);
        Player enemy = rig.AddPlayer(2, 5, 0, Race.Orc);
        rig.System.CastSpell(priest, VampiricEmbrace, SpellCastTargets.ForUnit(enemy.Guid), triggered: true);
        rig.Kit.Now++;

        rig.System.ProcDamageAndSpell(otherPriest, SpellHit(enemy, rig.Kit.Store.Get(ShadowBolt)!, ProcFlagsEx.NormalHit, 150, 150)); // not the caster
        rig.System.ProcDamageAndSpell(priest, SpellHit(enemy, rig.Kit.Store.Get(Fireball)!, ProcFlagsEx.NormalHit, 150, 150));       // not shadow
        Assert.Empty(rig.Probes);

        rig.System.ProcDamageAndSpell(priest, SpellHit(enemy, rig.Kit.Store.Get(ShadowBolt)!, ProcFlagsEx.NormalHit, 2, 2));
        Assert.Equal(1, Assert.Single(rig.Probes).Value); // 20% of 2 dithers to 0 or 1: "don't heal for 0"
    }

    // --- Blade Flurry (UnitAuraProcHandler.cpp:951-971) ------------------------------------------------------------------------------------

    [Fact]
    public void BladeFlurry_HitsAnotherEnemyWithinFiveYards_WithTheDamageBeforeArmor()
    {
        using TalentProcRig rig = NewRig();
        Player rogue = rig.AddPlayer(1, 0, 0);
        Player victim = rig.AddPlayer(2, 2, 0, Race.Orc);
        Player second = rig.AddPlayer(3, 0, -3, Race.Orc);
        victim.SetInt32(UpdateFields.UnitFieldResistances, 2750);
        rig.Apply(rogue, BladeFlurry);

        rig.System.ProcDamageAndSpell(rogue, Swing(victim, 66));
        rig.System.ProcDamageAndSpell(rogue, new ProcEvent // the flurry strike itself never chains
        {
            Victim = second,
            AttackerFlags = ProcFlags.DealMeleeAbility,
            VictimFlags = ProcFlags.TakeMeleeAbility | ProcFlags.TakenAnyDamage,
            Extra = ProcFlagsEx.NormalHit,
            Amount = 100,
            OriginalAmount = 100,
            ProcSpell = rig.Kit.Store.Get(BladeFlurryDamage),
        });

        TalentProcRig.ProbeHit hit = Assert.Single(rig.Probes);
        Assert.Equal((BladeFlurryDamage, rogue, second, 100), (hit.SpellId, hit.Caster, hit.Target, hit.Value));
    }

    // --- events ---------------------------------------------------------------------------------------------------------------------------

    private static ProcEvent Swing(Unit victim, uint amount) => new()
    {
        Victim = victim,
        AttackerFlags = ProcFlags.DealMeleeSwing,
        VictimFlags = ProcFlags.TakeMeleeSwing | ProcFlags.TakenAnyDamage,
        Extra = ProcFlagsEx.NormalHit,
        Amount = amount,
        OriginalAmount = amount,
        AttackType = WeaponAttackType.BaseAttack,
    };

    private static ProcEvent Struck(Unit victim) => new()
    {
        Victim = victim,
        AttackerFlags = ProcFlags.DealMeleeSwing,
        VictimFlags = ProcFlags.TakeMeleeSwing | ProcFlags.TakenAnyDamage,
        Extra = ProcFlagsEx.NormalHit,
        Amount = 20,
        OriginalAmount = 20,
        AttackType = WeaponAttackType.BaseAttack,
    };

    /// <summary>A harmful spell hit: the victim side only, or (<paramref name="attacker"/>) the caster side as well.</summary>
    private static ProcEvent SpellHit(Unit victim, SpellInfo spell, ProcFlagsEx extra, uint amount, uint original, bool attacker = false) => new()
    {
        Victim = victim,
        AttackerFlags = attacker ? ProcFlags.DealHarmfulSpell : ProcFlags.None,
        VictimFlags = ProcFlags.TakeHarmfulSpell | ProcFlags.TakenAnyDamage,
        Extra = extra,
        Amount = amount,
        OriginalAmount = original,
        ProcSpell = spell,
    };
}
