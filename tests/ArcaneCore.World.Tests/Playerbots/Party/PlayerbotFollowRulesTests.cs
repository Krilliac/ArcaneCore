using System.Numerics;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Party;
using ArcaneCore.World.Reload;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Party;

/// <summary>
/// The catch-up rules of a follower apart from the world (<see cref="PlayerbotFollowRules"/>, <see cref="PlayerbotFollowProgress"/>)
/// and their configuration (<see cref="PlayerbotFollowOptions"/>, <c>World:Playerbots:Follow</c>). The numbers of the Deadmines cases
/// are the ones measured on the real mmaps (<see cref="PlayerbotFollowDeadminesTests"/>).
/// </summary>
public sealed class PlayerbotFollowRulesTests
{
    private static readonly PlayerbotFollowOptions Defaults = new();

    /// <summary>The ledge: 89.5 yards of way for 16.4 straight, 11.9 yards above, an elite on the way.</summary>
    private static PlayerbotRouteFacts Ledge(PlayerbotCatchUpMode mode) => new(mode, MayTeleport: true, Straight: 16.4f, Across: 11.1f,
        Above: 11.9f, RouteFound: true, RouteYards: 89.5f, Dangers: 1, DropAvailable: false, Stalls: 0);

    /// <summary>An open field: 31 yards of way for 30 straight, nothing hostile.</summary>
    private static PlayerbotRouteFacts Field(PlayerbotCatchUpMode mode) => new(mode, MayTeleport: true, Straight: 30f, Across: 30f,
        Above: 0.5f, RouteFound: true, RouteYards: 31f, Dangers: 0, DropAvailable: false, Stalls: 0);

    [Theory]
    [InlineData(PlayerbotCatchUpMode.Teleport)]
    [InlineData(PlayerbotCatchUpMode.FightThrough)]
    [InlineData(PlayerbotCatchUpMode.Auto)]
    public void AnOpenFieldWay_IsWalked_InEveryMode(PlayerbotCatchUpMode mode)
        => Assert.Equal((PlayerbotFollowStep.Walk, PlayerbotCatchUpReason.None), PlayerbotFollowRules.Judge(Field(mode), Defaults));

    [Fact]
    public void Teleport_TheLedgeWayPastAnElite_IsNoWay_AndTheDropIsPreferredToTheTeleport()
    {
        Assert.Equal((PlayerbotFollowStep.Teleport, PlayerbotCatchUpReason.Danger), PlayerbotFollowRules.Judge(Ledge(PlayerbotCatchUpMode.Teleport), Defaults));
        Assert.Equal((PlayerbotFollowStep.Teleport, PlayerbotCatchUpReason.Detour),
            PlayerbotFollowRules.Judge(Ledge(PlayerbotCatchUpMode.Teleport) with { Dangers = 0 }, Defaults));
        Assert.Equal((PlayerbotFollowStep.Drop, PlayerbotCatchUpReason.Danger),
            PlayerbotFollowRules.Judge(Ledge(PlayerbotCatchUpMode.Teleport) with { DropAvailable = true }, Defaults));
    }

    [Fact]
    public void FightThrough_NeverTeleports_ItDropsOrWalksTheWayItHas()
    {
        Assert.Equal((PlayerbotFollowStep.Walk, PlayerbotCatchUpReason.Danger), PlayerbotFollowRules.Judge(Ledge(PlayerbotCatchUpMode.FightThrough), Defaults));
        Assert.Equal((PlayerbotFollowStep.Drop, PlayerbotCatchUpReason.Danger),
            PlayerbotFollowRules.Judge(Ledge(PlayerbotCatchUpMode.FightThrough) with { DropAvailable = true }, Defaults));
        Assert.Equal((PlayerbotFollowStep.Wait, PlayerbotCatchUpReason.NoRoute),
            PlayerbotFollowRules.Judge(Ledge(PlayerbotCatchUpMode.FightThrough) with { RouteFound = false, RouteYards = float.NaN }, Defaults));
        Assert.Equal((PlayerbotFollowStep.Walk, PlayerbotCatchUpReason.Stalled),
            PlayerbotFollowRules.Judge(Field(PlayerbotCatchUpMode.FightThrough) with { Stalls = 5 }, Defaults));
    }

