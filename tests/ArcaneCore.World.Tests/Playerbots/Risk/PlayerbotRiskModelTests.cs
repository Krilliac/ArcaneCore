using ArcaneCore.Game;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Party;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Risk;

/// <summary>
/// The risk against reward arithmetic (<see cref="PlayerbotRiskModel"/>) and the escape order (<see cref="PlayerbotEscapes"/>), on
/// plain snapshots: a level 10 bot with 300 health and the level estimate of its damage (20 per second) against level 10 creatures
/// of 400 health hitting for 7 per second.
/// </summary>
public sealed class PlayerbotRiskModelTests
{
    private static readonly PlayerbotRiskOptions Defaults = new();

    private static RiskEnemy Mob(RiskJoin join = RiskJoin.Target, byte level = 10, uint health = 400, float dps = 7f, uint rank = 0, uint entry = 100)
        => new(entry, level, rank, health, dps, join);

    private static PlayerbotEngagementFacts Bot(params RiskEnemy[] enemies) => new()
    {
        BotLevel = 10, BotHealth = 300, BotMaxHealth = 300, BotDps = PlayerbotRiskModel.PriorBotDps(10), Enemies = enemies, LootValue = 0.1f,
    };

    [Fact]
    public void ALoneSameLevelCreature_IsPulled_APackOfThree_IsNot()
    {
        PlayerbotEngagement lone = PlayerbotRiskModel.Assess(Bot(Mob()), Defaults);
        Assert.Equal(PlayerbotEngageDecision.Engage, lone.Decision);
        Assert.InRange(lone.Risk, 0.3f, 0.6f);

        PlayerbotEngagement pack = PlayerbotRiskModel.Assess(Bot(Mob(), Mob(RiskJoin.Assist), Mob(RiskJoin.FightSpot)), Defaults);
        Assert.Equal(PlayerbotEngageDecision.Avoid, pack.Decision);
        Assert.Equal("pack-of-3", pack.Reason);
        Assert.True(pack.Reward > lone.Reward, "three kills give more experience");
        Assert.Contains("decision=avoid reason=pack-of-3", pack.ToString());
    }

    /// <summary>
    /// Strength, not worth (<see cref="PlayerbotRiskModel.BeyondStrength"/>, the rule of an unsafe body): a lower-level creature that
    /// gives too little to be pulled (risk 0.7 at full health against a bar of about 0.5 for its reward) is not beyond the bot, which
    /// survives it with health to spare; live 2026-10-11, such creatures (level 9-13 Defias and gnolls against level-14 bots) had bodies
    /// given up as "too strong". A fight above the highest bar any pull is taken at (Tolerance x 0.9), a creature that kills outright and
    /// an elite three levels above are beyond it.
    /// </summary>
    [Fact]
    public void AFightNotWorthAPull_IsNotBeyondTheBotsStrength_ButAFightItWouldNotSurviveIs()
    {
        RiskEnemy lowWorth = Mob(level: 7, health: 400, dps: 10.5f);
        PlayerbotEngagementFacts facts = Bot(lowWorth) with { LootValue = 0 };
        PlayerbotEngagement pull = PlayerbotRiskModel.Assess(facts, Defaults);
        Assert.Equal(PlayerbotEngageDecision.Avoid, pull.Decision); // not worth a pull: the old unsafe-body rule gave the body up
        Assert.InRange(pull.Risk, 0.65f, 0.75f);
        Assert.False(PlayerbotRiskModel.BeyondStrength(facts, Defaults));

        Assert.True(PlayerbotRiskModel.BeyondStrength(Bot(Mob(level: 7, health: 400, dps: 20f)), Defaults)); // risk 1.33
        Assert.True(PlayerbotRiskModel.BeyondStrength(Bot(Mob(level: 13, rank: 1, health: 300, dps: 3f)), Defaults)); // elite above
        Assert.True(PlayerbotRiskModel.BeyondStrength(Bot(Mob(dps: 1f) with { Lethal = true }), Defaults));
        Assert.False(PlayerbotRiskModel.BeyondStrength(Bot(), Defaults));
    }

