using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.Stratholme;
using ArcaneCore.Game.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// The Stratholme boss scripts of vmangos eastern_plaguelands/stratholme/ (boss_ramstein_the_gorger, boss_magistrate_barthilas,
/// boss_nerubenkan, boss_postmaster_malown): one test group per ported mechanic, timers driven by hand.
/// </summary>
public sealed class StratholmeBossTests
{
    private const uint Ramstein = 10439, Barthilas = 10435, Nerubenkan = 10437, Malown = 11143;

    private static (DungeonScriptTestKit Rig, Creature Boss) Fighting(uint entry, ICreatureSpellCaster caster, params uint[] extraTemplates)
    {
        DungeonScriptTestKit rig = new(map => new StratholmeInstance(map), [entry, .. extraTemplates], [entry], [],
            aiServices: new CreatureAiServices { Spells = caster });
        Creature boss = rig.Creature(entry);
        rig.Player.Relocate(boss.X + 1, boss.Y, boss.Z, 0, 0);
        Assert.True(rig.Creatures.AttackStart(boss, rig.Player));
        boss.Combat.Threat.AddThreat(rig.Player, 100);
        return (rig, boss);
    }

    /// <summary>A caster that records every cast and reports the webs aura only while <see cref="Webbed"/> is set.</summary>
    private sealed class WebCaster : ICreatureSpellCaster
    {
        public List<(uint Spell, Unit? Target)> Casts { get; } = [];
        public bool Webbed { get; set; }

        public event Action<Unit, Unit, SpellInfo>? SpellHit { add { } remove { } }

        public CreatureCastResult Cast(Creature caster, uint spellId, Unit? target, bool triggered)
        {
            Casts.Add((spellId, target));
            return CreatureCastResult.Ok;
        }

        public bool IsCasting(Creature caster) => false;

        public bool HasAura(Unit unit, uint spellId) => Webbed && spellId == NerubenkanAI.SpellEncasingWebs;

        public void Interrupt(Creature caster)
        {
        }

        public void OnCreatureRemoved(Creature creature)
        {
        }
    }

    [Fact]
    public void DungeonBossTable_MapsTheFourStratholmeBosses()
    {
        Assert.Equal("RamsteinTheGorgerAI", DungeonBossAis.Describe(329, Ramstein));
        Assert.Equal("MagistrateBarthilasAI", DungeonBossAis.Describe(329, Barthilas));
        Assert.Equal("NerubenkanAI", DungeonBossAis.Describe(329, Nerubenkan));
        Assert.Equal("PostmasterMalownAI", DungeonBossAis.Describe(329, Malown));
        Assert.Null(DungeonBossAis.Describe(289, Ramstein));
    }

    // --- Ramstein the Gorger -----------------------------------------------------------------

    [Fact]
    public void Ramstein_TrampleOnHimselfAtThreeThenEverySeven()
    {
        var caster = new DungeonTestCaster();
        (DungeonScriptTestKit rig, Creature boss) = Fighting(Ramstein, caster);
        using (rig)
        {
            var ai = new RamsteinTheGorgerAI(boss);
            ai.OnRespawn();
            int Trample() => caster.Casts.Count(c => c.Spell == RamsteinTheGorgerAI.SpellTrample);

            ai.OnUpdate(2_900);
            Assert.Equal(0, Trample());
            ai.OnUpdate(100); // 3 s
            Assert.Equal(1, Trample());
            Assert.Same(boss, caster.Casts.Single(c => c.Spell == RamsteinTheGorgerAI.SpellTrample).Target);
            ai.OnUpdate(6_900);
            Assert.Equal(1, Trample());
            ai.OnUpdate(100); // 10 s
            Assert.Equal(2, Trample());
        }
    }

