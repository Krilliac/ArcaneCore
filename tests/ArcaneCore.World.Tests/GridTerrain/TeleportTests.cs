using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Teleport;
using Xunit;

namespace ArcaneCore.World.Tests.GridTerrain;

/// <summary>
/// Teleports end to end over real sessions: <c>.go xyz</c> / <c>.tele</c>, the near-teleport
/// ack, the far-teleport transfer (SMSG_TRANSFER_PENDING → SMSG_NEW_WORLD →
/// MSG_MOVE_WORLDPORT_ACK → login packets on the new map) and CMSG_AREATRIGGER.
/// </summary>
public sealed class TeleportTests
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(200);

    private static byte[] TeleportAck(ulong guid, uint counter)
    {
        var ack = new PacketWriter(16);
        ack.WriteUInt64(guid);
        ack.WriteUInt32(counter);
        ack.WriteUInt32(0);
        return ack.ToArray();
    }

    private static byte[] AreaTrigger(uint id)
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, id);
        return payload;
    }

    [Fact]
    public async Task Feature_LoadsTheMapTables_AndSkipsTeleportsWithoutATrigger()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldMaps maps = await host.OnWorldAsync(() => WorldMaps.Of(host.World));

        Assert.True(maps.Registry.Contains(36));
        Assert.NotNull(maps.FindAreaTriggerTeleport(InMemoryMapDataStore.DeadminesTrigger));
        Assert.Null(maps.FindAreaTriggerTeleport(InMemoryMapDataStore.OrphanTeleport));
        Assert.Equal("Stormwind", maps.FindGameTele("storm")?.Name);
    }

    [Fact]
    public async Task GoXyz_NearTeleport_MovesOnTheAck_AndTellsObservers()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("TPNEARGM", "Tpneargm", AccountSecurity.GameMaster);
        await using WorldTestClient bob = await host.EnterWorldAsync("TPNEARBOB", "Tpnearbob");
        await gm.CollectAsync(Quiet);
        await bob.CollectAsync(Quiet);
        ulong guid = await host.PlayerStateAsync("Tpneargm", p => p.Guid.Value);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".go xyz -8900 -130 90");
        byte[] ack = await gm.ReadUntilAsync(WorldOpcode.MsgMoveTeleportAck);
        var reader = new PacketReader(ack);
        Assert.Equal(guid, reader.ReadPackedGuid());
        uint counter = reader.ReadUInt32();
        MovementInfo info = MovementInfo.Read(ref reader);
        Assert.Equal((-8900f, -130f, 90f), (info.X, info.Y, info.Z));
        Assert.Equal(-8949.95f, await host.PlayerStateAsync("Tpneargm", p => p.X)); // not before the ack

        // A heartbeat sent before the teleport ack must not overwrite the pending position
        // or be relayed to observers (vmangos HandleMovementOpcodes teleport semaphore).
        MovementInfo stale = await host.PlayerStateAsync("Tpneargm", p => p.Movement);
        stale.X = -8800;
        var stalePacket = new PacketWriter();
        stale.Write(stalePacket);
        await gm.SendAsync(WorldOpcode.MsgMoveHeartbeat, stalePacket.ToArray());
        Assert.DoesNotContain(await bob.CollectAsync(Quiet), p => p.Opcode == WorldOpcode.MsgMoveHeartbeat);
        Assert.Equal(-8949.95f, await host.PlayerStateAsync("Tpneargm", p => p.X));

        await gm.SendAsync(WorldOpcode.MsgMoveTeleportAck, TeleportAck(guid, counter));
        byte[] observed = await bob.ReadUntilAsync(WorldOpcode.MsgMoveTeleport);
        var observedReader = new PacketReader(observed);
        Assert.Equal(guid, observedReader.ReadPackedGuid());
        Assert.Equal(-8900f, MovementInfo.Read(ref observedReader).X);

        await host.OnWorldAsync(() => true); // the ack has been handled once a later command runs
        Assert.Equal((-8900f, -130f, 90f), await host.PlayerStateAsync("Tpneargm", p => (p.X, p.Y, p.Z)));
    }

    [Fact]
    public async Task Tele_FarTeleport_TransfersToTheNewMap_WithTheLoginPackets()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("TPFARGM", "Tpfargm", AccountSecurity.GameMaster);
        await using WorldTestClient bob = await host.EnterWorldAsync("TPFARBOB", "Tpfarbob");
        await gm.CollectAsync(Quiet);
        await bob.CollectAsync(Quiet);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".tele cross");
        byte[] pending = await gm.ReadUntilAsync(WorldOpcode.SmsgTransferPending);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(pending));
        (WorldOpcode op, byte[] newWorld) = await gm.ReadAsync();
        Assert.Equal(WorldOpcode.SmsgNewWorld, op);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(newWorld));
        Assert.Equal(-441.8f, BinaryPrimitives.ReadSingleLittleEndian(newWorld.AsSpan(4)));

        // Bob's client drops the departed player.
        Assert.Contains(await bob.CollectAsync(Quiet), p => p.Opcode is WorldOpcode.SmsgDestroyObject);

        // While loading, everything but the world-port ack is dropped.
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".gps");
        Assert.DoesNotContain(await gm.CollectAsync(Quiet), p => p.Opcode == WorldOpcode.SmsgMessagechat);
        Assert.Null(await host.PlayerStateAsync("Tpfargm", p => p.Map));

        await gm.SendAsync(WorldOpcode.MsgMoveWorldportAck, []);
        WorldOpcode[] expected =
        [
            WorldOpcode.SmsgSetRestStart, WorldOpcode.SmsgBindpointupdate, WorldOpcode.SmsgTutorialFlags,
            WorldOpcode.SmsgInitialSpells, WorldOpcode.SmsgActionButtons, WorldOpcode.SmsgInitializeFactions,
            WorldOpcode.SmsgLoginSettimespeed, WorldOpcode.SmsgUpdateObject, WorldOpcode.SmsgInitWorldStates,
        ];
        foreach (WorldOpcode opcode in expected)
        {
            Assert.Equal(opcode, (await gm.ReadAsync()).Opcode);
        }

        Assert.Equal((1u, -441.8f), await host.PlayerStateAsync("Tpfargm", p => (p.Map!.MapId, p.X)));

        // In the world again: commands work.
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".gps");
        Assert.Contains("map 1", (await gm.ReadChatAsync()).Text);
    }

    [Theory]
    [InlineData(".tele nosuchplace", TeleportCommands.TeleNotFoundText)]
    [InlineData(".tele Nowhere", "Target map or coordinates is invalid (X: 1.000000 Y: 1.000000 MapId: 99)")]
    [InlineData(".go xyz 100 100", "Target map or coordinates is invalid (X: 100.000000 Y: 100.000000 MapId: 0)")] // no terrain data
    [InlineData(".go xyz 99999 0 0", "Target map or coordinates is invalid (X: 99999.000000 Y: 0.000000 MapId: 0)")]
    public async Task Commands_ExplainWhyTheyCannotTeleport(string command, string reply)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("TPBADGM", "Tpbadgm", AccountSecurity.GameMaster);
        await gm.CollectAsync(Quiet);

        await gm.SendChatAsync(ChatType.Say, Language.Common, command);

        Assert.Equal(reply, (await gm.ReadChatAsync()).Text);
        Assert.Equal(0u, await host.PlayerStateAsync("Tpbadgm", p => p.MapId));
    }

    [Fact]
    public async Task TeleportCommands_NeedTheTicketMasterLevel()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient player = await host.EnterWorldAsync("TPPLAIN", "Tpplain");
        await player.CollectAsync(Quiet);

        await player.SendChatAsync(ChatType.Say, Language.Common, ".tele Stormwind");

        Assert.Equal("This command is not available to you.", (await player.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task AreaTrigger_BelowTheRequiredLevel_ShowsTheMessage_AndGmModeSkipsIt()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("TPTRIGGM", "Tptriggm", AccountSecurity.GameMaster);
        await gm.CollectAsync(Quiet);

        // Out of the trigger (more than 5 yards outside the box): ignored.
        await host.PlaceAsync("Tptriggm", -8980f, -132.5f, 83.5f);
        await gm.SendAsync(WorldOpcode.CmsgAreatrigger, AreaTrigger(InMemoryMapDataStore.DeadminesTrigger));
        Assert.Empty(await gm.CollectAsync(Quiet));

        // In it at level 1: SMSG_AREA_TRIGGER_MESSAGE with mangos_string 49.
        await host.PlaceAsync("Tptriggm", -8962f, -130f, 84f);
        await gm.SendAsync(WorldOpcode.CmsgAreatrigger, AreaTrigger(InMemoryMapDataStore.DeadminesTrigger));
        byte[] message = await gm.ReadUntilAsync(WorldOpcode.SmsgAreaTriggerMessage);
        string text = "You must be at least level 10 to enter.";
        Assert.Equal((uint)(text.Length + 1), BinaryPrimitives.ReadUInt32LittleEndian(message));
        Assert.Equal(text, System.Text.Encoding.UTF8.GetString(message, 4, text.Length));
        Assert.Equal(0, message[^1]);

        // GM mode ignores the level requirement: the dungeon transfer starts.
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".gm on");
        await gm.CollectAsync(Quiet);
        await gm.SendAsync(WorldOpcode.CmsgAreatrigger, AreaTrigger(InMemoryMapDataStore.DeadminesTrigger));
        byte[] pending = await gm.ReadUntilAsync(WorldOpcode.SmsgTransferPending);
        Assert.Equal(36u, BinaryPrimitives.ReadUInt32LittleEndian(pending));
    }

    [Fact]
    public async Task AreaTrigger_OnTheSameMap_IsANearTeleport()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient player = await host.EnterWorldAsync("TPTRIGNEAR", "Tptrignear");
        await player.CollectAsync(Quiet);

        await host.PlaceAsync("Tptrignear", -8941f, -131f, 83.5f);
        await player.SendAsync(WorldOpcode.CmsgAreatrigger, AreaTrigger(InMemoryMapDataStore.ShortcutTrigger));

        var reader = new PacketReader(await player.ReadUntilAsync(WorldOpcode.MsgMoveTeleportAck));
        reader.ReadPackedGuid();
        reader.ReadUInt32();
        MovementInfo info = MovementInfo.Read(ref reader);
        Assert.Equal((-8913.23f, 554.633f, 0.5f), (info.X, info.Y, info.Orientation));
    }

    [Fact]
    public async Task UnknownAreaTrigger_IsIgnored()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient player = await host.EnterWorldAsync("TPTRIGNONE", "Tptrignone");
        await player.CollectAsync(Quiet);

        await player.SendAsync(WorldOpcode.CmsgAreatrigger, AreaTrigger(4242));

        Assert.Empty(await player.CollectAsync(Quiet));
        Assert.True(await host.PlayerStateAsync("Tptrignone", p => p.Map is not null));
    }
}