    [Fact]
    public void CreaturesOnlyAlongTheWay_AreWalkedRound()
    {
        PlayerbotEngagement verdict = PlayerbotRiskModel.Assess(Bot(Mob(), Mob(RiskJoin.Path), Mob(RiskJoin.Path)), Defaults);
        Assert.Equal(PlayerbotEngageDecision.Detour, verdict.Decision);
        Assert.Equal("path-adds-2", verdict.Reason);
    }

    [Fact]
    public void AnEliteThreeLevelsAbove_IsAvoided_UnlessItIsAQuestObjectiveTheBotCanSolo()
    {
        RiskEnemy elite = Mob(level: 13, rank: 1, health: 300, dps: 3f);
        Assert.Equal("elite-above", PlayerbotRiskModel.Assess(Bot(elite), Defaults).Reason);

        PlayerbotEngagement quest = PlayerbotRiskModel.Assess(Bot(elite) with { QuestObjective = true }, Defaults);
        Assert.Equal(PlayerbotEngageDecision.Engage, quest.Decision);

        RiskEnemy strong = Mob(level: 13, rank: 1, health: 1500, dps: 18f);
        PlayerbotEngagement tooStrong = PlayerbotRiskModel.Assess(Bot(strong) with { QuestObjective = true }, Defaults);
        Assert.Equal(PlayerbotEngageDecision.Avoid, tooStrong.Decision);
        Assert.Equal("elite", tooStrong.Reason);
    }

    [Fact]
    public void AFightFineAtFullHealth_WaitsToRecover_AndToleranceMovesTheLine()
    {
        PlayerbotEngagement hurt = PlayerbotRiskModel.Assess(Bot(Mob()) with { BotHealth = 90 }, Defaults);
        Assert.Equal(PlayerbotEngageDecision.Rest, hurt.Decision);
        Assert.Equal("low-health", hurt.Reason);

        PlayerbotEngagement drained = PlayerbotRiskModel.Assess(Bot(Mob()) with { BotManaPct = 5f, ManaDependence = 1f, BotHealth = 290 }, Defaults);
        Assert.Equal(PlayerbotEngageDecision.Rest, drained.Decision);
        Assert.Equal("low-mana", drained.Reason);

        var pair = Bot(Mob(), Mob(RiskJoin.Assist));
        Assert.Equal(PlayerbotEngageDecision.Avoid, PlayerbotRiskModel.Assess(pair, Defaults).Decision);
        Assert.Equal(PlayerbotEngageDecision.Engage, PlayerbotRiskModel.Assess(pair, new PlayerbotRiskOptions { Tolerance = 3f }).Decision);
    }

    [Fact]
    public void RememberedDanger_RaisesTheRisk_AndARememberedCreatureIsNotPulled()
    {
        PlayerbotEngagement plain = PlayerbotRiskModel.Assess(Bot(Mob()), Defaults);
        PlayerbotEngagement danger = PlayerbotRiskModel.Assess(Bot(Mob()) with { DangerHits = 2 }, Defaults);
        Assert.Equal(plain.Risk * 2f, danger.Risk, 3);
        Assert.Equal("remembered", PlayerbotRiskModel.Assess(Bot(Mob()) with { Remembered = true }, Defaults).Reason);
    }

    [Fact]
    public void AGrayCreatureGivesNoExperience()
    {
        Assert.Equal(0.1f, PlayerbotRiskModel.Reward(Bot(Mob(level: 3))), 3);
        Assert.Equal(1.1f, PlayerbotRiskModel.Reward(Bot(Mob())), 2);
    }