    [Fact]
    public void Auto_TeleportsOnlyWhenTheMeshHasNoWay_OrTheFollowerStalledTwice()
    {
        Assert.Equal((PlayerbotFollowStep.Walk, PlayerbotCatchUpReason.Danger), PlayerbotFollowRules.Judge(Ledge(PlayerbotCatchUpMode.Auto), Defaults));
        Assert.Equal((PlayerbotFollowStep.Teleport, PlayerbotCatchUpReason.NoRoute),
            PlayerbotFollowRules.Judge(Field(PlayerbotCatchUpMode.Auto) with { RouteFound = false, RouteYards = float.NaN }, Defaults));
        Assert.Equal((PlayerbotFollowStep.Walk, PlayerbotCatchUpReason.None),
            PlayerbotFollowRules.Judge(Field(PlayerbotCatchUpMode.Auto) with { Stalls = 1 }, Defaults));
        Assert.Equal((PlayerbotFollowStep.Teleport, PlayerbotCatchUpReason.Stalled),
            PlayerbotFollowRules.Judge(Field(PlayerbotCatchUpMode.Auto) with { Stalls = 2 }, Defaults));
    }

    /// <summary>A bot that may not teleport now (in combat beside its master) walks the way it has, whatever the way.</summary>
    [Fact]
    public void Teleport_InCombat_WalksTheWayItHasOrWaits()
    {
        Assert.Equal((PlayerbotFollowStep.Walk, PlayerbotCatchUpReason.Danger),
            PlayerbotFollowRules.Judge(Ledge(PlayerbotCatchUpMode.Teleport) with { MayTeleport = false }, Defaults));
        Assert.Equal((PlayerbotFollowStep.Wait, PlayerbotCatchUpReason.NoRoute),
            PlayerbotFollowRules.Judge(Field(PlayerbotCatchUpMode.Teleport) with { MayTeleport = false, RouteFound = false }, Defaults));
    }

    /// <summary>mangoszero's vertical rule: within 50 yards across, 30 or more above or below, the follower teleports (or drops).</summary>
    [Fact]
    public void Teleport_TheVerticalRule()
    {
        Assert.Equal((PlayerbotFollowStep.Teleport, PlayerbotCatchUpReason.Vertical),
            PlayerbotFollowRules.Judge(Field(PlayerbotCatchUpMode.Teleport) with { Across = 20f, Above = -35f }, Defaults));
        Assert.Equal((PlayerbotFollowStep.Walk, PlayerbotCatchUpReason.None),
            PlayerbotFollowRules.Judge(Field(PlayerbotCatchUpMode.Teleport) with { Across = 60f, Above = 35f }, Defaults));
        Assert.True(PlayerbotFollowRules.IsVertical(50f, 30f));
        Assert.False(PlayerbotFollowRules.IsVertical(50.1f, 30f));
        Assert.False(PlayerbotFollowRules.IsVertical(10f, 29.9f));
    }

    /// <summary>The thresholds are the configuration's: a looser ratio or a larger detour allowance walks the ledge way.</summary>
    [Fact]
    public void TheDetourThresholds_AreConfigurable()
    {
        PlayerbotRouteFacts ledge = Ledge(PlayerbotCatchUpMode.Teleport) with { Dangers = 0 };
        Assert.True(PlayerbotFollowRules.IsDetour(16.4f, 89.5f, Defaults)); // 5.5 times, 73 yards longer
        Assert.Equal(PlayerbotFollowStep.Walk, PlayerbotFollowRules.Judge(ledge, new PlayerbotFollowOptions { MaxPathRatio = 6f }).Step);
        Assert.Equal(PlayerbotFollowStep.Walk, PlayerbotFollowRules.Judge(ledge, new PlayerbotFollowOptions { MinDetourYards = 80f }).Step);
        Assert.False(PlayerbotFollowRules.IsDetour(5f, 30f, Defaults)); // 6 times, but only 25 yards longer: a short way round a wall
    }

    // --- loop detection ------------------------------------------------------------------------------------------------

    [Fact]
    public void Progress_NoProgressTowardsALeaderStandingStill_CountsAStallPerWindow()
    {
        var progress = new PlayerbotFollowProgress();
        var leader = new Vector3(0, 0, 0);
        Assert.False(progress.Observe(0, 80f, leader, 8));
        Assert.False(progress.Observe(4_000, 80.5f, leader, 8)); // running in a circle: the way left does not shrink
        Assert.True(progress.Observe(8_000, 79.5f, leader, 8));
        Assert.Equal(1, progress.Stalls);
        Assert.False(progress.Observe(12_000, 80f, leader, 8));
        Assert.True(progress.Observe(16_000, 80f, leader, 8));
        Assert.Equal(2, progress.Stalls);
    }

    [Fact]
    public void Progress_GettingCloser_OrALeaderOnTheMove_IsNoStall()
    {
        var progress = new PlayerbotFollowProgress();
        Assert.False(progress.Observe(0, 80f, Vector3.Zero, 8));
        for (int second = 1; second <= 20; second++) Assert.False(progress.Observe(second * 1_000, 80f - (second * 3f), Vector3.Zero, 8));
        Assert.Equal(0, progress.Stalls);

        var trailing = new PlayerbotFollowProgress(); // a steady 10 yards behind a leader who runs: never a stall
        for (int second = 0; second <= 30; second++) Assert.False(trailing.Observe(second * 1_000, 10f, new Vector3(second * 7f, 0, 0), 8));
        Assert.Equal(0, trailing.Stalls);
    }

