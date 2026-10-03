using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.WorldData;
using Xunit;

namespace ArcaneCore.Game.Tests.WorldState;

/// <summary>PvP-enforced areas, FFA and the flag timer freeze (vmangos Player.cpp:6566-6636, :17199-17207).</summary>
public sealed class PvpAreaRulesTests
{

    [Theory]
    // zone team, flags, player team, realm, in battleground -> enforced
    [InlineData(AreaTeams.Horde, 0x100u, Team.Alliance, PvpRealmMode.Normal, false, true)]     // Alliance in a Horde capital
    [InlineData(AreaTeams.Horde, 0u, Team.Alliance, PvpRealmMode.Normal, false, false)]         // Horde field zone on a PvE realm
    [InlineData(AreaTeams.Horde, 0u, Team.Alliance, PvpRealmMode.Pvp, false, true)]             // ... on a PvP realm
    [InlineData(AreaTeams.Horde, 0x100u, Team.Horde, PvpRealmMode.Pvp, false, false)]          // own team's zone is never hostile
    [InlineData(AreaTeams.Ally, 0x100u, Team.Horde, PvpRealmMode.Normal, false, true)]         // Horde in an Alliance capital
    [InlineData(AreaTeams.Ally, 0u, Team.Alliance, PvpRealmMode.FfaPvp, false, false)]
    [InlineData(AreaTeams.None, 0u, Team.Alliance, PvpRealmMode.Normal, false, false)]
    [InlineData(AreaTeams.None, 0u, Team.Alliance, PvpRealmMode.Pvp, false, true)]              // an unowned zone on a PvP realm
    [InlineData(AreaTeams.None, 0u, Team.Horde, PvpRealmMode.Normal, true, true)]               // battlegrounds
    [InlineData(6u, 0x100u, Team.Alliance, PvpRealmMode.Pvp, true, false)]                     // team 6 never
    public void Enforced_FollowsTheVmangosTruthTable(uint team, uint flags, Team player, PvpRealmMode realm, bool bg, bool expected)
        => Assert.Equal(expected, PvpAreaRules.IsEnforced(team, (AreaFlags)flags, player, realm, bg));

    private sealed class Locator : IZoneLocator
    {
        public (uint Zone, uint Area) Position = (12, 9);

        public Dictionary<uint, AreaTemplate> Entries { get; } = new()
        {
            [12] = new AreaTemplate(12, 0, 0, 41, 0, 1, "Elwynn", AreaTeams.Ally, 0),
            [1637] = new AreaTemplate(1637, 1, 0, 50, (uint)AreaFlags.Capital, 1, "Orgrimmar", AreaTeams.Horde, 0),
            [9] = new AreaTemplate(9, 0, 12, 42, 0, 1, "Plain", 0, 0),
            [77] = new AreaTemplate(77, 0, 12, 43, (uint)AreaFlags.Arena, 1, "Arena", 0, 0),
        };

        public bool CanDeriveZones => true;

        public (uint ZoneId, uint AreaId) Locate(Map map, Player player) => Position;

        public AreaTemplate? Find(uint areaId) => Entries.GetValueOrDefault(areaId);
    }

