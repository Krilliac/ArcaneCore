using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.OutdoorPvP;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Diagnostics;
using Xunit;
using static ArcaneCore.Game.Tests.Battlegrounds.BgTestData;

namespace ArcaneCore.Game.Tests.Battlegrounds;

/// <summary>
/// Pins the team to race and team to slot mappings, and the battleground start-location finiteness check, that the
/// copies in the battleground, capture point and social code must keep giving after they are unified.
/// </summary>
public sealed class TeamRulesCharacterisationTests
{
    [Theory]
    [InlineData(Team.Alliance, 0)]
    [InlineData(Team.Horde, 1)]
    public void TheBattlegroundTeamIndexIsAllianceZeroHordeOne(Team team, int index)
    {
        Assert.Equal(index, BattlegroundConstants.TeamIndex(team));
    }

    [Theory]
    [InlineData(Team.Alliance, 0)]
    [InlineData(Team.Horde, 1)]
    public void TheCapturePointTeamIndexIsAllianceZeroHordeOne(Team team, int index)
    {
        Assert.Equal(index, CapturePoint.TeamIndex(team));
    }

    [Theory]
    [InlineData(Race.Human, Team.Alliance)]
    [InlineData(Race.Orc, Team.Horde)]
    [InlineData(Race.Dwarf, Team.Alliance)]
    [InlineData(Race.NightElf, Team.Alliance)]
    [InlineData(Race.Undead, Team.Horde)]
    [InlineData(Race.Tauren, Team.Horde)]
    [InlineData(Race.Gnome, Team.Alliance)]
    [InlineData(Race.Troll, Team.Horde)]
    public void ACharacterInfoTeamFollowsItsRace(Race race, Team expected)
    {
        var info = new CharacterInfo(1, 1, "Someone", race, Class.Warrior);

        Assert.Equal(expected, info.Team);
    }

    [Theory]
    [InlineData(float.NaN, 0f, 0f, 0f)]
    [InlineData(0f, float.PositiveInfinity, 0f, 0f)]
    [InlineData(0f, 0f, float.NegativeInfinity, 0f)]
    [InlineData(0f, 0f, 0f, float.NaN)]
    public void AManagerRefusesQuietlyATemplateWhoseAllianceStartIsNotFinite(float x, float y, float z, float o)
    {
        using InvariantCapture capture = Invariant.Capture();
        var mgr = NewBareManager();

        mgr.RegisterTemplate(WsgTemplate() with { AllianceStart = new BattlegroundStartLocation(x, y, z, o) });

        Assert.Null(mgr.TemplateOf(BattlegroundType.WarsongGulch));
        InvariantFailureEvent failure = Assert.Single(capture.Failures);
        Assert.Equal("battleground template WarsongGulch has non-finite start coordinates", failure.Message);
    }

    [Fact]
    public void AManagerChecksTheHordeStartToo_AndAFiniteTemplateIsRegistered()
    {
        using InvariantCapture capture = Invariant.Capture();
        var mgr = NewBareManager();

        mgr.RegisterTemplate(WsgTemplate() with { HordeStart = new BattlegroundStartLocation(1f, 2f, 3f, float.NaN) });
        Assert.Null(mgr.TemplateOf(BattlegroundType.WarsongGulch));
        Assert.Single(capture.Failures);

        mgr.RegisterTemplate(WsgTemplate() with
        {
            AllianceStart = new BattlegroundStartLocation(float.MaxValue, float.MinValue, 0f, -1f),
            HordeStart = new BattlegroundStartLocation(1f, 2f, 3f, 4f),
        });
        Assert.NotNull(mgr.TemplateOf(BattlegroundType.WarsongGulch));
        Assert.Single(capture.Failures);
    }

    [Theory]
    [InlineData(true, float.NaN, 0f, 0f, 0f)]
    [InlineData(true, 0f, 0f, 0f, float.PositiveInfinity)]
    [InlineData(false, 0f, float.NegativeInfinity, 0f, 0f)]
    [InlineData(false, 0f, 0f, float.NaN, 0f)]
    public void AMatchWithANonFiniteStartThrowsAnArgumentExceptionNamingTheTemplate(bool alliance, float x, float y, float z, float o)
    {
        using InvariantCapture capture = Invariant.Capture();
        var bad = new BattlegroundStartLocation(x, y, z, o);
        BattlegroundTemplate template = alliance ? WsgTemplate() with { AllianceStart = bad } : WsgTemplate() with { HordeStart = bad };
        var ports = new RecordingPorts();

        ArgumentException ex = Assert.Throws<ArgumentException>(() => new WarsongGulch(template, 5, 101, 1, new BattlegroundOptions(), ports.ToPorts(new RecordingHost())));

        Assert.Equal("template", ex.ParamName);
        Assert.StartsWith("battleground WarsongGulch template has non-finite start coordinates", ex.Message, StringComparison.Ordinal);
        InvariantFailureEvent failure = Assert.Single(capture.Failures);
        Assert.Equal("battleground WarsongGulch template has non-finite start coordinates", failure.Message);
    }

    [Fact]
    public void AMatchWithFiniteStartsIsBuilt()
    {
        using InvariantCapture capture = Invariant.Capture();

        var (bg, _, _) = NewWsg();

        Assert.Equal(BattlegroundType.WarsongGulch, bg.Type);
        Assert.Empty(capture.Failures);
    }

    private static BattlegroundManager NewBareManager()
    {
        var ports = new RecordingPorts();
        return new BattlegroundManager(new BattlegroundOptions(), new RecordingManagerHost(), ports.ToPorts(new RecordingHost()));
    }
}
