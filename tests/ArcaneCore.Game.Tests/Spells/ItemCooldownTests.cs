using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

public sealed class ItemCooldownTests
{
    private const uint Spell = 49001;
    private const uint ItemEntry = 6948;

    [Fact]
    public void ItemCooldown_CaptureRestoreAndInitialUi_PreserveItemAndEffectiveCategory()
    {
        SpellInfo spell = SpellTestKit.Spell(Spell, SpellTestKit.Effect(SpellEffectName.Dummy, 0)) with { RecoveryTime = 1_000, Category = 4, CategoryRecoveryTime = 1_000 };
        using var kit = new SpellTestKit(spell);
        (Player player, _) = kit.AddPlayer(1);
        Wire(player, new ItemSpell(Spell, 0, 5, 0, 1_000, 77, 10_000));
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleItemUse(player, InventorySlots.Bag0, InventorySlots.ItemStart, 0, SpellCastTargets.ForSelf()));
        kit.Advance(1_500);
        InitialSpellCooldown active = Assert.Single(kit.System.GetActiveCooldowns(player));
        Assert.Equal((ItemEntry, 77u), (active.ItemId, active.Category));
        Assert.Equal(0u, active.CooldownMs);
        Assert.True(active.CategoryCooldownMs > 0);

        SpellStateSnapshot snapshot = kit.System.CaptureState(player, 1_000_000);
        using var fresh = new SpellTestKit(spell);
        (Player restored, _) = fresh.AddPlayer(2);
        Wire(restored, new ItemSpell(Spell, 0, 5, 0, 1_000, 77, 10_000));
        Assert.True(fresh.System.RestoreCooldowns(restored, snapshot.Cooldowns, 1_000_000) > 0);
        InitialSpellCooldown reloaded = Assert.Single(fresh.System.GetActiveCooldowns(restored));
        Assert.Equal((ItemEntry, 77u), (reloaded.ItemId, reloaded.Category));
        Assert.NotEqual(SpellCastResult.CastOk, fresh.System.HandleItemUse(restored, InventorySlots.Bag0, InventorySlots.ItemStart, 0, SpellCastTargets.ForSelf()));
        fresh.System.ClearCooldown(restored, Spell);
        Assert.Empty(fresh.System.GetActiveCooldowns(restored));
    }

    [Fact]
    public void UnknownOwnerRow_IsIgnoredByInitialSpellProjection()
    {
        using var kit = new SpellTestKit(SpellTestKit.Spell(Spell, SpellTestKit.Effect(SpellEffectName.Dummy, 0)));
        (Player player, _) = kit.AddPlayer(1);
        Assert.Equal(0, kit.System.RestoreCooldowns(player, [new PersistedCooldown(SpellCooldownKind.Spell, 65500, 2_000_000, ItemEntry, 77, 65500)], 1_000_000));
        Assert.Empty(kit.System.GetActiveCooldowns(player));
    }

    [Fact]
    public void ItemCategoryZero_FallsBackToSpellCategory()
    {
        SpellInfo spell = SpellTestKit.Spell(Spell, SpellTestKit.Effect(SpellEffectName.Dummy, 0)) with { Category = 4, CategoryRecoveryTime = 5000 };
        using var kit = new SpellTestKit(spell);
        (Player player, _) = kit.AddPlayer(1);
        Wire(player, new ItemSpell(Spell, 0, 2, 0, 2000, 0, 3000));
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleItemUse(player, InventorySlots.Bag0, InventorySlots.ItemStart, 0, SpellCastTargets.ForSelf()));
        Assert.Equal((uint)4, Assert.Single(kit.System.GetActiveCooldowns(player)).Category);
    }

    private static void Wire(Player player, ItemSpell spell)
    {
        player.Inventory.Templates = new ItemTemplateStore([new ItemTemplate { Entry = ItemEntry, Class = 0, Stackable = 1, Spells = [spell] }]);
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        player.Inventory.Load([]);
        Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(ItemEntry, 1, out _));
    }
}
