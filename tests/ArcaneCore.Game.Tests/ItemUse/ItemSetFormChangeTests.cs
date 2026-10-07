using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Items.ItemSets;
using ArcaneCore.Game.Items.ItemUse;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Items;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.ItemUse;

/// <summary>
/// Set bonus spells and the shapeshift form (vmangos AddItemsSetItem, Item.cpp:73-90: "spell casted only if fit form requirement, in other case
/// will casted at form change"; Player::UpdateEquipSpellsAtFormChange, Player.cpp:7242-7253: every active set spell is re-checked, a spell that no
/// longer fits loses its aura, one that now fits and has none is cast).
/// </summary>
public sealed class ItemSetFormChangeTests
{
    private const uint SetId = 7101;
    private const uint CatOnlyBonus = 96101;
    private const uint AnyFormBonus = 96102;
    private const uint Head = 97101;
    private const uint Chest = 97102;

    private static readonly ItemSetCatalog Catalog = new(
        [new ItemSetRecord(SetId, "Form Set", [CatOnlyBonus, AnyFormBonus, 0, 0, 0, 0, 0, 0], [2, 2, 0, 0, 0, 0, 0, 0], 0, 0)]);

    private static SpellInfo Bonus(uint id, uint stances = 0) => Spell(id, Effect(SpellEffectName.ApplyAura, 5, aura: AuraType.ModStat, misc: 0)) with
    {
        Attributes = SpellAttributes.Passive, Duration = new SpellDuration(-1, 0, -1), StartRecoveryCategory = 0, StartRecoveryTime = 0, Stances = stances,
    };

    private static ItemTemplate Piece(uint entry, uint inventoryType) => new ItemTemplate
    {
        Entry = entry, Name = $"Piece {entry}", DisplayId = entry, Class = 4, SubClass = 1, InventoryType = inventoryType, Quality = 3, MaxDurability = 40, SetId = SetId,
    }.Normalized();

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Kit = new SpellTestKit(Bonus(CatOnlyBonus, 1u << ((int)ShapeshiftForm.Cat - 1)), Bonus(AnyFormBonus));
            (Player, _) = Kit.AddPlayer(1);
            Player.Inventory.Templates = new ItemTemplateStore([.. ItemTestData.Templates, Piece(Head, 1), Piece(Chest, 5)]);
            Player.Inventory.GuidAllocator = new ItemGuidAllocator();
            Equips = new ItemEquipSpells(Kit.System, new ItemSetBonuses(Catalog, Kit.System));
            Equips.Attach(Player);
        }

        public SpellTestKit Kit { get; }

        public Player Player { get; }

        public ItemEquipSpells Equips { get; }

        public bool Has(uint spell) => Kit.System.GetAuras(Player).Any(h => h.Spell.Id == spell && !h.IsRemoved);

        public Item Wear(uint entry)
        {
            Item item = ItemTestData.Give(Player.Inventory, entry);
            Player.Inventory.AutoEquipItem(item.BagSlot, item.Slot);
            Assert.Equal(InventorySlots.Bag0, item.BagSlot);
            return item;
        }

        public void Shift(ShapeshiftForm form)
        {
            Player.SetByte(UpdateFields.UnitFieldBytes1, 2, (byte)form);
            Equips.ReconcileAtFormChange(Player);
        }

        public void Dispose() => Kit.Dispose();
    }

    [Fact]
    public void ABonusOfAnotherForm_IsHeldBack_UntilTheFormFits_AndGoesWhenItNoLongerDoes()
    {
        using var rig = new Rig();
        rig.Wear(Head);
        rig.Wear(Chest);
        Assert.False(rig.Has(CatOnlyBonus));
        Assert.True(rig.Has(AnyFormBonus));
        Assert.Equal([CatOnlyBonus, AnyFormBonus], ItemEquipSpells.SetsOf(rig.Player.Inventory)!.ActiveSpells(SetId));

        rig.Shift(ShapeshiftForm.Cat);
        Assert.True(rig.Has(CatOnlyBonus));
        Assert.True(rig.Has(AnyFormBonus));
        Assert.Equal(1, rig.Kit.System.GetAuras(rig.Player).Count(h => h.Spell.Id == AnyFormBonus && !h.IsRemoved));

        rig.Shift(ShapeshiftForm.None);
        Assert.False(rig.Has(CatOnlyBonus));
        Assert.True(rig.Has(AnyFormBonus));
    }

    [Fact]
    public void ABonusHeldBackByTheForm_IsNotCastAfterThePieceComesOff()
    {
        using var rig = new Rig();
        Item head = rig.Wear(Head);
        rig.Wear(Chest);
        rig.Player.Inventory.RemoveItem(head.BagSlot, head.Slot);
        rig.Shift(ShapeshiftForm.Cat);
        Assert.False(rig.Has(CatOnlyBonus));
        Assert.False(rig.Has(AnyFormBonus));
    }
}
