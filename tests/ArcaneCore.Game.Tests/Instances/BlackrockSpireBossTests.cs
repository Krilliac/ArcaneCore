using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.BlackrockSpire;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// The Lower Blackrock Spire boss scripts of vmangos blackrock_spire/ (boss_warmaster_voone, boss_highlord_omokk,
/// boss_shadow_hunter_voshgajin, boss_halycon, boss_urok): one test group per boss, timers on the manual clock.
/// </summary>
public sealed class BlackrockSpireBossTests
{
    private const uint Voone = 9237, Omokk = 9196, Vosh = 9236, Halycon = 10220, Gizrul = 10268;
    private const uint Enforcer = 10601, Magus = 10602, Doomhowl = 10584;

    private static DungeonScriptTestKit Rig(uint[] creatures, uint[] spawns, (uint Entry, GameObjectType Type)[]? objects = null, CreatureAiServices? services = null)
        => new(map => new BlackrockSpireInstance(map), creatures, spawns, objects ?? [], aiServices: services);

    private static (DungeonScriptTestKit Rig, Creature Boss) Fighting(uint entry, ICreatureSpellCaster caster, ItemTemplate? axe = null)
    {
        DungeonScriptTestKit rig = Rig([entry], [entry], services: new CreatureAiServices
        {
            Spells = caster,
            ItemTemplateOf = id => axe is not null && id == axe.Entry ? axe : null,
        });
        Creature boss = rig.Creature(entry);
        rig.Player.Relocate(boss.X + 1, boss.Y, boss.Z, 0, 0);
        Assert.True(rig.Creatures.AttackStart(boss, rig.Player));
        boss.Combat.Threat.AddThreat(rig.Player, 100);
        return (rig, boss);
    }

    [Fact]
    public void DungeonBossTable_MapsTheLowerSpireBossesAndUrokUnderlings()
    {
        Assert.Equal("WarmasterVooneAI", DungeonBossAis.Describe(229, Voone));
        Assert.Equal("HighlordOmokkAI", DungeonBossAis.Describe(229, Omokk));
        Assert.Equal("ShadowHunterVoshgajinAI", DungeonBossAis.Describe(229, Vosh));
        Assert.Equal("HalyconAI", DungeonBossAis.Describe(229, Halycon));
        Assert.Equal("UrokEnforcerAI", DungeonBossAis.Describe(229, Enforcer));
        Assert.Equal("UrokOgreMagusAI", DungeonBossAis.Describe(229, Magus));
        Assert.Null(DungeonBossAis.Describe(329, Voone));
    }

    // --- Warmaster Voone ---------------------------------------------------------------------

    [Fact]
    public void Voone_ThrowsAnAxeAtOneSecond_ThenEveryEightSeconds_AtTheVictim()
    {
        var caster = new DungeonTestCaster();
        (DungeonScriptTestKit rig, Creature boss) = Fighting(Voone, caster);
        using (rig)
        {
            var ai = new WarmasterVooneAI(boss);
            ai.OnRespawn();
            int Throws() => caster.Casts.Count(c => c.Spell == WarmasterVooneAI.SpellThrowAxe);

            ai.OnUpdate(900);
            Assert.Equal(0, Throws());
            ai.OnUpdate(100);
            Assert.Equal(1, Throws());
            Assert.All(caster.Casts.Where(c => c.Spell == WarmasterVooneAI.SpellThrowAxe), c => Assert.Same(rig.Player, c.Target));

            ai.OnUpdate(7_900);
            Assert.Equal(1, Throws());
            ai.OnUpdate(100);
            Assert.Equal(2, Throws());
        }
    }

