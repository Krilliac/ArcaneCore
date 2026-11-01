using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.Scholomance;
using ArcaneCore.Game.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// The Scholomance boss scripts of vmangos western_plaguelands/scholomance/ (boss_ras_frostwhisper, boss_instructor_malicia,
/// boss_doctor_theolen_krastinov, boss_lorekeeper_polkelt, boss_the_ravenian, boss_illucia_barov, boss_lord_alexei_barov): one test group
/// per boss, timers driven by hand.
/// </summary>
public sealed class ScholomanceBossTests
{
    private const uint Ras = 10508, Malicia = 10505, Krastinov = 11261, Polkelt = 10901, Ravenian = 10507, Illucia = 10502, Alexei = 10504;

    private static (DungeonScriptTestKit Rig, Creature Boss) Fighting(uint entry, ICreatureSpellCaster caster)
    {
        DungeonScriptTestKit rig = new(map => new ScholomanceInstance(map), [entry], [entry], [],
            aiServices: new CreatureAiServices { Spells = caster });
        Creature boss = rig.Creature(entry);
        rig.Player.Relocate(boss.X + 1, boss.Y, boss.Z, 0, 0);
        Assert.True(rig.Creatures.AttackStart(boss, rig.Player));
        boss.Combat.Threat.AddThreat(rig.Player, 100);
        return (rig, boss);
    }

    [Fact]
    public void DungeonBossTable_MapsTheScholomanceBosses()
    {
        Assert.Equal("RasFrostwhisperAI", DungeonBossAis.Describe(289, Ras));
        Assert.Equal("InstructorMaliciaAI", DungeonBossAis.Describe(289, Malicia));
        Assert.Equal("TheolenKrastinovAI", DungeonBossAis.Describe(289, Krastinov));
        Assert.Equal("LorekeeperPolkeltAI", DungeonBossAis.Describe(289, Polkelt));
        Assert.Equal("TheRavenianAI", DungeonBossAis.Describe(289, Ravenian));
        Assert.Equal("IlluciaBarovAI", DungeonBossAis.Describe(289, Illucia));
        Assert.Equal("LordAlexeiBarovAI", DungeonBossAis.Describe(289, Alexei));
        Assert.Null(DungeonBossAis.Describe(229, Ras));
    }

    // --- Ras Frostwhisper --------------------------------------------------------------------

    [Fact]
    public void Ras_IceArmorAtResetAndEveryThreeMinutes_AndTheFrostSpellsFollowTheirTimers()
    {
        var caster = new DungeonTestCaster();
        (DungeonScriptTestKit rig, Creature boss) = Fighting(Ras, caster);
        using (rig)
        {
            var ai = new RasFrostwhisperAI(boss);
            ai.OnRespawn();
            int Count(uint spell) => caster.Casts.Count(c => c.Spell == spell);
            Assert.Equal(1, Count(RasFrostwhisperAI.SpellIceArmor)); // Reset: Ice Armor on himself

            ai.OnUpdate(2_000);
            Assert.Equal(2, Count(RasFrostwhisperAI.SpellIceArmor));
            ai.OnUpdate(6_000); // 8 s
            Assert.Equal(1, Count(RasFrostwhisperAI.SpellFrostbolt));
            ai.OnUpdate(4_000); // 12 s
            Assert.Equal(1, Count(RasFrostwhisperAI.SpellChillNova));
            ai.OnUpdate(6_000); // 18 s
            Assert.Equal(1, Count(RasFrostwhisperAI.SpellFreeze));
            ai.OnUpdate(6_000); // 24 s
            Assert.Equal(1, Count(RasFrostwhisperAI.SpellFrostVolley));
            Assert.Equal(0, Count(RasFrostwhisperAI.SpellFear));
            ai.OnUpdate(21_000); // 45 s
            Assert.Equal(1, Count(RasFrostwhisperAI.SpellFear));
            Assert.Same(rig.Player, caster.Casts.Single(c => c.Spell == RasFrostwhisperAI.SpellFear).Target);

            ai.OnUpdate(137_000); // 182 s: the next Ice Armor is 180 s after the first
            Assert.Equal(3, Count(RasFrostwhisperAI.SpellIceArmor));
        }
    }