    [Fact]
    public void ALosingFight_Retreats_AWinningOrNearlyWonOne_DoesNot()
    {
        // 100 of 300 health left, taking 20 a second (5 s to live), the creature needs 300 more damage at 20 a second (15 s).
        PlayerbotFightVerdict losing = PlayerbotRiskModel.Judge(new(100, 300, 20f, 20f, 300, 1, 75f), Defaults);
        Assert.True(losing.Retreat);
        Assert.Equal("losing", losing.Reason);

        PlayerbotFightVerdict winning = PlayerbotRiskModel.Judge(new(100, 300, 5f, 20f, 300, 1, 75f), Defaults);
        Assert.False(winning.Retreat);

        // The creature at 10%: 40 health at 20 a second is 2 s, the bot lives 3 s.
        PlayerbotFightVerdict nearlyWon = PlayerbotRiskModel.Judge(new(60, 300, 20f, 20f, 40, 1, 10f), Defaults);
        Assert.False(nearlyWon.Retreat);
        Assert.Equal("nearly-won", nearlyWon.Reason);

        // Losing but healthy: hold on until RetreatHealthPct, unless about to die.
        Assert.False(PlayerbotRiskModel.Judge(new(250, 300, 10f, 10f, 400, 1, 100f), Defaults).Retreat);
        Assert.True(PlayerbotRiskModel.Judge(new(250, 300, 10f, 10f, 400, 1, 100f), new PlayerbotRiskOptions { RetreatHealthPct = 90f }).Retreat);
        Assert.Equal("losing-to-3", PlayerbotRiskModel.Judge(new(30, 300, 20f, 20f, 900, 3, 90f), Defaults).Reason);
    }

    [Fact]
    public void ALowBotFacingSeveral_RetreatsWhenOneRoundWouldCarryItBelowTheLine_WhileTheRetreatCanStillSucceed()
    {
        // 25 of 60 health (41%, above the 35% line), three pursuers at 4.2 yards a second against the bot's 7, 30 yards to run.
        var pursuit = new PlayerbotPursuit(30f, 7f, [new RiskPursuer(4.2f, 4.7f / 3f), new RiskPursuer(4.2f, 4.7f / 3f), new RiskPursuer(4.2f, 4.7f / 3f)],
            PlayerbotEscapeKit.None, FleeDamageFactor: 1.25f);
        PlayerbotFightFacts facts = new(25, 60, 4.7f, 2f, 180, 3, 100f, Pursuit: pursuit);

        // The mean rate alone says "behind"; one round of three blows (15) would leave 10 health (17%), under the line.
        PlayerbotFightVerdict burst = PlayerbotRiskModel.Judge(facts with { RoundDamage = 15f }, Defaults);
        Assert.True(burst.Retreat, burst.ToString());
        Assert.Equal("losing-to-3", burst.Reason);

        PlayerbotFightVerdict mean = PlayerbotRiskModel.Judge(facts with { RoundDamage = 0f }, Defaults);
        Assert.False(mean.Retreat);
        Assert.Equal("behind", mean.Reason);

        // A round that leaves the bot above the line changes nothing.
        Assert.Equal("behind", PlayerbotRiskModel.Judge(facts with { RoundDamage = 3f }, Defaults).Reason);

        // Feasibility is judged at the current health: the run costs about 10.5 health, fine from 25 and not from 10 (90% of 10 is 9).
        PlayerbotFightVerdict cornered = PlayerbotRiskModel.Judge(facts with { BotHealth = 10, RoundDamage = 15f }, Defaults);
        Assert.False(cornered.Retreat);
        Assert.Equal(PlayerbotRiskModel.CorneredReason, cornered.Reason);
    }

    [Theory]
    [InlineData(Class.Mage, PlayerbotEscapes.FrostNova)]
    [InlineData(Class.Rogue, PlayerbotEscapes.Vanish)]
    [InlineData(Class.Hunter, PlayerbotEscapes.FeignDeath)]
    [InlineData(Class.Priest, PlayerbotEscapes.PsychicScream)]
    [InlineData(Class.Warrior, PlayerbotEscapes.IntimidatingShout)]
    [InlineData(Class.Warlock, PlayerbotEscapes.HowlOfTerror)]
    [InlineData(Class.Druid, PlayerbotEscapes.EntanglingRoots)]
    [InlineData(Class.Paladin, PlayerbotEscapes.HammerOfJustice)]
    [InlineData(Class.Shaman, PlayerbotEscapes.FrostShock)]
    public void EachClass_UsesItsEscape_First(Class playerClass, string first)
    {
        var situation = new EscapeSituation(playerClass, 30f, true, AttackersWithin8: 2, AttackersWithin10: 2, VictimInMelee: true, HasVictim: true, InCatForm: false);
        Assert.Equal(first, PlayerbotEscapes.Choose(situation, _ => true)?.Spell);
    }

