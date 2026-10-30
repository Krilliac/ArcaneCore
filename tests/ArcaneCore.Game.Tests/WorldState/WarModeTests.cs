using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.WorldData;
using Xunit;

namespace ArcaneCore.Game.Tests.WorldState;

/// <summary>
/// War Mode (<c>World:WarMode</c>, not retail): a server-wide switch that flags every player outside the exempt areas and keeps the flag
/// from lapsing. The exempt areas are a capital or city of the player's own team or of no team, and an arena. With the switch off nothing changes.
/// </summary>
public sealed class WarModeTests
{
    private sealed class Locator : IZoneLocator
    {
        public (uint Zone, uint Area) Position = (12, 9);

        public Dictionary<uint, AreaTemplate> Entries { get; } = new()
        {
            [12] = new AreaTemplate(12, 0, 0, 41, 0, 1, "Elwynn", AreaTeams.Ally, 0),
            [9] = new AreaTemplate(9, 0, 12, 42, 0, 1, "Plain", 0, 0),
            [1519] = new AreaTemplate(1519, 0, 0, 44, (uint)AreaFlags.Capital, 1, "Stormwind", AreaTeams.Ally, 0),
            [1637] = new AreaTemplate(1637, 1, 0, 50, (uint)AreaFlags.Capital, 1, "Orgrimmar", AreaTeams.Horde, 0),
            [33] = new AreaTemplate(33, 0, 0, 60, 0, 1, "Stranglethorn", 0, 0),
            // The real 5875 rows: a neutral town is flagged 0x40 with no team, neither Capital nor City.
            [35] = new AreaTemplate(35, 0, 33, 61, 0x40, 1, "Booty Bay", 0, 0),
            [2177] = new AreaTemplate(2177, 0, 33, 62, (uint)AreaFlags.Arena, 1, "The Battle Ring", 0, 0),
            [1741] = new AreaTemplate(1741, 0, 33, 63, 0, 1, "Gurubashi Arena", 0, 0),
            [77] = new AreaTemplate(77, 0, 12, 43, (uint)AreaFlags.Arena, 1, "Arena", 0, 0),
        };

        public bool CanDeriveZones => true;

        public (uint ZoneId, uint AreaId) Locate(Map map, Player player) => Position;

        public AreaTemplate? Find(uint areaId) => Entries.GetValueOrDefault(areaId);
    }

