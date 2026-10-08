using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Procs;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Procs.TalentProcRig;

namespace ArcaneCore.Game.Tests.Procs;

/// <summary>
/// The talent cases of vmangos <c>Unit::HandleProcTriggerSpellAuraProc</c> (UnitAuraProcHandler.cpp:1147-1624) whose Spell.dbc trigger spell is
/// not what the talent casts: Illumination (the original spell's mana cost, Holy Shock remapped), Pyroclasm (the chance over the tick count),
/// Blessed Recovery (rank remap, a third of the damage share) and Shadowguard (rank remap).
/// </summary>
public sealed class TalentProcTriggerTests
{
    private const uint FamilyWarlock = 5;
    private const uint FamilyPriest = 6;
    private const uint FamilyPaladin = 10;

    private const uint Illumination = 20215;
    private const uint IlluminationMana = 20272;
    private const uint HolyLight = 639;
    private const uint HolyShockRank1 = 20473;
    private const uint HolyShockHealRank1 = 25914;
    private const uint UnknownHolyShockHeal = 995_101;
    private const uint DbcTrigger = 995_102;

    private const uint PyroclasmRank1 = 18096;
    private const uint PyroclasmRank2 = 18073;
    private const uint PyroclasmStun = 18093;
    private const uint Hellfire = 995_103;
    private const uint RainOfFire = 995_104;
    private const uint ShadowBolt = 995_105;

    private const uint BlessedRecoveryRank1 = 27811;
    private const uint BlessedRecoveryRank3 = 27816;
    private const uint BlessedRecoveryHeal1 = 27813;
    private const uint BlessedRecoveryHeal3 = 27818;

    private const uint ShadowguardRank1 = 18137;
    private const uint ShadowguardDamage1 = 28377;
    private const uint ShadowguardUnknownRank = 995_106;

    private const ulong HolyShockFlag = 1UL << 21; // CF_PALADIN_HOLY_SHOCK
    private const ulong RainOfFireFlag = 1UL << 5; // CF_WARLOCK_RAIN_OF_FIRE
    private const ulong HellfireFlag = 1UL << 6;   // CF_WARLOCK_HELLFIRE

    private static TalentProcRig NewRig() => new(
        Talent(Illumination, AuraType.ProcTriggerSpell, 0, ProcFlags.DealHelpfulSpell, FamilyPaladin, icon: 241, trigger: DbcTrigger),
        Probe(DbcTrigger, atEnemy: false, value: 7),
        Probe(IlluminationMana, atEnemy: false),
        Heal(HolyLight, FamilyPaladin, 0x80000000, manaCost: 140),
        Heal(HolyShockRank1, FamilyPaladin, HolyShockFlag, manaCost: 335),
        Heal(HolyShockHealRank1, FamilyPaladin, HolyShockFlag, manaCost: 0),
        Heal(UnknownHolyShockHeal, FamilyPaladin, HolyShockFlag, manaCost: 0),
        Talent(PyroclasmRank1, AuraType.ProcTriggerSpell, 0, ProcFlags.DealHarmfulSpell, FamilyWarlock, icon: 1137, trigger: DbcTrigger),
        Talent(PyroclasmRank2, AuraType.ProcTriggerSpell, 0, ProcFlags.DealHarmfulSpell, FamilyWarlock, icon: 1137, trigger: DbcTrigger),
        Probe(PyroclasmStun, atEnemy: true),
        Bolt(Hellfire, SpellSchool.Fire, FamilyWarlock, HellfireFlag),
        Bolt(RainOfFire, SpellSchool.Fire, FamilyWarlock, RainOfFireFlag),
        Bolt(ShadowBolt, SpellSchool.Shadow, FamilyWarlock, 0x1),
        Talent(BlessedRecoveryRank1, AuraType.ProcTriggerSpell, 8, ProcFlags.TakeMeleeSwing, FamilyPriest, icon: 1875, trigger: DbcTrigger),
        Talent(BlessedRecoveryRank3, AuraType.ProcTriggerSpell, 25, ProcFlags.TakeMeleeSwing, FamilyPriest, icon: 1875, trigger: DbcTrigger),
        Probe(BlessedRecoveryHeal1, atEnemy: false),
        Probe(BlessedRecoveryHeal3, atEnemy: false),
        Talent(ShadowguardRank1, AuraType.ProcTriggerSpell, 0, ProcFlags.TakeMeleeSwing, FamilyPriest, icon: 19, trigger: DbcTrigger),
        Talent(ShadowguardUnknownRank, AuraType.ProcTriggerSpell, 0, ProcFlags.TakeMeleeSwing, FamilyPriest, icon: 19, trigger: DbcTrigger),
        Probe(ShadowguardDamage1, atEnemy: true));

