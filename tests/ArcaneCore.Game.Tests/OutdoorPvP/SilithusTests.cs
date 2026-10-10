using ArcaneCore.Game.Entities;
using ArcaneCore.Game.OutdoorPvP;
using ArcaneCore.Game.WorldState.States;
using Xunit;
using S = ArcaneCore.Game.OutdoorPvP.SilithusCatalog;

namespace ArcaneCore.Game.Tests.OutdoorPvP;

/// <summary>Silithyst turn-ins (vmangos OutdoorPvPSI.cpp:160-282).</summary>
public sealed class SilithusTests
{
    private static readonly OutdoorPvPSpawn Camp = new(0, S.MapId, -7140f, 1400f, 5f, 0);

    private static (FakeOutdoorPvPHost Host, SilithusZone Zone) Setup(uint max = S.DefaultMaxResources)
    {
        var host = new FakeOutdoorPvPHost();
        var zone = new SilithusZone(host, max) { AreaTriggerPosition = id => id == S.AreaTriggerAlliance ? (-7140f, 1400f, 5f) : (-7596f, 756f, -16f) };
        zone.Setup();
        return (host, zone);
    }

    private static ObjectGuid Carrier(FakeOutdoorPvPHost host, SilithusZone zone, uint id, Team team)
    {
        ObjectGuid guid = host.AddPlayer(id, team, Camp);
        zone.OnPlayerEnter(host.P(guid));
        host.Auras[guid].Add(S.SpellSilithystFlag);
        return guid;
    }

    [Fact]
    public void A_turn_in_counts_rewards_and_takes_the_flag()
    {
        (FakeOutdoorPvPHost host, SilithusZone zone) = Setup();
        ObjectGuid player = Carrier(host, zone, 1, Team.Alliance);

        Assert.True(zone.HandleAreaTrigger(host.P(player), S.AreaTriggerAlliance));

        Assert.Equal(1u, zone.GatheredAlliance);
        Assert.False(host.HasAura(player, S.SpellSilithystFlag));
        Assert.Contains((player, S.SpellTracesOfSilithyst), host.Casts);
        Assert.Contains((player, S.SpellHonorPoints199), host.Casts);
        Assert.Contains((player, S.SpellSilithystCapReward), host.Casts);
        Assert.Contains((player, S.TurnInCreditAlliance), host.Credits);
        Assert.Equal(1u, host.State(player, S.WorldStateGatheredAlliance));
        Assert.Equal(200u, host.State(player, S.WorldStateSilithystMax));
    }

    [Fact]
    public void No_flag_or_the_enemy_camp_does_nothing()
    {
        (FakeOutdoorPvPHost host, SilithusZone zone) = Setup();
        ObjectGuid player = Carrier(host, zone, 1, Team.Horde);

        Assert.True(zone.HandleAreaTrigger(host.P(player), S.AreaTriggerAlliance)); // swallowed
        Assert.True(host.HasAura(player, S.SpellSilithystFlag));
        Assert.Equal(0u, zone.GatheredHorde + zone.GatheredAlliance);

        host.Auras[player].Clear();
        Assert.False(zone.HandleAreaTrigger(host.P(player), S.AreaTriggerHorde));
        Assert.False(zone.HandleAreaTrigger(host.P(player), 1234));
        Assert.Equal(0u, zone.GatheredHorde);
    }

    [Fact]
    public void Dust_bags_pile_up_every_fifteen_and_the_announcer_yells_at_each_quarter()
    {
        (FakeOutdoorPvPHost host, SilithusZone zone) = Setup();
        ObjectGuid player = Carrier(host, zone, 1, Team.Horde);
        for (int i = 0; i < 50; i++)
        {
            host.Auras[player].Add(S.SpellSilithystFlag);
            zone.HandleAreaTrigger(host.P(player), S.AreaTriggerHorde);
        }

        Assert.Equal(50u, zone.GatheredHorde);
        Assert.Equal(3, zone.DustBagsHorde);
        Assert.Equal(3, host.ObjectsOf(S.DustBagEntry));
        Assert.Equal([(S.AnnouncerHorde, S.YellsHorde[0])], host.Says);
    }

    [Fact]
    public void Reaching_the_maximum_grants_cenarion_favor_and_resets_both_sides()
    {
        (FakeOutdoorPvPHost host, SilithusZone zone) = Setup(max: 20);
        ObjectGuid alliance = Carrier(host, zone, 1, Team.Alliance);
        ObjectGuid horde = Carrier(host, zone, 2, Team.Horde);
        host.Auras[horde].Add(S.SpellCenarionFavor);
        host.Auras[horde].Add(S.SpellSilithystFlag);
        zone.HandleAreaTrigger(host.P(horde), S.AreaTriggerHorde);

        for (int i = 0; i < 20; i++)
        {
            host.Auras[alliance].Add(S.SpellSilithystFlag);
            zone.HandleAreaTrigger(host.P(alliance), S.AreaTriggerAlliance);
        }

        Assert.Equal(Team.Alliance, zone.LastController);
        Assert.True(host.HasAura(alliance, S.SpellCenarionFavor));
        Assert.False(host.HasAura(horde, S.SpellCenarionFavor));
        Assert.Equal(0u, zone.GatheredAlliance);
        Assert.Equal(0u, zone.GatheredHorde);
        Assert.Equal(0, host.ObjectsOf(S.DustBagEntry));
        Assert.Contains((S.SilithusZone, S.CaptureTextAlliance), host.ZoneTexts);
        Assert.Contains((S.AnnouncerAlliance, S.YellsAlliance[3]), host.Says);

        ObjectGuid late = host.AddPlayer(3, Team.Alliance, Camp);
        zone.OnPlayerEnter(host.P(late));
        Assert.True(host.HasAura(late, S.SpellCenarionFavor));
    }

    [Fact]
    public void Mounting_away_from_the_camp_drops_the_flag_but_at_the_trigger_keeps_it()
    {
        (FakeOutdoorPvPHost host, SilithusZone zone) = Setup();
        ObjectGuid player = Carrier(host, zone, 1, Team.Alliance);

        Assert.False(zone.HandleDropFlag(host.P(player), S.SpellSilithystFlag));
        Assert.DoesNotContain((player, S.SpellFlagDrop), host.Casts);

        host.Move(player, -7000f, 1400f, 5f);
        Assert.True(zone.HandleDropFlag(host.P(player), S.SpellSilithystFlag));
        Assert.Contains((player, S.SpellFlagDrop), host.Casts);
        Assert.False(zone.HandleDropFlag(host.P(player), 12345));
    }

    [Fact]
    public void World_states_show_the_counters_and_clear_on_leaving()
    {
        (FakeOutdoorPvPHost host, SilithusZone zone) = Setup();
        var states = new List<WorldStatePair>();
        zone.FillInitialWorldStates(states);
        Assert.Equal([new(S.WorldStateGatheredAlliance, 0), new(S.WorldStateGatheredHorde, 0), new(S.WorldStateSilithystMax, 200)], states);

        ObjectGuid player = Carrier(host, zone, 1, Team.Horde);
        zone.OnPlayerLeave(host.P(player), loggingOut: false);
        Assert.Equal(0u, host.State(player, S.WorldStateSilithystMax));
        Assert.Equal(3, host.WorldStates.Count);
    }
}