    [Fact]
    public void Voone_FirstAxeLeavesOneAxeInTheMainHand_SecondLeavesHimUnarmedAndHeThrowsNoMore_EvadeRestoresTheWeapons()
    {
        var caster = new AuraTrackingCaster();
        var axe = new ItemTemplate { Entry = WarmasterVooneAI.ItemRemainingAxe, Class = 2, SubClass = 0, DisplayId = 7777, Material = 1, InventoryType = 21, Sheath = 3 };
        (DungeonScriptTestKit rig, Creature boss) = Fighting(Voone, caster, axe);
        using (rig)
        {
            boss.SetVirtualItem(0, 111, 2, 0, 1, 21, 3);
            boss.SetVirtualItem(1, 222, 2, 0, 1, 21, 3);
            var ai = new WarmasterVooneAI(boss);
            ai.OnRespawn();
            int Throws() => caster.Casts.Count(c => c.Spell == WarmasterVooneAI.SpellThrowAxe);
            uint Display(int slot) => boss.GetUInt32(UpdateFields.UnitVirtualItemSlotDisplay + slot);
            var axeHit = new SpellInfo { Id = WarmasterVooneAI.SpellThrowAxe };

            ai.OnSpellHitTarget(rig.Player, new SpellInfo { Id = 12345 }); // another spell changes nothing
            Assert.Equal(0, ai.AxesThrown);
            Assert.Equal(111u, Display(0));

            ai.OnUpdate(1_000);
            Assert.Equal(1, Throws());
            ai.OnSpellHitTarget(rig.Player, axeHit);
            Assert.Equal(1, ai.AxesThrown);
            Assert.Equal(7777u, Display(0));
            Assert.Equal(0u, Display(1));
            Assert.DoesNotContain(caster.Casts, c => c.Spell == WarmasterVooneAI.SpellUnarmedPassive);

            ai.OnUpdate(8_000);
            Assert.Equal(2, Throws());
            ai.OnSpellHitTarget(rig.Player, axeHit);
            Assert.Equal(2, ai.AxesThrown);
            Assert.Equal(0u, Display(0));
            Assert.Equal(0u, Display(1));
            Assert.Single(caster.Casts, c => c.Spell == WarmasterVooneAI.SpellUnarmedPassive && ReferenceEquals(c.Target, boss));

            ai.OnUpdate(8_000);
            ai.OnUpdate(8_000);
            Assert.Equal(2, Throws()); // Unarmed Passive stops the throwing

            ai.OnEvade();
            Assert.Equal(0, ai.AxesThrown);
            Assert.Equal(111u, Display(0));
            Assert.Equal(222u, Display(1));
            Assert.Contains(caster.Removed, r => ReferenceEquals(r.Unit, boss) && r.Spell == WarmasterVooneAI.SpellUnarmedPassive);
        }
    }

    [Fact]
    public void Voone_MeleeSpellTimers_FollowTheScript()
    {
        var caster = new DungeonTestCaster();
        (DungeonScriptTestKit rig, Creature boss) = Fighting(Voone, caster);
        using (rig)
        {
            var ai = new WarmasterVooneAI(boss);
            ai.OnRespawn();
            int Count(uint spell) => caster.Casts.Count(c => c.Spell == spell);

            ai.OnUpdate(8_000);
            Assert.Equal(1, Count(WarmasterVooneAI.SpellSnapKick));
            Assert.Equal(0, Count(WarmasterVooneAI.SpellMortalStrike));
            ai.OnUpdate(4_000); // 12 s
            Assert.Equal(1, Count(WarmasterVooneAI.SpellMortalStrike));
            ai.OnUpdate(2_000); // 14 s
            Assert.Equal(1, Count(WarmasterVooneAI.SpellCleave));
            Assert.Equal(2, Count(WarmasterVooneAI.SpellSnapKick)); // 8 s + 6 s
            ai.OnUpdate(6_000); // 20 s
            Assert.Equal(1, Count(WarmasterVooneAI.SpellUppercut));
            ai.OnUpdate(12_000); // 32 s
            Assert.Equal(1, Count(WarmasterVooneAI.SpellPummel));
        }
    }

    // --- Highlord Omokk ----------------------------------------------------------------------

