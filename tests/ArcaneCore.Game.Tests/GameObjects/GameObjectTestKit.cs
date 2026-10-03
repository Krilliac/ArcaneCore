using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>A scriptable quest journal for loot and game object tests.</summary>
internal sealed class FakeQuestJournal : ILootQuestJournal
{
    public HashSet<(ObjectGuid Player, uint Item)> Needs { get; } = [];

    public HashSet<(ObjectGuid Player, uint Quest)> Incomplete { get; } = [];

    public List<(ObjectGuid Player, uint Item, uint Count)> Looted { get; } = [];

    public List<ObjectGuid> MoneyEvents { get; } = [];

    public List<(ObjectGuid Player, uint Entry, ObjectGuid Guid)> Used { get; } = [];

    public bool NeedsQuestItem(Player player, uint itemId) => Needs.Contains((player.Guid, itemId));

    public bool IsQuestIncomplete(Player player, uint questId) => Incomplete.Contains((player.Guid, questId));

    public void ItemLooted(Player player, uint itemId, uint count) => Looted.Add((player.Guid, itemId, count));

    public void MoneyLooted(Player player) => MoneyEvents.Add(player.Guid);

    public void GameObjectUsed(Player player, uint entry, ObjectGuid guid) => Used.Add((player.Guid, entry, guid));
}

internal sealed class FakeGroups : ILootGroups
{
    public Dictionary<ObjectGuid, Group> ByMember { get; } = [];

    public List<Group> LooterUpdates { get; } = [];

    public HashSet<ObjectGuid> Offline { get; } = [];

    public Group? GroupOf(Player player) => ByMember.GetValueOrDefault(player.Guid);

    public void LooterChanged(Group group) => LooterUpdates.Add(group);

    public bool IsMemberOnline(ObjectGuid guid) => !Offline.Contains(guid);

    public Group Create(LootMethod method, params Player[] members)
    {
        var group = new Group(1) { LootMethod = method, IsCreated = true, LeaderGuid = members[0].Guid, LooterGuid = members[0].Guid }; // GroupManager.cs:135 / vmangos Group::Create
        foreach (Player member in members)
        {
            group.AddMemberSlot(member.Guid, member.Name);
            ByMember[member.Guid] = group;
        }

        return group;
    }
}

/// <summary>Content builders and a parsed SMSG_LOOT_RESPONSE.</summary>
internal static class GameObjectTestKit
{
    public const uint QuestItem = 91000;
    public const uint PartyItem = 91001;
    public const uint Lockbox = 91002;
    public const uint LockedBox = 91003;
    public const uint Hide = 91004;

    /// <summary>Item content: the shared test items plus loot-specific ones.</summary>
    public static ItemTemplateStore ItemStore { get; } = new(
    [
        .. ItemTestData.Templates,
        new ItemTemplate { Entry = QuestItem, Class = 12, Name = "Test Quest Drop", DisplayId = 200, Stackable = 20 },
        new ItemTemplate { Entry = PartyItem, Class = 12, Name = "Test Party Drop", DisplayId = 201, Flags = LootService.ItemFlagPartyLoot, Stackable = 20 },
        new ItemTemplate { Entry = Lockbox, Class = 15, Name = "Test Clam", DisplayId = 202, Flags = LootService.ItemFlagLootable },
        new ItemTemplate { Entry = LockedBox, Class = 15, Name = "Test Lockbox", DisplayId = 203, Flags = LootService.ItemFlagLootable, LockId = 5 },
        new ItemTemplate { Entry = Hide, Class = 7, Name = "Test Hide", DisplayId = 204, Stackable = 20 },
    ], ItemTestData.StartingItems);

    public static GameObjectTemplate GoTemplate(uint entry, GameObjectType type, params (int Index, uint Value)[] data)
    {
        var values = new uint[GameObjectTemplate.DataCount];
        foreach ((int index, uint value) in data)
        {
            values[index] = value;
        }

        return new GameObjectTemplate { Entry = entry, Type = (uint)type, DisplayId = 1000 + entry, Name = $"GO {entry}", Data = values };
    }

    public static GameObjectSpawn GoSpawn(uint guid, uint entry, float x, float y, int spawnTimeSeconds = 60, float orientation = 1.0f)
        => new() { Guid = guid, Entry = entry, MapId = 0, X = x, Y = y, Z = 83.5f, Orientation = orientation, SpawnTimeSeconds = spawnTimeSeconds };

    public static LootStoreRow Row(uint entry, uint item, float chance, byte group = 0, int minOrRef = 1, uint max = 1, uint condition = 0)
        => new(entry, item, chance, group, minOrRef, max, condition);

    public static (Player Player, FakeSession Session) Player(uint guid, float x = 0, float y = 0)
    {
        var session = new FakeSession((int)guid);
        Player player = TestWorld.CreatePlayer(guid, x, y, session);
        player.Inventory.Templates = ItemStore;
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        return (player, session);
    }

    public static List<(WorldOpcode Opcode, byte[] Payload)> Packets(FakeSession session, WorldOpcode opcode)
    {
        var all = new List<(WorldOpcode, byte[])>();
        CreatureTestSupport.DrainBlocks(session, all);
        return [.. all.Where(p => p.Item1 == opcode)];
    }

    public static List<(WorldOpcode Opcode, byte[] Payload)> NonUpdatePackets(FakeSession session)
    {
        var all = new List<(WorldOpcode, byte[])>();
        CreatureTestSupport.DrainBlocks(session, all);
        return all;
    }
}

internal sealed record ParsedLootItem(byte Slot, uint ItemId, uint Count, uint DisplayId, LootSlotType SlotType);

internal sealed record ParsedLoot(ulong Guid, LootType Type, uint Gold, IReadOnlyList<ParsedLootItem> Items)
{
    public static ParsedLoot Parse(byte[] payload)
    {
        var reader = new PacketReader(payload);
        ulong guid = reader.ReadUInt64();
        var type = (LootType)reader.ReadByte();
        uint gold = reader.ReadUInt32();
        byte count = reader.ReadByte();
        var items = new List<ParsedLootItem>();
        for (int i = 0; i < count; i++)
        {
            byte slot = reader.ReadByte();
            uint item = reader.ReadUInt32();
            uint n = reader.ReadUInt32();
            uint display = reader.ReadUInt32();
            Xunit.Assert.Equal(0u, reader.ReadUInt32()); // random suffix
            Xunit.Assert.Equal(0u, reader.ReadUInt32()); // random property
            items.Add(new ParsedLootItem(slot, item, n, display, (LootSlotType)reader.ReadByte()));
        }

        Xunit.Assert.Equal(0, reader.Remaining);
        return new ParsedLoot(guid, type, gold, items);
    }
}
