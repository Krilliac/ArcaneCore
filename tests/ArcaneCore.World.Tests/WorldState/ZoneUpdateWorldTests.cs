using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests.WorldState;

/// <summary>
/// The zone is derived server-side (vmangos MiscHandler.cpp:381-386, Player.cpp:1215-1236,
/// :19154-19159) and SMSG_INIT_WORLD_STATES follows real zone changes only.
/// </summary>
public sealed class ZoneUpdateWorldTests
{
    private sealed class Locator(bool hasAreaData) : IZoneLocator
    {
        public volatile uint Zone = 40;
        public volatile uint Area = 87;

        public bool CanDeriveZones => hasAreaData;

        public (uint ZoneId, uint AreaId) Locate(Map map, Player player) => (Zone, Area);

        public AreaTemplate? Find(uint areaId)
            => areaId is 12 or 40 ? new AreaTemplate(areaId, 0, 0, areaId, 0, 1, "zone", 0, 0) : null;
    }

    private static byte[] ZonePayload(uint zone)
    {
        var writer = new PacketWriter(4);
        writer.WriteUInt32(zone);
        return writer.ToArray();
    }

    private static uint ZoneOf(byte[] initWorldStates)
    {
        var reader = new PacketReader(initWorldStates);
        reader.ReadUInt32();
        return reader.ReadUInt32();
    }

    [Fact]
    public async Task Login_SendsTheWorldStatesOfTheWalkedZone_NotTheStoredOne()
    {
        await using var host = WorldTestHost.Start();
        WorldStateHooks.For(host.World).Locator = new Locator(hasAreaData: true);

        await using WorldTestClient client = await host.EnterWorldAsync("ZONER", "Zoner");

        Assert.Single(client.LastLoginPackets, p => p.Opcode == WorldOpcode.SmsgInitWorldStates);
        Assert.Equal(40u, ZoneOf(client.LoginPacket(WorldOpcode.SmsgInitWorldStates)));
        Assert.Equal(40u, await host.PlayerStateAsync("Zoner", p => p.ZoneId));
    }

    [Fact]
    public async Task ZoneUpdate_WithAreaData_IgnoresTheClientValue_AndSendsNothing()
    {
        await using var host = WorldTestHost.Start();
        WorldStateHooks.For(host.World).Locator = new Locator(hasAreaData: true);
        await using WorldTestClient client = await host.EnterWorldAsync("IGNORE", "Ignore");

        await client.SendAsync(WorldOpcode.CmsgZoneupdate, ZonePayload(12));
        await client.AssertSilentAsync(TimeSpan.FromMilliseconds(300));

        Assert.Equal(40u, await host.PlayerStateAsync("Ignore", p => p.ZoneId));
    }

    [Fact]
    public async Task ZoneUpdate_WithoutAreaData_AcceptsTheClientValue_Once()
    {
        await using var host = WorldTestHost.Start();
        WorldStateHooks.For(host.World).Locator = new Locator(hasAreaData: false);
        await using WorldTestClient client = await host.EnterWorldAsync("TRUST", "Trust");

        await client.SendAsync(WorldOpcode.CmsgZoneupdate, ZonePayload(40));
        Assert.Equal(40u, ZoneOf(await client.ReadUntilAsync(WorldOpcode.SmsgInitWorldStates)));

        await client.SendAsync(WorldOpcode.CmsgZoneupdate, ZonePayload(40)); // same zone: nothing new
        await client.AssertSilentAsync(TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public async Task WalkingIntoAnotherZone_SendsWorldStatesOnce_AndAnAreaChangeSendsNone()
    {
        await using var host = WorldTestHost.Start();
        var locator = new Locator(hasAreaData: true) { Zone = 12, Area = 9 };
        WorldStateHooks.For(host.World).Locator = locator;
        await using WorldTestClient client = await host.EnterWorldAsync("WALKER", "Walker");
        Assert.Equal(12u, await host.PlayerStateAsync("Walker", p => p.ZoneId));

        locator.Area = 10; // another area of the same zone: no packet
        await client.AssertSilentAsync(TimeSpan.FromMilliseconds(1300));

        locator.Zone = 40; // crossing the zone border: the 1 s timer picks it up
        Assert.Equal(40u, ZoneOf(await client.ReadUntilAsync(WorldOpcode.SmsgInitWorldStates)));
        Assert.Equal(40u, await host.PlayerStateAsync("Walker", p => p.ZoneId));
        await client.AssertSilentAsync(TimeSpan.FromMilliseconds(1300));
    }
}
