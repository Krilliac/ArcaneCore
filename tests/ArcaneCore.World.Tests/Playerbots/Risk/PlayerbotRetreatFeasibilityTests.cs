using ArcaneCore.Game.Entities;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Risk;

/// <summary>
/// The retreat feasibility estimate (<see cref="PlayerbotRiskModel.RetreatSucceeds"/>, <see cref="PlayerbotRiskModel.Judge"/>): pure
/// arithmetic on the numbers of the w29 deaths. A level 20 bot has about 1000 health and runs 7.0 yards a second (Unit.BaseRunSpeed);
/// a creature of speed_run 1.14286 runs 8.0; the retreat's goal lies 55 yards and more away (the leash 50 + 5).
/// </summary>
public sealed class PlayerbotRetreatFeasibilityTests
{
    private static readonly PlayerbotRiskOptions Options = new();
    private const float Fast = Unit.BaseRunSpeed * 1.14286f;

    /// <summary>A level 20 bot at 30% of 1000 health losing to one same-level creature that hits for 60 a second.</summary>
    private static PlayerbotFightFacts Losing(PlayerbotPursuit? pursuit) => new(
        BotHealth: 300, BotMaxHealth: 1000, DpsIn: 60f, DpsOut: 20f, EnemyHealth: 900, Enemies: 1, TargetHealthPct: 100f, Pursuit: pursuit);

    private static PlayerbotPursuit Pursuit(float creatureSpeed, PlayerbotEscapeKit? kit = null, float yards = 55f)
        => new(yards, Unit.BaseRunSpeed, [new RiskPursuer(creatureSpeed, 60f)], kit ?? PlayerbotEscapeKit.None);

    [Fact]
    public void WithoutPursuitFacts_TheLosingBotRetreatsAsBefore()
    {
        PlayerbotFightVerdict verdict = PlayerbotRiskModel.Judge(Losing(null), Options);
        Assert.True(verdict.Retreat);
        Assert.Equal("losing", verdict.Reason);
    }

