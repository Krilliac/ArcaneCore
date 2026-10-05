using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Updates;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>Item casts retain the live item identity and settle charges only after successful SpellGo.</summary>
public sealed class ItemUseTests
{
    private const uint UseSpell = 99001;
    private const uint DelayedSpell = 99002;
    private const uint ChargedItem = 99011;
    private const uint ExpendableItem = 99012;

    [Fact]
    public void UseItem_ConsumesOnePositiveCharge_AndDoesNotRequireSpellbookKnowledge()
    {
        using var kit = new SpellTestKit(SpellTestKit.Spell(UseSpell, SpellTestKit.Effect(SpellEffectName.Dummy, 0)));
        (var player, _) = kit.AddPlayer(1);
        player.Inventory.Templates = new ItemTemplateStore([
            new ItemTemplate { Entry = ChargedItem, Class = 0, Stackable = 1, Spells = [new ItemSpell(UseSpell, 0, 2, 0, 0, 0, 0)] }]);
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        player.Inventory.Load([]);
        Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(ChargedItem, 1, out Item? item));

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleItemUse(player, item!.BagSlot, item.Slot, 0, SpellCastTargets.ForSelf()));
        Assert.Equal(1, item.GetInt32(UpdateFields.ItemFieldSpellCharges));
    }

    [Fact]
    public void UseItem_ExpendableStack_RemovesOneAfterSuccessfulCast()
    {
        using var kit = new SpellTestKit(SpellTestKit.Spell(UseSpell, SpellTestKit.Effect(SpellEffectName.Dummy, 0)));
        (var player, _) = kit.AddPlayer(1);
        player.Inventory.Templates = new ItemTemplateStore([
            new ItemTemplate { Entry = ExpendableItem, Class = 0, Stackable = 20, Spells = [new ItemSpell(UseSpell, 0, -1, 0, 0, 0, 0)] }]);
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        player.Inventory.Load([]);
        Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(ExpendableItem, 2, out Item? item));

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleItemUse(player, item!.BagSlot, item.Slot, 0, SpellCastTargets.ForSelf()));
        Assert.Equal(1u, player.Inventory.GetItemCount(ExpendableItem));
    }

    [Fact]
    public void UseItem_CancelledAfterMove_DoesNotConsumeCharge()
    {
        SpellInfo delayed = SpellTestKit.Spell(DelayedSpell, SpellTestKit.Effect(SpellEffectName.Dummy, 0)) with
        {
            CastTime = new SpellCastTime(1000, 0, 0),
            InterruptFlags = SpellInterruptFlags.Movement,
        };
        using var kit = new SpellTestKit(delayed);
        (var player, _) = kit.AddPlayer(1);
        player.Inventory.Templates = new ItemTemplateStore([
            new ItemTemplate { Entry = ChargedItem, Class = 0, Stackable = 1, Spells = [new ItemSpell(DelayedSpell, 0, 1, 0, 0, 0, 0)] }]);
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        player.Inventory.Load([]);
        Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(ChargedItem, 1, out Item? item));
        byte originalSlot = item!.Slot;

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleItemUse(player, item.BagSlot, item.Slot, 0, SpellCastTargets.ForSelf()));
        player.Inventory.SwapItem(item.BagSlot, item.Slot, InventorySlots.Bag0, (byte)(originalSlot + 1));
        kit.Advance(1000);

        Assert.Same(item, player.Inventory.GetItem(InventorySlots.Bag0, (byte)(originalSlot + 1)));
        Assert.Equal(1, item.GetInt32(UpdateFields.ItemFieldSpellCharges));
    }

    [Fact]
    public void UseItem_DoesNotPaySpellPowerCost()
    {
        SpellInfo powered = SpellTestKit.Spell(UseSpell, SpellTestKit.Effect(SpellEffectName.Dummy, 0)) with
        {
            PowerType = (int)PowerType.Rage,
            ManaCost = 50,
        };
        using var kit = new SpellTestKit(powered);
        Player player = TestPlayers.Add(kit, 1, Class.Warrior, PowerType.Rage, maxPower: 100);
        player.SetUInt32(UpdateFields.UnitFieldPower1, 70);
        player.Inventory.Templates = new ItemTemplateStore([
            new ItemTemplate { Entry = ChargedItem, Class = 0, Stackable = 1, Spells = [new ItemSpell(UseSpell, 0, 1, 0, 0, 0, 0)] }]);
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        player.Inventory.Load([]);
        Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(ChargedItem, 1, out Item? item));

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleItemUse(player, item!.BagSlot, item.Slot, 0, SpellCastTargets.ForSelf()));
        Assert.Equal(70u, player.GetUInt32(UpdateFields.UnitFieldPower1));
    }

    [Fact]
    public void UseItem_RejectsAnEmptyChargeBeforeDispatch()
    {
        using var kit = new SpellTestKit(SpellTestKit.Spell(UseSpell, SpellTestKit.Effect(SpellEffectName.Dummy, 0)));
        (var player, var session) = kit.AddPlayer(1);
        player.Inventory.Templates = new ItemTemplateStore([
            new ItemTemplate { Entry = ChargedItem, Class = 0, Stackable = 1, Spells = [new ItemSpell(UseSpell, 0, 1, 0, 0, 0, 0)] }]);
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        player.Inventory.Load([]);
        Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(ChargedItem, 1, out Item? item));
        item!.SetInt32(UpdateFields.ItemFieldSpellCharges, 0);

        Assert.Equal(SpellCastResult.ItemNotReady, kit.System.HandleItemUse(player, item.BagSlot, item.Slot, 0, SpellCastTargets.ForSelf()));
        Assert.Empty(SpellTestKit.Packets(session, ArcaneCore.Protocol.WorldOpcode.SmsgSpellStart));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void UseItem_DispatchesLaterOnUseSpellsAsTriggered(int charges)
    {
        const uint second = 99003;
        SpellInfo aura = SpellTestKit.Spell(second, SpellTestKit.Effect(SpellEffectName.ApplyAura, 1, aura: AuraType.Dummy));
        using var kit = new SpellTestKit(SpellTestKit.Spell(UseSpell, SpellTestKit.Effect(SpellEffectName.Dummy, 0)), aura);
        (var player, var session) = kit.AddPlayer(1);
        player.Inventory.Templates = new ItemTemplateStore([
            new ItemTemplate { Entry = ChargedItem, Class = 0, Stackable = 1, Spells = [new ItemSpell(UseSpell, 0, charges, 0, 0, 0, 0), new ItemSpell(second, 0, charges, 0, 0, 0, 0)] }]);
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        player.Inventory.Load([]);
        Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(ChargedItem, 1, out Item? item));

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleItemUse(player, item!.BagSlot, item.Slot, 0, SpellCastTargets.ForSelf()));
        Assert.Single(SpellTestKit.Packets(session, ArcaneCore.Protocol.WorldOpcode.SmsgSpellStart));
        Assert.True(kit.System.HasAura(player, second));
        Assert.Equal(charges < 0 ? 0u : 1u, player.Inventory.GetItemCount(ChargedItem));
        if (charges > 0) Assert.Equal(0, item.GetInt32(UpdateFields.ItemFieldSpellCharges));
    }

    [Fact]
    public void UseItem_SameEntryReagent_ConsumesEarlierStacksExactlyOnce_AndLeavesCastItemChargeUntouched()
    {
        const uint reagentEntry = 99004;
        SpellInfo reagentSpell = SpellTestKit.Spell(UseSpell, SpellTestKit.Effect(SpellEffectName.Dummy, 0)) with
        {
            Reagents = [new SpellReagent((int)reagentEntry, 2)],
        };
        using var kit = new SpellTestKit(reagentSpell);
        (var player, _) = kit.AddPlayer(1);
        player.Inventory.Templates = new ItemTemplateStore([
            new ItemTemplate { Entry = reagentEntry, Class = 0, Stackable = 20,
                Spells = [new ItemSpell(UseSpell, 0, -1, 0, 0, 0, 0)] },
        ]);
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        player.Inventory.Load([
            new(0, InventorySlots.ItemStart, new ItemInstanceData { Guid = 100, Entry = reagentEntry, Count = 3, Charges = [-1, 0, 0, 0, 0] }),
            new(0, (byte)(InventorySlots.ItemStart + 1), new ItemInstanceData { Guid = 101, Entry = reagentEntry, Count = 1, Charges = [-1, 0, 0, 0, 0] }),
        ]);
        Item castItem = player.Inventory.GetItem(InventorySlots.Bag0, (byte)(InventorySlots.ItemStart + 1))!;

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleItemUse(player, castItem.BagSlot, castItem.Slot, 0, SpellCastTargets.ForSelf()));
        Assert.Same(castItem, player.Inventory.GetItem(InventorySlots.Bag0, (byte)(InventorySlots.ItemStart + 1)));
        Assert.Equal(-1, castItem.GetInt32(UpdateFields.ItemFieldSpellCharges));
        Assert.Equal(1u, player.Inventory.GetItemCount(reagentEntry));
    }

    [Fact]
    public void UseItem_ItemCooldownAndCategoryOverride_AreCapturedForRelog()
    {
        SpellInfo cooldownSpell = SpellTestKit.Spell(UseSpell, SpellTestKit.Effect(SpellEffectName.Dummy, 0)) with
        {
            RecoveryTime = 9000,
            Category = 4,
            CategoryRecoveryTime = 8000,
        };
        using var kit = new SpellTestKit(cooldownSpell);
        (var player, _) = kit.AddPlayer(1);
        player.Inventory.Templates = new ItemTemplateStore([
            new ItemTemplate { Entry = ChargedItem, Class = 0, Stackable = 1,
                Spells = [new ItemSpell(UseSpell, 0, 3, 0, 1200, 77, 2400)] },
        ]);
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        player.Inventory.Load([]);
        Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(ChargedItem, 1, out Item? item));

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleItemUse(player, item!.BagSlot, item.Slot, 0, SpellCastTargets.ForSelf()));
        SpellStateSnapshot snapshot = kit.System.CaptureState(player, nowUnixMs: 1_000_000);
        Assert.Contains(snapshot.Cooldowns, c => c.Kind == SpellCooldownKind.Spell && c.Id == UseSpell);
        Assert.Contains(snapshot.Cooldowns, c => c.Kind == SpellCooldownKind.Category && c.Id == 77);
        Assert.Contains(snapshot.Cooldowns, c => c.Kind == SpellCooldownKind.Spell && c.EndsAtUnixMs == 1_001_200);
        Assert.Contains(snapshot.Cooldowns, c => c.Kind == SpellCooldownKind.Category && c.EndsAtUnixMs == 1_002_400);
        kit.Advance(1300);
        Assert.Equal(SpellCastResult.NotReady, kit.System.HandleItemUse(player, item.BagSlot, item.Slot, 0, SpellCastTargets.ForSelf()));
        Assert.Equal(2, item.GetInt32(UpdateFields.ItemFieldSpellCharges));

        var cold = new SpellSystem(kit.System.Store, () => 0);
        Assert.Equal(1, cold.RestoreCooldowns(player, snapshot.Cooldowns, nowUnixMs: 1_001_300));
        Assert.Equal(SpellCastResult.NotReady, cold.HandleItemUse(player, item.BagSlot, item.Slot, 0, SpellCastTargets.ForSelf()));
        Assert.Equal(2, item.GetInt32(UpdateFields.ItemFieldSpellCharges));
    }

    [Fact]
    public void Consumable_AtFullHealthAndPower_IsRefused_ButMixedRestorableResourcesAreAllowed()
    {
        const uint heal = 99005;
        const uint mixedItem = 99007;
        SpellInfo mixedSpell = SpellTestKit.Spell(heal,
            SpellTestKit.Effect(SpellEffectName.Heal, 10),
            SpellTestKit.Effect(SpellEffectName.Energize, 10, misc: (int)PowerType.Rage));
        using var kit = new SpellTestKit(mixedSpell);
        (var player, _) = kit.AddPlayer(1);
        player.Health = player.GetUInt32(UpdateFields.UnitFieldMaxhealth);
        player.SetUInt32(UpdateFields.UnitFieldPower2, player.GetUInt32(UpdateFields.UnitFieldMaxpower2));
        player.Inventory.Templates = new ItemTemplateStore([
            new ItemTemplate { Entry = ChargedItem, Class = 0, Stackable = 1,
                Spells = [new ItemSpell(heal, 0, 1, 0, 0, 0, 0)] },
        ]);
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        player.Inventory.Load([]);
        Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(ChargedItem, 1, out Item? full));
        // The last applicable resource effect selects the failure, as in CheckItems.
        Assert.Equal(SpellCastResult.AlreadyAtFullPower, kit.System.HandleItemUse(player, full!.BagSlot, full.Slot, 0, SpellCastTargets.ForSelf()));

        player.Inventory.Templates = new ItemTemplateStore([
            new ItemTemplate { Entry = ChargedItem, Class = 0, Stackable = 1, Spells = [new ItemSpell(heal, 0, 1, 0, 0, 0, 0)] },
            new ItemTemplate { Entry = mixedItem, Class = 0, Stackable = 1, Spells = [new ItemSpell(heal, 0, 1, 0, 0, 0, 0)] },
        ]);
        player.SetUInt32(UpdateFields.UnitFieldPower2, 0);
        Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(mixedItem, 1, out Item? mixed));
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleItemUse(player, mixed!.BagSlot, mixed.Slot, 0, SpellCastTargets.ForSelf()));
    }
}