    [Fact]
    public void Ramstein_KnockoutWipesTheVictimsThreat_FirstAtTwelveSecondsThenEveryTen()
    {
        var caster = new DungeonTestCaster();
        (DungeonScriptTestKit rig, Creature boss) = Fighting(Ramstein, caster);
        using (rig)
        {
            var ai = new RamsteinTheGorgerAI(boss);
            ai.OnRespawn();
            int Knockout() => caster.Casts.Count(c => c.Spell == RamsteinTheGorgerAI.SpellKnockout);

            ai.OnUpdate(11_900);
            Assert.Equal(0, Knockout());
            Assert.Equal(100f, boss.Combat.Threat.GetThreat(rig.Player));
            ai.OnUpdate(100); // 12 s
            Assert.Equal(1, Knockout());
            Assert.Same(rig.Player, caster.Casts.Single(c => c.Spell == RamsteinTheGorgerAI.SpellKnockout).Target);
            Assert.Equal(0f, boss.Combat.Threat.GetThreat(rig.Player));

            boss.Combat.Threat.AddThreat(rig.Player, 50);
            ai.OnUpdate(9_900); // 21.9 s
            Assert.Equal(1, Knockout());
            ai.OnUpdate(100); // 22 s
            Assert.Equal(2, Knockout());
            Assert.Equal(0f, boss.Combat.Threat.GetThreat(rig.Player));
        }
    }

    // --- Magistrate Barthilas ----------------------------------------------------------------

    [Fact]
    public void Barthilas_UndeadModelWhileAlive_HumanModelOnDeath()
    {
        var caster = new DungeonTestCaster();
        (DungeonScriptTestKit rig, Creature boss) = Fighting(Barthilas, caster);
        using (rig)
        {
            var ai = new MagistrateBarthilasAI(boss);
            ai.OnRespawn();
            Assert.Equal(MagistrateBarthilasAI.ModelNormal, boss.DisplayId);

            ai.OnDeath(rig.Player);
            Assert.Equal(MagistrateBarthilasAI.ModelHuman, boss.DisplayId);
        }
    }

    [Fact]
    public void Barthilas_SpellTimers_FollowTheScript()
    {
        var caster = new DungeonTestCaster();
        (DungeonScriptTestKit rig, Creature boss) = Fighting(Barthilas, caster);
        using (rig)
        {
            var ai = new MagistrateBarthilasAI(boss);
            ai.OnRespawn();
            int Count(uint spell) => caster.Casts.Count(c => c.Spell == spell);

            ai.OnUpdate(5_000);
            Assert.Equal(1, Count(MagistrateBarthilasAI.SpellFuriousAnger));
            Assert.Same(boss, caster.Casts.Single(c => c.Spell == MagistrateBarthilasAI.SpellFuriousAnger).Target);
            ai.OnUpdate(3_000); // 8 s
            Assert.Equal(1, Count(MagistrateBarthilasAI.SpellMightyBlow));
            Assert.Equal(1, Count(MagistrateBarthilasAI.SpellFuriousAnger)); // the next one is at 9 s
            ai.OnUpdate(4_000); // 12 s
            Assert.Equal(1, Count(MagistrateBarthilasAI.SpellCrowdPummel));
            ai.OnUpdate(4_000); // 16 s
            Assert.Equal(1, Count(MagistrateBarthilasAI.SpellDrainingBlow));
            Assert.Same(rig.Player, caster.Casts.Single(c => c.Spell == MagistrateBarthilasAI.SpellDrainingBlow).Target);
        }
    }

    [Fact]
    public void Barthilas_FuriousAngerStopsAfterTwentySixCasts()
    {
        var caster = new DungeonTestCaster();
        (DungeonScriptTestKit rig, Creature boss) = Fighting(Barthilas, caster);
        using (rig)
        {
            var ai = new MagistrateBarthilasAI(boss);
            ai.OnRespawn();
            ai.OnUpdate(5_000);
            for (int i = 0; i < 25; i++)
            {
                ai.OnUpdate(4_000);
            }

            int Anger() => caster.Casts.Count(c => c.Spell == MagistrateBarthilasAI.SpellFuriousAnger);
            Assert.Equal(26, Anger());
            Assert.Equal(26u, ai.AngerCount);

            ai.OnUpdate(4_000);
            ai.OnUpdate(4_000);
            Assert.Equal(26, Anger());
        }
    }