    [Fact]
    public void ALosingBot_WithNoEscape_FightsOn_WhenTheCreatureOutrunsIt()
    {
        PlayerbotFightVerdict verdict = PlayerbotRiskModel.Judge(Losing(Pursuit(Fast)), Options);
        Assert.False(verdict.Retreat);
        Assert.Equal("cornered-no-escape", verdict.Reason);
        Assert.Contains("decision=fight reason=cornered-no-escape", verdict.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ALosingBot_RetreatsFromASlowerOrEquallyFastCreature()
    {
        Assert.True(PlayerbotRiskModel.Judge(Losing(Pursuit(Unit.BaseRunSpeed * 0.9f)), Options).Retreat);
        Assert.True(PlayerbotRiskModel.Judge(Losing(Pursuit(Unit.BaseRunSpeed)), Options).Retreat);
    }

    [Fact]
    public void ASnaredBot_CannotOutrunACreatureThatWouldHaveBeenSlower()
    {
        // 4.2 yards a second (a 40% snare) against a creature of 6.3 (speed_run 0.9): the retreat is a chase the bot loses.
        var snared = new PlayerbotPursuit(55f, Unit.BaseRunSpeed * 0.6f, [new RiskPursuer(Unit.BaseRunSpeed * 0.9f, 60f)], PlayerbotEscapeKit.None);
        Assert.Equal("cornered-no-escape", PlayerbotRiskModel.Judge(Losing(snared), Options).Reason);
    }

    [Theory]
    [InlineData(PlayerbotEscapes.FrostNova)]
    [InlineData(PlayerbotEscapes.Vanish)]
    [InlineData(PlayerbotEscapes.FeignDeath)]
    [InlineData(PlayerbotEscapes.PsychicScream)]
    [InlineData(PlayerbotEscapes.Sprint)]
    [InlineData(PlayerbotEscapes.EntanglingRoots)]
    public void AReadyEscape_MakesTheRetreatWorthIt(string escape)
    {
        PlayerbotEscapeKit kit = PlayerbotEscapes.Kit([new EscapeAction(escape, EscapeTarget.Self)]);
        PlayerbotFightVerdict verdict = PlayerbotRiskModel.Judge(Losing(Pursuit(Fast, kit)), Options);
        Assert.True(verdict.Retreat, $"{escape}: {verdict}");
    }

    [Fact]
    public void ASnareOnTheNearestCreature_LetsTheBotOutrunIt_ButNotASecondOne()
    {
        PlayerbotEscapeKit kit = PlayerbotEscapes.Kit([new EscapeAction(PlayerbotEscapes.Hamstring, EscapeTarget.Victim)]);
        var one = new PlayerbotPursuit(55f, Unit.BaseRunSpeed, [new RiskPursuer(Fast, 60f)], kit);
        Assert.True(PlayerbotRiskModel.Judge(Losing(one), Options).Retreat);
        var two = new PlayerbotPursuit(55f, Unit.BaseRunSpeed, [new RiskPursuer(Fast, 10f), new RiskPursuer(Fast, 50f)], kit);
        Assert.Equal("cornered-no-escape", PlayerbotRiskModel.Judge(Losing(two), Options).Reason);
    }

    [Fact]
    public void ASpeedEscape_OnlyHelpsAgainstCreaturesItOutpaces()
    {
        // Sprint (+50%) runs 10.5 yards a second: it outpaces a speed_run 1.43 creature (10.0) but not a 1.6 one (11.2).
        PlayerbotEscapeKit sprint = PlayerbotEscapes.Kit([new EscapeAction(PlayerbotEscapes.Sprint, EscapeTarget.Self)]);
        Assert.True(PlayerbotRiskModel.Judge(Losing(Pursuit(Unit.BaseRunSpeed * 1.43f, sprint)), Options).Retreat);
        Assert.Equal("cornered-no-escape", PlayerbotRiskModel.Judge(Losing(Pursuit(Unit.BaseRunSpeed * 1.6f, sprint)), Options).Reason);
    }

    [Fact]
    public void ARetreatThatIsShort_IsTakenFromAFastCreature_WhenTheBotSurvivesTheRun()
    {
        // A 12 yard run is 1.7 seconds under 60 damage a second: 103 of 300 health.
        Assert.True(PlayerbotRiskModel.Judge(Losing(Pursuit(Fast, yards: 12f)), Options).Retreat);
    }

    [Fact]
    public void ALethalEnemy_IsStillFled()
    {
        PlayerbotFightFacts facts = Losing(Pursuit(Fast)) with { Lethal = true };
        Assert.True(PlayerbotRiskModel.Judge(facts, Options).Retreat);
    }

    [Fact]
    public void TheKit_AddsFreeSeconds_TakesTheFastestSpeedAndTheStrongestSnare()
    {
        PlayerbotEscapeKit kit = PlayerbotEscapes.Kit(
        [
            new EscapeAction(PlayerbotEscapes.FrostNova, EscapeTarget.Self), new EscapeAction(PlayerbotEscapes.Sprint, EscapeTarget.Self),
            new EscapeAction(PlayerbotEscapes.Gouge, EscapeTarget.Victim), new EscapeAction(PlayerbotEscapes.WingClip, EscapeTarget.Victim),
        ]);
        Assert.Equal(8f, kit.AllFreeSeconds);
        Assert.Equal(4f, kit.VictimFreeSeconds);
        Assert.Equal(1.5f, kit.SelfSpeed);
        Assert.Equal(0.5f, kit.VictimSpeed);
        Assert.Equal(PlayerbotEscapeKit.None, PlayerbotEscapes.Kit([]));
        Assert.Equal(PlayerbotEscapeKit.None, PlayerbotEscapes.Kit([new EscapeAction(PlayerbotEscapes.Fade, EscapeTarget.Self)]));
    }

    [Fact]
    public void EveryEscape_HasAnEffect_OrBuysNothingOnPurpose()
    {
        foreach (string name in PlayerbotEscapes.All)
        {
            PlayerbotEscapes.EscapeEffect effect = PlayerbotEscapes.EffectOf(name);
            Assert.True(effect.FreeSeconds >= 0f && effect.SelfSpeed >= 1f && effect.VictimSpeed is > 0f and <= 1f, name);
        }
    }

    [Fact]
    public void TheRetreatStats_CountOutcomesAndRefusals_AndDescribeThem()
    {
        var stats = new PlayerbotRetreatStats();
        stats.Record("safe");
        stats.Record("cornered");
        stats.Record("died");
        stats.Record("died");
        stats.Record("bogus");
        stats.Record(null);
        stats.RecordRefused();
        Assert.Equal((1, 1, 2, 1), (stats.Safe, stats.Cornered, stats.Died, stats.Refused));
        Assert.Equal(4, stats.Ended);
        Assert.Equal("Retreats: 4 ended (safe 1, cornered 1, died 2), 1 refused as cornered-no-escape.", stats.Describe());
    }
}
