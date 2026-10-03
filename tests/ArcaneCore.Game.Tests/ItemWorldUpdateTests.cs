using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.ItemTestData;

namespace ArcaneCore.Game.Tests;

/// <summary>How items reach the client: create blocks at login, values updates to the owner, destroys.</summary>
public sealed class ItemWorldUpdateTests
{
    [Fact]
    public void Login_SendsItemCreates_BeforeThePlayer_EquipmentFirst_BagContentsAfterTheirBag()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        (Player player, FakeSession session) = CreatePlayer(1);
        var bag = new ItemInstanceData { Guid = 50, Entry = SmallBrownPouch, Count = 1 };
        var inBag = new ItemInstanceData { Guid = 51, Entry = RecruitsBoots, Count = 1 };
        var sword = new ItemInstanceData { Guid = 52, Entry = WornShortsword, Count = 1 };
        var jerky = new ItemInstanceData { Guid = 53, Entry = ToughJerky, Count = 3 };
        player.Inventory.Load(
        [
            new(0, InventorySlots.ItemStart, jerky),
            new(0, InventorySlots.BagStart, bag),
            new(50, 2, inBag),
            new(0, InventorySlots.MainHand, sword),
        ]);

        world.AddPlayer(player);
        (WorldOpcode opcode, byte[] payload) = session.Next();
        Assert.Equal(WorldOpcode.SmsgUpdateObject, opcode);
        List<(byte Type, ulong Guid, byte TypeId, Dictionary<int, uint> Values)> blocks = UpdateParser.Parse(payload);
        Assert.Equal([ObjectGuid.Item(52).Value, ObjectGuid.Item(50).Value, ObjectGuid.Item(51).Value, ObjectGuid.Item(53).Value, player.Guid.Value],
            blocks.Select(b => b.Guid));
        Assert.Equal([(byte)TypeId.Item, (byte)TypeId.Container, (byte)TypeId.Item, (byte)TypeId.Item, (byte)TypeId.Player], blocks.Select(b => b.TypeId));

        // Owner-only item fields are in the owner's create (stack count is UF_FLAG_OWNER_ONLY).
        Assert.Equal(3u, blocks[3].Values[UpdateFields.ItemFieldStackCount]);
        Assert.Equal((uint)player.Guid.Low, blocks[3].Values[UpdateFields.ItemFieldOwner]);

        // The player's own create carries the inventory slots and the visible weapon.
        Dictionary<int, uint> self = blocks[4].Values;
        Assert.Equal(52u, self[UpdateFields.PlayerFieldInvSlotHead + (InventorySlots.MainHand * 2)]);
        Assert.Equal(WornShortsword, self[UpdateFields.PlayerVisibleItem10 + (InventorySlots.MainHand * 12)]);
    }

    [Fact]
    public void ItemChanges_GoToTheOwnerOnly_AsValuesUpdates()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        (Player a, FakeSession sa) = CreatePlayer(1, 0, 0);
        (Player b, FakeSession sb) = CreatePlayer(2, 5, 0);
        a.Inventory.Load([new(0, InventorySlots.ItemStart, new ItemInstanceData { Guid = 60, Entry = ToughJerky, Count = 3 })]);
        world.AddPlayer(a);
        world.AddPlayer(b);
        world.RunTick(50);
        sa.Clear();
        sb.Clear();

        Item jerky = a.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.ItemStart)!;
        jerky.Count = 7;
        world.RunTick(50);

        (WorldOpcode op, byte[] payload) = sa.Next();
        Assert.Equal(WorldOpcode.SmsgUpdateObject, op);
        var block = Assert.Single(UpdateParser.Parse(payload));
        Assert.Equal(((byte)ObjectUpdateType.Values, jerky.Guid.Value), (block.Type, block.Guid));
        Assert.Equal(7u, block.Values[UpdateFields.ItemFieldStackCount]);
        Assert.True(sb.Sent.IsEmpty);
    }

    [Fact]
    public void NewItemInWorld_SendsCreateThenPushResult_AndDestroySendsDestroyObject()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        (Player player, FakeSession session) = CreatePlayer(1);
        player.Inventory.Load([]);
        world.AddPlayer(player);
        session.Clear();

        Item item = Give(player.Inventory, RecruitsShirt);
        Assert.Equal(WorldOpcode.SmsgItemPushResult, session.Next().Opcode); // the create waits for the flush
        world.RunTick(50);
        (WorldOpcode op, byte[] payload) = session.Next();
        Assert.Equal(WorldOpcode.SmsgUpdateObject, op);
        List<(byte Type, ulong Guid, byte TypeId, Dictionary<int, uint> Values)> blocks = UpdateParser.Parse(payload);
        Assert.Equal(item.Guid.Value, blocks[0].Guid);
        Assert.Equal((byte)ObjectUpdateType.CreateObject, blocks[0].Type);
        Assert.Equal(item.Guid.Value, ((ulong)blocks[1].Values[UpdateFields.PlayerFieldInvSlotHead + (InventorySlots.ItemStart * 2) + 1] << 32)
            | blocks[1].Values[UpdateFields.PlayerFieldInvSlotHead + (InventorySlots.ItemStart * 2)]);
        session.Clear();

        player.Inventory.DestroyItemRequest(item.BagSlot, item.Slot, 0);
        (op, payload) = session.Next();
        Assert.Equal(WorldOpcode.SmsgDestroyObject, op);
        Assert.Equal(item.Guid.Value, BitConverter.ToUInt64(payload));
    }

    [Fact]
    public void Save_CarriesTheInventorySnapshot()
    {
        var saves = new RecordingSaveQueue();
        using WorldRuntime world = TestWorld.CreateRuntime(saves);
        (Player player, _) = CreatePlayer(1);
        player.Inventory.Load([]);
        world.AddPlayer(player);
        Give(player.Inventory, ToughJerky, 4);
        world.RemovePlayer(player);
        InventorySnapshot inventory = Assert.Single(saves.Saved).Inventory!;
        Assert.Equal((ToughJerky, 4u), (inventory.Items[0].Item.Entry, inventory.Items[0].Item.Count));
    }
}