    [Fact]
    public void Omokk_SixTimers_AtTheScriptIntervals()
    {
        var caster = new DungeonTestCaster();
        (DungeonScriptTestKit rig, Creature boss) = Fighting(Omokk, caster);
        using (rig)
        {
            var ai = new HighlordOmokkAI(boss);
            ai.OnRespawn();
            int Count(uint spell) => caster.Casts.Count(c => c.Spell == spell);

            ai.OnUpdate(1_900);
            Assert.Equal(0, Count(HighlordOmokkAI.SpellSunderArmor));
            ai.OnUpdate(100);
            Assert.Equal(1, Count(HighlordOmokkAI.SpellSunderArmor));
            Assert.Same(rig.Player, caster.Casts.Single(c => c.Spell == HighlordOmokkAI.SpellSunderArmor).Target);

            ai.OnUpdate(8_000); // 10 s
            Assert.Equal(1, Count(HighlordOmokkAI.SpellStrike));
            ai.OnUpdate(4_000); // 14 s
            Assert.Equal(1, Count(HighlordOmokkAI.SpellRend));
            ai.OnUpdate(1_000); // 15 s
            Assert.Equal(1, Count(HighlordOmokkAI.SpellWarStomp));
            Assert.Same(boss, caster.Casts.Single(c => c.Spell == HighlordOmokkAI.SpellWarStomp).Target);
            ai.OnUpdate(3_000); // 18 s
            Assert.Equal(1, Count(HighlordOmokkAI.SpellKnockAway));
            ai.OnUpdate(6_000); // 24 s
            Assert.Equal(1, Count(HighlordOmokkAI.SpellSlow));
            Assert.Equal(2, Count(HighlordOmokkAI.SpellStrike)); // Strike again at 20 s
        }
    }

    [Fact]
    public void Omokk_EnragesOnceAtTwentyPercentHealth()
    {
        var caster = new DungeonTestCaster();
        (DungeonScriptTestKit rig, Creature boss) = Fighting(Omokk, caster);
        using (rig)
        {
            var ai = new HighlordOmokkAI(boss);
            ai.OnRespawn();

            ai.OnUpdate(100);
            Assert.DoesNotContain(caster.Casts, c => c.Spell == HighlordOmokkAI.SpellEnrage);

            boss.Health = (uint)(boss.MaxHealth / 5);
            ai.OnUpdate(100);
            ai.OnUpdate(100);
            Assert.Single(caster.Casts, c => c.Spell == HighlordOmokkAI.SpellEnrage);
        }
    }

    // --- Shadow Hunter Vosh'gajin --------------------------------------------------------------

    [Fact]
    public void Voshgajin_CurseHexCleave_AtTheScriptIntervals()
    {
        var caster = new DungeonTestCaster();
        (DungeonScriptTestKit rig, Creature boss) = Fighting(Vosh, caster);
        using (rig)
        {
            var ai = new ShadowHunterVoshgajinAI(boss);
            ai.OnRespawn();
            int Count(uint spell) => caster.Casts.Count(c => c.Spell == spell);

            ai.OnUpdate(1_900);
            Assert.Equal(0, Count(ShadowHunterVoshgajinAI.SpellCurseOfBlood));
            ai.OnUpdate(100);
            Assert.Equal(1, Count(ShadowHunterVoshgajinAI.SpellCurseOfBlood));
            Assert.Same(boss, caster.Casts.Single(c => c.Spell == ShadowHunterVoshgajinAI.SpellCurseOfBlood).Target);

            ai.OnUpdate(6_000); // 8 s
            Assert.Equal(1, Count(ShadowHunterVoshgajinAI.SpellHex));
            Assert.Same(rig.Player, caster.Casts.Single(c => c.Spell == ShadowHunterVoshgajinAI.SpellHex).Target);
            ai.OnUpdate(6_000); // 14 s
            Assert.Equal(1, Count(ShadowHunterVoshgajinAI.SpellCleave));
            ai.OnUpdate(7_000); // 21 s: Cleave again, Hex again at 23 s not yet
            Assert.Equal(2, Count(ShadowHunterVoshgajinAI.SpellCleave));
            Assert.Equal(1, Count(ShadowHunterVoshgajinAI.SpellHex));
            ai.OnUpdate(2_000); // 23 s
            Assert.Equal(2, Count(ShadowHunterVoshgajinAI.SpellHex));
            ai.OnUpdate(22_000); // 45 s + 2 s after the first curse
            Assert.Equal(2, Count(ShadowHunterVoshgajinAI.SpellCurseOfBlood));
        }
    }

    // --- Halycon -----------------------------------------------------------------------------