    private static (WorldRuntime World, Locator Locator, Player Player, WorldStateHooks Hooks) Setup(PvpRealmMode realm = PvpRealmMode.Normal)
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        WorldStateHooks hooks = WorldStateHooks.For(world);
        var locator = new Locator();
        hooks.Locator = locator;
        hooks.Zones.PvpRealmMode = realm;
        hooks.AddLocationListener(new PvpAreaTracker(hooks));
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession()); // a human: Alliance
        world.AddPlayer(player);
        return (world, locator, player, hooks);
    }

    private static bool IsPvp(Player player) => (player.UnitFlags & UnitFlags.Pvp) != 0;

    [Fact]
    public void WalkingIntoAHostileCapital_FlagsThePlayer_AndKeepsItPastFiveMinutesWhileInside()
    {
        (WorldRuntime world, Locator locator, Player player, _) = Setup();
        world.RunTick(50);
        Assert.False(IsPvp(player));
        Assert.False(PvpAreaState.IsInEnforcedArea(player));

        locator.Position = (1637, 1637);
        world.RunTick(1000);
        Assert.True(IsPvp(player));
        Assert.True(PvpAreaState.IsInEnforcedArea(player));

        for (int i = 0; i < 5; i++)
        {
            world.RunTick(100_000); // 500 s inside: the 300 s timer is frozen
        }

        Assert.True(IsPvp(player));
        Assert.Equal(CombatConstants.PvpFlagTimerMs, player.Combat.PvpFlagTimer);

        // leaving starts the countdown (the 300 s timer, PvpFlagTimerMs)
        locator.Position = (12, 9);
        world.RunTick(1000);
        Assert.False(PvpAreaState.IsInEnforcedArea(player));
        Assert.True(IsPvp(player));
        world.RunTick(CombatConstants.PvpFlagTimerMs - 1);
        Assert.True(IsPvp(player));
        world.RunTick(2000);
        Assert.False(IsPvp(player));
    }

    [Fact]
    public void AnAreaChangeInsideAnEnforcedZone_DoesNotRecomputeIt()
    {
        (WorldRuntime world, Locator locator, Player player, _) = Setup();
        locator.Position = (1637, 1637);
        world.RunTick(50);
        Assert.True(PvpAreaState.IsInEnforcedArea(player));
        locator.Position = (1637, 9);
        world.RunTick(1000);
        Assert.True(PvpAreaState.IsInEnforcedArea(player));
    }

    [Fact]
    public void FfaRealm_TogglesFfaWithThePvpFlag_AndNotWhileResting()
    {
        (WorldRuntime world, Locator locator, Player player, _) = Setup(PvpRealmMode.FfaPvp);
        locator.Position = (12, 9);
        world.RunTick(50);
        // an Alliance zone for an Alliance player is not hostile: no PvP flag, no FFA
        Assert.False(IsPvp(player));
        Assert.Equal(0u, (uint)(player.Flags & PlayerFlags.FfaPvp));

        locator.Position = (1637, 1637);
        world.RunTick(1000);
        Assert.True(IsPvp(player));
        Assert.NotEqual(0u, (uint)(player.Flags & PlayerFlags.FfaPvp));

        player.Flags |= PlayerFlags.Resting; // resting clears FFA on the next zone entry
        locator.Position = (12, 9);
        world.RunTick(1000);
        locator.Position = (1637, 1637);
        world.RunTick(1000);
        Assert.Equal(0u, (uint)(player.Flags & PlayerFlags.FfaPvp));
    }

    [Fact]
    public void Arena_SetsFfaForPlayers_NotForGms_AndLeavingClearsItOutsideFfaRealms()
    {
        (WorldRuntime world, Locator locator, Player player, _) = Setup();
        world.RunTick(50);
        locator.Position = (12, 77);
        world.RunTick(1000);
        Assert.NotEqual(0u, (uint)(player.Flags & PlayerFlags.FfaPvp));

        locator.Position = (12, 9);
        world.RunTick(1000);
        Assert.Equal(0u, (uint)(player.Flags & PlayerFlags.FfaPvp));

        player.Flags |= PlayerFlags.Gm;
        locator.Position = (12, 77);
        world.RunTick(1000);
        Assert.Equal(0u, (uint)(player.Flags & PlayerFlags.FfaPvp));
    }

    [Fact]
    public void AnFfaRealm_KeepsTheFfaFlagWhenLeavingAnArena()
    {
        (WorldRuntime world, Locator locator, Player player, _) = Setup(PvpRealmMode.FfaPvp);
        world.RunTick(50);
        locator.Position = (12, 77);
        world.RunTick(1000);
        locator.Position = (12, 9);
        world.RunTick(1000);
        Assert.NotEqual(0u, (uint)(player.Flags & PlayerFlags.FfaPvp));
    }
}