    [Fact]
    public void EscapesFollowTheirOrder_WhenTheFirstIsNotReady()
    {
        var mage = new EscapeSituation(Class.Mage, 30f, true, 1, 1, true, true, false);
        Assert.Equal(new EscapeAction(PlayerbotEscapes.Blink, EscapeTarget.Self, FaceAway: true),
            PlayerbotEscapes.Choose(mage, a => a.Spell != PlayerbotEscapes.FrostNova));
        var rogue = mage with { Class = Class.Rogue };
        Assert.Equal(PlayerbotEscapes.Sprint, PlayerbotEscapes.Choose(rogue, a => a.Spell == PlayerbotEscapes.Sprint)?.Spell);
        var warrior = mage with { Class = Class.Warrior, AttackersWithin8 = 0 };
        Assert.Equal(new EscapeAction(PlayerbotEscapes.Hamstring, EscapeTarget.Victim), PlayerbotEscapes.Choose(warrior, _ => true));
        var hunter = mage with { Class = Class.Hunter, InCombat = false };
        Assert.Equal(PlayerbotEscapes.AspectOfTheCheetah, PlayerbotEscapes.Choose(hunter, _ => true)?.Spell);
        var priest = mage with { Class = Class.Priest, AttackersWithin8 = 0 };
        Assert.Equal(PlayerbotEscapes.PowerWordShield, PlayerbotEscapes.Choose(priest, _ => true)?.Spell);
        Assert.Null(PlayerbotEscapes.Choose(mage, _ => false));
    }

    [Fact]
    public void AGroupIsWiping_WhenItsMasterOrHalfOfItIsDead()
    {
        Assert.False(PlayerbotPartyAI.IsWiping(masterDead: false, membersAlive: 3, membersDead: 1));
        Assert.True(PlayerbotPartyAI.IsWiping(masterDead: false, membersAlive: 2, membersDead: 2));
        Assert.True(PlayerbotPartyAI.IsWiping(masterDead: true, membersAlive: 4, membersDead: 1));
    }