    private static (WorldRuntime World, Locator Locator, Player Player, WarModeOptions War) Setup()
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        WorldStateHooks hooks = WorldStateHooks.For(world);
        var locator = new Locator();
        hooks.Locator = locator;
        hooks.AddLocationListener(new PvpAreaTracker(hooks));
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession()); // a human: Alliance
        world.AddPlayer(player);
        return (world, locator, player, hooks.WarMode);
    }

    private static bool IsPvp(Player player) => (player.UnitFlags & UnitFlags.Pvp) != 0;

    private static void RunMinutes(WorldRuntime world, int minutes)
    {
        for (int i = 0; i < minutes * 6; i++)
        {
            world.RunTick(10_000);
        }
    }

    [Fact]
    public void TheDefaults_AreOffWithAHonorMultiplierOfOnePointFiveAndATenPercentLoss()
    {
        var options = new WarModeOptions();
        Assert.False(options.Enabled);
        Assert.Equal(1.5f, options.HonorMultiplier);
        Assert.Equal(0.10f, options.PvpDeathDurabilityLoss);
        options.Validate();
    }

    [Theory]
    [InlineData(0.99f)]
    [InlineData(10.01f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void AHonorMultiplierOutsideOneToTen_IsRejected(float value)
    {
        Assert.NotNull(WarModeOptions.CheckHonorMultiplier(value));
        Assert.Throws<InvalidOperationException>(() => new WarModeOptions { HonorMultiplier = value }.Validate());
    }

    [Theory]
    [InlineData(-0.01f)]
    [InlineData(1.01f)]
    [InlineData(float.NaN)]
    public void ADurabilityLossOutsideZeroToOne_IsRejected(float value)
    {
        Assert.NotNull(WarModeOptions.CheckPvpDeathDurabilityLoss(value));
        Assert.Throws<InvalidOperationException>(() => new WarModeOptions { PvpDeathDurabilityLoss = value }.Validate());
    }

    [Theory]
    [InlineData(1f, 0f)]
    [InlineData(10f, 1f)]
    public void TheEdgesOfBothRanges_AreAccepted(float honor, float loss)
        => new WarModeOptions { HonorMultiplier = honor, PvpDeathDurabilityLoss = loss }.Validate();

    [Fact]
    public void WarModeOff_NobodyIsFlaggedInTheOpenWorld_AndNothingIsForced()
    {
        (WorldRuntime world, _, Player player, _) = Setup();
        using (world)
        {
            RunMinutes(world, 10);

            Assert.False(IsPvp(player));
        }
    }

    [Fact]
    public void WarModeOn_APlayerInTheOpenWorld_IsFlaggedWithinOneTick_AndStillFlaggedAfterTenMinutes()
    {
        (WorldRuntime world, _, Player player, WarModeOptions war) = Setup();
        using (world)
        {
            world.RunTick(50);
            Assert.False(IsPvp(player));

            war.Enabled = true;
            world.RunTick(50);
            Assert.True(IsPvp(player));

            RunMinutes(world, 10);

            Assert.True(IsPvp(player));
            Assert.Equal(CombatConstants.PvpFlagTimerMs, player.Combat.PvpFlagTimer);
        }
    }

    [Fact]
    public void WarModeOn_APlayerInTheirOwnCapital_IsNotForceFlagged_AndALingeringFlagLapsesThere()
    {
        (WorldRuntime world, Locator locator, Player player, WarModeOptions war) = Setup();
        using (world)
        {
            war.Enabled = true;
            world.RunTick(50);
            world.RunTick(50);
            Assert.True(IsPvp(player));

            locator.Position = (1519, 1519);
            world.RunTick(1000);
            Assert.True(PvpAreaState.IsWarModeExempt(player));
            Assert.True(IsPvp(player)); // the 5-minute linger runs from here
            world.RunTick(CombatConstants.PvpFlagTimerMs - 1000);
            Assert.True(IsPvp(player));
            world.RunTick(2000);
            Assert.False(IsPvp(player));

            RunMinutes(world, 10);
            Assert.False(IsPvp(player)); // and it stays down: nothing force-flags the player in the capital
        }
    }

    [Fact]
    public void WarModeOn_ANeutralTown_IsExemptToo()
    {
        (WorldRuntime world, Locator locator, Player player, WarModeOptions war) = Setup();
        using (world)
        {
            war.Enabled = true;
            locator.Position = (33, 35);
            world.RunTick(1000);
            world.RunTick(1000);

            Assert.True(PvpAreaState.IsWarModeExempt(player));
            Assert.False(IsPvp(player));

            locator.Position = (33, 33); // the field around the town is not
            world.RunTick(1000);
            world.RunTick(1000);
            Assert.False(PvpAreaState.IsWarModeExempt(player));
            Assert.True(IsPvp(player));
        }
    }

    [Fact]
    public void WarModeOn_AnEnemyCapital_IsNotExempt_AndTheFlagSurvivesLeavingIt()
    {
        (WorldRuntime world, Locator locator, Player player, WarModeOptions war) = Setup();
        using (world)
        {
            war.Enabled = true;
            locator.Position = (1637, 1637);
            world.RunTick(1000);
            world.RunTick(1000);
            Assert.False(PvpAreaState.IsWarModeExempt(player));
            Assert.True(IsPvp(player));

            locator.Position = (12, 9);
            world.RunTick(1000);
            RunMinutes(world, 10);

            Assert.True(IsPvp(player));
        }
    }

    [Fact]
    public void WarModeOn_AnArena_IsLeftToItsOwnRules()
    {
        (WorldRuntime world, Locator locator, Player player, WarModeOptions war) = Setup();
        using (world)
        {
            war.Enabled = true;
            locator.Position = (12, 77);
            world.RunTick(1000);
            world.RunTick(1000);

            Assert.True(PvpAreaState.IsWarModeExempt(player));
            Assert.False(IsPvp(player));
        }
    }

    [Fact]
    public void SwitchingWarModeOffLive_LetsTheFlagLapseAfterTheNormalTimer()
    {
        (WorldRuntime world, _, Player player, WarModeOptions war) = Setup();
        using (world)
        {
            war.Enabled = true;
            RunMinutes(world, 8);
            Assert.True(IsPvp(player));

            war.Enabled = false;
            world.RunTick(1000);
            Assert.True(IsPvp(player));
            world.RunTick(CombatConstants.PvpFlagTimerMs - 2000);
            Assert.True(IsPvp(player));
            world.RunTick(2000);

            Assert.False(IsPvp(player));
        }
    }

    [Fact]
    public void SwitchingWarModeOffLive_KeepsAFlagThePlayerWants()
    {
        (WorldRuntime world, _, Player player, WarModeOptions war) = Setup();
        using (world)
        {
            war.Enabled = true;
            world.RunTick(100);
            world.RunTick(100);
            player.Flags |= PlayerFlags.PvpDesired;
            war.Enabled = false;

            RunMinutes(world, 10);

            Assert.True(IsPvp(player));
        }
    }

    [Theory]
    // area flags, area team, zone team, player team -> exempt
    [InlineData(0x100u, AreaTeams.Ally, AreaTeams.Ally, Team.Alliance, true)]
    [InlineData(0x100u, AreaTeams.Horde, AreaTeams.Horde, Team.Alliance, false)]
    [InlineData(0x100u, AreaTeams.Horde, AreaTeams.Horde, Team.Horde, true)]
    [InlineData(0x200u, AreaTeams.None, AreaTeams.None, Team.Horde, true)]
    [InlineData(0x200u, AreaTeams.None, AreaTeams.Ally, Team.Horde, false)] // a city without a team of its own takes its zone's
    [InlineData(0x300u, AreaTeams.None, AreaTeams.None, Team.Alliance, true)]
    [InlineData(0x008u, AreaTeams.None, AreaTeams.None, Team.Alliance, false)] // a slave capital is no capital
    [InlineData(0x000u, AreaTeams.Ally, AreaTeams.Ally, Team.Alliance, false)]
    [InlineData(0x100u, 6u, 6u, Team.Alliance, false)]
    public void IsExempt_FollowsFlagsAndTeam(uint flags, uint areaTeam, uint zoneTeam, Team player, bool expected)
    {
        var zone = new AreaTemplate(1, 0, 0, 1, 0, 1, "Zone", zoneTeam, 0);
        var area = new AreaTemplate(2, 0, 1, 2, flags, 1, "Area", areaTeam, 0);
        Assert.Equal(expected, WarModeAreas.IsExempt(area, zone, player));
    }


    [Fact]
    public void ExemptAreas_DefaultToTheSevenNeutralTowns_AndRefuseAnythingButIds()
    {
        var options = new WarModeOptions();
        Assert.Equal(new uint[] { 35, 392, 976, 2255, 2268, 2361, 3425 }, options.ExemptAreaIds().OrderBy(id => id));
        Assert.Null(WarModeOptions.CheckExemptAreas(""));
        Assert.Null(WarModeOptions.CheckExemptAreas(" 35 , 392 "));
        Assert.NotNull(WarModeOptions.CheckExemptAreas("35,Booty"));
        Assert.NotNull(WarModeOptions.CheckExemptAreas("-5"));
        Assert.Throws<InvalidOperationException>(() => new WarModeOptions { ExemptAreas = "35,x" }.Validate());
    }

    [Fact]
    public void ANeutralTownWithTheRealFlagsAndNoCity_IsExemptOnlyThroughTheList_AndTheListIsLive()
    {
        (WorldRuntime world, Locator locator, Player player, WarModeOptions war) = Setup();
        using (world)
        {
            war.Enabled = true;
            locator.Position = (33, 35);
            world.RunTick(1000);
            world.RunTick(1000);
            Assert.True(PvpAreaState.IsWarModeExempt(player));
            Assert.False(IsPvp(player));

            war.ExemptAreas = "392"; // Booty Bay struck off the list
            locator.Position = (33, 33);
            world.RunTick(1000);
            locator.Position = (33, 35);
            world.RunTick(1000);
            world.RunTick(1000);
            Assert.False(PvpAreaState.IsWarModeExempt(player));
            Assert.True(IsPvp(player));
        }
    }

    [Fact]
    public void OnlyTheBattleRing_IsExemptOfTheGurubashiArena_NotTheStandsAroundIt()
    {
        (WorldRuntime world, Locator locator, Player player, WarModeOptions war) = Setup();
        using (world)
        {
            war.Enabled = true;
            locator.Position = (33, 1741);
            world.RunTick(1000);
            world.RunTick(1000);
            Assert.False(PvpAreaState.IsWarModeExempt(player));
            Assert.True(IsPvp(player));

            locator.Position = (33, 2177);
            world.RunTick(1000);
            Assert.True(PvpAreaState.IsWarModeExempt(player));
        }
    }

    [Fact]
    public void APlayerTheTrackerHasNotPlacedYet_IsProtectedFromTheBots_ButNotExempt()
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        using (world)
        {
            Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
            world.AddPlayer(player);

            Assert.False(PvpAreaState.IsWarModeKnown(player));
            Assert.True(PvpAreaState.IsWarModeProtected(player));
            Assert.False(PvpAreaState.IsWarModeExempt(player));
        }
    }

    [Theory]
    [InlineData(0x40u, 35u, true)]
    [InlineData(0x40u, 36u, false)]
    [InlineData(0x00u, 1741u, false)]
    [InlineData(0x80u, 2177u, true)]
    public void IsExempt_TheRealNeutralTownFlagsAreNoCity_SoTheListDecides(uint flags, uint entry, bool expected)
    {
        var zone = new AreaTemplate(33, 0, 0, 1, 0, 1, "Stranglethorn", 0, 0);
        var area = new AreaTemplate(entry, 0, 33, 2, flags, 1, "Area", 0, 0);
        var listed = new HashSet<uint> { 35 };
        Assert.Equal(expected, WarModeAreas.IsExempt(area, zone, Team.Alliance, listed));
        if (flags == 0x40) Assert.False(WarModeAreas.IsExempt(area, zone, Team.Alliance));
    }
    [Fact]
    public void IsExempt_ASubzoneOfACapitalIsExemptThroughItsZone_AndUnknownAreasAreNot()
    {
        var capital = new AreaTemplate(1, 0, 0, 1, (uint)AreaFlags.Capital, 1, "Stormwind", AreaTeams.Ally, 0);
        var subzone = new AreaTemplate(2, 0, 1, 2, 0, 1, "Trade District", 0, 0);
        Assert.True(WarModeAreas.IsExempt(subzone, capital, Team.Alliance));
        Assert.False(WarModeAreas.IsExempt(subzone, capital, Team.Horde));
        Assert.False(WarModeAreas.IsExempt(null, null, Team.Alliance));
    }
}
