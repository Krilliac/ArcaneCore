using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Npc;
using Xunit;

namespace ArcaneCore.World.Tests.Npc;

/// <summary>NPC service opcodes over the socket: registration, exact payload lengths, and innkeeper/banker flows.</summary>
public sealed class NpcServiceWorldTests
{
    [Fact]
    public void Handlers_RegisterEveryServiceOpcode()
    {
        var table = new OpcodeTable();
        new NpcServiceHandlers().Register(table);
        foreach (WorldOpcode opcode in new[]
        {
            WorldOpcode.CmsgGossipSelectOption, WorldOpcode.CmsgListInventory, WorldOpcode.CmsgBuyItem, WorldOpcode.CmsgBuyItemInSlot,
            WorldOpcode.CmsgSellItem, WorldOpcode.CmsgBuybackItem, WorldOpcode.CmsgRepairItem, WorldOpcode.CmsgTrainerList,
            WorldOpcode.CmsgTrainerBuySpell, WorldOpcode.CmsgBinderActivate, WorldOpcode.CmsgBankerActivate, WorldOpcode.CmsgBuyBankSlot,
            WorldOpcode.CmsgSpiritHealerActivate, WorldOpcode.CmsgTaxinodeStatusQuery, WorldOpcode.CmsgTaxiqueryavailablenodes,
            WorldOpcode.CmsgActivatetaxi, WorldOpcode.CmsgActivatetaxiexpress, WorldOpcode.CmsgMoveSplineDone,
        })
        {
            Assert.True(table.TryGet(opcode, out _), opcode.ToString());
        }
    }

    [Fact]
    public void BuyItemInSlot_ReadsTheBagAndSlotTheClientSent()
    {
        // gtker/wow_messages cmsg_buy_item_in_slot (versions "1 2"): u64 vendor, u32 item, u64 bag, u8 bag_slot, u8 amount.
        byte[] payload =
        [
            0x64, 0, 0, 0, 0, 0, 0, 0,        // vendor 100
            0xC8, 0, 0, 0,                    // item 200
            0x2C, 0x01, 0, 0, 0, 0, 0, 0,     // bag 300
            0x07,                             // bag slot 7
            0x03,                             // amount 3
        ];
        NpcServiceHandlers.BuyItemInSlotRequest request = NpcServiceHandlers.ReadBuyItemInSlot(payload);
        Assert.Equal(100ul, request.Vendor.Value);
        Assert.Equal(200u, request.Item);
        Assert.Equal(300ul, request.Bag.Value);
        Assert.Equal((byte)7, request.Slot);
        Assert.Equal((byte)3, request.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => NpcServiceHandlers.ReadBuyItemInSlot(payload[..21]));
        Assert.Throws<ArgumentOutOfRangeException>(() => NpcServiceHandlers.ReadBuyItemInSlot([.. payload, 0]));
    }

    public static TheoryData<WorldOpcode, byte[]> MalformedPayloads() => new()
    {
        { WorldOpcode.CmsgListInventory, new byte[7] },
        { WorldOpcode.CmsgBuyItem, new byte[13] },
        { WorldOpcode.CmsgBuyItem, new byte[15] },
        { WorldOpcode.CmsgBuyItemInSlot, new byte[21] },
        { WorldOpcode.CmsgSellItem, new byte[16] },
        { WorldOpcode.CmsgBuybackItem, new byte[11] },
        { WorldOpcode.CmsgRepairItem, new byte[17] },
        { WorldOpcode.CmsgTrainerList, new byte[9] },
        { WorldOpcode.CmsgTrainerBuySpell, new byte[11] },
        { WorldOpcode.CmsgBinderActivate, new byte[7] },
        { WorldOpcode.CmsgBankerActivate, new byte[9] },
        { WorldOpcode.CmsgBuyBankSlot, new byte[7] },
        { WorldOpcode.CmsgSpiritHealerActivate, new byte[9] },
        { WorldOpcode.CmsgTaxinodeStatusQuery, new byte[7] },
        { WorldOpcode.CmsgTaxiqueryavailablenodes, new byte[9] },
        { WorldOpcode.CmsgActivatetaxi, new byte[15] },
        { WorldOpcode.CmsgActivatetaxiexpress, Express(2, 1) },          // count says 2, one node sent
        { WorldOpcode.CmsgActivatetaxiexpress, Express(1, 2) },          // trailing node
        { WorldOpcode.CmsgActivatetaxiexpress, Express(100_000, 0) },    // absurd count
        { WorldOpcode.CmsgGossipSelectOption, new byte[11] },
        { WorldOpcode.CmsgGossipSelectOption, [.. new byte[12], (byte)'a'] },          // unterminated code
        { WorldOpcode.CmsgGossipSelectOption, [.. new byte[12], (byte)'a', 0, 0] },    // two strings
    };