    // --- configuration -------------------------------------------------------------------------------------------------

    [Fact]
    public void TheDefaults_AreTheReferenceBehaviour()
    {
        var options = new PlayerbotFollowOptions();
        Assert.Equal(PlayerbotCatchUpMode.Teleport, options.CatchUpMode);
        Assert.Equal(PlayerbotParty.TeleportDistance, options.TeleportDistanceYards);
        Assert.Equal(PlayerbotParty.PartyAssistRange, options.LeaderCombatRangeYards);
        options.Validate();
    }

    [Fact]
    public void TheKeys_BindFromWorldPlayerbotsFollow()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["World:Playerbots:Follow:CatchUpMode"] = "FightThrough",
            ["World:Playerbots:Follow:TeleportDistanceYards"] = "150",
            ["World:Playerbots:Follow:MaxPathRatio"] = "4.5",
            ["World:Playerbots:Follow:MinDetourYards"] = "25",
            ["World:Playerbots:Follow:DangerLevelMargin"] = "5",
            ["World:Playerbots:Follow:LeaderCombatRangeYards"] = "70",
            ["World:Playerbots:Follow:NoProgressSeconds"] = "12",
            ["World:Playerbots:Follow:MaxFallHealthPercent"] = "0",
        }).Build();
        PlayerbotFollowOptions follow = PlayerbotOptions.Bind(configuration).Follow;
        Assert.Equal(PlayerbotCatchUpMode.FightThrough, follow.CatchUpMode);
        Assert.Equal(150f, follow.TeleportDistanceYards);
        Assert.Equal(4.5f, follow.MaxPathRatio);
        Assert.Equal(25f, follow.MinDetourYards);
        Assert.Equal(5, follow.DangerLevelMargin);
        Assert.Equal(70f, follow.LeaderCombatRangeYards);
        Assert.Equal(12, follow.NoProgressSeconds);
        Assert.Equal(0, follow.MaxFallHealthPercent);
    }

    [Theory]
    [InlineData(nameof(PlayerbotFollowOptions.TeleportDistanceYards), 5f)]
    [InlineData(nameof(PlayerbotFollowOptions.MaxPathRatio), 1f)]
    [InlineData(nameof(PlayerbotFollowOptions.MinDetourYards), -1f)]
    [InlineData(nameof(PlayerbotFollowOptions.DangerLevelMargin), 61f)]
    [InlineData(nameof(PlayerbotFollowOptions.LeaderCombatRangeYards), float.NaN)]
    [InlineData(nameof(PlayerbotFollowOptions.NoProgressSeconds), 1f)]
    [InlineData(nameof(PlayerbotFollowOptions.MaxFallHealthPercent), 100f)]
    public void AValueOutOfRange_IsRefusedAtStart(string key, float value)
    {
        var options = new PlayerbotOptions();
        System.Reflection.PropertyInfo property = typeof(PlayerbotFollowOptions).GetProperty(key)!;
        property.SetValue(options.Follow, property.PropertyType == typeof(int) ? (object)(int)value : value);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("World:Playerbots:Follow: " + key, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUndefinedMode_IsRefused()
    {
        var options = new PlayerbotOptions { Follow = { CatchUpMode = (PlayerbotCatchUpMode)7 } };
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    /// <summary>Every key is live: <c>.reload config</c> applies it to the running followers, and refuses a value out of range.</summary>
    [Fact]
    public void EveryKey_IsLive_WithItsRangeCheck()
    {
        string[] keys = [.. typeof(PlayerbotFollowOptions).GetProperties().Select(p => "World:Playerbots:Follow:" + p.Name)];
        foreach (string key in keys)
        {
            WorldConfigKey reload = Assert.Single(WorldConfigKeys.All, k => k.Path == key);
            Assert.True(reload.Live, key);
        }

        WorldConfigKey mode = WorldConfigKeys.All.Single(k => k.Path == "World:Playerbots:Follow:CatchUpMode");
        var view = new WorldConfigView(new Game.Maps.WorldRuntimeOptions(), null, Playerbots: new PlayerbotOptions());
        mode.Apply!(view, PlayerbotCatchUpMode.Auto);
        Assert.Equal(PlayerbotCatchUpMode.Auto, view.Playerbots!.Follow.CatchUpMode);
        Assert.NotNull(WorldConfigKeys.All.Single(k => k.Path == "World:Playerbots:Follow:MaxPathRatio").Check!(1.2f));
        Assert.Null(WorldConfigKeys.All.Single(k => k.Path == "World:Playerbots:Follow:MaxPathRatio").Check!(3f));
    }
}