    // --- Instructor Malicia ------------------------------------------------------------------

    [Fact]
    public void Malicia_FlashHealComesInABurstOfThree_ThenWaitsThirtySeconds()
    {
        var caster = new DungeonTestCaster();
        (DungeonScriptTestKit rig, Creature boss) = Fighting(Malicia, caster);
        using (rig)
        {
            var ai = new InstructorMaliciaAI(boss);
            ai.OnRespawn();
            int Flash() => caster.Casts.Count(c => c.Spell == InstructorMaliciaAI.SpellFlashHeal);

            ai.OnUpdate(21_900);
            Assert.Equal(0, Flash());
            ai.OnUpdate(100); // 22 s
            Assert.Equal(1, Flash());
            ai.OnUpdate(5_000); // 27 s
            Assert.Equal(2, Flash());
            ai.OnUpdate(5_000); // 32 s
            Assert.Equal(3, Flash());
            ai.OnUpdate(29_900); // 61.9 s: 30 s after the third
            Assert.Equal(3, Flash());
            ai.OnUpdate(100);
            Assert.Equal(4, Flash());
            Assert.All(caster.Casts.Where(c => c.Spell == InstructorMaliciaAI.SpellFlashHeal), c => Assert.Same(boss, c.Target));
        }
    }

    [Fact]
    public void Malicia_CallOfGravesCorruptionRenewAndHealingTouch_AtTheScriptIntervals()
    {
        var caster = new DungeonTestCaster();
        (DungeonScriptTestKit rig, Creature boss) = Fighting(Malicia, caster);
        using (rig)
        {
            var ai = new InstructorMaliciaAI(boss);
            ai.OnRespawn();
            int Count(uint spell) => caster.Casts.Count(c => c.Spell == spell);

            ai.OnUpdate(4_000);
            Assert.Equal(1, Count(InstructorMaliciaAI.SpellCallOfGraves));
            Assert.Same(rig.Player, caster.Casts.Single(c => c.Spell == InstructorMaliciaAI.SpellCallOfGraves).Target);
            ai.OnUpdate(4_000); // 8 s
            Assert.Equal(1, Count(InstructorMaliciaAI.SpellCorruption));
            ai.OnUpdate(7_000); // 15 s
            Assert.Equal(1, Count(InstructorMaliciaAI.SpellRenew));
            Assert.Same(boss, caster.Casts.Single(c => c.Spell == InstructorMaliciaAI.SpellRenew).Target);
            ai.OnUpdate(10_000); // 25 s
            Assert.Equal(1, Count(InstructorMaliciaAI.SpellHealingTouch));
            Assert.Equal(2, Count(InstructorMaliciaAI.SpellRenew)); // Renew again at 25 s
            ai.OnUpdate(5_500); // 30.5 s: no 5.5 s Healing Touch repeat in the source
            Assert.Equal(1, Count(InstructorMaliciaAI.SpellHealingTouch));
            ai.OnUpdate(24_500); // 55 s
            Assert.Equal(2, Count(InstructorMaliciaAI.SpellHealingTouch));
        }
    }

    // --- Doctor Theolen Krastinov --------------------------------------------------------------

