using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Tests;

/// <summary>
/// Item content for the tests. The starting outfit entries are the vmangos human warrior rows
/// (playercreateinfo_item race 1 class 1); stats are trimmed to what the tests look at.
/// Entries from 90000 up are synthetic.
/// </summary>
internal static class ItemTestData
{
    public const uint WornShortsword = 25;
    public const uint RecruitsShirt = 38;
    public const uint RecruitsPants = 39;
    public const uint RecruitsBoots = 40;
    public const uint ToughJerky = 117;
    public const uint WornWoodenShield = 2362;
    public const uint Hearthstone = 6948;
    public const uint SmallBrownPouch = 4496;
    public const uint LightQuiver = 2101;
    public const uint SmallAmmoPouch = 2102;
    public const uint RoughArrow = 2512;
    public const uint TwoHandAxe = 90001;
    public const uint LevelTenHelm = 90002;
    public const uint MageRobe = 90003;
    public const uint StrengthRing = 90004;
    public const uint UniqueKey = 90005;
    public const uint IndestructibleRock = 90006;
    public const uint SoulPouch = 90007;
    public const uint UniqueTrinket = 90008;

    public static IReadOnlyList<ItemTemplate> Templates { get; } =
    [
        new() { Entry = WornShortsword, Class = 2, SubClass = 7, Name = "Worn Shortsword", DisplayId = 1542, Quality = 1, InventoryType = 21, Delay = 1900, MaxDurability = 20, Damages = [new ItemDamage(1, 3, 0)], Material = 1, Sheath = 3 },
        new() { Entry = RecruitsShirt, Class = 4, SubClass = 0, Name = "Recruit's Shirt", DisplayId = 9891, Quality = 1, InventoryType = 4, Material = 7 },
        new() { Entry = RecruitsPants, Class = 4, SubClass = 1, Name = "Recruit's Pants", DisplayId = 9892, Quality = 1, InventoryType = 7, Armor = 2, MaxDurability = 25, Material = 7 },
        new() { Entry = RecruitsBoots, Class = 4, SubClass = 1, Name = "Recruit's Boots", DisplayId = 10141, Quality = 1, InventoryType = 8, Armor = 1, MaxDurability = 16, Material = 7 },
        new() { Entry = ToughJerky, Class = 0, SubClass = 0, Name = "Tough Jerky", DisplayId = 2473, Quality = 1, Stackable = 20, Spells = [new ItemSpell(433, 0, -1, 0, -1, 11, -1)] },
        new() { Entry = WornWoodenShield, Class = 4, SubClass = 6, Name = "Worn Wooden Shield", DisplayId = 18730, Quality = 0, InventoryType = 14, Armor = 5, Block = 1, MaxDurability = 20, Material = 2, Sheath = 4 },
        new() { Entry = Hearthstone, Class = 15, SubClass = 0, Name = "Hearthstone", DisplayId = 6418, Quality = 1, Bonding = 1, Flags = 0x40 },
        new() { Entry = SmallBrownPouch, Class = 1, SubClass = 0, Name = "Small Brown Pouch", DisplayId = 1281, Quality = 1, InventoryType = 18, ContainerSlots = 6 },
        new() { Entry = LightQuiver, Class = 11, SubClass = 2, Name = "Light Quiver", DisplayId = 21328, Quality = 1, InventoryType = 18, ContainerSlots = 8, BagFamily = 1 },
        new() { Entry = SmallAmmoPouch, Class = 11, SubClass = 3, Name = "Small Ammo Pouch", DisplayId = 1816, Quality = 1, InventoryType = 18, ContainerSlots = 8, BagFamily = 2 },
        new() { Entry = RoughArrow, Class = 6, SubClass = 2, Name = "Rough Arrow", DisplayId = 5996, Quality = 1, InventoryType = 24, Stackable = 200, BagFamily = 1 },
        new() { Entry = TwoHandAxe, Class = 2, SubClass = 1, Name = "Test Greataxe", DisplayId = 100, InventoryType = 17, Delay = 3300, MaxDurability = 50 },
        new() { Entry = LevelTenHelm, Class = 4, SubClass = 1, Name = "Test Helm", DisplayId = 101, InventoryType = 1, RequiredLevel = 10, Armor = 20 },
        new() { Entry = MageRobe, Class = 4, SubClass = 1, Name = "Test Robe", DisplayId = 102, InventoryType = 20, AllowableClass = 128 },
        new() { Entry = StrengthRing, Class = 4, SubClass = 0, Name = "Test Ring", DisplayId = 103, InventoryType = 11, Armor = 10, Stats = [new ItemStat(4, 5), new ItemStat(7, -2)] },
        new() { Entry = UniqueKey, Class = 13, SubClass = 0, Name = "Test Key", DisplayId = 104, BagFamily = 9, MaxCount = 1 },
        new() { Entry = IndestructibleRock, Class = 12, SubClass = 0, Name = "Test Rock", DisplayId = 105, Flags = 0x20 },
        new() { Entry = SoulPouch, Class = 1, SubClass = 1, Name = "Test Soul Pouch", DisplayId = 106, InventoryType = 18, ContainerSlots = 4, BagFamily = 3 },
        new() { Entry = UniqueTrinket, Class = 4, SubClass = 0, Name = "Test Trinket", DisplayId = 107, InventoryType = 12, Flags = 0x80000 },
    ];

    /// <summary>vmangos playercreateinfo_item for race 1 (human), class 1 (warrior).</summary>
    public static IReadOnlyList<StartingItem> StartingItems { get; } =
    [
        new(1, 1, WornShortsword, 1),
        new(1, 1, RecruitsShirt, 1),
        new(1, 1, RecruitsPants, 1),
        new(1, 1, RecruitsBoots, 1),
        new(1, 1, ToughJerky, 4),
        new(1, 1, WornWoodenShield, 1),
        new(1, 1, Hearthstone, 1),
    ];

    public static ItemTemplateStore Store { get; } = new(Templates, StartingItems);

    public static ItemTemplate Get(uint entry) => Store.Find(entry)!;

    /// <summary>A level-1 human warrior with an empty inventory wired to the test content.</summary>
    public static (Player Player, FakeSession Session) CreatePlayer(uint guid = 1, float x = 0, float y = 0, byte level = 1)
    {
        var session = new FakeSession((int)guid);
        Player player = TestWorld.CreatePlayer(guid, x, y, session);
        player.Level = level;
        Wire(player.Inventory);
        return (player, session);
    }

    public static PlayerInventory Wire(PlayerInventory inventory, ItemGuidAllocator? guids = null)
    {
        inventory.Templates = Store;
        inventory.GuidAllocator = guids ?? new ItemGuidAllocator();
        return inventory;
    }

    /// <summary>Create <paramref name="count"/> of <paramref name="entry"/> anywhere; asserts it fitted.</summary>
    public static Item Give(PlayerInventory inventory, uint entry, uint count = 1)
    {
        InventoryResult result = inventory.AddItem(entry, count, out Item? item);
        if (result != InventoryResult.Ok)
        {
            throw new InvalidOperationException($"could not add {entry}: {result}");
        }

        return item!;
    }

    /// <summary>The SMSG_INVENTORY_CHANGE_FAILURE results the session got, in order.</summary>
    public static List<InventoryResult> EquipErrors(FakeSession session)
        => session.Sent.Where(p => p.Opcode == WorldOpcode.SmsgInventoryChangeFailure).Select(p => (InventoryResult)p.Payload[0]).ToList();
}
