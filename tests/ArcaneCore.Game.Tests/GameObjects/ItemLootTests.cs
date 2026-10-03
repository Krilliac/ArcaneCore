using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Loot;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>
/// Container items (vmangos HandleOpenItemOpcode SpellHandler.cpp:142-226, Player::SendLoot Player.cpp:7716-7756, DoLootRelease LootHandler.cpp:549-566).
/// Fixtures mirror classic-db: a lockbox (4632, locked, no money), a junkbox (16882: 25..125 copper) and a stackable clam.
/// </summary>
public sealed class ItemLootTests
{
    private const uint Lockbox = 4632;
    private const uint Junkbox = 16882;
    private const uint Clam = 5523;
    private const uint KeyBox = 4633;
    private const uint UnknownLockBox = 4634;
    private const uint EmptyBox = 4635;
    private const uint PickLock = 5;
    private const uint KeyLock = 6;
    private const uint Dagger = 2092;
    private const uint Hide = 91004;

    private static readonly ItemTemplateStore Store = new(
    [
        .. ItemTestData.Templates,
        new ItemTemplate { Entry = Lockbox, Class = 15, Name = "Large Iron Lockbox", DisplayId = 301, Flags = LootService.ItemFlagLootable, LockId = PickLock },
        new ItemTemplate { Entry = Junkbox, Class = 15, Name = "Battered Junkbox", DisplayId = 302, Flags = LootService.ItemFlagLootable, LockId = PickLock, MinMoneyLoot = 25, MaxMoneyLoot = 125 },
        new ItemTemplate { Entry = Clam, Class = 15, Name = "Small Barnacled Clam", DisplayId = 303, Flags = LootService.ItemFlagLootable, Stackable = 20 },
        new ItemTemplate { Entry = KeyBox, Class = 15, Name = "Key Chest", DisplayId = 304, Flags = LootService.ItemFlagLootable, LockId = KeyLock },
        new ItemTemplate { Entry = UnknownLockBox, Class = 15, Name = "Odd Chest", DisplayId = 305, Flags = LootService.ItemFlagLootable, LockId = 999 },
        new ItemTemplate { Entry = EmptyBox, Class = 15, Name = "Empty Box", DisplayId = 306, Flags = LootService.ItemFlagLootable },
        new ItemTemplate { Entry = Hide, Class = 7, Name = "Test Hide", DisplayId = 204, Stackable = 20 },
    ], ItemTestData.StartingItems);

    private static readonly Dictionary<uint, LockEntry> Locks = new()
    {
        [PickLock] = new LockEntry(PickLock, [2, 0, 0, 0, 0, 0, 0, 0], [1, 0, 0, 0, 0, 0, 0, 0], [1, 0, 0, 0, 0, 0, 0, 0]),   // Lockpicking skill 1
        [KeyLock] = new LockEntry(KeyLock, [1, 0, 0, 0, 0, 0, 0, 0], [5555, 0, 0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0, 0, 0]),   // a key item, no skill
    };

    private sealed class Rig
    {
        public Rig(ItemLootOptions? options = null, IEnumerable<(LootTableKind, LootStoreRow)>? rows = null, int seed = 5)
        {
            Loot = new LootService(new LootContent(
                rows ??
                [
                    (LootTableKind.Item, Row(Lockbox, ItemTestData.ToughJerky, 100, minOrRef: 2, max: 2)),
                    (LootTableKind.Item, Row(Lockbox, Hide, 100)),
                    (LootTableKind.Item, Row(Junkbox, Dagger, 100)),
                    (LootTableKind.Item, Row(Clam, Hide, 100)),
                    (LootTableKind.Item, Row(KeyBox, Hide, 100)),
                    (LootTableKind.Item, Row(UnknownLockBox, Hide, 100)),
                ], []), random: new Random(seed)) { Items = Store };
            Source = new ItemLootSource(Loot, options, new Random(seed)) { Locks = id => Locks.GetValueOrDefault(id) };
            Loot.ItemLoot = Source;
            (Player, Session) = GameObjectTestKit.Player(1);
            Player.Inventory.Templates = Store;
            Player.Inventory.GuidAllocator = new ItemGuidAllocator();
            Player.Inventory.Load([]);
        }

        public LootService Loot { get; }

        public ItemLootSource Source { get; }

        public Player Player { get; }

        public FakeSession Session { get; }

        public Item Give(uint entry, uint count = 1)
        {
            Assert.Equal(InventoryResult.Ok, Player.Inventory.AddItem(entry, count, out Item? item));
            Session.Clear();
            return item!;
        }