    [Fact]
    public void Krastinov_BackhandWipesTheVictimsThreat()
    {
        var caster = new DungeonTestCaster();
        (DungeonScriptTestKit rig, Creature boss) = Fighting(Krastinov, caster);
        using (rig)
        {
            var ai = new TheolenKrastinovAI(boss);
            ai.OnRespawn();
            int Count(uint spell) => caster.Casts.Count(c => c.Spell == spell);

            ai.OnUpdate(8_000);
            Assert.Equal(1, Count(TheolenKrastinovAI.SpellRend));
            Assert.Equal(0, Count(TheolenKrastinovAI.SpellBackhand));
            Assert.Equal(100f, boss.Combat.Threat.GetThreat(rig.Player));

            ai.OnUpdate(1_000); // 9 s
            Assert.Equal(1, Count(TheolenKrastinovAI.SpellBackhand));
            Assert.Equal(0f, boss.Combat.Threat.GetThreat(rig.Player));

            ai.OnUpdate(10_000); // Rend and Backhand repeat after 10 s
            Assert.Equal(2, Count(TheolenKrastinovAI.SpellRend));
            Assert.Equal(2, Count(TheolenKrastinovAI.SpellBackhand));
        }
    }

    [Fact]
    public void Krastinov_FrenzyOnlyBelowTwentySixPercent_ThenEveryTwoMinutes()
    {
        var caster = new DungeonTestCaster();
        (DungeonScriptTestKit rig, Creature boss) = Fighting(Krastinov, caster);
        using (rig)
        {
            var ai = new TheolenKrastinovAI(boss);
            ai.OnRespawn();
            int Frenzy() => caster.Casts.Count(c => c.Spell == TheolenKrastinovAI.SpellFrenzy);

            ai.OnUpdate(5_000);
            Assert.Equal(0, Frenzy());

            boss.Health = (uint)(boss.MaxHealth / 5);
            ai.OnUpdate(1_000); // the 1 s timer only counts while below 26 percent
            Assert.Equal(1, Frenzy());
            Assert.Same(boss, caster.Casts.Single(c => c.Spell == TheolenKrastinovAI.SpellFrenzy).Target);

            ai.OnUpdate(119_900);
            Assert.Equal(1, Frenzy());
            ai.OnUpdate(100);
            Assert.Equal(2, Frenzy());
        }
    }

    // --- Lorekeeper Polkelt --------------------------------------------------------------------

    [Fact]
    public void Polkelt_DarkPlagueAuraOnAggroOnlyWhenAbsent()
    {
        var caster = new AuraTrackingCaster();
        (DungeonScriptTestKit rig, Creature boss) = Fighting(Polkelt, caster);
        using (rig)
        {
            var ai = new LorekeeperPolkeltAI(boss);
            ai.OnRespawn();

            ai.OnAggro(rig.Player);
            Assert.Single(caster.Casts, c => c.Spell == LorekeeperPolkeltAI.SpellDarkPlagueAura && ReferenceEquals(c.Target, boss));

            ai.OnAggro(rig.Player); // the aura is already on him
            Assert.Single(caster.Casts, c => c.Spell == LorekeeperPolkeltAI.SpellDarkPlagueAura);
        }
    }

    [Fact]
    public void Polkelt_VolatileInfectionCorrosiveAcidNoxiousCatalyst_AtTheScriptIntervals()
    {
        var caster = new DungeonTestCaster();
        (DungeonScriptTestKit rig, Creature boss) = Fighting(Polkelt, caster);
        using (rig)
        {
            var ai = new LorekeeperPolkeltAI(boss);
            ai.OnRespawn();
            int Count(uint spell) => caster.Casts.Count(c => c.Spell == spell);

            ai.OnUpdate(35_000);
            Assert.Equal(1, Count(LorekeeperPolkeltAI.SpellNoxiousCatalyst));
            ai.OnUpdate(3_000); // 38 s
            Assert.Equal(1, Count(LorekeeperPolkeltAI.SpellVolatileInfection));
            ai.OnUpdate(7_000); // 45 s
            Assert.Equal(1, Count(LorekeeperPolkeltAI.SpellCorrosiveAcid));
            ai.OnUpdate(25_000); // 70 s: Acid again after 25 s, Volatile after 32 s (at 70 s)
            Assert.Equal(2, Count(LorekeeperPolkeltAI.SpellCorrosiveAcid));
            Assert.Equal(2, Count(LorekeeperPolkeltAI.SpellVolatileInfection));
            Assert.All(caster.Casts, c => Assert.Same(rig.Player, c.Target));
        }
    }

