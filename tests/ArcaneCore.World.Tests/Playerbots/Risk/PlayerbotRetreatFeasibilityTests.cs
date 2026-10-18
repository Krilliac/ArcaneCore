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
    public void AShortRun_EarlyInTheFight_IsHitUntilThePursuersLeashClockRunsOut()
    {
        // 14 yards from safety at 7 yd/s is 2 s under a fast creature's blows (120 of a 270 budget): a retreat.
        Assert.True(PlayerbotRiskModel.Judge(Losing(Pursuit(Fast, yards: 14f)), Options).Retreat);
        // Six seconds into the fight the creature cannot leash for another 7 s (vmangos IsOutOfThreatArea, 12 whole seconds):
        // 7 s at 60 a second is 420, more than the bot has.
        var early = new PlayerbotPursuit(14f, Unit.BaseRunSpeed, [new RiskPursuer(Fast, 60f)], PlayerbotEscapeKit.None, MinContactSeconds: 7f);
        Assert.Equal("cornered-no-escape", PlayerbotRiskModel.Judge(Losing(early), Options).Reason);
        Assert.Equal(420f, PlayerbotRiskModel.RetreatDamage(early, 60f), 3);
        // A slower creature is left behind whatever its clock says.
        var slow = new PlayerbotPursuit(14f, Unit.BaseRunSpeed, [new RiskPursuer(Unit.BaseRunSpeed * 0.5f, 60f)], PlayerbotEscapeKit.None, MinContactSeconds: 7f);
        Assert.True(PlayerbotRiskModel.Judge(Losing(slow), Options).Retreat);
    }

    [Fact]
    public void BlowsFromBehind_LandMoreOften_ByTheBotsAvoidance()
    {
        Assert.Equal(1f, PlayerbotRiskModel.FleeDamageFactor(0f, 0f, 0f), 3);
        // A shield warrior with 10% dodge, 8% parry and 7% block: a quarter of the blows it turned aside now land (x1.33).
        Assert.Equal(1f / 0.75f, PlayerbotRiskModel.FleeDamageFactor(10f, 8f, 7f), 3);
        Assert.Equal(2.5f, PlayerbotRiskModel.FleeDamageFactor(50f, 50f, 50f), 3); // held to 60% avoidance
        Assert.Equal(1f, PlayerbotRiskModel.FleeDamageFactor(float.NaN, -5f, 0f), 3);
        // 14 yards (2 s, 120 damage) becomes 160 from behind at x1.33.
        var shielded = new PlayerbotPursuit(14f, Unit.BaseRunSpeed, [new RiskPursuer(Fast, 60f)], PlayerbotEscapeKit.None, FleeDamageFactor: 1f / 0.75f);
        Assert.Equal(160f, PlayerbotRiskModel.RetreatDamage(shielded, 60f), 2);
    }

    [Fact]
    public void ASnaredBot_CannotOutrunACreatureThatWouldHaveBeenSlower()
    {
        // 4.2 yards a second (a 40% snare) against a creature of 6.3 (speed_run 0.9): the retreat is a chase the bot loses.
        var snared = new PlayerbotPursuit(55f, Unit.BaseRunSpeed * 0.6f, [new RiskPursuer(Unit.BaseRunSpeed * 0.9f, 60f)], PlayerbotEscapeKit.None);
        Assert.Equal("cornered-no-escape", PlayerbotRiskModel.Judge(Losing(snared), Options).Reason);
    }

    /// <summary>A synthetic spell for the model tests: the figures are inputs here (the derivation from real spell data is <see cref="PlayerbotEscapeSpellDataTests"/>).</summary>
    private static (EscapeAction Action, SpellInfo? Spell) Ready(string name, EscapeTarget target, int durationMs = 0, AuraType aura = default, int percent = 0, int castTimeMs = 0)
        => (new EscapeAction(name, target), new SpellInfo
        {
            Id = 1, Name = name, CastTime = new SpellCastTime(castTimeMs, 0, castTimeMs), Duration = new SpellDuration(durationMs, 0, durationMs),
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

    [Fact]
    public void AHoldingEscape_CountsItsCastTimeAndTheGapOpenedAgainstSoftLeashExtension()
    {
        // 8 s Frost Nova opens 56 yd at 7 yd/s; an 8 yd/s pursuer needs (56 - 5) / 1 = 51 s to close back into melee,
        // so even a 20.86 s soft-leash clock (7.86 s to 50 yd + 13 s extension) only hits after the hold + gap.
        PlayerbotEscapeKit nova = PlayerbotEscapes.Kit([Ready(PlayerbotEscapes.FrostNova, EscapeTarget.Self, 8_000)]);
        var softLeashWithNova = new PlayerbotPursuit(55f, Unit.BaseRunSpeed, [new RiskPursuer(Fast, 60f)], nova, MinContactSeconds: 20.86f);
        Assert.Equal(0f, PlayerbotRiskModel.RetreatDamage(softLeashWithNova, 60f), 3);

        // A 1.5 s cast-time root takes 1.5 s of blows while standing still (90 damage at 60 dps), then holds for 12 s.
        PlayerbotEscapeKit roots = PlayerbotEscapes.Kit([Ready(PlayerbotEscapes.EntanglingRoots, EscapeTarget.Victim, 12_000, castTimeMs: 1_500)]);
        Assert.Equal(1.5f, roots.CastSeconds, 3);
        var softLeashWithRoots = new PlayerbotPursuit(55f, Unit.BaseRunSpeed, [new RiskPursuer(Fast, 60f)], roots, MinContactSeconds: 20.86f);
        Assert.Equal(90f, PlayerbotRiskModel.RetreatDamage(softLeashWithRoots, 60f), 3);
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
        stats.Record("safe", 0.1f);
        stats.Record("cornered");
        stats.Record("died", 0.45f);
        stats.Record("died", 1.2f);
        stats.Record("bogus", 0.1f);
        stats.Record(null);
        stats.RecordRefused();
        stats.RecordRefusedEnd(died: true);
        stats.RecordRefusedEnd(died: false);
        stats.RecordRefusedEnd(died: false);
        Assert.Equal((1, 1, 2, 1), (stats.Safe, stats.Cornered, stats.Died, stats.Refused));
        Assert.Equal(4, stats.Ended);
        Assert.Equal((1L, 0L), stats.Band(0));
        Assert.Equal((1L, 1L), stats.Band(1));
        Assert.Equal((1L, 1L), stats.Band(2));
        Assert.Equal((1L, 0L), stats.Band(3));
        Assert.Equal("Retreats: 4 ended (safe 1, cornered 1, died 2), 1 refused as cornered-no-escape (fights fought on: died 1, survived 2). "
            + "Died by predicted loss: <0.3 0/1, 0.3-0.6 1/1, >=0.6 1/1, not weighed 0/1.", stats.Describe());
    }
}
