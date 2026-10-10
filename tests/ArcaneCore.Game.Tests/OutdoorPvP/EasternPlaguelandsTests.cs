using ArcaneCore.Game.Entities;
using ArcaneCore.Game.OutdoorPvP;
using ArcaneCore.Game.WorldState.States;
using Xunit;
using C = ArcaneCore.Game.OutdoorPvP.EasternPlaguelandsCatalog;

namespace ArcaneCore.Game.Tests.OutdoorPvP;

/// <summary>
/// The capture point slider (vmangos ZoneScript.cpp:301-433) and the Eastern Plaguelands towers (vmangos OutdoorPvPEP.cpp), with the
/// fallback template: slider max 1200, grey band 240, one player moves it 1 per second.
/// </summary>
public sealed class EasternPlaguelandsTests
{
    private static (FakeOutdoorPvPHost Host, EasternPlaguelandsZone Zone) Setup()
    {
        var host = new FakeOutdoorPvPHost();
        var zone = new EasternPlaguelandsZone(host);
        zone.Setup();
        return (host, zone);
    }

    private static ObjectGuid AtTower(FakeOutdoorPvPHost host, EasternPlaguelandsZone zone, uint id, Team team, EpTowerData tower)
    {
        ObjectGuid guid = host.AddPlayer(id, team, tower.CapturePoint);
        zone.OnPlayerEnter(host.P(guid));
        return guid;
    }

    [Fact]
    public void Setup_summons_four_capture_points_with_their_banners_all_neutral()
    {
        (FakeOutdoorPvPHost host, EasternPlaguelandsZone zone) = Setup();

        Assert.Equal(4, zone.CapturePoints.Count);
        Assert.Equal(8, host.ObjectsOf(C.TowerBannerEntry));
        Assert.All(zone.Towers, t => Assert.Equal(EpTowerState.Neutral, t.TowerState));
        Assert.All(zone.Towers, t => Assert.Equal(50u, t.ValuePct));
        Assert.All(host.ArtKits.Values, kit => Assert.Equal(C.ArtKitNeutral, kit));
    }

    [Fact]
    public void One_player_moves_the_slider_through_contested_progressing_and_full_control()
    {
        (FakeOutdoorPvPHost host, EasternPlaguelandsZone zone) = Setup();
        ObjectGuid player = AtTower(host, zone, 1, Team.Alliance, C.Eastwall);
        EasternPlaguelandsTower tower = zone.Tower(EpTower.Eastwall);

        zone.Update(100_000);
        Assert.Equal(ObjectiveState.AllianceContested, tower.State);
        Assert.Equal(EpTowerState.AllianceContested, tower.TowerState);
        Assert.Equal(1u, host.State(player, C.WorldStateSliderDisplay));   // the slider is shown on entering
        Assert.Null(zone.Control(EpTower.Eastwall));

        zone.Update(150_000);
        Assert.Equal(ObjectiveState.AllianceProgressing, tower.State);
        Assert.Equal(Team.Alliance, zone.Control(EpTower.Eastwall));
        Assert.Equal(1u, zone.AllianceTowers);
        Assert.Contains(C.Eastwall.TakenAllianceText, host.DefenseMessages);
        Assert.Contains(C.SoundFlagCapturedAlliance, host.Sounds);
        Assert.Equal(1u, host.State(player, C.WorldStateTowerCountAlliance));
        Assert.Equal(1u, host.State(player, C.Eastwall.States.AllianceProgressing));
        Assert.Equal(0u, host.State(player, C.Eastwall.States.Neutral));
        Assert.Contains((player, C.AllianceBuffs[0]), host.Casts);

        zone.Update(1_000_000);
        Assert.Equal(ObjectiveState.Alliance, tower.State);
        Assert.Equal(1200f, tower.Value);
        Assert.Equal(100u, tower.ValuePct);
        Assert.Contains(C.SoundVictoryAlliance, host.Sounds);
        Assert.Equal(1, host.ObjectsOf(181852));                           // the Alliance flare
        Assert.Equal(Team.Alliance, zone.Control(EpTower.Eastwall));        // still counted after full capture
    }

