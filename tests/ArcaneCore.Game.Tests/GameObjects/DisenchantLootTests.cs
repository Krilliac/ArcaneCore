using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Skills;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Skills;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>
/// Disenchanting (vmangos Spell.cpp:7376-7392, SpellEffects.cpp:5059-5073, LootHandler.cpp:543-553). Loot ids follow classic-db's
/// disenchant_loot_template shapes: id 48 always gives one Large Brilliant Shard (14344), id 49 a 0.5% Nexus Crystal (20725).
/// </summary>
public sealed class DisenchantLootTests
{
    private const uint GreenSword = 90101;
    private const uint NoDisenchantRing = 90102;
    private const uint PlainTrinket = 90103;
    private const uint LargeShard = 14344;
    private const uint NexusCrystal = 20725;
    private const uint SecondCrystal = 20726;
    private const uint DisenchantSpell = 13262;

    private static readonly ItemTemplateStore Store = new(
    [
        .. ItemTestData.Templates,
        new ItemTemplate { Entry = GreenSword, Class = 2, SubClass = 7, Name = "Green Sword", DisplayId = 10, Quality = 2, InventoryType = 21, DisenchantId = 48 },
        new ItemTemplate { Entry = NoDisenchantRing, Class = 4, SubClass = 0, Name = "Quest Ring", DisplayId = 11, Quality = 2, InventoryType = 11, DisenchantId = 49, Flags = DisenchantLoot.ItemFlagNoDisenchant },
        new ItemTemplate { Entry = PlainTrinket, Class = 4, SubClass = 0, Name = "Plain Trinket", DisplayId = 12, Quality = 2, InventoryType = 12 },
        new ItemTemplate { Entry = LargeShard, Class = 7, Name = "Large Brilliant Shard", DisplayId = 13, Quality = 3, Stackable = 20 },
        new ItemTemplate { Entry = NexusCrystal, Class = 7, Name = "Nexus Crystal", DisplayId = 14, Quality = 4, Stackable = 10 },
        new ItemTemplate { Entry = SecondCrystal, Class = 7, Name = "Second Crystal", DisplayId = 15, Quality = 4, Stackable = 10 },
    ], ItemTestData.StartingItems);

    private sealed class Rig : IDisposable
    {
        public Rig(IEnumerable<(LootTableKind, LootStoreRow)>? rows = null)
        {
            Kit = new SpellTestKit(SpellTestKit.Spell(DisenchantSpell, SpellTestKit.Effect(SpellEffectName.Disenchant, 0)) with
            {
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            });
            Loot = new LootService(new LootContent(
                rows ??
                [
                    (LootTableKind.Disenchant, Row(48, LargeShard, 100)),
                    (LootTableKind.Disenchant, Row(49, NexusCrystal, 0.5f)),
                ], []), random: new Random(9)) { Items = Store };
            Disenchant = new DisenchantLoot(Loot);
            new DisenchantSpells(_ => Disenchant).Register(Kit.System);
            (Player, Session) = Kit.AddPlayer(1);
            Player.Inventory.Templates = Store;
            Player.Inventory.GuidAllocator = new ItemGuidAllocator();
            Player.Inventory.Load([]);
            SkillRandom = new ScriptedSkillRandom();
            Skills = new PlayerSkills(Player, SkillTestKit.Catalog(), new SkillOptions(), new FakeSkillSpellHost { Cascade = Player }, SkillRandom);
            Player.AttachSkills(Skills);
            Kit.Spellbook.Teach(Player, DisenchantSpell);
        }

        public SpellTestKit Kit { get; }

        public LootService Loot { get; }

        public DisenchantLoot Disenchant { get; }

        public Player Player { get; }

        public FakeSession Session { get; }

        public PlayerSkills Skills { get; }

        public ScriptedSkillRandom SkillRandom { get; }

        public Item Give(uint entry)
        {
            Assert.Equal(InventoryResult.Ok, Player.Inventory.AddItem(entry, 1, out Item? item));
            return item!;
        }

        public SpellCastResult Cast(Item? item)
        {
            Session.Clear();
            var targets = item is null
                ? SpellCastTargets.ForSelf()
                : new SpellCastTargets { Mask = SpellCastTargetFlags.Item, Item = item.Guid };
            return Kit.System.HandleCastRequest(Player, DisenchantSpell, targets);
        }

        public void Dispose() => Kit.Dispose();
    }

    private static ParsedLoot Window(FakeSession session)
        => ParsedLoot.Parse(Assert.Single(SpellTestKit.Packets(session, WorldOpcode.SmsgLootResponse)));

    // --- the cast check -------------------------------------------------------------------------

    [Theory]
    [InlineData(PlainTrinket)]       // no DisenchantID
    [InlineData(NoDisenchantRing)]   // ITEM_FLAG_NO_DISENCHANT 0x8000
    public void AnItemWithoutDisenchantLoot_OrFlaggedNoDisenchant_AnswersCantBeDisenchanted(uint entry)
    {
        using var rig = new Rig();
        Item item = rig.Give(entry);

        Assert.Equal(SpellCastResult.CantBeDisenchanted, rig.Cast(item));

        Assert.Empty(SpellTestKit.Packets(rig.Session, WorldOpcode.SmsgLootResponse));
        Assert.False(item.IsSoulBound);
    }

