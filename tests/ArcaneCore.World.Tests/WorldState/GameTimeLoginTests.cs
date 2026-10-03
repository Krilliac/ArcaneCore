using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Time;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests.WorldState;

/// <summary>SMSG_LOGIN_SETTIMESPEED carries the server's local time (vmangos Player.cpp:19141-19145, Misc.cpp:924-944).</summary>
public sealed class GameTimeLoginTests
{
    [Fact]
    public async Task Login_SendsTheLocalGameTime_WithTheOneSixtiethTimescale()
    {
        await using var host = WorldTestHost.Start();
        var zone = TimeZoneInfo.CreateCustomTimeZone("test-7", TimeSpan.FromHours(-7), "test", "test");
        WorldStateHooks.For(host.World).Time = new FixedGameTime(new DateTimeOffset(2022, 8, 13, 8, 10, 0, TimeSpan.Zero), zone);

        await using WorldTestClient client = await host.EnterWorldAsync("CLOCK", "Clock");
        byte[] packet = client.LoginPacket(WorldOpcode.SmsgLoginSettimespeed);

        Assert.Equal(8, packet.Length);
        // 2022-08-13 01:10 local (Saturday): 0x1673 (year, month, day, weekday) with hour 1 and minute 10
        uint expected = GameTimePacker.Pack(new DateTimeOffset(2022, 8, 13, 1, 10, 0, TimeSpan.FromHours(-7)));
        Assert.Equal(expected, BitConverter.ToUInt32(packet, 0));
        Assert.Equal(new byte[] { 0x89, 0x88, 0x88, 0x3C }, packet[4..]);

        // and it precedes the world states in the login order (vmangos Player.cpp:19141 before :19154)
        int time = client.LastLoginPackets.FindIndex(p => p.Opcode == WorldOpcode.SmsgLoginSettimespeed);
        int states = client.LastLoginPackets.FindIndex(p => p.Opcode == WorldOpcode.SmsgInitWorldStates);
        Assert.InRange(time, 0, states - 1);
    }

    [Fact]
    public async Task Login_UsesUtc_WhenServerLocalTimeIsSwitchedOff()
    {
        await using var host = WorldTestHost.Start();
        var zone = TimeZoneInfo.CreateCustomTimeZone("test-7", TimeSpan.FromHours(-7), "test", "test");
        WorldStateHooks hooks = WorldStateHooks.For(host.World);
        hooks.Time = new FixedGameTime(new DateTimeOffset(2022, 8, 13, 8, 10, 0, TimeSpan.Zero), zone);
        hooks.TimeSettings.UseServerLocalTime = false;

        await using WorldTestClient client = await host.EnterWorldAsync("CLOCKU", "Clocku");

        Assert.Equal(0x1673320Au, BitConverter.ToUInt32(client.LoginPacket(WorldOpcode.SmsgLoginSettimespeed), 0));
    }
}