    [Fact]
    public void Taking_a_tower_summons_its_buffer_which_pulses_the_capture_spell()
    {
        (FakeOutdoorPvPHost host, EasternPlaguelandsZone zone) = Setup();
        AtTower(host, zone, 1, Team.Horde, C.Northpass);

        zone.Update(250_000);

        Assert.Equal(1, host.CreaturesOf(17795));
        Assert.Single(host.CreatureCasts, c => c.Spell == C.SpellTowerCaptureTest);
        Assert.Equal(1, host.ObjectsOf(181955));                           // Northpass: the Horde curing shrine
        Assert.Equal(1, host.ObjectsOf(180101));
    }

    [Fact]
    public void Eastwall_squad_arrives_on_capture_and_leaves_when_the_tower_goes_grey()
    {
        (FakeOutdoorPvPHost host, EasternPlaguelandsZone zone) = Setup();
        ObjectGuid alliance = AtTower(host, zone, 1, Team.Alliance, C.Eastwall);
        zone.Update(250_000);
        Assert.Equal(1, host.CreaturesOf(17635));
        Assert.Equal(4, host.CreaturesOf(17647));

        host.Move(alliance, 0, 0, 0);
        ObjectGuid h1 = AtTower(host, zone, 2, Team.Horde, C.Eastwall);
        ObjectGuid h2 = AtTower(host, zone, 3, Team.Horde, C.Eastwall);
        Assert.NotEqual(h1, h2);
        zone.Update(10_000);                                                // value 250 - 20 = 230: back in the grey band
        Assert.Equal(ObjectiveState.HordeContested, zone.Tower(EpTower.Eastwall).State);
        Assert.Equal(0, host.CreaturesOf(17635) + host.CreaturesOf(17647));
        Assert.Equal(0u, host.State(alliance, C.WorldStateSliderDisplay));  // left the radius: slider hidden
    }

    [Fact]
    public void Losing_full_control_clears_the_tower_count_and_the_buff()
    {
        (FakeOutdoorPvPHost host, EasternPlaguelandsZone zone) = Setup();
        ObjectGuid alliance = AtTower(host, zone, 1, Team.Alliance, C.Plaguewood);
        zone.Update(1_300_000);
        Assert.Equal(ObjectiveState.Alliance, zone.Tower(EpTower.Plaguewood).State);
        Assert.Equal(1, host.CreaturesOf(17209));
        Assert.Equal(C.FactionFlightMasterAlliance, host.Creatures.Values.Single(c => c.Spawn.Entry == 17209).Faction);

        host.Move(alliance, 0, 0, 0);
        AtTower(host, zone, 2, Team.Horde, C.Plaguewood);
        zone.Update(1_000);                                                 // leaves max blue: progressing (defending)
        Assert.Equal(ObjectiveState.AllianceProgressing, zone.Tower(EpTower.Plaguewood).State);
        Assert.Null(zone.Control(EpTower.Plaguewood));                      // vmangos: leaving full control zeroes EP_Controls
        Assert.Equal(0u, zone.AllianceTowers);
        Assert.DoesNotContain(C.AllianceBuffs[0], host.Auras[alliance]);
        Assert.Contains(C.SoundWarningAlliance, host.Sounds);
    }

    [Fact]
    public void Crown_guard_links_the_graveyard_to_its_owner()
    {
        (FakeOutdoorPvPHost host, EasternPlaguelandsZone zone) = Setup();
        Assert.Null(zone.GraveyardTeam);
        AtTower(host, zone, 1, Team.Horde, C.CrownGuard);

        zone.Update(250_000);

        Assert.Equal(Team.Horde, zone.GraveyardTeam);
        Assert.Equal(1, host.ObjectsOf(180422));
        Assert.Equal(C.SpellSpiritParticlesRedSuperBig, host.Creatures.Values.Single(c => c.Spawn.Entry == 18039).Aura);
    }

    [Fact]
    public void Four_towers_give_rank_four_and_the_all_towers_announcement()
    {
        (FakeOutdoorPvPHost host, EasternPlaguelandsZone zone) = Setup();
        uint id = 1;
        foreach (EpTowerData tower in C.Towers)
        {
            AtTower(host, zone, id++, Team.Alliance, tower);
        }

        zone.Update(250_000);

        Assert.Equal(4u, zone.AllianceTowers);
        Assert.Equal(C.AllianceBuffs[3], zone.BuffFor(Team.Alliance));
        Assert.Contains(C.TextAllAlliance, host.DefenseMessages);

        ObjectGuid late = host.AddPlayer(99, Team.Alliance, new OutdoorPvPSpawn(0, 0, 0, 0, 0, 0));
        zone.OnPlayerEnter(host.P(late));
        Assert.Contains((late, C.AllianceBuffs[3]), host.Casts);
        zone.OnPlayerLeave(host.P(late), loggingOut: false);
        Assert.Empty(host.Auras[late]);
    }