        public ParsedLoot Window() => ParsedLoot.Parse(Assert.Single(Packets(Session, WorldOpcode.SmsgLootResponse)).Payload);
    }

    private static Item Unlocked(Item item)
    {
        item.DynamicFlags |= ItemDynFlags.Unlocked;
        return item;
    }

    // --- the lock rule -------------------------------------------------------------------------

    [Fact]
    public void ALockWithASkillRequirement_AndNoUnlockedFlag_AnswersItemLocked_ThenOpensOncePicked()
    {
        var rig = new Rig();
        Item box = rig.Give(Lockbox);

        Assert.Equal(LootResult.Locked, rig.Loot.OpenItem(rig.Player, box));
        Assert.Empty(Packets(rig.Session, WorldOpcode.SmsgLootResponse));

        Unlocked(box); // Pick Lock sets ITEM_DYNFLAG_UNLOCKED (GatheringSpells.EffectOpenLock)
        Assert.Equal(LootResult.Ok, rig.Loot.OpenItem(rig.Player, box));
    }

    [Fact]
    public void AKeyOnlyLock_Opens_WithoutTheUnlockedFlag()
    {
        var rig = new Rig();
        Assert.Equal(LootResult.Ok, rig.Loot.OpenItem(rig.Player, rig.Give(KeyBox))); // vmangos only blocks locks with a skill
    }

    [Fact]
    public void ALockIdWithoutALockRow_AnswersItemLocked()
    {
        var rig = new Rig();
        Assert.Equal(LootResult.Locked, rig.Loot.OpenItem(rig.Player, rig.Give(UnknownLockBox)));
    }

    [Fact]
    public void ADeadPlayer_AnswersDead_AndNothingIsGenerated()
    {
        var rig = new Rig();
        Item box = Unlocked(rig.Give(Lockbox));
        rig.Player.Health = 0;

        Assert.Equal(LootResult.Dead, rig.Loot.OpenItem(rig.Player, box));
        Assert.Null(box.Loot);
    }

    [Fact]
    public void AWrappedItem_IsNotOpenedAsLoot()
    {
        var rig = new Rig();
        Item box = Unlocked(rig.Give(Lockbox));
        box.DynamicFlags |= ItemDynFlags.Wrapped;

        Assert.Equal(LootResult.NotAllowed, rig.Loot.OpenItem(rig.Player, box));
        Assert.Null(box.Loot);
    }

    [Fact]
    public void TheCastInProgressIsInterrupted_BeforeTheWindowOpens()
    {
        var rig = new Rig();
        var interrupted = new List<Player>();
        rig.Source.InterruptSpells = interrupted.Add;

        rig.Loot.OpenItem(rig.Player, Unlocked(rig.Give(Lockbox)));

        Assert.Same(rig.Player, Assert.Single(interrupted));
    }

    // --- generation and the window ------------------------------------------------------------

    [Fact]
    public void TheFirstOpen_RollsTheItemTable_ShowsItInTheOwnersWindow_AndKeepsItOnTheItem()
    {
        var rig = new Rig();
        Item box = Unlocked(rig.Give(Lockbox));

        Assert.Equal(LootResult.Ok, rig.Loot.OpenItem(rig.Player, box));

        ParsedLoot window = rig.Window();
        Assert.Equal((box.Guid.Value, LootType.Corpse), (window.Guid, window.Type));
        Assert.Equal([(ItemTestData.ToughJerky, 2u), (Hide, 1u)], window.Items.Select(i => (i.ItemId, i.Count)));
        ItemLootData kept = Assert.IsType<ItemLootData>(box.Loot);
        Assert.Equal([(byte)0, (byte)1], kept.Items.Select(i => i.Slot));
        Assert.Equal(box.ToData().Loot, kept);                       // saved with the inventory snapshot
    }

    [Fact]
    public void AJunkbox_PaysItsMoneyRange_AndTheMoneyIsNotShared()
    {
        var rig = new Rig();
        Item box = Unlocked(rig.Give(Junkbox));

        rig.Loot.OpenItem(rig.Player, box);

        ParsedLoot window = rig.Window();
        Assert.InRange(window.Gold, 25u, 125u);
        uint before = rig.Player.Money;
        Assert.True(rig.Loot.TakeMoney(rig.Player));
        Assert.Equal(window.Gold, rig.Player.Money - before);
        Assert.Equal(0u, box.Loot!.Gold);
    }

