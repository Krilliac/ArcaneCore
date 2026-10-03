using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Teleport;

/// <summary>
/// .recall, .goname, .namego and .tele name (vmangos TeleportCommands.cpp:657-711, 1106-1346;
/// levels Chat.cpp:1006,1236-1237,1259; texts mangos_string 102, 108-111, 113-114, 164, 171, 499).
/// Accounts are Administrator so the tests hold under any security map.
/// </summary>
public sealed class GmTeleportTests
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(200);

    private static string Link(string name) => $"|cffffffff|Hplayer:{name}|h[{name}]|h|r";

    [Fact]
    public void Levels_FollowTheVmangosTable()
    {
        CommandTable table = ChatCommands.CreateTable();
        Assert.NotNull(table.Resolve("recall", AccountSecurity.Moderator));      // SEC_MODERATOR
        foreach (string path in new[] { "goname", "namego", "tele name" })       // SEC_TICKETMASTER (2): reached by GameMaster+ under the default map
        {
            Assert.Null(table.Resolve(path, AccountSecurity.Moderator));
            Assert.NotNull(table.Resolve(path, AccountSecurity.GameMaster));
        }
    }

    [Fact]
    public async Task Recall_ReturnsToTheSpotBeforeTheLastCommandTeleport()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("TPRECGM", "Tprecgm", AccountSecurity.Administrator);
        await gm.CollectAsync(Quiet);
        (float x, float y, float z) start = await host.PlayerStateAsync("Tprecgm", p => (p.X, p.Y, p.Z));

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".go xyz -8900 -130 90");
        await AcknowledgeAsync(gm, host, "Tprecgm");
        Assert.Equal(-8900f, await host.PlayerStateAsync("Tprecgm", p => p.X));

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".recall");
        MovementInfo back = await AcknowledgeAsync(gm, host, "Tprecgm");
        Assert.Equal(start, (back.X, back.Y, back.Z));
    }

    [Fact]
    public async Task Namego_SummonsThePlayerToTheCaller_AndTellsBoth()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("TPNGGM", "Tpnggm", AccountSecurity.Administrator);
        await using WorldTestClient victim = await host.EnterWorldAsync("TPNGVIC", "Tpngvic");
        await host.PlaceAsync("Tpnggm", -8800f, -100f, 90f);
        await host.PlaceAsync("Tpngvic", -8700f, -50f, 91f);
        await gm.CollectAsync(Quiet);
        await victim.CollectAsync(Quiet);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".namego tpngvic");

        Assert.Equal($"You are summoning {Link("Tpngvic")}.", (await gm.ReadChatAsync()).Text);
        Assert.Equal($"You are being summoned by {Link("Tpnggm")}.", (await victim.ReadChatAsync()).Text);
        MovementInfo arrival = await AcknowledgeAsync(victim, host, "Tpngvic");
        Assert.Equal((-8800f, -100f, 90f), (arrival.X, arrival.Y, arrival.Z));

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".namego tpnggm");
        Assert.Equal("You can't teleport self to self!", (await gm.ReadChatAsync()).Text);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".namego Nobodyhere");
        Assert.Equal("Player not found!", (await gm.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task Goname_TeleportsTheCallerNextToThePlayer_FiveYardsAbove()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("TPGNGM", "Tpgngm", AccountSecurity.Administrator);
        await using WorldTestClient victim = await host.EnterWorldAsync("TPGNVIC", "Tpgnvic");
        await host.PlaceAsync("Tpgngm", -8800f, -100f, 90f);
        await host.PlaceAsync("Tpgnvic", -8700f, -100f, 91f);
        await gm.CollectAsync(Quiet);
        await victim.CollectAsync(Quiet);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".goname Tpgnvic");

        Assert.Equal($"Appearing at {Link("Tpgnvic")}'s location.", (await gm.ReadChatAsync()).Text);
        Assert.Equal($"{Link("Tpgngm")} is appearing to your location.", (await victim.ReadChatAsync()).Text);
        MovementInfo arrival = await AcknowledgeAsync(gm, host, "Tpgngm");
        Assert.Equal((-8700f, -100f, 96f), (arrival.X, arrival.Y, arrival.Z));
        Assert.Equal(0f, arrival.Orientation, 3);   // the victim lies due +X of the caller: GetAngle = atan2(0, 100)
    }

    [Fact]
    public async Task TeleName_SendsAPlayerToAGameTele_AndNeedsATeleportLocation()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("TPTNGM", "Tptngm", AccountSecurity.Administrator);
        await using WorldTestClient victim = await host.EnterWorldAsync("TPTNVIC", "Tptnvic");
        await gm.CollectAsync(Quiet);
        await victim.CollectAsync(Quiet);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".tele name tptnvic Stormwind");

        Assert.Equal($"You are teleporting {Link("Tptnvic")} to Stormwind.", (await gm.ReadChatAsync()).Text);
        Assert.Equal($"You are being teleported by {Link("Tptngm")}.", (await victim.ReadChatAsync()).Text);
        MovementInfo arrival = await AcknowledgeAsync(victim, host, "Tptnvic");
        Assert.Equal((-8913.23f, 554.633f, 93.7944f), (arrival.X, arrival.Y, arrival.Z));

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".tele name Tptnvic Nosuchplace");
        Assert.Equal("Teleport location not found!", (await gm.ReadChatAsync()).Text);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".tele name Nobodyhere Stormwind");
        Assert.Equal("Player not found!", (await gm.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task TeleName_WithOnlyALocation_TeleportsTheSelection_OrTheCaller()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("TPTSGM", "Tptsgm", AccountSecurity.Administrator);
        await gm.CollectAsync(Quiet);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".tele name Stormwind");

        Assert.Equal($"You are teleporting {Link("Tptsgm")} to Stormwind.", (await gm.ReadChatAsync()).Text);
        MovementInfo arrival = await AcknowledgeAsync(gm, host, "Tptsgm");
        Assert.Equal(-8913.23f, arrival.X);
    }

    /// <summary>Read the pending near-teleport's ack packet, send the client ack and wait until the player is there.</summary>
    private static async Task<MovementInfo> AcknowledgeAsync(WorldTestClient client, WorldTestHost host, string name)
    {
        byte[] ack = await client.ReadUntilAsync(WorldOpcode.MsgMoveTeleportAck);
        var reader = new PacketReader(ack);
        ulong guid = reader.ReadPackedGuid();
        uint counter = reader.ReadUInt32();
        MovementInfo info = MovementInfo.Read(ref reader);

        var reply = new PacketWriter(16);
        reply.WriteUInt64(guid);
        reply.WriteUInt32(counter);
        reply.WriteUInt32(0);
        await client.SendAsync(WorldOpcode.MsgMoveTeleportAck, reply.ToArray());
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(name)!.X == info.X, "teleport ack handled");
        return info;
    }
}