    private static SpellInfo Heal(uint id, uint family, ulong flags, uint manaCost) =>
        SpellTestKit.Spell(id, SpellTestKit.Effect(SpellEffectName.Heal, 100, SpellImplicitTarget.UnitFriend)) with
        {
            SpellFamilyName = family,
            SpellFamilyFlags = flags,
            ManaCost = manaCost,
            DamageClass = SpellDamageClass.Magic,
            School = SpellSchool.Holy,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };

    private static ProcEvent CriticalHeal(Unit target, SpellInfo spell) => new()
    {
        Victim = target,
        AttackerFlags = ProcFlags.DealHelpfulSpell,
        VictimFlags = ProcFlags.TakeHelpfulSpell,
        Extra = ProcFlagsEx.CriticalHit,
        Amount = 500,
        OriginalAmount = 500,
        ProcSpell = spell,
    };

    private static ProcEvent Harmful(Unit target, SpellInfo spell) => new()
    {
        Victim = target,
        AttackerFlags = ProcFlags.DealHarmfulSpell,
        Extra = ProcFlagsEx.NormalHit,
        Amount = 100,
        OriginalAmount = 100,
        ProcSpell = spell,
    };

    private static ProcEvent Struck(Unit victim, uint amount) => new()
    {
        Victim = victim,
        AttackerFlags = ProcFlags.DealMeleeSwing,
        VictimFlags = ProcFlags.TakeMeleeSwing | ProcFlags.TakenAnyDamage,
        Extra = ProcFlagsEx.NormalHit,
        Amount = amount,
        OriginalAmount = amount,
    };

    // --- Illumination (UnitAuraProcHandler.cpp:1468-1500) ---------------------------------------------------------------------------------

    [Fact]
    public void Illumination_Casts20272_WithTheHealsManaCost_OnThePaladin()
    {
        using TalentProcRig rig = NewRig();
        Player paladin = rig.AddPlayer(1, 0, 0);
        Player friend = rig.AddPlayer(2, 3, 0);
        rig.Apply(paladin, Illumination);

        rig.System.ProcDamageAndSpell(paladin, CriticalHeal(friend, rig.Kit.Store.Get(HolyLight)!));

        TalentProcRig.ProbeHit hit = Assert.Single(rig.Probes);
        Assert.Equal((IlluminationMana, paladin, paladin, 140), (hit.SpellId, hit.Caster, hit.Target, hit.Value));
    }

    [Fact]
    public void Illumination_ReadsHolyShocksCastSpell_ForTheTriggeredHeal_AndFailsOnAnUnknownRank()
    {
        using TalentProcRig rig = NewRig();
        Player paladin = rig.AddPlayer(1, 0, 0);
        Player friend = rig.AddPlayer(2, 3, 0);
        rig.Apply(paladin, Illumination);

        rig.System.ProcDamageAndSpell(paladin, CriticalHeal(friend, rig.Kit.Store.Get(HolyShockHealRank1)!));
        rig.System.ProcDamageAndSpell(paladin, CriticalHeal(friend, rig.Kit.Store.Get(UnknownHolyShockHeal)!));

        // 25914 is the heal Holy Shock rank 1 (20473, 335 mana) triggers; an unmapped Holy Shock heal casts nothing.
        TalentProcRig.ProbeHit hit = Assert.Single(rig.Probes);
        Assert.Equal((IlluminationMana, 335), (hit.SpellId, hit.Value));
    }

