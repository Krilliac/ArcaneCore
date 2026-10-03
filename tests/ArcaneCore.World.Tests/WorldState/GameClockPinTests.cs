using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Time;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests.WorldState;

/// <summary>
/// Characterization of the game clock: these pass on the code as it is and keep it that way. A far teleport sends
/// SMSG_LOGIN_SETTIMESPEED again (vmangos <c>HandleMoveWorldportAckOpcode</c> repeats <c>SendInitialPacketsBeforeAddToMap</c>,
/// Player.cpp:19141-19145) in the CONFIGURED local zone, computed anew each time.
/// </summary>
public sealed class GameClockPinTests
{
    [Fact]
    public async Task FarTeleport_ResendsTheTimeSpeed_AsTheLocalTimeNow_InTheConfiguredZone()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        var zone = TimeZoneInfo.CreateCustomTimeZone("test-7", TimeSpan.FromHours(-7), "test", "test");
        var clock = new FixedGameTime(new DateTimeOffset(2022, 8, 13, 8, 10, 0, TimeSpan.Zero), zone);
        WorldStateHooks.For(host.World).Time = clock;
        await using WorldTestClient gm = await host.EnterWorldAsync("CLOCKTP", "Clocktp", AccountSecurity.GameMaster);
        Assert.Equal(GameTimePacker.Pack(new DateTimeOffset(2022, 8, 13, 1, 10, 0, TimeSpan.FromHours(-7))), BitConverter.ToUInt32(gm.LoginPacket(WorldOpcode.SmsgLoginSettimespeed), 0));

        // an hour and a half later, in the middle of the day
        clock.UtcNow = new DateTimeOffset(2022, 8, 13, 9, 40, 0, TimeSpan.Zero);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".tele cross");
        await gm.ReadUntilAsync(WorldOpcode.SmsgNewWorld);
        await gm.SendAsync(WorldOpcode.MsgMoveWorldportAck, []);
        byte[] resent = await gm.ReadUntilAsync(WorldOpcode.SmsgLoginSettimespeed);

        Assert.Equal(8, resent.Length);
        Assert.Equal(GameTimePacker.Pack(new DateTimeOffset(2022, 8, 13, 2, 40, 0, TimeSpan.FromHours(-7))), BitConverter.ToUInt32(resent, 0));
        Assert.Equal(new byte[] { 0x89, 0x88, 0x88, 0x3C }, resent[4..]);
    }
}