    private static byte[] Express(uint count, int nodes)
    {
        var w = new PacketWriter(16 + (nodes * 4));
        w.WriteUInt64(QuestInteractionFixture.Guid.Value);
        w.WriteUInt32(0);
        w.WriteUInt32(count);
        for (int i = 0; i < nodes; i++)
        {
            w.WriteUInt32((uint)i + 1);
        }

        return w.ToArray();
    }

    [Theory]
    [MemberData(nameof(MalformedPayloads))]
    public async Task MalformedServicePayload_Disconnects(WorldOpcode opcode, byte[] payload)
    {
        await using WorldTestHost host = Start(new QuestInteractionFixture());
        await using WorldTestClient client = await host.EnterWorldAsync("BADSERVICE", "Badservice");
        await client.CollectAsync();
        await client.SendAsync(opcode, payload);
        Assert.True(await client.IsClosedByServerAsync());
    }

    [Fact]
    public async Task Innkeeper_BindsTheHomeOverTheSocket()
    {
        var fixture = new QuestInteractionFixture();
        await using WorldTestHost host = Start(fixture);
        await using WorldTestClient client = await host.EnterWorldAsync("INNGUEST", "Innguest");
        await VisibleAsync(host, "Innguest");
        await host.OnWorldAsync(() => Creature(host, "Innguest").NpcFlags = (uint)(NpcFlags.Innkeeper | NpcFlags.Banker));
        await client.CollectAsync();

        await client.SendAsync(WorldOpcode.CmsgBinderActivate, GuidBody());
        byte[] bound = await client.ReadUntilAsync(WorldOpcode.SmsgPlayerbound);
        Assert.Equal(QuestInteractionFixture.Guid.Value, BitConverter.ToUInt64(bound, 0));
        (float x, float y) = await host.PlayerStateAsync("Innguest", p => (p.X, p.Y));
        Assert.Equal((x, y), await host.PlayerStateAsync("Innguest", p => (p.Home.X, p.Home.Y)));

        await client.SendAsync(WorldOpcode.CmsgBankerActivate, GuidBody());
        byte[] bank = await client.ReadUntilAsync(WorldOpcode.SmsgShowBank);
        Assert.Equal(QuestInteractionFixture.Guid.Value, BitConverter.ToUInt64(bank, 0));
        Assert.True(await host.PlayerStateAsync("Innguest", p => p.Inventory.BankUsable));

        // A living player gets nothing from a spirit healer request; the session stays open.
        await client.SendAsync(WorldOpcode.CmsgSpiritHealerActivate, GuidBody());
        await client.SendAsync(WorldOpcode.CmsgMoveSplineDone, []);
        await client.SendAsync(WorldOpcode.CmsgBankerActivate, GuidBody());
        await client.ReadUntilAsync(WorldOpcode.SmsgShowBank);
        Assert.True(await host.PlayerStateAsync("Innguest", p => p.IsAlive));
    }

    private static Creature Creature(WorldTestHost host, string name)
        => (Creature)host.World.FindOnlinePlayer(name)!.Map!.FindObject(QuestInteractionFixture.Guid)!;

    private static WorldTestHost Start(QuestInteractionFixture fixture)
    {
        QuestInteractionTestServices.Current.Value = fixture;
        try { return WorldTestHost.Start(); }
        finally { QuestInteractionTestServices.Current.Value = null; }
    }

    private static Task VisibleAsync(WorldTestHost host, string name) => host.WaitForWorldAsync(
        () => host.World.FindOnlinePlayer(name)!.VisibleObjects.Contains(QuestInteractionFixture.Guid), "service npc becomes visible");

    private static byte[] GuidBody()
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt64(QuestInteractionFixture.Guid.Value);
        return writer.ToArray();
    }
}
