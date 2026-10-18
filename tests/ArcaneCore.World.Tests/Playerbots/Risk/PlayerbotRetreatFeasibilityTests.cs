using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
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
    public void ALosingBot_RetreatsOnlyFromAClearlySlowerCreature()
    {
        // 3.5 yd/s slower: the 5 yd reach opens in 1.4 s (86 damage of a 270 budget).
        Assert.True(PlayerbotRiskModel.Judge(Losing(Pursuit(Unit.BaseRunSpeed * 0.5f)), Options).Retreat);
        // As fast as the bot it stays in contact the whole run; 0.7 yd/s slower still hits for about 7 s.
        Assert.Equal("cornered-no-escape", PlayerbotRiskModel.Judge(Losing(Pursuit(Unit.BaseRunSpeed)), Options).Reason);
        Assert.Equal("cornered-no-escape", PlayerbotRiskModel.Judge(Losing(Pursuit(Unit.BaseRunSpeed * 0.9f)), Options).Reason);
    }

    [Fact]
    public void ASnaredBot_CannotOutrunACreatureThatWouldHaveBeenSlower()
    {
        // 4.2 yards a second (a 40% snare) against a creature of 6.3 (speed_run 0.9): the retreat is a chase the bot loses.
        var snared = new PlayerbotPursuit(55f, Unit.BaseRunSpeed * 0.6f, [new RiskPursuer(Unit.BaseRunSpeed * 0.9f, 60f)], PlayerbotEscapeKit.None);
        Assert.Equal("cornered-no-escape", PlayerbotRiskModel.Judge(Losing(snared), Options).Reason);
    }

    /// <summary>A synthetic spell for the model tests: the figures are inputs here (the derivation from real spell data is <see cref="PlayerbotEscapeSpellDataTests"/>).</summary>
    private static (EscapeAction Action, SpellInfo? Spell) Ready(string name, EscapeTarget target, int durationMs = 0, AuraType aura = default, int percent = 0)
        => (new EscapeAction(name, target), new SpellInfo
        {
            Id = 1, Name = name, Duration = new SpellDuration(durationMs, 0, durationMs),
            Effects = aura == default ? [] : [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = aura, BasePoints = percent - 1, BaseDice = 1 }],
        });

    [Theory]
    [InlineData(PlayerbotEscapes.FrostNova, 8_000)]
    [InlineData(PlayerbotEscapes.PsychicScream, 8_000)]
    [InlineData(PlayerbotEscapes.EntanglingRoots, 12_000)]
    public void AHoldingEscape_MakesTheRetreatWorthIt_WhenItsDurationCoversTheRun(string escape, int durationMs)
    {
        PlayerbotEscapeKit kit = PlayerbotEscapes.Kit([Ready(escape, EscapeTarget.Self, durationMs)]);
        Assert.True(PlayerbotRiskModel.Judge(Losing(Pursuit(Fast, kit)), Options).Retreat, escape);
        // The same spell with a duration far short of the run buys nothing.
        PlayerbotEscapeKit brief = PlayerbotEscapes.Kit([Ready(escape, EscapeTarget.Self, 1_000)]);
        Assert.Equal("cornered-no-escape", PlayerbotRiskModel.Judge(Losing(Pursuit(Fast, brief)), Options).Reason);
    }

    [Theory]
    [InlineData(PlayerbotEscapes.Vanish)]
    [InlineData(PlayerbotEscapes.FeignDeath)]
    public void AnEscapeThatBreaksContact_MakesTheRetreatWorthIt(string escape)
    {
        PlayerbotEscapeKit kit = PlayerbotEscapes.Kit([Ready(escape, EscapeTarget.Self)]);
        Assert.True(PlayerbotRiskModel.Judge(Losing(Pursuit(Fast, kit)), Options).Retreat, escape);
    }

    [Fact]
    public void AnEscapeWhoseSpellDoesNotResolveOrLacksItsData_BuysNothing()
    {
        Assert.Equal(PlayerbotEscapeKit.None, PlayerbotEscapes.Kit([(new EscapeAction(PlayerbotEscapes.FrostNova, EscapeTarget.Self), null)]));
        Assert.Equal(PlayerbotEscapeKit.None, PlayerbotEscapes.Kit([Ready(PlayerbotEscapes.FrostNova, EscapeTarget.Self, durationMs: 0)]));
        Assert.Equal(PlayerbotEscapeKit.None, PlayerbotEscapes.Kit([Ready(PlayerbotEscapes.Sprint, EscapeTarget.Self)]));
        Assert.Equal(PlayerbotEscapeKit.None, PlayerbotEscapes.Kit([Ready(PlayerbotEscapes.Hamstring, EscapeTarget.Victim, 5_000)]));
        Assert.Equal(PlayerbotEscapeKit.None, PlayerbotEscapes.Kit([Ready(PlayerbotEscapes.Fade, EscapeTarget.Self, 10_000)]));
        Assert.Equal(PlayerbotEscapeKit.None, PlayerbotEscapes.Kit([]));
    }

    [Fact]
    public void ASnareOnTheNearestCreature_LetsTheBotOutrunIt_ButNotASecondOne()
    {
        PlayerbotEscapeKit kit = PlayerbotEscapes.Kit([Ready(PlayerbotEscapes.Hamstring, EscapeTarget.Victim, 5_000, AuraType.ModDecreaseSpeed, -50)]);
        Assert.Equal(0.5f, kit.VictimSpeed);
        var one = new PlayerbotPursuit(55f, Unit.BaseRunSpeed, [new RiskPursuer(Fast, 60f)], kit);
        Assert.True(PlayerbotRiskModel.Judge(Losing(one), Options).Retreat);
        var two = new PlayerbotPursuit(55f, Unit.BaseRunSpeed, [new RiskPursuer(Fast, 10f), new RiskPursuer(Fast, 50f)], kit);
        Assert.Equal("cornered-no-escape", PlayerbotRiskModel.Judge(Losing(two), Options).Reason);
    }

    [Fact]
    public void ASpeedEscape_OnlyHelpsAgainstCreaturesItOutpaces()
    {
        PlayerbotEscapeKit sprint = PlayerbotEscapes.Kit([Ready(PlayerbotEscapes.Sprint, EscapeTarget.Self, 15_000, AuraType.ModIncreaseSpeed, 50)]);
        Assert.Equal(1.5f, sprint.SelfSpeed);
        // 10.5 yards a second opens the 5 yd reach on a speed_run 1.2 creature (8.4) in 2.4 s; a 1.43 one (10.0) stays in reach the whole run.
        Assert.True(PlayerbotRiskModel.Judge(Losing(Pursuit(Unit.BaseRunSpeed * 1.2f, sprint)), Options).Retreat);
        Assert.Equal("cornered-no-escape", PlayerbotRiskModel.Judge(Losing(Pursuit(Unit.BaseRunSpeed * 1.43f, sprint)), Options).Reason);
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
            Ready(PlayerbotEscapes.FrostNova, EscapeTarget.Self, 6_000), Ready(PlayerbotEscapes.PsychicScream, EscapeTarget.Self, 3_000),
            Ready(PlayerbotEscapes.Sprint, EscapeTarget.Self, 15_000, AuraType.ModIncreaseSpeed, 40),
            Ready(PlayerbotEscapes.Dash, EscapeTarget.Self, 15_000, AuraType.ModIncreaseSpeed, 70),
            Ready(PlayerbotEscapes.Gouge, EscapeTarget.Victim, 4_000),
            Ready(PlayerbotEscapes.WingClip, EscapeTarget.Victim, 5_000, AuraType.ModDecreaseSpeed, -30),
            Ready(PlayerbotEscapes.Hamstring, EscapeTarget.Victim, 5_000, AuraType.ModDecreaseSpeed, -60),
        ]);
        Assert.Equal(9f, kit.AllFreeSeconds);
        Assert.Equal(4f, kit.VictimFreeSeconds);
        Assert.Equal(1.7f, kit.SelfSpeed, 0.001f);
        Assert.Equal(0.4f, kit.VictimSpeed, 0.001f);
    }

    [Fact]
    public void EveryEscapeName_HasAKind()
    {
        Assert.All(PlayerbotEscapes.All, name => Assert.True(Enum.IsDefined(PlayerbotEscapes.KindOf(name)), name));
        Assert.Equal(PlayerbotEscapes.EscapeKind.None, PlayerbotEscapes.KindOf("Fireball"));
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
