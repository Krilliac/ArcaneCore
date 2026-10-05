using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Stats;
using ArcaneCore.Kernel.Items;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

public sealed class DurabilitySpellTests
{
    [Theory]
    [InlineData(SpellEffectName.DurabilityDamage)]
    [InlineData(SpellEffectName.DurabilityDamagePct)]
    public void HandlerIsDiscovered(SpellEffectName effect)
    {
        using var rig = new DurabilityRig(effect, 5, InventorySlots.MainHand);
        Assert.True(rig.Kit.System.HasEffectHandler(effect));
    }

    [Theory]
    [InlineData(SpellEffectName.DurabilityDamage, 5, 15u)]
    [InlineData(SpellEffectName.DurabilityDamagePct, 25, 15u)]
    [InlineData(SpellEffectName.DurabilityDamagePct, 1, 19u)]
    [InlineData(SpellEffectName.DurabilityDamage, 100, 0u)]
    [InlineData(SpellEffectName.DurabilityDamagePct, 150, 0u)]
    public void SelectedEquipment_NormalCastUpdatesDurabilityAndSnapshot(SpellEffectName effect, int value, uint expected)
    {
        using var rig = new DurabilityRig(effect, value, InventorySlots.MainHand);
        Assert.Equal(SpellCastResult.CastOk, rig.Cast());
        Assert.Equal(expected, rig.Sword.Durability);
        Assert.Equal(expected, rig.Sword.GetUInt32(UpdateFields.ItemFieldDurability));
        Assert.Equal(expected, Assert.Single(rig.Player.Inventory.CreateSnapshot().Items, r => r.Item.Guid == rig.Sword.Guid.Low).Item.Durability);
        Assert.Equal(20u, rig.Backpack.Durability);
    }

    [Theory]
    [InlineData(SpellEffectName.DurabilityDamage, -1, 20u)]
    [InlineData(SpellEffectName.DurabilityDamage, -2, 15u)]
    [InlineData(SpellEffectName.DurabilityDamage, -3, 15u)]
    [InlineData(SpellEffectName.DurabilityDamagePct, -1, 20u)]
    [InlineData(SpellEffectName.DurabilityDamagePct, -2, 15u)]
    public void NegativeSelectors_WalkOnlyEquipmentOrCarriedContents(SpellEffectName effect, int selector, uint carried)
    {
        using var rig = new DurabilityRig(effect, effect == SpellEffectName.DurabilityDamage ? 5 : 25, selector);
        Assert.Equal(SpellCastResult.CastOk, rig.Cast());
        Assert.Equal(15u, rig.Sword.Durability);
        Assert.Equal(carried, rig.Backpack.Durability);
        Assert.Equal(carried, rig.BagContent.Durability);
        Assert.Equal(20u, rig.Bag.Durability);
        Assert.Equal(20u, rig.Bank.Durability);
        Assert.Equal(20u, rig.BankBag.Durability);
        Assert.Equal(20u, rig.BankBagContent.Durability);
        Assert.Equal(20u, rig.Key.Durability);
    }

    [Theory]
    [InlineData(SpellEffectName.DurabilityDamage)]
    [InlineData(SpellEffectName.DurabilityDamagePct)]
    public void SelectedEquippedBag_IsInsideSourceSlotBound(SpellEffectName effect)
    {
        using var rig = new DurabilityRig(effect, effect == SpellEffectName.DurabilityDamage ? 5 : 25, InventorySlots.BagStart);
        Assert.Equal(SpellCastResult.CastOk, rig.Cast());
        Assert.Equal(15u, rig.Bag.Durability);
        Assert.Equal(20u, rig.BagContent.Durability);
    }

    [Theory]
    [InlineData(SpellEffectName.DurabilityDamage, InventorySlots.BagEnd)]
    [InlineData(SpellEffectName.DurabilityDamagePct, InventorySlots.BagEnd)]
    [InlineData(SpellEffectName.DurabilityDamage, InventorySlots.BankItemStart)]
    [InlineData(SpellEffectName.DurabilityDamagePct, int.MaxValue)]
    [InlineData(SpellEffectName.DurabilityDamage, InventorySlots.Head)]
    public void InvalidOrEmptySelectedSlot_DoesNothing(SpellEffectName effect, int selector)
    {
        using var rig = new DurabilityRig(effect, 25, selector);
        Assert.Equal(SpellCastResult.CastOk, rig.Cast());
        Assert.All(rig.Player.Inventory.AllItems, i => Assert.Equal(20u, i.Durability));
    }

