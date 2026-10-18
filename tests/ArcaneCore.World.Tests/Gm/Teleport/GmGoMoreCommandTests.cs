using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Tests.GridTerrain;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Teleport;

/// <summary>.go target, trigger, xyzo, grid, forward, up and relative (vmangos TeleportCommands.cpp:280-322, 762-775, 878-1036).</summary>
public sealed class GmGoMoreCommandTests
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(200);

    [Fact]
    public void Levels_AreTicketMaster()
    {
        CommandTable table = ChatCommands.CreateTable();
        foreach (string path in new[] { "go target", "go trigger", "go xyzo", "go grid", "go forward", "go up", "go relative" })
        {
            Assert.Null(table.Resolve(path, AccountSecurity.Moderator));
            ChatCommand? command = table.Resolve(path, AccountSecurity.GameMaster);
            Assert.NotNull(command);
            Assert.Equal(2, command.RequiredLevel(table.Gm));
        }
    }

    [Fact]
    public async Task Trigger_GoesToTheTrigger_OrItsTarget_AndNamesUnknownIds()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("GOTRGM", "Gotrgm", AccountSecurity.Administrator);
        await gm.CollectAsync(Quiet);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".go trigger 4242");
        Assert.Equal("Areatrigger ID 4242 not found!", (await gm.ReadChatAsync()).Text);

        await gm.SendChatAsync(ChatType.Say, Language.Common, $".go trigger {InMemoryMapDataStore.ShortcutTrigger} target");
        MovementInfo target = await AcknowledgeAsync(gm, host, "Gotrgm");
        Assert.Equal((-8913.23f, 554.633f, 93.7944f), (target.X, target.Y, target.Z));

        await gm.SendChatAsync(ChatType.Say, Language.Common, $".go trigger {InMemoryMapDataStore.ShortcutTrigger}");
        MovementInfo trigger = await AcknowledgeAsync(gm, host, "Gotrgm");
        Assert.Equal((-8940f, -132.5f, 83.5f), (trigger.X, trigger.Y, trigger.Z));
    }

    [Fact]
    public async Task Xyzo_SetsTheFacing_AndRelativeMovesAlongIt()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("GORELGM", "Gorelgm", AccountSecurity.Administrator);
        await gm.CollectAsync(Quiet);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".go xyzo -8900 -130 90 0");
        MovementInfo placed = await AcknowledgeAsync(gm, host, "Gorelgm");
        Assert.Equal((-8900f, -130f, 90f, 0f), (placed.X, placed.Y, placed.Z, placed.Orientation));

        // Facing east (orientation 0): forward is +x, left is +y.
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".go forward 5");
        MovementInfo ahead = await AcknowledgeAsync(gm, host, "Gorelgm");
        Assert.Equal(-8895f, ahead.X, 3);
        Assert.Equal(-130f, ahead.Y, 3);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".go up 3");
        MovementInfo above = await AcknowledgeAsync(gm, host, "Gorelgm");
        Assert.Equal(93f, above.Z, 3);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".go relative -5 2 -3");
        Assert.Equal("Teleportation : Forward-Backward -5.000000 Left-Right 2.000000 Up-Down -3.000000", (await gm.ReadChatAsync()).Text);
        MovementInfo back = await AcknowledgeAsync(gm, host, "Gorelgm");
        Assert.Equal(-8900f, back.X, 3);
        Assert.Equal(-128f, back.Y, 3);
        Assert.Equal(90f, back.Z, 3);
    }

    [Fact]
    public async Task Target_NeedsASelectionOnTheMap()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("GOTGGM", "Gotggm", AccountSecurity.Administrator);
        await using WorldTestClient other = await host.EnterWorldAsync("GOTGOT", "Gotgot");
        await host.PlaceAsync("Gotggm", -8800f, -100f, 90f);
        await host.PlaceAsync("Gotgot", -8790f, -95f, 91f);
        await gm.CollectAsync(Quiet);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".go target");
        Assert.Equal("You should select a character or a creature.", (await gm.ReadChatAsync()).Text);

        ObjectGuid otherGuid = await host.PlayerStateAsync("Gotgot", p => p.Guid);
        await host.PlayerStateAsync("Gotggm", p => p.Selection = otherGuid);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".go target");
        MovementInfo arrival = await AcknowledgeAsync(gm, host, "Gotggm");
        Assert.Equal((-8790f, -95f, 91f), (arrival.X, arrival.Y, arrival.Z));
    }

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