    [Fact]
    public void Equal_numbers_hold_the_slider()
    {
        (FakeOutdoorPvPHost host, EasternPlaguelandsZone zone) = Setup();
        AtTower(host, zone, 1, Team.Alliance, C.Eastwall);
        AtTower(host, zone, 2, Team.Horde, C.Eastwall);

        zone.Update(500_000);

        Assert.Equal(ObjectiveState.Neutral, zone.Tower(EpTower.Eastwall).State);
        Assert.Equal(0f, zone.Tower(EpTower.Eastwall).Value);
    }

    [Fact]
    public void A_player_counts_for_one_objective_and_an_inactive_player_for_none()
    {
        (FakeOutdoorPvPHost host, EasternPlaguelandsZone zone) = Setup();
        ObjectGuid inactive = host.AddPlayer(1, Team.Alliance, C.Eastwall.CapturePoint, active: false);
        zone.OnPlayerEnter(host.P(inactive));

        zone.Update(250_000);

        Assert.Equal(ObjectiveState.Neutral, zone.Tower(EpTower.Eastwall).State);
        Assert.False(zone.IsInsideObjective(inactive));
    }

    [Fact]
    public void The_update_runs_after_more_than_one_second()
    {
        (FakeOutdoorPvPHost host, EasternPlaguelandsZone zone) = Setup();
        AtTower(host, zone, 1, Team.Alliance, C.Eastwall);

        zone.Tick(1000);
        Assert.Equal(0f, zone.Tower(EpTower.Eastwall).Value);
        zone.Tick(1);
        Assert.Equal(1.001f, zone.Tower(EpTower.Eastwall).Value, 3);
    }

    [Fact]
    public void Initial_world_states_carry_the_counts_the_slider_defaults_and_every_tower_icon()
    {
        (_, EasternPlaguelandsZone zone) = Setup();
        var states = new List<WorldStatePair>();

        zone.FillInitialWorldStates(states);

        Assert.Equal(5 + (4 * 7), states.Count);
        Assert.Contains(new WorldStatePair(C.WorldStateSliderPosition, 50), states);
        Assert.Contains(new WorldStatePair(C.WorldStateSliderNeutral, 100), states);
        Assert.Contains(new WorldStatePair(C.Northpass.States.Neutral, 1), states);
        Assert.Contains(new WorldStatePair(C.Northpass.States.Alliance, 0), states);
        Assert.Equal(states.Count, states.Select(s => s.State).Distinct().Count());
    }

    [Fact]
    public void Leaving_the_zone_clears_the_states_unless_logging_out()
    {
        (FakeOutdoorPvPHost host, EasternPlaguelandsZone zone) = Setup();
        ObjectGuid player = host.AddPlayer(1, Team.Horde, new OutdoorPvPSpawn(0, 0, 0, 0, 0, 0));
        zone.OnPlayerEnter(host.P(player));

        zone.OnPlayerLeave(host.P(player), loggingOut: true);
        Assert.Empty(host.WorldStates);

        zone.OnPlayerEnter(host.P(player));
        zone.OnPlayerLeave(host.P(player), loggingOut: false);
        Assert.Equal(5 + (4 * 7), host.WorldStates.Count);
        Assert.All(host.WorldStates.Values, v => Assert.Equal(0u, v));
        Assert.False(zone.HasPlayer(player));
    }

    [Fact]
    public void A_loaded_capture_point_template_replaces_the_fallback()
    {
        var host = new FakeOutdoorPvPHost { Template = new CapturePointTemplate(50f, 2426, 2427, 2428, 50, 10, 100) };
        var zone = new EasternPlaguelandsZone(host);
        zone.Setup();
        EasternPlaguelandsTower tower = zone.Tower(EpTower.Eastwall);

        Assert.Equal(100f, tower.MaxValue);
        Assert.Equal(50f, tower.MinValue);
        Assert.Equal(10f, tower.MaxSpeed);

        // Ten players for 100 ms: 1 point per player-second, so 1.0, under the speed cap of 10 per ms.
        for (uint i = 1; i <= 10; i++)
        {
            AtTower(host, zone, i, Team.Horde, C.Eastwall);
        }

        zone.Update(100);
        Assert.Equal(-1f, tower.Value, 3);
    }
}