    [Theory]
    [InlineData(0, InventorySlots.MainHand, 20u)]
    [InlineData(-25, InventorySlots.MainHand, 20u)]
    [InlineData(0, -1, 19u)]
    public void PercentNonpositive_PreservesSourceSelectorBranchOrder(int value, int selector, uint expected)
    {
        using var rig = new DurabilityRig(SpellEffectName.DurabilityDamagePct, value, selector);
        Assert.Equal(SpellCastResult.CastOk, rig.Cast());
        Assert.Equal(expected, rig.Sword.Durability);
        Assert.Equal(20u, rig.Backpack.Durability);
    }

    [Fact]
    public void SignedPointRepair_ClampsToMaximumAndRestoresBrokenWeaponStats()
    {
        using var rig = new DurabilityRig(SpellEffectName.DurabilityDamage, -100, InventorySlots.MainHand, broken: true);
        Assert.Equal(1f, rig.Player.StatState.WeaponDamage(ArcaneCore.Game.Combat.WeaponAttackType.BaseAttack, 0).Min);
        Assert.Equal(SpellCastResult.CastOk, rig.Cast());
        Assert.Equal(20u, rig.Sword.Durability);
        Assert.Equal(5f, rig.Player.StatState.WeaponDamage(ArcaneCore.Game.Combat.WeaponAttackType.BaseAttack, 0).Min);
    }

    [Theory]
    [InlineData(SpellEffectName.DurabilityDamage)]
    [InlineData(SpellEffectName.DurabilityDamagePct)]
    public void DurabilityDisabled_CastLeavesItemsUnchanged(SpellEffectName effect)
    {
        using var rig = new DurabilityRig(effect, 100, -2);
        rig.Player.Inventory.Options.DurabilityLossEnable = false;
        Assert.Equal(SpellCastResult.CastOk, rig.Cast());
        Assert.All(rig.Player.Inventory.AllItems, i => Assert.Equal(20u, i.Durability));
    }

    [Theory]
    [InlineData(SpellEffectName.DurabilityDamage)]
    [InlineData(SpellEffectName.DurabilityDamagePct)]
    public void NonPlayerTarget_IsIgnored(SpellEffectName effect)
    {
        using var rig = new DurabilityRig(effect, 100, -2);
        var creature = new CombatTestUnit();
        creature.Spawn(rig.Kit.World.GetMap(0), 0, 0);
        uint health = creature.Health;
        Assert.Equal(SpellCastResult.CastOk, rig.Kit.System.CastSpell(creature, DurabilityRig.SpellId, SpellCastTargets.ForSelf(), triggered: false));
        Assert.Equal(health, creature.Health);
        Assert.All(rig.Player.Inventory.AllItems, i => Assert.Equal(20u, i.Durability));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QuestSettlement_HeldCasterOrTargetBlocksMutationUntilRetried(bool holdTarget)
    {
        using var rig = new DurabilityRig(SpellEffectName.DurabilityDamage, 5, -2, otherTarget: true);
        (Player caster, _) = rig.Kit.AddPlayer(2);
        rig.Kit.Spellbook.Teach(caster, DurabilityRig.SpellId);
        Player held = holdTarget ? rig.Player : caster;
        Guid operation = Guid.NewGuid();
        Assert.True(held.BeginQuestSettlement(operation));
        Assert.Equal(holdTarget ? SpellCastResult.BadTargets : SpellCastResult.NotReady,
            rig.Kit.System.HandleCastRequest(caster, DurabilityRig.SpellId, SpellCastTargets.ForUnit(rig.Player.Guid)));
        Assert.Equal(20u, rig.Sword.Durability);
        Assert.True(held.EndQuestSettlement(operation));
        Assert.Equal(SpellCastResult.CastOk, rig.Kit.System.HandleCastRequest(caster, DurabilityRig.SpellId, SpellCastTargets.ForUnit(rig.Player.Guid)));
        Assert.Equal(15u, rig.Sword.Durability);
    }
}

public sealed class DurabilityPointStatTests
{
    [Fact]
    public void BreakingEquippedWeapon_RemovesItsCombatStatsBeforeDurabilityBecomesZero()
    {
        using var rig = new DurabilityRig(SpellEffectName.DurabilityDamage, 5, -1);
        Assert.Equal(5f, rig.Player.StatState.WeaponDamage(ArcaneCore.Game.Combat.WeaponAttackType.BaseAttack, 0).Min);
        rig.Player.Inventory.DurabilityPointsLoss(rig.Sword, 100);
        Assert.Equal(0u, rig.Sword.Durability);
        Assert.Equal(0f, rig.Player.StatState.WeaponDamage(ArcaneCore.Game.Combat.WeaponAttackType.BaseAttack, 0).Min);
    }