    [Fact]
    public void Barthilas_PlayerWithinTenYards_LiftsTheSpawningFlag_FartherOneDoesNot()
    {
        var caster = new DungeonTestCaster();
        (DungeonScriptTestKit rig, Creature boss) = Fighting(Barthilas, caster);
        using (rig)
        {
            var ai = new MagistrateBarthilasAI(boss);
            boss.UnitFlags |= UnitFlags.Spawning;

            rig.Player.Relocate(boss.X + 30, boss.Y, boss.Z, 0, 0);
            ai.MoveInLineOfSight(rig.Player);
            Assert.NotEqual(0u, (uint)(boss.UnitFlags & UnitFlags.Spawning));

            rig.Player.Relocate(boss.X + 5, boss.Y, boss.Z, 0, 0);
            ai.MoveInLineOfSight(rig.Player);
            Assert.Equal(0u, (uint)(boss.UnitFlags & UnitFlags.Spawning));
        }
    }

    // --- Nerub'enkan -------------------------------------------------------------------------

    [Fact]
    public void Nerubenkan_EncasingWebsWipeTheThreat_AndItComesBackWhenTheWebIsGone()
    {
        var caster = new WebCaster();
        (DungeonScriptTestKit rig, Creature boss) = Fighting(Nerubenkan, caster, 10577, 10876);
        using (rig)
        {
            var ai = new NerubenkanAI(boss);
            ai.OnRespawn();

            caster.Webbed = true;
            ai.OnUpdate(7_000);
            Assert.Equal(1, caster.Casts.Count(c => c.Spell == NerubenkanAI.SpellEncasingWebs));
            Assert.Same(rig.Player, caster.Casts.Single(c => c.Spell == NerubenkanAI.SpellEncasingWebs).Target);
            Assert.Equal(0f, boss.Combat.Threat.GetThreat(rig.Player));

            ai.OnUpdate(100); // still webbed: nothing returns
            Assert.Equal(0f, boss.Combat.Threat.GetThreat(rig.Player));

            caster.Webbed = false;
            ai.OnUpdate(100);
            Assert.Equal(100f, boss.Combat.Threat.GetThreat(rig.Player));
        }
    }

    [Fact]
    public void Nerubenkan_PierceArmorAtFifteenSeconds()
    {
        var caster = new DungeonTestCaster();
        (DungeonScriptTestKit rig, Creature boss) = Fighting(Nerubenkan, caster, 10577, 10876);
        using (rig)
        {
            var ai = new NerubenkanAI(boss);
            ai.OnRespawn();

            ai.OnUpdate(14_900);
            Assert.Equal(0, caster.Casts.Count(c => c.Spell == NerubenkanAI.SpellPierceArmor));
            ai.OnUpdate(100);
            Assert.Equal(1, caster.Casts.Count(c => c.Spell == NerubenkanAI.SpellPierceArmor));
            Assert.Same(rig.Player, caster.Casts.Single(c => c.Spell == NerubenkanAI.SpellPierceArmor).Target);
        }
    }

    [Fact]
    public void Nerubenkan_RaiseUndeadScarab_AtThreeSeconds_SummonsOneUndeadScarabOrAFewCryptScarabs()
    {
        var caster = new DungeonTestCaster();
        (DungeonScriptTestKit rig, Creature boss) = Fighting(Nerubenkan, caster, 10577, 10876);
        using (rig)
        {
            var ai = new NerubenkanAI(boss);
            ai.OnRespawn();
            int Crypt() => rig.Creatures.Creatures.Count(c => c.Entry == NerubenkanAI.NpcCryptScarab);
            int Undead() => rig.Creatures.Creatures.Count(c => c.Entry == NerubenkanAI.NpcUndeadScarab);

            ai.OnUpdate(2_900);
            Assert.Equal(0, Crypt() + Undead());
            ai.OnUpdate(100);

            // One raise: either the crypt version (4, 6 or 8 Crypt Scarabs) or a single Undead Scarab.
            if (Undead() == 0)
            {
                Assert.Contains(Crypt(), new[] { 4, 6, 8 });
            }
            else
            {
                Assert.Equal(1, Undead());
                Assert.Equal(0, Crypt());
            }
        }
    }

