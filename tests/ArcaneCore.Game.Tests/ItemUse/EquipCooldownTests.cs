using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Items.ItemUse;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.ItemUse;

/// <summary>
/// The equip cooldown (vmangos Player::ApplyEquipCooldown, Player.cpp:19235-19261, called by Player::EquipItem, :10380 and :10404): an
/// item put on starts a 30 s cooldown on each of its on-use spells (Player::AddCooldown with forcedDuration 30000, Player.cpp:22139-22243:
/// the spell's own recovery is replaced, its Spell.dbc category and category time are kept, an unexpired cooldown is left alone) and
/// tells the client with SMSG_ITEM_COOLDOWN (item guid, spell; vmangos Item.cpp:326-330, gtker smsg_item_cooldown.wowm), unless the
/// template carries ITEM_FLAG_NO_EQUIP_COOLDOWN (0x80, ItemPrototype.h:71).
/// </summary>
public sealed class EquipCooldownTests
{
    private const uint Trinket = 95_000;
    private const uint FlaggedTrinket = 95_001;
    private const uint EquipEffectTrinket = 95_002;
    private const uint UnknownSpellTrinket = 95_003;
    private const uint CategoryTrinket = 95_004;

    private const uint UseSpell = 95_100;
    private const uint EquipSpell = 95_101;
    private const uint CategorySpell = 95_102;
    private const uint CategorySibling = 95_103;
    private const uint MissingSpell = 95_199;

    private const uint SharedCategory = 1_234;
    private const uint EquipCooldownMs = 30_000;

    private static SpellInfo Dummy(uint id) => SpellTestKit.Spell(id, SpellTestKit.Effect(SpellEffectName.Dummy, 0));

    private static ItemTemplate TrinketTemplate(uint entry, uint spellId, uint trigger, uint flags = 0) => new()
    {
        Entry = entry, Class = 4, SubClass = 0, Name = $"Trinket {entry}", DisplayId = 1, InventoryType = 12, Flags = flags,
        Spells = [new ItemSpell(spellId, trigger, 0, 0, -1, 0, -1)],
    };

    private static (SpellTestKit Kit, Player Player, FakeSession Session) Create()
    {
        var kit = new SpellTestKit(
            Dummy(UseSpell) with { RecoveryTime = 120_000 },
            Dummy(EquipSpell),
            Dummy(CategorySpell) with { Category = SharedCategory, CategoryRecoveryTime = 60_000 },
            Dummy(CategorySibling) with { Category = SharedCategory, CategoryRecoveryTime = 60_000 });
        (Player player, FakeSession session) = kit.AddPlayer(1);
        player.Inventory.Templates = new ItemTemplateStore(
        [
            TrinketTemplate(Trinket, UseSpell, ItemSpellTriggers.OnUse),
            TrinketTemplate(FlaggedTrinket, UseSpell, ItemSpellTriggers.OnUse, (uint)ItemTemplateFlags.NoEquipCooldown),
            TrinketTemplate(EquipEffectTrinket, EquipSpell, ItemSpellTriggers.OnEquip),
            TrinketTemplate(UnknownSpellTrinket, MissingSpell, ItemSpellTriggers.OnUse),
            TrinketTemplate(CategoryTrinket, CategorySpell, ItemSpellTriggers.OnUse),
        ], []);
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        player.Inventory.Load([]);
        new ItemEquipSpells(kit.System).Attach(player);
        foreach (uint entry in new[] { Trinket, FlaggedTrinket, EquipEffectTrinket, UnknownSpellTrinket, CategoryTrinket })
        {
            Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(entry, 1, out _));
        }

