using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using ArcaneCore.World.Playerbots.Battlegrounds;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Battlegrounds;

/// <summary>How many bots may queue: the per-battleground cap, the per-team limit and the fill of real players' queues.</summary>
public sealed class PlayerbotBattlegroundDemandTests
{
    // Fixture template: 5 minimum and 10 maximum per team.
    private static readonly BattlegroundTemplate Wsg = PlayerbotBattlegroundBracketTests.Wsg;
    private static readonly PlayerbotBattlegroundChoice[] OneChoice = [new(BattlegroundType.WarsongGulch, 2)];

    private static BattlegroundTemplate? Templates(BattlegroundType type) => type == BattlegroundType.WarsongGulch ? Wsg : null;

    private static PlayerbotBattlegroundOptions Options(int maxBots, bool prefer = true) => new() { MaxBotsPerBattleground = maxBots, PreferFillingRealPlayerQueues = prefer };

    /// <summary>Queues bots of alternating teams (Alliance first) until nobody is admitted; returns how many got in.</summary>
    private static int Fill(PlayerbotBattlegroundDemand demand, PlayerbotBattlegroundOptions options, bool alternate = true)
    {
        int admitted = 0;
        for (int i = 0; i < 500; i++)
        {
            Team team = alternate && i % 2 == 1 ? Team.Horde : Team.Alliance;
            if (demand.Pick(OneChoice, team, Templates, options, chanceHit: true, pick: 0) is not { } choice) return admitted;
            demand.AddBotCommitted(choice.Type, choice.Bracket, team);
            admitted++;
        }

        throw new InvalidOperationException("The cap never stopped the fill.");
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    [InlineData(20, 20)]
    [InlineData(80, 20)] // limited by 2 teams x MaxPlayersPerTeam (10), not by the cap
    public void BotsWaiting_NeverExceedMaxBotsPerBattleground(int cap, int expectedAdmitted)
    {
        var demand = new PlayerbotBattlegroundDemand();
        int admitted = Fill(demand, Options(cap));
        Assert.Equal(expectedAdmitted, admitted);
        Assert.Equal(admitted, demand.BotsCommitted(BattlegroundType.WarsongGulch, 2));
        Assert.True(demand.BotsCommitted(BattlegroundType.WarsongGulch, 2) <= cap);
    }

    [Fact]
    public void BotsWaiting_CountPerBracket_NotAcrossBrackets()
    {
        var demand = new PlayerbotBattlegroundDemand();
        for (int i = 0; i < 3; i++) demand.AddBotCommitted(BattlegroundType.WarsongGulch, 2, Team.Alliance);
        Assert.False(demand.CanQueue(BattlegroundType.WarsongGulch, 2, Team.Horde, Wsg, 3));
        Assert.True(demand.CanQueue(BattlegroundType.WarsongGulch, 3, Team.Horde, Wsg, 3));
        Assert.True(demand.CanQueue(BattlegroundType.ArathiBasin, 2, Team.Horde, Wsg, 3));
    }

    [Fact]
    public void TeamCommitment_NeverExceedsMaxPlayersPerTeam()
    {
        var demand = new PlayerbotBattlegroundDemand();
        int admitted = Fill(demand, Options(80), alternate: false);
        Assert.Equal((int)Wsg.MaxPlayersPerTeam, admitted);
        Assert.Equal((int)Wsg.MaxPlayersPerTeam, demand.BotsCommitted(BattlegroundType.WarsongGulch, 2, Team.Alliance));
        Assert.False(demand.CanQueue(BattlegroundType.WarsongGulch, 2, Team.Alliance, Wsg, 80));
        Assert.True(demand.CanQueue(BattlegroundType.WarsongGulch, 2, Team.Horde, Wsg, 80));

        // Half the cap, rounded up, also bounds a team: cap 5 allows 3.
        Assert.Equal(3, PlayerbotBattlegroundDemand.TeamAllowance(10, 5));
        Assert.Equal(2, PlayerbotBattlegroundDemand.TeamAllowance(10, 4));
        Assert.Equal(1, PlayerbotBattlegroundDemand.TeamAllowance(10, 1));
        Assert.Equal(10, PlayerbotBattlegroundDemand.TeamAllowance(10, 80));
        Assert.Equal(0, PlayerbotBattlegroundDemand.TeamAllowance(10, 0));
    }

    [Fact]
    public void RunningMatchAtCap_BlocksNewQueuers()
    {
        var demand = new PlayerbotBattlegroundDemand();
        demand.BlockBracket(BattlegroundType.WarsongGulch, 2);
        Assert.False(demand.CanQueue(BattlegroundType.WarsongGulch, 2, Team.Alliance, Wsg, 20));
        Assert.Null(demand.Pick(OneChoice, Team.Alliance, Templates, Options(20), chanceHit: true, pick: 0));
        Assert.True(demand.CanQueue(BattlegroundType.WarsongGulch, 3, Team.Alliance, Wsg, 20));
    }

    [Fact]
    public void RealPlayerWaiting_NeedsMinPlayersOnBothSides()
    {
        var demand = new PlayerbotBattlegroundDemand();
        BattlegroundType type = BattlegroundType.WarsongGulch;

        // No real player: no need.
        Assert.Equal(0, demand.RealNeed(true, type, 2, Team.Alliance, Wsg));

        demand.AddRealWaiting(type, 2, Team.Alliance);
        Assert.True(demand.AnyRealWaiting);
        Assert.Equal(4, demand.RealNeed(true, type, 2, Team.Alliance, Wsg)); // 5 - 1 real
        Assert.Equal(5, demand.RealNeed(true, type, 2, Team.Horde, Wsg));
        Assert.Equal(0, demand.RealNeed(true, type, 3, Team.Horde, Wsg)); // another bracket

        // Bots count against the need, down to zero and no further.
        for (int i = 0; i < 6; i++) demand.AddBotCommitted(type, 2, Team.Alliance);
        Assert.Equal(0, demand.RealNeed(true, type, 2, Team.Alliance, Wsg));
        Assert.Equal(5, demand.RealNeed(true, type, 2, Team.Horde, Wsg));
    }

    [Fact]
    public void RealPlayerWaiting_PickSkipsTheChanceRoll_UntilBothTeamsHaveTheMinimum()
    {
        var demand = new PlayerbotBattlegroundDemand();
        demand.AddRealWaiting(BattlegroundType.WarsongGulch, 2, Team.Alliance);
        PlayerbotBattlegroundOptions options = Options(20);
        int bots = 0;
        while (demand.Pick(OneChoice, Team.Horde, Templates, options, chanceHit: false, pick: 0) is { } choice)
        {
            demand.AddBotCommitted(choice.Type, choice.Bracket, Team.Horde);
            bots++;
            Assert.True(bots <= 5, "Horde was filled past the template minimum.");
        }

        Assert.Equal((int)Wsg.MinPlayersPerTeam, bots);
    }

    [Fact]
    public void PreferFillingOff_IgnoresRealDemand()
    {
        var demand = new PlayerbotBattlegroundDemand();
        demand.AddRealWaiting(BattlegroundType.WarsongGulch, 2, Team.Alliance);
        PlayerbotBattlegroundOptions off = Options(20, prefer: false);
        Assert.Equal(0, demand.RealNeed(false, BattlegroundType.WarsongGulch, 2, Team.Horde, Wsg));
        Assert.Null(demand.Pick(OneChoice, Team.Horde, Templates, off, chanceHit: false, pick: 0));

        // The ordinary chance still queues.
        Assert.Equal(OneChoice[0], demand.Pick(OneChoice, Team.Horde, Templates, off, chanceHit: true, pick: 0));
    }

    [Fact]
    public void MaxBotsZero_QueuesNone()
    {
        var demand = new PlayerbotBattlegroundDemand();
        demand.AddRealWaiting(BattlegroundType.WarsongGulch, 2, Team.Alliance);
        Assert.False(demand.CanQueue(BattlegroundType.WarsongGulch, 2, Team.Alliance, Wsg, 0));
        Assert.Null(demand.Pick(OneChoice, Team.Horde, Templates, Options(0), chanceHit: true, pick: 0));
        Assert.True(PlayerbotBattlegroundDemand.MustDecline(0, 0));
    }

    [Fact]
    public void Disabled_PickReturnsNothing_EvenForRealDemand()
    {
        var demand = new PlayerbotBattlegroundDemand();
        demand.AddRealWaiting(BattlegroundType.WarsongGulch, 2, Team.Alliance);
        PlayerbotBattlegroundOptions options = Options(20);
        options.Enabled = false;
        Assert.Null(demand.Pick(OneChoice, Team.Horde, Templates, options, chanceHit: true, pick: 0));
    }

    [Fact]
    public void Pick_WithNoTemplateForAChoice_SkipsIt_AndUniformPickSelectsByIndex()
    {
        PlayerbotBattlegroundChoice[] two = [new(BattlegroundType.AlteracValley, 0), new(BattlegroundType.WarsongGulch, 1), new(BattlegroundType.WarsongGulch, 2)];
        var demand = new PlayerbotBattlegroundDemand();
        PlayerbotBattlegroundOptions options = Options(20);

        // AV has no template here and is skipped; the remaining two split the 0..1 pick.
        Assert.Equal(two[1], demand.Pick(two, Team.Alliance, Templates, options, chanceHit: true, pick: 0.0));
        Assert.Equal(two[1], demand.Pick(two, Team.Alliance, Templates, options, chanceHit: true, pick: 0.49));
        Assert.Equal(two[2], demand.Pick(two, Team.Alliance, Templates, options, chanceHit: true, pick: 0.5));
        Assert.Equal(two[2], demand.Pick(two, Team.Alliance, Templates, options, chanceHit: true, pick: 1.0));
        Assert.Null(demand.Pick(two, Team.Alliance, Templates, options, chanceHit: false, pick: 0.0));
        Assert.Null(demand.Pick([], Team.Alliance, Templates, options, chanceHit: true, pick: 0.0));
    }

    [Fact]
    public void Clear_ForgetsTheSnapshot()
    {
        var demand = new PlayerbotBattlegroundDemand();
        demand.AddRealWaiting(BattlegroundType.WarsongGulch, 2, Team.Alliance);
        demand.AddBotCommitted(BattlegroundType.WarsongGulch, 2, Team.Horde);
        demand.BlockBracket(BattlegroundType.WarsongGulch, 2);
        demand.Clear();
        Assert.False(demand.AnyRealWaiting);
        Assert.Equal(0, demand.BotsCommitted(BattlegroundType.WarsongGulch, 2));
        Assert.True(demand.CanQueue(BattlegroundType.WarsongGulch, 2, Team.Horde, Wsg, 20));
    }

    [Fact]
    public void MustDecline_IsTrueAtTheCap_NotBelow()
    {
        Assert.False(PlayerbotBattlegroundDemand.MustDecline(19, 20));
        Assert.True(PlayerbotBattlegroundDemand.MustDecline(20, 20));
        Assert.True(PlayerbotBattlegroundDemand.MustDecline(21, 20));
    }

    [Fact]
    public void ChancePerThink_MatchesPerMinuteRate()
    {
        // A scan of exactly one minute is the per-minute chance itself.
        Assert.Equal(0.05f, PlayerbotBattlegroundDemand.ChancePerThink(0.05f, 60_000), 5);

        // Two half-minute scans compound to the per-minute chance: 1 - (1 - c)^2 == p, so the rate does not depend on the scan interval.
        float half = PlayerbotBattlegroundDemand.ChancePerThink(0.05f, 30_000);
        Assert.Equal(0.05, 1d - Math.Pow(1d - half, 2), 5);
        Assert.Equal(1 - Math.Sqrt(0.95), half, 5);

        // Boundaries: never at 0, always at 1, nothing without elapsed time or from a non-finite rate.
        Assert.Equal(0f, PlayerbotBattlegroundDemand.ChancePerThink(0f, 60_000));
        Assert.Equal(1f, PlayerbotBattlegroundDemand.ChancePerThink(1f, 1));
        Assert.Equal(0f, PlayerbotBattlegroundDemand.ChancePerThink(0.5f, 0));
        Assert.Equal(0f, PlayerbotBattlegroundDemand.ChancePerThink(float.NaN, 60_000));
        Assert.Equal(0f, PlayerbotBattlegroundDemand.ChancePerThink(-0.1f, 60_000));
    }

    [Fact]
    public void BotWaitingWithNoRunningMatch_FillsBothSidesUpToMinPlayersPerTeam_SoTheMatchPops()
    {
        var demand = new PlayerbotBattlegroundDemand();
        BattlegroundType type = BattlegroundType.WarsongGulch;
        PlayerbotBattlegroundOptions options = Options(20);

        // One Alliance bot initiates a queue in bracket 2 where no match is running yet.
        demand.AddBotCommitted(type, 2, Team.Alliance);
        Assert.Equal(4, demand.RealNeed(true, type, 2, Team.Alliance, Wsg));
        Assert.Equal(5, demand.RealNeed(true, type, 2, Team.Horde, Wsg));

        // Pick without a random chance hit recruits bots on both sides up to MinPlayersPerTeam (5 each, 10 total).
        for (int i = 0; i < 9; i++)
        {
            Team team = demand.BotsCommitted(type, 2, Team.Alliance) < (int)Wsg.MinPlayersPerTeam ? Team.Alliance : Team.Horde;
            PlayerbotBattlegroundChoice? picked = demand.Pick(OneChoice, team, Templates, options, chanceHit: false, pick: 0);
            Assert.NotNull(picked);
            demand.AddBotCommitted(picked!.Value.Type, picked.Value.Bracket, team);
        }

        Assert.Equal((int)Wsg.MinPlayersPerTeam, demand.BotsCommitted(type, 2, Team.Alliance));
        Assert.Equal((int)Wsg.MinPlayersPerTeam, demand.BotsCommitted(type, 2, Team.Horde));
        Assert.Null(demand.Pick(OneChoice, Team.Alliance, Templates, options, chanceHit: false, pick: 0));
        Assert.Null(demand.Pick(OneChoice, Team.Horde, Templates, options, chanceHit: false, pick: 0));
    }
}