    // --- Postmaster Malown -------------------------------------------------------------------

    [Fact]
    public void Malown_SpellsRollTheirChancesAtTheirIntervals()
    {
        var caster = new DungeonTestCaster();
        (DungeonScriptTestKit rig, Creature boss) = Fighting(Malown, caster);
        using (rig)
        {
            var ai = new PostmasterMalownAI(boss);
            ai.OnRespawn();
            int Count(uint spell) => caster.Casts.Count(c => c.Spell == spell);

            ai.OnUpdate(7_900); // nothing is due before 8 s
            Assert.Empty(caster.Casts);

            // 600 ticks of 30 s: every timer is due each tick, so each spell rolls its chance 600 times (seeded RNG).
            for (int i = 0; i < 600; i++)
            {
                ai.OnUpdate(30_000);
            }

            Assert.InRange(Count(PostmasterMalownAI.SpellWailingDead), 330, 450); // 65 %
            Assert.InRange(Count(PostmasterMalownAI.SpellBackhand), 220, 320);    // 45 %
            Assert.InRange(Count(PostmasterMalownAI.SpellCurseOfWeakness), 2, 40); // 3 %
            Assert.InRange(Count(PostmasterMalownAI.SpellCurseOfTongues), 2, 40);  // 3 %
            Assert.InRange(Count(PostmasterMalownAI.SpellCallOfTheGrave), 10, 55); // 5 %
            Assert.All(caster.Casts, c => Assert.Same(rig.Player, c.Target));
        }
    }

    [Fact]
    public void Malown_EachSpellIsCastOnlyAtItsOwnInterval()
    {
        var caster = new DungeonTestCaster();
        (DungeonScriptTestKit rig, Creature boss) = Fighting(Malown, caster);
        using (rig)
        {
            var ai = new PostmasterMalownAI(boss);
            ai.OnRespawn();

            // Spell -> first due time and reset interval (ms); both are equal for every Malown spell.
            Dictionary<uint, uint> intervals = new()
            {
                [PostmasterMalownAI.SpellWailingDead] = 19_000,
                [PostmasterMalownAI.SpellBackhand] = 8_000,
                [PostmasterMalownAI.SpellCurseOfWeakness] = 20_000,
                [PostmasterMalownAI.SpellCurseOfTongues] = 22_000,
                [PostmasterMalownAI.SpellCallOfTheGrave] = 25_000,
            };

            // 1 s ticks for 5000 s: a timer comes due once per interval, so a cast can only land at initial + k * interval.
            List<(uint Spell, uint ElapsedMs)> castTimes = [];
            uint elapsed = 0;
            for (int i = 0; i < 5_000; i++)
            {
                int before = caster.Casts.Count;
                ai.OnUpdate(1_000);
                elapsed += 1_000;
                for (int c = before; c < caster.Casts.Count; c++)
                {
                    castTimes.Add((caster.Casts[c].Spell, elapsed));
                }
            }

            Assert.Equal(caster.Casts.Count, castTimes.Count);
            Assert.All(castTimes, t =>
            {
                uint interval = intervals[t.Spell];
                Assert.True(t.ElapsedMs % interval == 0, $"spell {t.Spell} cast at {t.ElapsedMs} ms, not a multiple of {interval} ms");
            });

            // The two common spells must have been cast (zero casts would pass the check above), and never before their first due time.
            Assert.Contains(castTimes, t => t.Spell == PostmasterMalownAI.SpellWailingDead);
            Assert.Contains(castTimes, t => t.Spell == PostmasterMalownAI.SpellBackhand);
            Assert.All(castTimes, t => Assert.True(t.ElapsedMs >= intervals[t.Spell]));

            // A cast count above the number of due times would mean the timer was reset short (for example to 0).
            int backhandRolls = 5_000 / 8;
            int wailingRolls = 5_000 / 19;
            Assert.InRange(castTimes.Count(t => t.Spell == PostmasterMalownAI.SpellBackhand), 1, backhandRolls);
            Assert.InRange(castTimes.Count(t => t.Spell == PostmasterMalownAI.SpellWailingDead), 1, wailingRolls);
        }
    }