        session.Clear();
        return (kit, player, session);
    }

    private static byte BackpackSlotOf(Player player, uint entry)
    {
        for (byte slot = InventorySlots.ItemStart; slot < InventorySlots.ItemEnd; slot++)
        {
            if (player.Inventory.GetItem(InventorySlots.Bag0, slot)?.Entry == entry)
            {
                return slot;
            }
        }

        throw new InvalidOperationException($"{entry} is not in the backpack");
    }

    private static Item Equip(Player player, uint entry)
    {
        Item item = player.Inventory.GetItem(InventorySlots.Bag0, BackpackSlotOf(player, entry))!;
        player.Inventory.AutoEquipItem(InventorySlots.Bag0, BackpackSlotOf(player, entry));
        Assert.Equal(InventorySlots.Bag0, item.BagSlot);
        Assert.True(item.Slot < InventorySlots.EquipmentEnd, $"{entry} was not equipped");
        return item;
    }

    private static List<(ulong Item, uint Spell)> ItemCooldowns(FakeSession session)
    {
        var result = new List<(ulong, uint)>();
        foreach (byte[] packet in SpellTestKit.Packets(session, WorldOpcode.SmsgItemCooldown))
        {
            Assert.Equal(12, packet.Length);
            var reader = new PacketReader(packet);
            result.Add((reader.ReadUInt64(), reader.ReadUInt32()));
        }

        return result;
    }

    private static bool Ready(SpellTestKit kit, Player player, uint spellId)
        => kit.System.IsSpellReadyIgnoringGlobalCooldown(player, kit.Store.Get(spellId)!);

    [Fact]
    public void Equip_OnUseItem_StartsAThirtySecondCooldown_AndTellsTheClient()
    {
        (SpellTestKit kit, Player player, FakeSession session) = Create();
        using SpellTestKit owner = kit;
        Assert.True(Ready(kit, player, UseSpell));

        Item item = Equip(player, Trinket);

        Assert.Equal([(item.Guid.Value, UseSpell)], ItemCooldowns(session));
        Assert.False(Ready(kit, player, UseSpell));
        kit.Advance(EquipCooldownMs - 100);
        Assert.False(Ready(kit, player, UseSpell));
        kit.Advance(100);
        Assert.True(Ready(kit, player, UseSpell)); // the forced 30 s, not the spell's own 120 s recovery
    }

    [Fact]
    public void NoEquipCooldownFlag_StartsNothing()
    {
        (SpellTestKit kit, Player player, FakeSession session) = Create();
        using SpellTestKit owner = kit;
        Equip(player, FlaggedTrinket);
        Assert.Empty(ItemCooldowns(session));
        Assert.True(Ready(kit, player, UseSpell));
    }

    [Fact]
    public void OnEquipSpell_HasNoEquipCooldown()
    {
        (SpellTestKit kit, Player player, FakeSession session) = Create();
        using SpellTestKit owner = kit;
        Equip(player, EquipEffectTrinket);
        Assert.Empty(ItemCooldowns(session));
        Assert.True(Ready(kit, player, EquipSpell));
    }

    [Fact]
    public void SpellMissingFromTheStore_IsSkipped()
    {
        (SpellTestKit kit, Player player, FakeSession session) = Create();
        using SpellTestKit owner = kit;
        Equip(player, UnknownSpellTrinket);
        Assert.Empty(ItemCooldowns(session));
    }

    [Fact]
    public void RunningLongerCooldown_IsKept_ButTheClientIsStillTold()
    {
        (SpellTestKit kit, Player player, FakeSession session) = Create();
        using SpellTestKit owner = kit;
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, UseSpell, SpellCastTargets.ForSelf(), triggered: true));
        Assert.False(Ready(kit, player, UseSpell));
        session.Clear();

        Item item = Equip(player, Trinket);

        Assert.Equal([(item.Guid.Value, UseSpell)], ItemCooldowns(session));
        kit.Advance(EquipCooldownMs);
        Assert.False(Ready(kit, player, UseSpell)); // still the 120 s cast cooldown (Player.cpp:22172-22178 returns early)
        kit.Advance(120_000 - EquipCooldownMs);
        Assert.True(Ready(kit, player, UseSpell));
    }

    [Fact]
    public void SpellCategory_StartsWithItsOwnCategoryTime()
    {
        (SpellTestKit kit, Player player, FakeSession session) = Create();
        using SpellTestKit owner = kit;
        Equip(player, CategoryTrinket);
        Assert.False(Ready(kit, player, CategorySpell));
        Assert.False(Ready(kit, player, CategorySibling));
        kit.Advance(EquipCooldownMs);
        // The 30 s spell time is over, but the spell's own Spell.dbc category time (60 s) still holds both spells of the category.
        Assert.False(Ready(kit, player, CategorySpell));
        Assert.False(Ready(kit, player, CategorySibling));
        kit.Advance(60_000 - EquipCooldownMs);
        Assert.True(Ready(kit, player, CategorySpell));
        Assert.True(Ready(kit, player, CategorySibling));
    }
}