    [Fact]
    public void RepairingBrokenEquippedWeapon_ReappliesItsCombatStatsOnce()
    {
        using var rig = new DurabilityRig(SpellEffectName.DurabilityDamage, 5, -1, broken: true);
        rig.Player.Inventory.DurabilityPointsLoss(rig.Sword, -3);
        Assert.Equal(3u, rig.Sword.Durability);
        Assert.Equal(5f, rig.Player.StatState.WeaponDamage(ArcaneCore.Game.Combat.WeaponAttackType.BaseAttack, 0).Min);
        rig.Player.Inventory.DurabilityPointsLoss(rig.Sword, -100);
        Assert.Equal(20u, rig.Sword.Durability);
        Assert.Equal(5f, rig.Player.StatState.WeaponDamage(ArcaneCore.Game.Combat.WeaponAttackType.BaseAttack, 0).Min);
    }

    [Fact]
    public void AlreadyBrokenOrDisabled_DoesNotChangeCombatStats()
    {
        using var rig = new DurabilityRig(SpellEffectName.DurabilityDamage, 5, -1, broken: true);
        rig.Player.Inventory.DurabilityPointsLoss(rig.Sword, 100);
        rig.Player.Inventory.Options.DurabilityLossEnable = false;
        rig.Player.Inventory.DurabilityPointsLoss(rig.Sword, -100);
        Assert.Equal(0u, rig.Sword.Durability);
        Assert.Equal(1f, rig.Player.StatState.WeaponDamage(ArcaneCore.Game.Combat.WeaponAttackType.BaseAttack, 0).Min);
    }
}

internal sealed class DurabilityRig : IDisposable
{
    public const uint SpellId = 993111;
    private const uint SwordEntry = 993201;
    private const uint BagEntry = 993202;
    private const uint KeyEntry = 993203;
    private static readonly ItemTemplateStore Templates = new(
    [
        new ItemTemplate { Entry = SwordEntry, Name = "Durability sword", Class = 2, SubClass = 7, InventoryType = 21,
            MaxDurability = 20, Delay = 1800, Damages = [new ItemDamage(5, 7, 0)] },
        new ItemTemplate { Entry = BagEntry, Name = "Synthetic durable bag", Class = 1, InventoryType = 18, ContainerSlots = 4, MaxDurability = 20 },
        new ItemTemplate { Entry = KeyEntry, Name = "Synthetic durable key", Class = 13, BagFamily = 9, MaxDurability = 20 },
    ], []);

    public DurabilityRig(SpellEffectName effect, int value, int selector, bool broken = false, bool otherTarget = false)
    {
        Kit = new SpellTestKit(Spell(SpellId, Effect(effect, value, otherTarget ? SpellImplicitTarget.UnitFriend : SpellImplicitTarget.UnitCaster, misc: selector)) with
        {
            StartRecoveryCategory = 0, StartRecoveryTime = 0,
            RangeIndex = otherTarget ? 4u : SpellConstants.RangeIndexSelfOnly, Range = new SpellRange(0, otherTarget ? 30 : 0),
        });
        (Player, _) = Kit.AddPlayer(1);
        Player.Inventory.Templates = Templates;
        var stats = new PlayerStatSystem();
        stats.Attach(Player);
        Player.Inventory.Load([
            Row(1, SwordEntry, InventorySlots.MainHand, durability: broken ? 0u : 20u),
            Row(2, BagEntry, InventorySlots.BagStart),
            Row(3, SwordEntry, InventorySlots.ItemStart),
            Row(4, SwordEntry, 0, container: 2),
            Row(5, SwordEntry, InventorySlots.BankItemStart),
            Row(6, BagEntry, InventorySlots.BankBagStart),
            Row(7, SwordEntry, 0, container: 6),
            Row(8, KeyEntry, InventorySlots.KeyringStart),
        ]);
        Assert.Equal(8, Player.Inventory.AllItems.Count());
        Kit.Spellbook.Teach(Player, SpellId);
    }

    public SpellTestKit Kit { get; }
    public Player Player { get; }
    public Item Sword => Player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.MainHand)!;
    public Item Bag => Player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.BagStart)!;
    public Item Backpack => Player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.ItemStart)!;
    public Item BagContent => Player.Inventory.GetItem(InventorySlots.BagStart, 0)!;
    public Item Bank => Player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.BankItemStart)!;
    public Item BankBag => Player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.BankBagStart)!;
    public Item BankBagContent => Player.Inventory.GetItem(InventorySlots.BankBagStart, 0)!;
    public Item Key => Player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.KeyringStart)!;
    public SpellCastResult Cast() => Kit.System.HandleCastRequest(Player, SpellId, SpellCastTargets.ForSelf());
    public void Dispose() => Kit.Dispose();

    private static InventoryItemData Row(uint guid, uint entry, byte slot, uint container = 0, uint durability = 20)
        => new(container, slot, new ItemInstanceData { Guid = guid, Entry = entry, Count = 1, Durability = durability });
}