    [Fact]
    public void AReopen_ShowsWhatIsLeft_WithTheSameSlots_AndNeverRerolls()
    {
        var rig = new Rig();
        Item box = Unlocked(rig.Give(Lockbox));
        rig.Loot.OpenItem(rig.Player, box);
        ParsedLoot first = rig.Window();
        Assert.Equal(2, first.Items.Count);
        ParsedLootItem taken = first.Items[0];

        Assert.Equal(InventoryResult.Ok, rig.Loot.TakeItem(rig.Player, taken.Slot));
        rig.Loot.Release(rig.Player, box.Guid);
        rig.Session.Clear();
        rig.Loot.OpenItem(rig.Player, box);

        ParsedLoot again = rig.Window();
        Assert.Equal(first.Items.Skip(1).Select(i => (i.Slot, i.ItemId, i.Count)), again.Items.Select(i => (i.Slot, i.ItemId, i.Count)));
        Assert.NotNull(rig.Player.Inventory.GetItemByGuid(box.Guid)); // loot is left: the item stays
    }

    [Fact]
    public void TakingEverything_AndReleasing_DestroysTheItem()
    {
        var rig = new Rig();
        Item box = Unlocked(rig.Give(Lockbox));
        rig.Loot.OpenItem(rig.Player, box);

        Assert.Equal(InventoryResult.Ok, rig.Loot.TakeItem(rig.Player, 0));
        Assert.Equal(InventoryResult.Ok, rig.Loot.TakeItem(rig.Player, 1));
        rig.Loot.Release(rig.Player, box.Guid);

        Assert.Null(rig.Player.Inventory.GetItemByGuid(box.Guid));
        Assert.Equal((2u, 1u), (rig.Player.Inventory.GetItemCount(ItemTestData.ToughJerky), rig.Player.Inventory.GetItemCount(Hide)));
        Assert.Null(rig.Loot.FindLoot(box.Guid));
    }

    [Fact]
    public void AnItemWithoutLootRows_ShowsAnEmptyWindow_AndIsDestroyedOnRelease()
    {
        var rig = new Rig();
        Item box = rig.Give(EmptyBox);

        Assert.Equal(LootResult.Ok, rig.Loot.OpenItem(rig.Player, box));
        Assert.Empty(rig.Window().Items);
        rig.Loot.Release(rig.Player, box.Guid);

        Assert.Null(rig.Player.Inventory.GetItemByGuid(box.Guid));
    }

    [Theory]
    [InlineData(true, 0u)]    // vmangos DestroyItem: the whole stack goes
    [InlineData(false, 2u)]   // switch: only one of the stack is consumed
    public void ALootedOutStackedItem_IsDestroyedWholeOrOneByOne_PerConfig(bool wholeStack, uint remaining)
    {
        var rig = new Rig(new ItemLootOptions { ConsumeWholeStack = wholeStack });
        Item clams = rig.Give(Clam, 3);
        rig.Loot.OpenItem(rig.Player, clams);
        rig.Loot.TakeItem(rig.Player, 0);

        rig.Loot.Release(rig.Player, clams.Guid);

        Assert.Equal(remaining, rig.Player.Inventory.GetItemCount(Clam));
        if (!wholeStack)
        {
            Assert.Null(rig.Player.Inventory.GetItemByGuid(clams.Guid)?.Loot); // the next clam rolls its own loot
        }
    }

    [Fact]
    public void TheWindowIsTheOwnersAlone_AndTheItemMustStayInTheInventory()
    {
        var rig = new Rig();
        Item box = Unlocked(rig.Give(Lockbox));
        rig.Loot.OpenItem(rig.Player, box);
        rig.Player.Inventory.DestroyItemCount(box, 1);

        Assert.NotEqual(InventoryResult.Ok, rig.Loot.TakeItem(rig.Player, 0));
    }

    // --- persistence through the inventory snapshot -------------------------------------------------

    [Fact]
    public void ARelog_KeepsTheUntakenLoot_ThroughTheInventorySnapshot()
    {
        var rig = new Rig();
        Item box = Unlocked(rig.Give(Lockbox));
        rig.Loot.OpenItem(rig.Player, box);
        rig.Loot.TakeItem(rig.Player, 0);
        rig.Loot.Release(rig.Player, box.Guid);
        InventorySnapshot saved = rig.Player.Inventory.CreateSnapshot();

        // A fresh player of the same character loads the stored rows: nothing is rerolled.
        var relog = new Rig();
        relog.Player.Inventory.Load(saved.Items);
        Item reloaded = relog.Player.Inventory.GetItemByGuid(box.Guid)!;
        Assert.Equal(box.Loot, reloaded.Loot);
        Assert.Equal(LootResult.Ok, relog.Loot.OpenItem(relog.Player, reloaded));

        ParsedLootItem left = Assert.Single(relog.Window().Items);
        Assert.Equal((1, Hide), (left.Slot, left.ItemId));
    }
}