using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Items.ItemSets;
using ArcaneCore.Game.Items.ItemUse;
using ArcaneCore.Game.Ranged;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Items;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.ItemUse;

/// <summary>
/// Item sets and ON_EQUIP item spells on one equip hook. Rules: mangos AddItemsSetItem / RemoveItemsSetItem (Item.cpp), ApplyItemEquipSpell /
/// ApplyEquipSpell (PlayerItemApply.cpp), the item set call sites in PlayerItemStorage.cpp (EquipItem, RemoveItem, DestroyItem). Spell,
/// item and set ids are synthetic; the set layout (8 bonus slots) is the ItemSet.dbc one.
/// </summary>
public sealed class ItemSetsAndEquipSpellsTests
{
    private const uint SetId = 7001;
    private const uint SkillSetId = 7002;
    private const uint UnlistedSetId = 7999;
    private const uint TwoPieceSpell = 96001;
    private const uint FourPieceSpell = 96002;
    private const uint SkillSetSpell = 96003;
    private const uint SwordEquipSpell = 96010;
    private const uint ChargedEquipSpell = 96011;
    private const uint BagEquipSpell = 96012;
    private const uint QuiverSpell = 14824;
    private const uint ConsumableUseSpell = 96013;

    private const uint Head = 97001;
    private const uint Chest = 97002;
    private const uint Legs = 97003;
    private const uint Feet = 97004;
    private const uint SkillHead = 97005;
    private const uint UnlistedHead = 97006;
    private const uint Sword = 97007;
    private const uint Ring = 97008;
    private const uint Bag = 97009;
    private const uint Quiver = 97010;
    private const uint Bow = 97011;
    private const uint SecondRing = 97012;
    private const uint SkillChest = 97013;

    private const uint RequiredSkill = 164;

    private static ItemSpell OnEquip(uint spell, int charges = 0) => new(spell, ItemSpellTriggers.OnEquip, charges, 0, -1, 0, -1);

    private static ItemTemplate Armor(uint entry, uint inventoryType, uint set = 0, params ItemSpell[] spells) => new ItemTemplate
    {
        Entry = entry, Name = $"Piece {entry}", DisplayId = entry, Class = 4, SubClass = 1, InventoryType = inventoryType, Quality = 3,
        MaxDurability = 40, SetId = set, Spells = spells,
    }.Normalized();

    private static readonly ItemTemplate[] Templates =
    [
        Armor(Head, 1, SetId), Armor(Chest, 5, SetId), Armor(Legs, 7, SetId), Armor(Feet, 8, SetId),
        Armor(SkillHead, 1, SkillSetId), Armor(SkillChest, 5, SkillSetId), Armor(UnlistedHead, 1, UnlistedSetId),
        new ItemTemplate { Entry = Sword, Name = "Sword", DisplayId = Sword, Class = 2, SubClass = 7, InventoryType = 13, Quality = 3, Delay = 2000, MaxDurability = 40, Spells = [OnEquip(SwordEquipSpell)] }.Normalized(),
        Armor(Ring, 11, 0, OnEquip(ChargedEquipSpell, charges: -1), new ItemSpell(ConsumableUseSpell, ItemSpellTriggers.OnUse, -1, 0, -1, 0, -1)),
        Armor(SecondRing, 11, SetId),
        new ItemTemplate { Entry = Bag, Name = "Plain bag", DisplayId = Bag, Class = 1, InventoryType = 18, ContainerSlots = 6, Quality = 1, Spells = [OnEquip(BagEquipSpell)] }.Normalized(),
        new ItemTemplate { Entry = Quiver, Name = "Quiver", DisplayId = Quiver, Class = 11, SubClass = 2, InventoryType = 18, ContainerSlots = 8, BagFamily = 1, Spells = [OnEquip(QuiverSpell)] }.Normalized(),
        new ItemTemplate { Entry = Bow, Name = "Bow", DisplayId = Bow, Class = 2, SubClass = 2, InventoryType = 15, Delay = 2500, MaxDurability = 40, AmmoType = 2 }.Normalized(),
    ];

    private static readonly ItemSetCatalog Catalog = new(
    [
        new ItemSetRecord(SetId, "Test Set", [TwoPieceSpell, FourPieceSpell, 0, 0, 0, 0, 0, 0], [2, 4, 0, 0, 0, 0, 0, 0], 0, 0),
        new ItemSetRecord(SkillSetId, "Skilled Set", [SkillSetSpell, 0, 0, 0, 0, 0, 0, 0], [1, 0, 0, 0, 0, 0, 0, 0], RequiredSkill, 200),
    ]);