    [Fact]
    public void Halycon_CrowdPummelAtEightSeconds_MightyBlowAtFourteen_ThenTheScriptRepeats()
    {
        var caster = new DungeonTestCaster();
        (DungeonScriptTestKit rig, Creature boss) = Fighting(Halycon, caster);
        using (rig)
        {
            var ai = new HalyconAI(boss);
            ai.OnRespawn();
            int Count(uint spell) => caster.Casts.Count(c => c.Spell == spell);

            ai.OnUpdate(7_900);
            Assert.Equal(0, Count(HalyconAI.SpellCrowdPummel));
            ai.OnUpdate(100);
            Assert.Equal(1, Count(HalyconAI.SpellCrowdPummel));
            ai.OnUpdate(6_000); // 14 s
            Assert.Equal(1, Count(HalyconAI.SpellMightyBlow));
            ai.OnUpdate(10_000); // 24 s: Mighty Blow repeats after 10 s, Crowd Pummel after 14 s from 8 s (22 s)
            Assert.Equal(2, Count(HalyconAI.SpellMightyBlow));
            Assert.Equal(2, Count(HalyconAI.SpellCrowdPummel));
        }
    }

    [Fact]
    public void Halycon_Death_SummonsGizrulOnce_WithHomeOnTheStairs_InCombatWithThePlayers()
    {
        using DungeonScriptTestKit rig = Rig([Halycon, Gizrul], [Halycon]);
        Creature boss = rig.Creature(Halycon);
        var ai = new HalyconAI(boss);
        ai.OnRespawn();
        Assert.DoesNotContain(rig.Creatures.Creatures, c => c.Entry == Gizrul);

        rig.Kill(Halycon);
        ai.OnDeath(rig.Player);

        Creature gizrul = Assert.Single(rig.Creatures.Creatures, c => c.Entry == Gizrul);
        Assert.Equal(-167.58f, gizrul.X, 0.01f);
        Assert.Equal(-382.41f, gizrul.Y, 0.01f);
        Assert.Equal(64.401f, gizrul.Z, 0.01f);
        Assert.Equal(-172.633f, gizrul.Home.X, 0.01f);
        Assert.Equal(-324.253f, gizrul.Home.Y, 0.01f);
        Assert.True(gizrul.Combat.IsInCombat);

        ai.OnDeath(rig.Player);
        Assert.Single(rig.Creatures.Creatures, c => c.Entry == Gizrul);
    }

    // --- Urok Doomhowl challenge ---------------------------------------------------------------

    private static (DungeonScriptTestKit Rig, GameObject Mound, UrokChallengeAI Ai) ChallengeRig()
    {
        // The rig places one database object per listed entry, so one circle (175571) is always in the map besides the summoned ones.
        DungeonScriptTestKit rig = Rig([Enforcer, Magus, Doomhowl], [],
            [(UrokChallengeAI.GoChallenge, GameObjectType.Generic), (UrokChallengeAI.GoSummonCircle, GameObjectType.Generic)]);
        GameObject mound = rig.Object(UrokChallengeAI.GoChallenge);
        var ai = Assert.IsType<UrokChallengeAI>(rig.Objects.AiFor(mound));
        for (int i = 0; i < 40; i++)
        {
            ai.Update(rig.Objects, mound, 100); // 4 s: the circles at once, the first three underlings after three seconds
        }

        return (rig, mound, ai);
    }

    private static int Circles(DungeonScriptTestKit rig) => rig.Objects.GameObjects.Count(g => g.Entry == UrokChallengeAI.GoSummonCircle) - 1;

    private static Creature[] Underlings(DungeonScriptTestKit rig)
        => [.. rig.Creatures.Creatures.Where(c => c.Entry is Enforcer or Magus)];

    [Fact]
    public void UrokChallenge_PutsSixCircles_ThenAMageAndTwoEnforcers_AtTheirCircles()
    {
        (DungeonScriptTestKit rig, GameObject _, UrokChallengeAI _) = ChallengeRig();
        using (rig)
        {
            Assert.Equal(6, Circles(rig));
            Creature[] adds = Underlings(rig);
            Assert.Equal(3, adds.Length);
            Assert.Single(adds, c => c.Entry == Magus);
            Assert.Equal(2, adds.Count(c => c.Entry == Enforcer));
            Assert.Equal(-13.7275f, adds.Single(c => c.Entry == Magus).X, 0.01f); // circle 0
        }
    }

