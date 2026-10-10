using System.Text;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.OutdoorPvP;
using ArcaneCore.Game.WorldState.States;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.OutdoorPvP;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Tests.Honor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.OutdoorPvP;

/// <summary>The world wiring of outdoor PvP: one script per continent, zone presence, world states and the Crown Guard graveyard link.</summary>
public sealed class OutdoorPvPFeatureTests
{
    private sealed class MemorySilithystStore : ISilithystStore
    {
        public SilithystState? State;

        public Task<SilithystState?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(State);

        public Task SaveAsync(SilithystState state, CancellationToken cancellationToken = default)
        {
            State = state;
            return Task.CompletedTask;
        }
    }

    private static (ServiceProvider Services, WorldRuntime World, OutdoorPvPFeature Feature) Start(ISilithystStore? store = null)
    {
        var collection = new ServiceCollection().AddLogging();
        if (store is not null) collection.AddSingleton(store);
        ServiceProvider services = collection.BuildServiceProvider();
        var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 },
            new CharacterSaveQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance),
            NullLogger<WorldRuntime>.Instance);
        var feature = new OutdoorPvPFeature(services, NullLogger<OutdoorPvPFeature>.Instance);
        feature.Attach(world);
        world.RunTick(1);                       // the posted install
        world.GetMap(0);
        world.GetMap(1);
        world.GetMap(1, 7);                     // an instance copy never gets a script
        return (services, world, feature);
    }

    private static Player NewPlayer(int id, byte race, uint mapId)
    {
        var character = new CharacterRecord { Id = id, AccountId = 1, Name = "Opvp" + id, Race = race, Class = 1, Gender = 0, Level = 60, MapId = mapId, ZoneId = 0 };
        return new Player(character, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), new HonorNullSession());
    }

    [Fact]
    public void Silithus_reads_back_the_saved_maximum()
    {
        var store = new MemorySilithystStore { State = new SilithystState(7, 9, 50) };
        (ServiceProvider services, WorldRuntime world, OutdoorPvPFeature feature) = Start(store);
        using (services)
        using (world)
        {
            SilithusZone zone = Assert.IsType<SilithusZone>(feature.Silithus);
            Assert.Equal(50u, zone.MaxResources);                           // vmangos reads back only the maximum
            Assert.Equal(0u, zone.GatheredAlliance);
        }
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(1f, 0f)]
    [InlineData(-1f, 2.35619f)]
    public void Squad_follow_angle_is_the_leaders_bearing_less_the_members_facing(float memberY, float memberO)
    {
        Player leader = NewPlayer(1, 1, 0);
        Player member = NewPlayer(2, 1, 0);
        leader.Relocate(0, 0, 0, 0, 0);
        member.Relocate(1, memberY, 0, memberO, 0);
        float expected = MathF.Atan2(memberY, 1) - memberO;
        if (expected < 0) expected += MathF.Tau;
        Assert.Equal(expected, OutdoorPvPWorldHost.FollowAngle(leader, member), 4);
    }

    [Fact]
    public void Each_continent_gets_its_script()
    {
        (ServiceProvider services, WorldRuntime world, OutdoorPvPFeature feature) = Start();
        using (services)
        using (world)
        {
            Assert.Equal(2, feature.Scripts.Count());
            Assert.NotNull(feature.EasternPlaguelands);
            Assert.NotNull(feature.Silithus);
            Assert.Same(feature.EasternPlaguelands, feature.ScriptFor(0, 2017));
            Assert.Same(feature.Silithus, feature.ScriptFor(1, 3429));
            Assert.Null(feature.ScriptFor(0, 1377));
        }
    }

    [Fact]
    public void Zone_changes_move_a_player_between_scripts_and_fill_the_world_states()
    {
        (ServiceProvider services, WorldRuntime world, OutdoorPvPFeature feature) = Start();
        using (services)
        using (world)
        {
            Player player = NewPlayer(1, race: 2, mapId: 0);
            world.GetMap(0).AddPlayer(player);

            feature.OnZoneChanged(player, 0, EasternPlaguelandsCatalog.ZoneId, 0, null);
            Assert.True(feature.EasternPlaguelands!.HasPlayer(player.Guid));

            // Eastern Plaguelands into Scholomance: the same script, still present.
            feature.OnZoneChanged(player, EasternPlaguelandsCatalog.ZoneId, 2057, 0, null);
            Assert.True(feature.EasternPlaguelands.HasPlayer(player.Guid));

            var states = new List<WorldStatePair>();
            feature.Fill(player, 2057, states);
            Assert.Equal(33, states.Count);

            feature.OnZoneChanged(player, 2057, 12, 0, null);
            Assert.False(feature.EasternPlaguelands.HasPlayer(player.Guid));
            states.Clear();
            feature.Fill(player, 12, states);
            Assert.Empty(states);
        }
    }

    [Fact]
    public void The_crown_guard_graveyard_is_not_offered_while_unlinked()
    {
        (ServiceProvider services, WorldRuntime world, OutdoorPvPFeature feature) = Start();
        using (services)
        using (world)
        {
            Player horde = NewPlayer(1, race: 2, mapId: 0);
            Assert.Empty(feature.ExtraLinks(horde, EasternPlaguelandsCatalog.ZoneId, 0));
        }
    }

    [Fact]
    public void The_defense_message_is_zone_length_and_terminated_text()
    {
        byte[] packet = OutdoorPvPWorldHost.BuildDefenseMessage(139, "Hi");

        Assert.Equal(new byte[] { 139, 0, 0, 0, 3, 0, 0, 0 }.Concat(Encoding.UTF8.GetBytes("Hi")).Append((byte)0), packet);
    }
}