    // --- Evade resets the fight (vmangos ScriptedAI::EnterEvadeMode calls Reset) ----------------

    [Fact]
    public void Barthilas_Evade_ResetsTimersAndAngerCount()
    {
        var caster = new DungeonTestCaster();
        (DungeonScriptTestKit rig, Creature boss) = Fighting(Barthilas, caster);
        using (rig)
        {
            var ai = new MagistrateBarthilasAI(boss);
            ai.OnRespawn();
            ai.OnUpdate(5_000);
            for (int i = 0; i < 25; i++)
            {
                ai.OnUpdate(4_000);
            }

            int Anger() => caster.Casts.Count(c => c.Spell == MagistrateBarthilasAI.SpellFuriousAnger);
            Assert.Equal(26u, ai.AngerCount);

            ai.OnEvade();
            Assert.Equal(0u, ai.AngerCount);

            ai.OnUpdate(4_900);
            Assert.Equal(26, Anger());
            ai.OnUpdate(100); // 5 s into the new pull
            Assert.Equal(27, Anger());
            Assert.Equal(1u, ai.AngerCount);
        }
    }

    [Fact]
    public void Ramstein_Evade_RestartsTrampleAndKnockoutTimers()
    {
        var caster = new DungeonTestCaster();
        (DungeonScriptTestKit rig, Creature boss) = Fighting(Ramstein, caster);
        using (rig)
        {
            var ai = new RamsteinTheGorgerAI(boss);
            ai.OnRespawn();
            ai.OnUpdate(2_000); // timers partly run down
            ai.OnEvade();
            int Trample() => caster.Casts.Count(c => c.Spell == RamsteinTheGorgerAI.SpellTrample);

            ai.OnUpdate(2_900);
            Assert.Equal(0, Trample()); // the first Trample is 3 s after the reset, not 1 s
            ai.OnUpdate(100);
            Assert.Equal(1, Trample());
        }
    }

    [Fact]
    public void Malown_Evade_RestartsTimers()
    {
        var caster = new DungeonTestCaster();
        (DungeonScriptTestKit rig, Creature boss) = Fighting(Malown, caster);
        using (rig)
        {
            var ai = new PostmasterMalownAI(boss);
            ai.OnRespawn();
            ai.OnUpdate(7_000);
            ai.OnEvade();
            int before = caster.Casts.Count;

            // Backhand (45 %) would be rolled at 8 s of the old pull (1 s from now); after the reset no timer is due before 8 s.
            for (int i = 0; i < 7; i++)
            {
                ai.OnUpdate(1_000);
            }

            Assert.Equal(before, caster.Casts.Count);
        }
    }

    [Fact]
    public void Nerubenkan_Evade_RestartsWebsTimer()
    {
        var caster = new WebCaster();
        (DungeonScriptTestKit rig, Creature boss) = Fighting(Nerubenkan, caster, NerubenkanAI.NpcCryptScarab, NerubenkanAI.NpcUndeadScarab);
        using (rig)
        {
            var ai = new NerubenkanAI(boss);
            ai.OnRespawn();
            ai.OnUpdate(6_000);
            ai.OnEvade();
            int Webs() => caster.Casts.Count(c => c.Spell == NerubenkanAI.SpellEncasingWebs);

            ai.OnUpdate(6_900);
            Assert.Equal(0, Webs()); // the first Encasing Webs is 7 s after the reset
            ai.OnUpdate(100);
            Assert.Equal(1, Webs());
        }
    }
}