    // --- The Ravenian --------------------------------------------------------------------------

    [Fact]
    public void Ravenian_FourVictimSpells_AtTheScriptIntervals()
    {
        var caster = new DungeonTestCaster();
        (DungeonScriptTestKit rig, Creature boss) = Fighting(Ravenian, caster);
        using (rig)
        {
            var ai = new TheRavenianAI(boss);
            ai.OnRespawn();
            int Count(uint spell) => caster.Casts.Count(c => c.Spell == spell);

            ai.OnUpdate(15_000);
            Assert.Equal(1, Count(TheRavenianAI.SpellCleave));
            ai.OnUpdate(9_000); // 24 s
            Assert.Equal(1, Count(TheRavenianAI.SpellTrample));
            Assert.Equal(2, Count(TheRavenianAI.SpellCleave)); // 22 s
            ai.OnUpdate(8_000); // 32 s
            Assert.Equal(1, Count(TheRavenianAI.SpellKnockAway));
            ai.OnUpdate(8_000); // 40 s
            Assert.Equal(1, Count(TheRavenianAI.SpellSunderingCleave));
            Assert.All(caster.Casts, c => Assert.Same(rig.Player, c.Target));
        }
    }

    // --- Illucia Barov -------------------------------------------------------------------------

    [Fact]
    public void Illucia_SilenceShadowShockCurseOfAgonyFear_AtTheScriptIntervals()
    {
        var caster = new DungeonTestCaster();
        (DungeonScriptTestKit rig, Creature boss) = Fighting(Illucia, caster);
        using (rig)
        {
            var ai = new IlluciaBarovAI(boss);
            ai.OnRespawn();
            int Count(uint spell) => caster.Casts.Count(c => c.Spell == spell);

            ai.OnUpdate(5_000);
            Assert.Equal(1, Count(IlluciaBarovAI.SpellSilence));
            ai.OnUpdate(4_000); // 9 s
            Assert.Equal(1, Count(IlluciaBarovAI.SpellShadowShock));
            Assert.Same(rig.Player, caster.Casts.Single(c => c.Spell == IlluciaBarovAI.SpellShadowShock).Target);
            ai.OnUpdate(9_000); // 18 s
            Assert.Equal(1, Count(IlluciaBarovAI.SpellCurseOfAgony));
            ai.OnUpdate(12_000); // 30 s
            Assert.Equal(1, Count(IlluciaBarovAI.SpellFear));
            Assert.Equal(2, Count(IlluciaBarovAI.SpellShadowShock)); // 9 s and 21 s
        }
    }

    // --- Lord Alexei Barov ---------------------------------------------------------------------

    [Fact]
    public void Alexei_ImmolateAndVeilOfShadow_AtTheScriptIntervals()
    {
        var caster = new DungeonTestCaster();
        (DungeonScriptTestKit rig, Creature boss) = Fighting(Alexei, caster);
        using (rig)
        {
            var ai = new LordAlexeiBarovAI(boss);
            ai.OnRespawn();
            int Count(uint spell) => caster.Casts.Count(c => c.Spell == spell);

            ai.OnUpdate(6_900);
            Assert.Equal(0, Count(LordAlexeiBarovAI.SpellImmolate));
            ai.OnUpdate(100); // 7 s
            Assert.Equal(1, Count(LordAlexeiBarovAI.SpellImmolate));
            ai.OnUpdate(8_000); // 15 s
            Assert.Equal(1, Count(LordAlexeiBarovAI.SpellVeilOfShadow));
            ai.OnUpdate(4_000); // 19 s: Immolate again 12 s after 7 s
            Assert.Equal(2, Count(LordAlexeiBarovAI.SpellImmolate));
            ai.OnUpdate(16_000); // 35 s: Veil again 20 s after 15 s
            Assert.Equal(2, Count(LordAlexeiBarovAI.SpellVeilOfShadow));
            Assert.All(caster.Casts, c => Assert.Same(rig.Player, c.Target));
        }
    }
}
