using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.States;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Packets;
using Xunit;

namespace ArcaneCore.World.Tests.WorldState;

/// <summary>Real world-state contents in SMSG_INIT_WORLD_STATES: defaults and providers on zone entry only.</summary>
public sealed class WorldStateWorldTests
{
    private sealed class Locator : IZoneLocator
    {
        public volatile uint Zone = 12;
        public volatile uint Area = 9;

        public bool CanDeriveZones => true;

        public (uint ZoneId, uint AreaId) Locate(Map map, Player player) => (Zone, Area);

        public AreaTemplate? Find(uint areaId) => new AreaTemplate(areaId, 0, 0, areaId, 0, 1, "z", 0, 0);
    }

    private sealed class Recording : IWorldStateProvider
    {
        public List<uint> Zones { get; } = [];

        public void Fill(Player player, uint zoneId, List<WorldStatePair> states)
        {
            lock (Zones)
            {
                Zones.Add(zoneId);
            }

            states.Add(new WorldStatePair(0x0515, zoneId == 40 ? -3 : 1));
        }
    }

    [Fact]
    public async Task Providers_AreAskedOnLoginAndOnEachRealZoneChange_NotOnAnAreaChange()
    {
        await using var host = WorldTestHost.Start();
        var locator = new Locator();
        WorldStateHooks hooks = WorldStateHooks.For(host.World);
        hooks.Locator = locator;
        hooks.WorldStates.Defaults = [new WorldStatePair(0x0A, 7)];
        var provider = new Recording();
        hooks.WorldStates.Add(provider);

        await using WorldTestClient client = await host.EnterWorldAsync("STATES", "States");
        var login = new PacketReader(client.LoginPacket(WorldOpcode.SmsgInitWorldStates));
        Assert.Equal((0u, 12u, (ushort)2), (login.ReadUInt32(), login.ReadUInt32(), login.ReadUInt16()));
        Assert.Equal((0x0Au, 7), (login.ReadUInt32(), login.ReadInt32()));
        Assert.Equal((0x0515u, 1), (login.ReadUInt32(), login.ReadInt32()));
        Assert.Equal([12u], provider.Zones.ToArray());

        locator.Area = 10; // an area change in the same zone: no world states
        await client.AssertSilentAsync(TimeSpan.FromMilliseconds(1300));
        Assert.Equal([12u], provider.Zones.ToArray());

        locator.Zone = 40;
        var changed = new PacketReader(await client.ReadUntilAsync(WorldOpcode.SmsgInitWorldStates));
        Assert.Equal((0u, 40u, (ushort)2), (changed.ReadUInt32(), changed.ReadUInt32(), changed.ReadUInt16()));
        changed.ReadUInt32();
        changed.ReadInt32();
        Assert.Equal((0x0515u, -3), (changed.ReadUInt32(), changed.ReadInt32()));
        Assert.Equal([12u, 40u], provider.Zones.ToArray());

        hooks.WorldStates.Remove(provider);
        hooks.WorldStates.Defaults = [];
        locator.Zone = 12;
        var back = new PacketReader(await client.ReadUntilAsync(WorldOpcode.SmsgInitWorldStates));
        Assert.Equal((0u, 12u, (ushort)0), (back.ReadUInt32(), back.ReadUInt32(), back.ReadUInt16()));
    }

    [Fact]
    public void BuildInitWorldStates_StaysSourceCompatible_AndEmpty()
        => Assert.Equal([1, 0, 0, 0, 12, 0, 0, 0, 0, 0], LoginPackets.BuildInitWorldStates(1, 12));
}