/// <summary>Minimal SMSG_UPDATE_OBJECT reader for the item tests.</summary>
internal static class UpdateParser
{
    public static List<(byte Type, ulong Guid, byte TypeId, Dictionary<int, uint> Values)> Parse(byte[] body)
    {
        var reader = new PacketReader(body);
        uint count = reader.ReadUInt32();
        reader.ReadByte(); // has transport
        var blocks = new List<(byte, ulong, byte, Dictionary<int, uint>)>();
        for (int i = 0; i < count; i++)
        {
            byte type = reader.ReadByte();
            ulong guid = reader.ReadPackedGuid();
            byte typeId = 0;
            if (type is (byte)ObjectUpdateType.CreateObject or (byte)ObjectUpdateType.CreateObject2)
            {
                typeId = reader.ReadByte();
                var flags = (ObjectUpdateFlags)reader.ReadByte();
                if ((flags & ObjectUpdateFlags.Living) != 0)
                {
                    MovementInfo.Read(ref reader);
                    reader.Skip(6 * 4);
                }
                else if ((flags & ObjectUpdateFlags.HasPosition) != 0)
                {
                    reader.Skip(16);
                }

                if ((flags & ObjectUpdateFlags.HighGuid) != 0)
                {
                    reader.Skip(4);
                }

                if ((flags & ObjectUpdateFlags.All) != 0)
                {
                    reader.Skip(4);
                }

                if ((flags & ObjectUpdateFlags.MeleeAttacking) != 0)
                {
                    reader.ReadPackedGuid();
                }

                if ((flags & ObjectUpdateFlags.Transport) != 0)
                {
                    reader.Skip(4);
                }
            }
            else if (type != (byte)ObjectUpdateType.Values)
            {
                throw new InvalidOperationException($"unexpected update type {type}");
            }

            int maskBlocks = reader.ReadByte();
            uint[] mask = new uint[maskBlocks];
            for (int m = 0; m < maskBlocks; m++)
            {
                mask[m] = reader.ReadUInt32();
            }

            var values = new Dictionary<int, uint>();
            for (int index = 0; index < maskBlocks * 32; index++)
            {
                if ((mask[index >> 5] & (1u << (index & 31))) != 0)
                {
                    values[index] = reader.ReadUInt32();
                }
            }

            blocks.Add((type, guid, typeId, values));
        }

        return blocks;
    }
}
