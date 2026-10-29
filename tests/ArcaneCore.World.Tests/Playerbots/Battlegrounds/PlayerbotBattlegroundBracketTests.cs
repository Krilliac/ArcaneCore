using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using ArcaneCore.World.Playerbots.Battlegrounds;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Battlegrounds;

/// <summary>
/// Which (type, bracket) a bot of a level may queue for. The bracket is (level - minLevel) / 10, -1 below the minimum (vmangos
/// Player::GetBattleGroundBracketIdFromLevel, Player.cpp:19455-19468). The template numbers are test fixtures (the server reads them from
/// battleground_template); the expected brackets follow from that formula.
/// </summary>
public sealed class PlayerbotBattlegroundBracketTests
{
    internal static BattlegroundTemplate Template(BattlegroundType type, uint minLevel, uint maxLevel, uint minPerTeam = 5, uint maxPerTeam = 10) => new()
    {
        Type = type, MapId = 1, Name = type.ToString(), MinPlayersPerTeam = minPerTeam, MaxPlayersPerTeam = maxPerTeam, MinLevel = minLevel, MaxLevel = maxLevel,
    };

    internal static readonly BattlegroundTemplate Wsg = Template(BattlegroundType.WarsongGulch, 10, 60);
    internal static readonly BattlegroundTemplate Ab = Template(BattlegroundType.ArathiBasin, 20, 60, 8, 15);
    internal static readonly BattlegroundTemplate Av = Template(BattlegroundType.AlteracValley, 51, 60, 10, 40);

    internal static BattlegroundTemplate? Templates(BattlegroundType type) => type switch
    {
        BattlegroundType.WarsongGulch => Wsg,
        BattlegroundType.ArathiBasin => Ab,
        BattlegroundType.AlteracValley => Av,
        _ => null,
    };

    [Theory]
    [InlineData(0u)]
    [InlineData(9u)]
    public void LevelBelowTemplateMin_IsNotEligible(uint level)
    {
        Assert.Equal(PlayerbotBattlegroundRefusal.LevelOutOfRange, PlayerbotBattlegroundBrackets.Check(BattlegroundType.WarsongGulch, level, Wsg, out int bracket));
        Assert.Equal(-1, bracket);
        Assert.Empty(PlayerbotBattlegroundBrackets.Eligible(level, Templates, _ => true, _ => true));
    }

    [Theory]
    [InlineData(10u, 0)]
    [InlineData(19u, 0)]
    [InlineData(20u, 1)]
    [InlineData(39u, 2)]
    [InlineData(40u, 3)]
    [InlineData(59u, 4)]
    [InlineData(60u, 5)]
    public void Wsg_Bracket_FollowsBracketOfLevel(uint level, int expected)
    {
        Assert.Equal(PlayerbotBattlegroundRefusal.None, PlayerbotBattlegroundBrackets.Check(BattlegroundType.WarsongGulch, level, Wsg, out int bracket));
        Assert.Equal(expected, bracket);
        Assert.Equal(BattlegroundConstants.BracketOfLevel(level, Wsg.MinLevel), bracket);

        PlayerbotBattlegroundChoice wsg = Assert.Single(PlayerbotBattlegroundBrackets.Eligible(level, Templates, _ => true, _ => true),
            c => c.Type == BattlegroundType.WarsongGulch);
        Assert.Equal(expected, wsg.Bracket);
    }

    [Fact]
    public void Wsg_AboveTemplateMax_IsOutOfRange()
        => Assert.Equal(PlayerbotBattlegroundRefusal.LevelOutOfRange, PlayerbotBattlegroundBrackets.Check(BattlegroundType.WarsongGulch, 61, Wsg, out _));

    [Fact]
    public void Av_IsAccepted_FromLevel51To60_WithATemplateAndContent()
    {
        for (uint level = 1; level <= 50; level++)
        {
            Assert.Equal(PlayerbotBattlegroundRefusal.LevelOutOfRange, PlayerbotBattlegroundBrackets.Check(BattlegroundType.AlteracValley, level, Av, out int bracket));
            Assert.Equal(-1, bracket);
        }

        for (uint level = 51; level <= 60; level++)
        {
            Assert.Equal(PlayerbotBattlegroundRefusal.None, PlayerbotBattlegroundBrackets.Check(BattlegroundType.AlteracValley, level, Av, out int bracket));
            Assert.Equal(0, bracket);
        }

        var refused = new List<(BattlegroundType, PlayerbotBattlegroundRefusal)>();
        List<PlayerbotBattlegroundChoice> choices = PlayerbotBattlegroundBrackets.Eligible(55, Templates, _ => true, _ => true, (t, why) => refused.Add((t, why)));
        Assert.Contains(new PlayerbotBattlegroundChoice(BattlegroundType.AlteracValley, 0), choices);
        Assert.Empty(refused);
        Assert.True(PlayerbotBattlegroundBrackets.Supported(BattlegroundType.AlteracValley));
        Assert.True(PlayerbotBattlegroundBrackets.Supported(BattlegroundType.WarsongGulch));
        Assert.True(PlayerbotBattlegroundBrackets.Supported(BattlegroundType.ArathiBasin));
        Assert.False(PlayerbotBattlegroundBrackets.Supported(BattlegroundType.None));
        Assert.Equal(PlayerbotBattlegroundRefusal.Unsupported, PlayerbotBattlegroundBrackets.Check(BattlegroundType.None, 60, Av, out _));
    }