    [Fact]
    public void TheRiskSection_Binds_AndOutOfRangeValuesAreRefused()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["World:Playerbots:Risk:Enabled"] = "false",
            ["World:Playerbots:Risk:Tolerance"] = "1.5",
            ["World:Playerbots:Risk:RetreatHealthPct"] = "25",
            ["World:Playerbots:Risk:DangerMemorySeconds"] = "60",
            ["World:Playerbots:Risk:PartyRetreatOnWipe"] = "false",
        }).Build();
        PlayerbotOptions options = PlayerbotOptions.Bind(configuration);
        options.Validate();
        Assert.False(options.Risk.Enabled);
        Assert.Equal(1.5f, options.Risk.Tolerance);
        Assert.Equal(25f, options.Risk.RetreatHealthPct);
        Assert.Equal(60, options.Risk.DangerMemorySeconds);
        Assert.False(options.Risk.PartyRetreatOnWipe);
        Assert.True(new PlayerbotOptions().Risk.Enabled);

        Assert.Throws<InvalidOperationException>(() => new PlayerbotOptions { Risk = { Tolerance = 9f } }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotOptions { Risk = { RetreatHealthPct = 0f } }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotOptions { Risk = { DangerMemorySeconds = -1 } }.Validate());
    }

    [Fact]
    public void ACreatureThatKillsOutright_IsAvoided_EvenAsAQuestObjective_AndFledInAFight()
    {
        RiskEnemy lethal = Mob() with { Lethal = true };
        PlayerbotEngagement verdict = PlayerbotRiskModel.Assess(Bot(lethal) with { QuestObjective = true }, Defaults);
        Assert.Equal(PlayerbotEngageDecision.Avoid, verdict.Decision);
        Assert.Equal("lethal", verdict.Reason);
        Assert.Equal("lethal", PlayerbotRiskModel.Assess(Bot(Mob(), lethal with { Join = RiskJoin.Assist }), Defaults).Reason);

        // Full health, winning on the rates: still away from a one-shot, unless it dies within two seconds.
        Assert.Equal("lethal", PlayerbotRiskModel.Judge(new(300, 300, 1f, 20f, 300, 1, 100f, Lethal: true), Defaults).Reason);
        Assert.False(PlayerbotRiskModel.Judge(new(300, 300, 1f, 20f, 30, 1, 10f, Lethal: true), Defaults).Retreat);
    }

    [Fact]
    public void ACreaturesEventAiCasts_AddToItsDanger()
    {
        var instakill = new SpellInfo { Id = 28265, Name = "Scourge Strike", Effects = [new SpellEffectInfo { Effect = SpellEffectName.Instakill, TargetA = SpellImplicitTarget.UnitEnemy }] };
        var bolt = new SpellInfo { Id = 9001, Name = "Bolt", Effects = [new SpellEffectInfo { Effect = SpellEffectName.SchoolDamage, TargetA = SpellImplicitTarget.UnitEnemy, BasePoints = 39, DieSides = 11 }] };
        Func<uint, SpellInfo?> spells = id => id == 28265 ? instakill : id == 9001 ? bolt : null;

        CreatureSpellThreat soldier = PlayerbotCreatureSpells.Of([Cast(16422, 28265, 5000, 10000)], spells);
        Assert.True(soldier.Instakill);
        Assert.True(soldier.IsLethalTo(10_000));

        CreatureSpellThreat caster = PlayerbotCreatureSpells.Of([Cast(100, 9001, 4000, 6000)], spells);
        Assert.False(caster.Instakill);
        Assert.Equal(50, caster.MaxHit);
        Assert.Equal(10f, caster.Dps, 3); // 50 every 5 s
        Assert.True(caster.IsLethalTo(50));
        Assert.False(caster.IsLethalTo(51));

        Assert.Equal(CreatureSpellThreat.None, PlayerbotCreatureSpells.Of([Cast(100, 777, 0, 0)], spells));

        // Spell 13322 Frostbolt (SpellLevel 20, ScalesWithCreatureLevel, 55-64 damage every 3.1-5.1 s on level 7 Darkeye Bonecaster 1522):
        // scaled at caster level 7 to MaxHit 19 (~4.63 DPS) instead of unscaled 64 (~15.61 DPS), so a level 7 bot engages rather than avoiding.
        var frostbolt = new SpellInfo
        {
            Id = 13322,
            Name = "Frostbolt",
            SpellLevel = 20,
            Attributes = (SpellAttributes)ArcaneCore.Game.Spells.Casters.CasterAttributes.ScalesWithCreatureLevel,
            Effects = [new SpellEffectInfo { Effect = SpellEffectName.SchoolDamage, TargetA = SpellImplicitTarget.UnitEnemy, BasePoints = 54, DieSides = 10 }],
        };
        CreatureSpellThreat bonecasterLv7 = PlayerbotCreatureSpells.Of([Cast(1522, 13322, 3100, 5100)], id => id == 13322 ? frostbolt : null, casterLevel: 7);
        CreatureSpellThreat bonecasterLv20 = PlayerbotCreatureSpells.Of([Cast(1522, 13322, 3100, 5100)], id => id == 13322 ? frostbolt : null, casterLevel: 20);
        Assert.Equal(19, bonecasterLv7.MaxHit);
        Assert.Equal(64, bonecasterLv20.MaxHit);
        Assert.True(bonecasterLv7.Dps < 5f);
    }

    private static CreatureAiEvent Cast(uint creature, uint spell, int repeatMin, int repeatMax) => new()
    {
        Id = creature * 100 + 1, CreatureId = creature, EventType = 0, Param3 = repeatMin, Param4 = repeatMax,
        Action1 = new CreatureAiAction(PlayerbotCreatureSpells.ActionCast, (int)spell, 1, 0),
    };
}