    [Fact]
    public void UrokChallenge_UnderlingDeathReplacesIt_EighthDeathBringsDoomhowlAndTakesTheCirclesAway()
    {
        (DungeonScriptTestKit rig, GameObject mound, UrokChallengeAI ai) = ChallengeRig();
        using (rig)
        {
            var reported = new HashSet<Creature>();
            for (int death = 1; death <= UrokChallengeAI.UnderlingsToKill; death++)
            {
                Creature dying = Underlings(rig).First(c => !reported.Contains(c));
                reported.Add(dying);
                ai.UnderlingDied(rig.Objects, mound, dying);
                Assert.Equal(death, ai.DeadUnderlings(mound));
                if (death < UrokChallengeAI.UnderlingsToKill)
                {
                    Assert.Equal(3, Underlings(rig).Count(c => !reported.Contains(c)));
                    Assert.DoesNotContain(rig.Creatures.Creatures, c => c.Entry == Doomhowl);
                }
            }

            Creature boss = Assert.Single(rig.Creatures.Creatures, c => c.Entry == Doomhowl);
            Assert.Equal(-47.9095f, boss.X, 0.01f); // circle 5
            Assert.Equal(0, Circles(rig));
            Assert.Equal(2, Underlings(rig).Count(c => !reported.Contains(c)));
        }
    }

    [Fact]
    public void UrokChallenge_UnderlingAiDeathReportsToTheObject()
    {
        (DungeonScriptTestKit rig, GameObject mound, UrokChallengeAI ai) = ChallengeRig();
        using (rig)
        {
            Creature first = Underlings(rig)[0];
            var underling = first.Entry == Magus ? (UrokUnderlingAI)new UrokOgreMagusAI(first) : new UrokEnforcerAI(first);
            underling.Mound = mound;

            underling.OnDeath(rig.Player);

            Assert.Equal(1, ai.DeadUnderlings(mound));
            Assert.Equal(4, Underlings(rig).Length);
        }
    }

    [Fact]
    public void UrokChallenge_BannerEventEndsTheChallenge_AndLaterDeathsSpawnNothing()
    {
        (DungeonScriptTestKit rig, GameObject mound, UrokChallengeAI ai) = ChallengeRig();
        using (rig)
        {
            Creature first = Underlings(rig)[0];
            var underling = new UrokEnforcerAI(first) { Mound = mound };
            first.AI = underling;

            Assert.True(((BlackrockSpireInstance)rig.Script).OnSpellEvent(first, UrokChallengeAI.EventBannerDestroyed));

            Assert.False(ai.IsActive(mound));
            Assert.Equal(0, Circles(rig));
            Assert.DoesNotContain(rig.Objects.GameObjects, g => g.Entry == UrokChallengeAI.GoChallenge);
            int before = Underlings(rig).Length;
            ai.UnderlingDied(rig.Objects, mound, first);
            Assert.Equal(before, Underlings(rig).Length);
            Assert.Equal(0, ai.DeadUnderlings(mound));
        }
    }

    [Fact]
    public void UrokUnderlings_SpellTimers()
    {
        var caster = new DungeonTestCaster();
        DungeonScriptTestKit rig = Rig([Enforcer, Magus], [Enforcer, Magus], services: new CreatureAiServices { Spells = caster });
        using (rig)
        {
            Creature enforcer = rig.Creature(Enforcer);
            Creature magus = rig.Creature(Magus);
            foreach (Creature c in new[] { enforcer, magus })
            {
                rig.Player.Relocate(c.X + 1, c.Y, c.Z, 0, 0);
                Assert.True(rig.Creatures.AttackStart(c, rig.Player));
                c.Combat.Threat.AddThreat(rig.Player, 100);
            }

            var strikeAi = new UrokEnforcerAI(enforcer);
            strikeAi.OnRespawn();
            strikeAi.OnUpdate(15_000); // the first Strike comes within 5 to 15 s
            Assert.Single(caster.Casts, c => c.Spell == UrokEnforcerAI.SpellStrike);

            var magusAi = new UrokOgreMagusAI(magus);
            magusAi.OnRespawn();
            magusAi.OnUpdate(100);
            Assert.Single(caster.Casts, c => c.Spell == UrokOgreMagusAI.SpellArcaneBolt);
            Assert.DoesNotContain(caster.Casts, c => c.Spell == UrokOgreMagusAI.SpellBloodlust);
            magus.Health = (uint)(magus.MaxHealth / 4);
            magusAi.OnUpdate(100);
            Assert.Single(caster.Casts, c => c.Spell == UrokOgreMagusAI.SpellBloodlust);
            magusAi.OnUpdate(11_000); // Slow comes within 8 to 11 s
            Assert.Single(caster.Casts, c => c.Spell == UrokOgreMagusAI.SpellSlow);
        }
    }
}