    private static SpellInfo Passive(uint id, AuraType aura = AuraType.ModStat, int amount = 5) => Spell(id, Effect(SpellEffectName.ApplyAura, amount, aura: aura, misc: 0)) with
    {
        Attributes = SpellAttributes.Passive,
        Duration = new SpellDuration(-1, 0, -1),
        SpellVisual = 1,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private sealed class Skilled(uint skill) : IItemRequirements
    {
        public uint Skill { get; set; } = skill;

        public bool CanDualWield(PlayerInventory inventory) => false;

        public uint SkillValue(PlayerInventory inventory, uint id) => Skill;

        public bool HasSpell(PlayerInventory inventory, uint spellId) => true;

        public byte HonorRank(PlayerInventory inventory) => 0;

        public uint ReputationRank(PlayerInventory inventory, uint faction) => 3;
    }

    private sealed class Rig : IDisposable
    {
        public Rig(bool attach = true, uint skill = 300)
        {
            Kit = new SpellTestKit(
                Passive(TwoPieceSpell), Passive(FourPieceSpell), Passive(SkillSetSpell), Passive(SwordEquipSpell), Passive(ChargedEquipSpell),
                Passive(BagEquipSpell), Passive(QuiverSpell, AuraType.ModRangedAmmoHaste, 10), Passive(ConsumableUseSpell));
            (Player, _) = Kit.AddPlayer(1);
            Player.Inventory.Templates = new ItemTemplateStore([.. ItemTestData.Templates, .. Templates]);
            Player.Inventory.GuidAllocator = new ItemGuidAllocator();
            Player.Inventory.Requirements = new Skilled(skill);
            Unknown = [];
            Equips = new ItemEquipSpells(Kit.System, new ItemSetBonuses(Catalog, Kit.System, (set, item) => Unknown.Add((set, item))));
            if (attach)
            {
                Equips.Attach(Player);
            }
        }

        public SpellTestKit Kit { get; }

        public Player Player { get; }

        public ItemEquipSpells Equips { get; }

        public List<(uint Set, uint Item)> Unknown { get; }

        public PlayerInventory Inventory => Player.Inventory;

        public int Holders(uint spell) => Kit.System.GetAuras(Player).Count(h => h.Spell.Id == spell && !h.IsRemoved);

        public Item Wear(uint entry)
        {
            Item item = ItemTestData.Give(Inventory, entry);
            Inventory.AutoEquipItem(item.BagSlot, item.Slot);
            Assert.Equal(InventorySlots.Bag0, item.BagSlot);
            Assert.True(item.Slot < InventorySlots.BagEnd, $"item {entry} was not worn");
            return item;
        }

        public void TakeOff(Item item) => Inventory.RemoveItem(item.BagSlot, item.Slot);

        public void Dispose() => Kit.Dispose();
    }

    // --- item sets ------------------------------------------------------------------------------------------------------

    [Fact]
    public void TwoThenFourPieces_ApplyTheMatchingBonusAuras()
    {
        using var rig = new Rig();

        rig.Wear(Head);
        Assert.Equal(0, rig.Holders(TwoPieceSpell));

        rig.Wear(Chest);
        Assert.Equal(1, rig.Holders(TwoPieceSpell));
        Assert.Equal(0, rig.Holders(FourPieceSpell));

        rig.Wear(Legs);
        Assert.Equal(0, rig.Holders(FourPieceSpell));

        rig.Wear(Feet);
        Assert.Equal(1, rig.Holders(TwoPieceSpell));
        Assert.Equal(1, rig.Holders(FourPieceSpell));
        PlayerItemSets sets = ItemEquipSpells.SetsOf(rig.Inventory)!;
        Assert.Equal(4, sets.PieceCount(SetId));
        Assert.Equal([TwoPieceSpell, FourPieceSpell], sets.ActiveSpells(SetId));
    }

    [Fact]
    public void UnequippingOnePiece_RemovesTheHighestBonus_AndLosingTheSecondRemovesTheLast()
    {
        using var rig = new Rig();
        Item head = rig.Wear(Head);
        Item chest = rig.Wear(Chest);
        Item legs = rig.Wear(Legs);
        Item feet = rig.Wear(Feet);

        rig.TakeOff(feet);
        Assert.Equal(1, rig.Holders(TwoPieceSpell));
        Assert.Equal(0, rig.Holders(FourPieceSpell));

        rig.TakeOff(legs);
        Assert.Equal(1, rig.Holders(TwoPieceSpell));

        rig.TakeOff(chest);
        Assert.Equal(0, rig.Holders(TwoPieceSpell));
        Assert.Equal(1, ItemEquipSpells.SetsOf(rig.Inventory)!.PieceCount(SetId));

        rig.TakeOff(head);
        Assert.Equal(0, ItemEquipSpells.SetsOf(rig.Inventory)!.PieceCount(SetId));
        Assert.Empty(ItemEquipSpells.SetsOf(rig.Inventory)!.ActiveSpells(SetId));
    }

    [Fact]
    public void DestroyingAWornPiece_DropsTheBonusToo()
    {
        using var rig = new Rig();
        rig.Wear(Head);
        Item chest = rig.Wear(Chest);
        Assert.Equal(1, rig.Holders(TwoPieceSpell));

        rig.Inventory.DestroyItem(chest.BagSlot, chest.Slot);

        Assert.Equal(0, rig.Holders(TwoPieceSpell));
        Assert.Equal(1, ItemEquipSpells.SetsOf(rig.Inventory)!.PieceCount(SetId));
    }

    [Fact]
    public void ABrokenPiece_StillCountsForTheSet()
    {
        using var rig = new Rig();
        Item head = rig.Wear(Head);
        rig.Wear(Chest);
        rig.Inventory.DurabilityPointsLoss(head, 1000);   // breaks it
        Assert.Equal(0u, head.Durability);

        Assert.Equal(1, rig.Holders(TwoPieceSpell));   // "still active for broken items"
        Assert.Equal(2, ItemEquipSpells.SetsOf(rig.Inventory)!.PieceCount(SetId));

        rig.TakeOff(head);
        Assert.Equal(0, rig.Holders(TwoPieceSpell));
    }

    [Fact]
    public void ASetWithARequiredSkillTheWearerLacks_DoesNotCount_AndRemovalIsHarmless()
    {
        using var rig = new Rig(skill: 100);

        Item piece = rig.Wear(SkillHead);

        Assert.Equal(0, rig.Holders(SkillSetSpell));
        Assert.Equal(0, ItemEquipSpells.SetsOf(rig.Inventory)!.PieceCount(SkillSetId));
        rig.TakeOff(piece);   // nothing to remove: must not throw or go negative
        Assert.Equal(0, ItemEquipSpells.SetsOf(rig.Inventory)!.PieceCount(SkillSetId));
    }

    [Fact]
    public void APieceWornBeforeTheSkillWasReached_IsNotCounted_SoTakingItOffKeepsTheBonusOfTheCountedPieces()
    {
        using var rig = new Rig(skill: 100);
        Item early = rig.Wear(SkillHead);                       // skill 100 < 200: not counted
        ((Skilled)rig.Inventory.Requirements).Skill = 200;
        rig.Wear(SkillChest);                                   // counted: one piece, the one-piece bonus is on
        Assert.Equal(1, rig.Holders(SkillSetSpell));

        rig.TakeOff(early);

        Assert.Equal(1, ItemEquipSpells.SetsOf(rig.Inventory)!.PieceCount(SkillSetId));
        Assert.Equal(1, rig.Holders(SkillSetSpell));
    }

    [Fact]
    public void ASetWithARequiredSkillTheWearerHas_Counts()
    {
        using var rig = new Rig(skill: 200);

        rig.Wear(SkillHead);

        Assert.Equal(1, rig.Holders(SkillSetSpell));
    }

    [Fact]
    public void ASetMissingFromTheCatalog_IsReported_AndAppliesNothing()
    {
        using var rig = new Rig();

        Item piece = rig.Wear(UnlistedHead);
        rig.TakeOff(piece);

        Assert.Equal([(UnlistedSetId, UnlistedHead)], rig.Unknown);
    }

    [Fact]
    public void TwoPiecesOfTheSameSetInTwoRingSlots_BothCount()
    {
        using var rig = new Rig();

        rig.Wear(SecondRing);
        rig.Wear(Head);

        Assert.Equal(1, rig.Holders(TwoPieceSpell));
    }

    // --- ON_EQUIP item spells ---------------------------------------------------------------------------------------------

    [Fact]
    public void AnEquipSpell_AppliesOnEquip_AndIsRemovedOnUnequip_AndBelongsToTheItem()
    {
        using var rig = new Rig();

        Item sword = rig.Wear(Sword);

        SpellAuraHolder holder = Assert.Single(rig.Kit.System.GetAuras(rig.Player), h => h.Spell.Id == SwordEquipSpell);
        Assert.Equal(sword.Guid, holder.CastItemGuid);

        rig.TakeOff(sword);
        Assert.Equal(0, rig.Holders(SwordEquipSpell));
    }

    [Fact]
    public void AnEquipSpell_IsRemovedWhenTheWornItemBreaks_AndComesBackWhenRepaired()
    {
        using var rig = new Rig();
        Item sword = rig.Wear(Sword);

        rig.Inventory.DurabilityPointsLoss(sword, 1000);
        Assert.Equal(0, rig.Holders(SwordEquipSpell));

        rig.Inventory.RepairDurability(sword);
        Assert.Equal(1, rig.Holders(SwordEquipSpell));
    }

    [Fact]
    public void AnEquipSpell_DoesNotConsumeCharges_AndAnOnUseExpendableAuraSurvivesUnequip()
    {
        using var rig = new Rig();
        Item ring = rig.Wear(Ring);
        Assert.Equal(1, rig.Holders(ChargedEquipSpell));
        int chargesBefore = ring.GetInt32(UpdateFields.ItemFieldSpellCharges);

        // an aura of the item's on-use spell with negative charges, cast from the item (a consumable's own use)
        rig.Kit.System.CastItemSpell(rig.Player, ring, ConsumableUseSpell, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(1, rig.Holders(ConsumableUseSpell));

        rig.TakeOff(ring);

        Assert.Equal(0, rig.Holders(ChargedEquipSpell));
        Assert.Equal(1, rig.Holders(ConsumableUseSpell));   // ApplyItemEquipSpell keeps on-use spells with negative charges
        Assert.Equal(chargesBefore, ring.GetInt32(UpdateFields.ItemFieldSpellCharges));
    }

    [Fact]
    public void AnEquippedBagThatIsNotAQuiver_AppliesItsEquipSpell()
    {
        using var rig = new Rig();

        Item bag = rig.Wear(Bag);
        Assert.Equal(1, rig.Holders(BagEquipSpell));

        rig.TakeOff(bag);
        Assert.Equal(0, rig.Holders(BagEquipSpell));
    }

    // --- login replay -----------------------------------------------------------------------------------------------------

    [Fact]
    public void LoginReplay_ReAppliesSetBonusesAndEquipSpellsOfWornItems()
    {
        using var rig = new Rig(attach: false);
        rig.Wear(Head);
        rig.Wear(Chest);
        rig.Wear(Sword);
        Assert.Equal(0, rig.Holders(TwoPieceSpell));   // worn before anything listened: the loaded character
        Assert.Equal(0, rig.Holders(SwordEquipSpell));

        rig.Equips.Attach(rig.Player);

        Assert.Equal(1, rig.Holders(TwoPieceSpell));
        Assert.Equal(1, rig.Holders(SwordEquipSpell));
        Assert.Equal(2, ItemEquipSpells.SetsOf(rig.Inventory)!.PieceCount(SetId));
    }

    [Fact]
    public void LoginReplay_DoesNotStackOnARestoredAura_NorOnASecondAttach()
    {
        using var rig = new Rig(attach: false);
        rig.Wear(Head);
        rig.Wear(Chest);
        rig.Wear(Sword);
        // the saved aura list restored these without an item before the replay
        rig.Kit.System.CastSpell(rig.Player, TwoPieceSpell, SpellCastTargets.ForSelf(), triggered: true);
        rig.Kit.System.CastSpell(rig.Player, SwordEquipSpell, SpellCastTargets.ForSelf(), triggered: true);

        rig.Equips.Attach(rig.Player);
        rig.Equips.Attach(rig.Player);

        Assert.Equal(1, rig.Holders(TwoPieceSpell));
        Assert.Equal(1, rig.Holders(SwordEquipSpell));
        Assert.Equal(2, ItemEquipSpells.SetsOf(rig.Inventory)!.PieceCount(SetId));
    }

    [Fact]
    public void LoginReplay_StillCountsABrokenPieceForTheSet_ButNotItsEquipSpell()
    {
        using var rig = new Rig(attach: false);
        Item sword = rig.Wear(Sword);
        Item head = rig.Wear(Head);
        rig.Wear(Chest);
        rig.Inventory.DurabilityPointsLoss(sword, 1000);
        rig.Inventory.DurabilityPointsLoss(head, 1000);

        rig.Equips.Attach(rig.Player);

        Assert.Equal(1, rig.Holders(TwoPieceSpell));
        Assert.Equal(0, rig.Holders(SwordEquipSpell));
    }

    // --- the quiver rule keeps working next to it ------------------------------------------------------------------------------

    [Fact]
    public void QuiverHaste_StillAppliesOnce_WhenTheGeneralEngineIsAttachedAsWell()
    {
        using var rig = new Rig(attach: false);
        ItemTemplate[] all = [.. ItemTestData.Templates, .. Templates];
        rig.Inventory.Templates = new ItemTemplateStore(all);
        rig.Player.SetUInt32(UpdateFields.UnitFieldRangedattacktime, 2500);
        rig.Wear(Bow);
        var quivers = new QuiverHaste(rig.Kit.System);
        quivers.Attach(rig.Player);
        rig.Equips.Attach(rig.Player);

        rig.Wear(Quiver);

        Assert.Equal(1, rig.Holders(QuiverSpell));
        Assert.Equal((uint)(2500f * (100.0f / 110.0f)), rig.Player.GetUInt32(UpdateFields.UnitFieldRangedattacktime));
    }
}