    // --- Pyroclasm (UnitAuraProcHandler.cpp:1226-1262) ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(PyroclasmRank1, Hellfire, 80, true)]    // 13% / 15 ticks = 0.867%
    [InlineData(PyroclasmRank1, Hellfire, 90, false)]
    [InlineData(PyroclasmRank1, RainOfFire, 320, true)] // 13% / 4 ticks = 3.25%
    [InlineData(PyroclasmRank1, RainOfFire, 330, false)]
    [InlineData(PyroclasmRank2, Hellfire, 170, true)]   // 26% / 15 ticks = 1.733%
    [InlineData(PyroclasmRank2, Hellfire, 180, false)]
    public void Pyroclasm_DividesItsChanceByTheTicksOfHellfireAndRainOfFire(uint rank, uint spell, int roll, bool procs)
    {
        using TalentProcRig rig = NewRig();
        Player warlock = rig.AddPlayer(1, 0, 0);
        Player enemy = rig.AddPlayer(2, 5, 0, Race.Orc);
        rig.Apply(warlock, rank);
        rig.System.Random = new SpellRules.ScriptedRandom(0, roll); // the generic 100% roll, then Pyroclasm's own (value / 100 percent)

        rig.System.ProcDamageAndSpell(warlock, Harmful(enemy, rig.Kit.Store.Get(spell)!));

        if (procs)
        {
            TalentProcRig.ProbeHit hit = Assert.Single(rig.Probes);
            Assert.Equal((PyroclasmStun, warlock, enemy), (hit.SpellId, hit.Caster, hit.Target));
        }
        else
        {
            Assert.Empty(rig.Probes);
        }
    }

    [Fact]
    public void Pyroclasm_IgnoresOtherSpells()
    {
        using TalentProcRig rig = NewRig();
        Player warlock = rig.AddPlayer(1, 0, 0);
        Player enemy = rig.AddPlayer(2, 5, 0, Race.Orc);
        rig.Apply(warlock, PyroclasmRank2);
        rig.System.Random = new SpellRules.ScriptedRandom(0, 0);

        rig.System.ProcDamageAndSpell(warlock, Harmful(enemy, rig.Kit.Store.Get(ShadowBolt)!));

        Assert.Empty(rig.Probes);
    }

    // --- Blessed Recovery and Shadowguard (UnitAuraProcHandler.cpp:1281-1330) -------------------------------------------------------------

    [Theory]
    [InlineData(BlessedRecoveryRank1, BlessedRecoveryHeal1, 8)]  // rand_dither(300 x 8 / 100 / 3)
    [InlineData(BlessedRecoveryRank3, BlessedRecoveryHeal3, 25)] // rand_dither(300 x 25 / 100 / 3)
    public void BlessedRecovery_CastsItsRanksHeal_ForAThirdOfItsShareOfTheDamage(uint rank, uint heal, int value)
    {
        using TalentProcRig rig = NewRig();
        Player priest = rig.AddPlayer(1, 0, 0);
        Player attacker = rig.AddPlayer(2, 2, 0, Race.Orc);
        rig.Apply(priest, rank);

        rig.System.ProcDamageAndSpell(attacker, Struck(priest, 300));

        TalentProcRig.ProbeHit hit = Assert.Single(rig.Probes);
        Assert.Equal((heal, priest, priest, value), (hit.SpellId, hit.Caster, hit.Target, hit.Value));
    }

    [Fact]
    public void Shadowguard_CastsItsRanksDamage_AtTheAttacker_AndAnUnknownRankCastsNothing()
    {
        using TalentProcRig rig = NewRig();
        Player priest = rig.AddPlayer(1, 0, 0);
        Player attacker = rig.AddPlayer(2, 2, 0, Race.Orc);
        rig.Apply(priest, ShadowguardRank1);

        rig.System.ProcDamageAndSpell(attacker, Struck(priest, 50));
        TalentProcRig.ProbeHit hit = Assert.Single(rig.Probes);
        Assert.Equal((ShadowguardDamage1, priest, attacker), (hit.SpellId, hit.Caster, hit.Target));

        rig.System.RemoveAuras(priest, ShadowguardRank1);
        rig.Apply(priest, ShadowguardUnknownRank);
        rig.System.ProcDamageAndSpell(attacker, Struck(priest, 50));
        Assert.Single(rig.Probes); // the Spell.dbc trigger (a value-7 probe) is not cast for an unmapped rank
    }
}
