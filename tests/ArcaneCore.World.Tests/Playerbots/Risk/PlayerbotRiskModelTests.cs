using ArcaneCore.Game;
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
}