    [Fact]
    public void ACastWithoutAnItemTarget_AnswersCantBeDisenchanted()
    {
        using var rig = new Rig();
        Assert.Equal(SpellCastResult.CantBeDisenchanted, rig.Cast(null));
    }

    [Fact]
    public void AnItemOfAnotherOwner_AndAnItemAlreadyBeingDisenchanted_AreRefused()
    {
        using var rig = new Rig();
        Item item = rig.Give(GreenSword);
        (Player other, _) = rig.Kit.AddPlayer(2, 1, 0);

        Assert.Equal(SpellCastResult.CantBeDisenchanted, rig.Disenchant.CheckTarget(other, item));   // not owned by the caster (trade window)

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(item));
        Assert.Equal(SpellCastResult.CantBeDisenchanted, rig.Disenchant.CheckTarget(rig.Player, item)); // generated loot still open
    }

    // --- the effect ----------------------------------------------------------------------------------

    [Fact]
    public void ADisenchant_BindsTheItem_AndShowsTheTemplateLoot_AsWireType4()
    {
        using var rig = new Rig();
        Item item = rig.Give(GreenSword);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(item));

        Assert.True(item.IsSoulBound);
        ParsedLoot window = Window(rig.Session);
        Assert.Equal((item.Guid.Value, LootType.Disenchanting), (window.Guid, window.Type));
        ParsedLootItem shard = Assert.Single(window.Items);   // id 48 always one shard; the 0.5% crystal belongs to id 49
        Assert.Equal((LargeShard, 1u, LootSlotType.AllowLoot), (shard.ItemId, shard.Count, shard.SlotType));
        Assert.Equal(0u, window.Gold);
        Assert.Equal(1, rig.Disenchant.ActiveItems);
    }

    [Fact]
    public void ADisenchant_RaisesTheCraftSkillOnce_ThroughTheSpellsSkillLineAbility()
    {
        using var rig = new Rig();
        rig.Skills.Set(SkillIds.Blacksmithing, 50, 150, 1);
        rig.SkillRandom.Ints.Enqueue(1);
        Item item = rig.Give(GreenSword);

        // The fixture catalog knows the Smelt Copper ability of Blacksmithing; the point is that the effect passes the casting spell to UpdateCraft.
        Assert.Equal(LootResult.Ok, rig.Disenchant.Disenchant(rig.Player, item, SkillTestKit.SmeltCopper));

        Assert.Equal((ushort)51, rig.Skills.GetValuePure(SkillIds.Blacksmithing));
    }

    // --- the release ---------------------------------------------------------------------------------

    [Fact]
    public void ReleasingTheWindow_StoresWhatIsLeft_AndDestroysTheItem()
    {
        using var rig = new Rig();
        Item item = rig.Give(GreenSword);
        rig.Cast(item);
        Assert.Equal(0u, rig.Player.Inventory.GetItemCount(LargeShard));

        rig.Loot.Release(rig.Player, item.Guid);

        Assert.Equal(1u, rig.Player.Inventory.GetItemCount(LargeShard));   // auto-stored
        Assert.Null(rig.Player.Inventory.GetItemByGuid(item.Guid));        // item destroyed
        Assert.Equal(0, rig.Disenchant.ActiveItems);
        Assert.Null(rig.Loot.FindLoot(item.Guid));
    }

    [Fact]
    public void TakingTheLootFirst_ThenReleasing_AlsoDestroysTheItem_WithoutStoringTwice()
    {
        using var rig = new Rig();
        Item item = rig.Give(GreenSword);
        rig.Cast(item);

        Assert.Equal(InventoryResult.Ok, rig.Loot.TakeItem(rig.Player, 0));
        rig.Loot.Release(rig.Player, item.Guid);

        Assert.Equal(1u, rig.Player.Inventory.GetItemCount(LargeShard));
        Assert.Null(rig.Player.Inventory.GetItemByGuid(item.Guid));
    }

    [Fact]
    public void WithFullBags_WhatDoesNotFitIsLost_TheItemStillGoes()
    {
        using var rig = new Rig(
        [
            (LootTableKind.Disenchant, Row(48, LargeShard, 100)),
            (LootTableKind.Disenchant, Row(48, NexusCrystal, 100)),
        ]);
        Item sword = rig.Give(GreenSword);
        // Fill every free backpack slot with non-stacking junk (and the large shard stack is only started by the loot).
        while (rig.Player.Inventory.AddItem(PlainTrinket, 1, out _) == InventoryResult.Ok)
        {
        }

        rig.Cast(sword);
        rig.Loot.Release(rig.Player, sword.Guid);

        Assert.Equal(0u, rig.Player.Inventory.GetItemCount(LargeShard));
        Assert.Equal(0u, rig.Player.Inventory.GetItemCount(NexusCrystal));
        Assert.Null(rig.Player.Inventory.GetItemByGuid(sword.Guid));
    }

    [Fact]
    public void TheWindowCloses_WhenTheItemLeavesTheInventory()
    {
        using var rig = new Rig();
        Item item = rig.Give(GreenSword);
        rig.Cast(item);
        rig.Player.Inventory.DestroyItemCount(item, 1);

        Assert.NotEqual(InventoryResult.Ok, rig.Loot.TakeItem(rig.Player, 0));
    }
}