    [Fact]
    public void MissingTemplate_FailsClosed()
    {
        Assert.Equal(PlayerbotBattlegroundRefusal.NoTemplate, PlayerbotBattlegroundBrackets.Check(BattlegroundType.WarsongGulch, 30, null, out int bracket));
        Assert.Equal(-1, bracket);

        // A template of another type is no template for this one.
        Assert.Equal(PlayerbotBattlegroundRefusal.NoTemplate, PlayerbotBattlegroundBrackets.Check(BattlegroundType.WarsongGulch, 30, Ab, out _));

        var refused = new List<(BattlegroundType, PlayerbotBattlegroundRefusal)>();
        List<PlayerbotBattlegroundChoice> choices = PlayerbotBattlegroundBrackets.Eligible(60, t => t == BattlegroundType.WarsongGulch ? null : Templates(t),
            _ => true, _ => true, (t, why) => refused.Add((t, why)));
        Assert.DoesNotContain(choices, c => c.Type == BattlegroundType.WarsongGulch);
        Assert.Contains((BattlegroundType.WarsongGulch, PlayerbotBattlegroundRefusal.NoTemplate), refused);
        Assert.Equal([new PlayerbotBattlegroundChoice(BattlegroundType.ArathiBasin, 4), new PlayerbotBattlegroundChoice(BattlegroundType.AlteracValley, 0)], choices);
    }

    [Fact]
    public void MissingObjectiveContent_FailsClosed()
    {
        var refused = new List<(BattlegroundType, PlayerbotBattlegroundRefusal)>();
        List<PlayerbotBattlegroundChoice> choices = PlayerbotBattlegroundBrackets.Eligible(60, Templates, t => t != BattlegroundType.ArathiBasin, _ => true,
            (t, why) => refused.Add((t, why)));
        Assert.DoesNotContain(choices, c => c.Type == BattlegroundType.ArathiBasin);
        Assert.Equal([(BattlegroundType.ArathiBasin, PlayerbotBattlegroundRefusal.MissingContent)], refused);
    }

    [Fact]
    public void NoFriendlyBattlemasterInRange_FailsClosed()
    {
        var refused = new List<(BattlegroundType, PlayerbotBattlegroundRefusal)>();
        List<PlayerbotBattlegroundChoice> choices = PlayerbotBattlegroundBrackets.Eligible(60, Templates, _ => true, t => t == BattlegroundType.ArathiBasin,
            (t, why) => refused.Add((t, why)));
        Assert.Equal([new PlayerbotBattlegroundChoice(BattlegroundType.ArathiBasin, 4)], choices);
        Assert.Equal([(BattlegroundType.WarsongGulch, PlayerbotBattlegroundRefusal.NoBattlemaster), (BattlegroundType.AlteracValley, PlayerbotBattlegroundRefusal.NoBattlemaster)], refused);

        Assert.Empty(PlayerbotBattlegroundBrackets.Eligible(60, Templates, _ => true, _ => false));
    }

    [Fact]
    public void RealPlayerDemand_PicksTheirTypeAndBracketFirst()
    {
        // Level 45 is eligible for WSG (bracket 3) and AB (bracket 2); a real player waits in AB bracket 2.
        List<PlayerbotBattlegroundChoice> eligible = PlayerbotBattlegroundBrackets.Eligible(45, Templates, _ => true, _ => true);
        Assert.Equal([new(BattlegroundType.WarsongGulch, 3), new(BattlegroundType.ArathiBasin, 2)], eligible);

        var demand = new PlayerbotBattlegroundDemand();
        demand.AddRealWaiting(BattlegroundType.ArathiBasin, 2, Team.Alliance);
        var options = new PlayerbotBattlegroundOptions { PreferFillingRealPlayerQueues = true };

        // No chance roll hit, and still the real player's queue is chosen, by either team.
        Assert.Equal(new PlayerbotBattlegroundChoice(BattlegroundType.ArathiBasin, 2), demand.Pick(eligible, Team.Horde, Templates, options, chanceHit: false, pick: 0));
        Assert.Equal(new PlayerbotBattlegroundChoice(BattlegroundType.ArathiBasin, 2), demand.Pick(eligible, Team.Alliance, Templates, options, chanceHit: true, pick: 0));

        // A bracket the real player does not wait in is not picked without a roll.
        var other = new PlayerbotBattlegroundDemand();
        other.AddRealWaiting(BattlegroundType.ArathiBasin, 3, Team.Alliance);
        Assert.Null(other.Pick(eligible, Team.Horde, Templates, options, chanceHit: false, pick: 0));
    }
}
