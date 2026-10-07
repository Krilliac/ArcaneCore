using System.Reflection;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using Xunit;

using LootSlot = ArcaneCore.MockClient.Scenarios.StartingZoneLoot.LootSlot;
using LootWindow = ArcaneCore.MockClient.Scenarios.StartingZoneLoot.LootWindow;

namespace ArcaneCore.MockClient.Tests;

/// <summary>Strict build-5875 SMSG_LOOT_RESPONSE parsing over production loot packets.</summary>
public sealed class StartingZoneLootTests
{
    [Fact]
    public void LootWindow_ParsesEmptyGoldWindow()
    {
        byte[] body = BuildPacket(37);
        LootWindow window = StartingZoneLoot.ParseWindow(body);
        Assert.Equal(ObjectGuid.WithEntry(HighGuid.GameObject, 9001, 17).Value, window.Guid);
        Assert.Equal((byte)LootType.Corpse, window.Type);
        Assert.Equal(37u, window.Gold);
        Assert.Empty(window.Items);
    }

    [Fact]
    public void LootWindow_ParsesMultipleItemsAndTrailerFields()
    {
        byte[] body = BuildPacket(9,
            (new LootItem(0, 1001, 2, false, false, 5001), LootSlotType.AllowLoot),
            (new LootItem(1, 1002, 1, false, false, 5002), LootSlotType.Locked));
        LootWindow window = StartingZoneLoot.ParseWindow(body);
        Assert.Equal(2, window.Items.Count);
        LootSlot first = Assert.Single(window.Items, item => item.Slot == 0);
        Assert.Equal(1001u, first.ItemId);
        Assert.Equal(2u, first.Count);
        Assert.Equal((byte)LootSlotType.AllowLoot, first.SlotType);
        LootSlot second = Assert.Single(window.Items, item => item.Slot == 1);
        Assert.Equal(1002u, second.ItemId);
        Assert.Equal((byte)LootSlotType.Locked, second.SlotType);
    }

    [Fact]
    public void LootWindow_RejectsTruncatedTrailingDuplicateOversizedAndZeroValues()
    {
        byte[] valid = BuildPacket(0,
            (new LootItem(0, 1001, 2, false, false, 5001), LootSlotType.AllowLoot),
            (new LootItem(1, 1002, 1, false, false, 5002), LootSlotType.AllowLoot));
        Assert.Throws<MockProtocolException>(() => StartingZoneLoot.ParseWindow(valid[..^1]));
        Assert.Throws<MockProtocolException>(() => StartingZoneLoot.ParseWindow([.. valid, 0]));

        byte[] duplicate = [.. valid];
        duplicate[14 + 22] = 0;
        Assert.Throws<MockProtocolException>(() => StartingZoneLoot.ParseWindow(duplicate));

        byte[] oversized = [.. valid];
        oversized[13] = 33;
        Assert.Throws<MockProtocolException>(() => StartingZoneLoot.ParseWindow(oversized));

        byte[] zeroGuid = [.. valid];
        Array.Clear(zeroGuid, 0, 8);
        Assert.Throws<MockProtocolException>(() => StartingZoneLoot.ParseWindow(zeroGuid));

        byte[] zeroCount = [.. valid];
        Array.Clear(zeroCount, 14 + 1 + 4, 4);
        Assert.Throws<MockProtocolException>(() => StartingZoneLoot.ParseWindow(zeroCount));
    }

    [Fact]
    public void ItemPush_ParsesProductionPacketAndRejectsTruncationTrailingAndForeignRecipient()
    {
        ulong player = ObjectGuid.Player(77).Value;
        var item = new Item(4001, new ItemTemplate { Entry = 1001 }, ObjectGuid.Player(77));
        byte[] body = ItemPackets.ItemPushResult(new ObjectGuid(player), item, 2, received: false, created: false, showInChat: true);
        Assert.Equal(41, body.Length);
        StartingZoneLoot.ItemPush parsed = StartingZoneLoot.ParseItemPush(body);
        Assert.Equal((player, 1001u, 2u), (parsed.PlayerGuid, parsed.ItemId, parsed.Count));
        Assert.Throws<MockProtocolException>(() => StartingZoneLoot.ParseItemPush(body[..^1]));
        Assert.Throws<MockProtocolException>(() => StartingZoneLoot.ParseItemPush([.. body, 0]));

        byte[] foreign = [.. body];
        BitConverter.GetBytes(ObjectGuid.Player(78).Value).CopyTo(foreign, 0);
        StartingZoneLoot.ItemPush foreignParsed = StartingZoneLoot.ParseItemPush(foreign);
        Assert.NotEqual(player, foreignParsed.PlayerGuid);
    }

    private static byte[] BuildPacket(uint gold, params (LootItem Item, LootSlotType Slot)[] items)
    {
        var character = new CharacterRecord { Id = 77, AccountId = 1, Name = "LootParser", Race = 1, Class = 1, Level = 1, MapId = 0, ZoneId = 12 };
        var player = new Player(character, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), new Session());
        var bag = new LootBag(ObjectGuid.WithEntry(HighGuid.GameObject, 9001, 17), LootSourceKind.GameObject, LootType.Corpse);
        typeof(LootBag).GetProperty(nameof(LootBag.Gold))!.SetValue(bag, gold);
        MethodInfo add = typeof(LootBag).GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach ((LootItem item, LootSlotType slot) in items)
        {
            item.AllowedLooters.Add(player.Guid);
            add.Invoke(bag, [item]);
        }

        byte[] packet = LootPackets.LootResponse(bag, player);
        if (items.Any(item => item.Slot == LootSlotType.Locked))
        {
            // SlotFor intentionally emits AllowLoot for this fixture's owner. Mutate only the
            // documented final slot-type byte of the second production item record to exercise
            // the parser's legal Locked value without duplicating LootPackets.
            packet[14 + 22 + 21] = (byte)LootSlotType.Locked;
        }

        return packet;
    }

    private sealed class Session : IPlayerSession
    {
        public int AccountId => 1;
        public AccountSecurity Security => AccountSecurity.Player;
        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload) { }
        public void ProcessWorldPackets(Player player) { }
        public void Kick() { }
        public void OnLoggedOut() { }
    }
}
