using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

public sealed class SpellReagentTests
{
    private const uint SpellId = 994900;
    internal const uint Ankh = 17030;

    [Fact]
    public void MissingReagentRejectsBeforePowerCooldownOrHealing()
    {
        using var kit = new SpellTestKit(CostSpell() with { ManaCost = 10, RecoveryTime = 60_000 });
        Player player = AddPlayer(kit, 0);
        player.Health = 10;
        player.SetUInt32(UpdateFields.UnitFieldPower1, 100);

        Assert.Equal(SpellCastResult.ItemNotReady, Cast(kit, player));

        Assert.Equal(10u, player.Health);
        Assert.Equal(100u, player.GetUInt32(UpdateFields.UnitFieldPower1));
        Assert.Empty(kit.System.GetActiveCooldowns(player));
    }

    [Fact]
    public void SuccessfulCastConsumesOneReagentBeforeItsEffect()
    {
        using var kit = new SpellTestKit(CostSpell());
        Player player = AddPlayer(kit, 2);
        player.Health = 10;
        uint countAtEffect = uint.MaxValue;
        kit.System.RegisterEffect(SpellEffectName.Heal, c => countAtEffect = player.Inventory.GetItemCount(Ankh));

        Assert.Equal(SpellCastResult.CastOk, Cast(kit, player));

        Assert.Equal(1u, countAtEffect);
        Assert.Equal(1u, player.Inventory.GetItemCount(Ankh));
        Assert.Equal(1u, Assert.Single(player.Inventory.CreateSnapshot().Items).Item.Count);
    }

    [Fact]
    public void BankItemsCannotPayAReagentCost()
    {
        using var kit = new SpellTestKit(CostSpell());
        Player player = AddPlayer(kit, 1, bank: true);
        Assert.Equal(SpellCastResult.ItemNotReady, Cast(kit, player));
        Assert.Equal(1u, player.Inventory.GetItemCount(Ankh, inBankAlso: true));
    }

    [Fact]
    public void ReagentRemovedDuringCastFailsAtCompletionWithoutHealing()
    {
        using var kit = new SpellTestKit(CostSpell() with { CastTime = new SpellCastTime(1000, 0, 1000) });
        Player player = AddPlayer(kit, 1);
        player.Health = 10;
        Assert.Equal(SpellCastResult.CastOk, Cast(kit, player));
        Assert.Equal(1u, player.Inventory.GetItemCount(Ankh));
        Assert.Equal(1u, player.Inventory.DestroyItemCount(Ankh, 1));
        kit.Advance(1000);
        Assert.Equal(10u, player.Health);
        Assert.Empty(kit.System.GetActiveCooldowns(player));
    }

    [Fact]
    public void CancellationDoesNotConsumeReagents()
    {
        using var kit = new SpellTestKit(CostSpell() with { CastTime = new SpellCastTime(1000, 0, 1000) });
        Player player = AddPlayer(kit, 1);
        Assert.Equal(SpellCastResult.CastOk, Cast(kit, player));
        kit.System.CancelCast(player, SpellId);
        kit.Advance(1000);
        Assert.Equal(1u, player.Inventory.GetItemCount(Ankh));
    }

    [Fact]
    public void StandaloneTriggeredCastDoesNotConsumeReagents()
    {
        using var kit = new SpellTestKit(CostSpell());
        Player player = AddPlayer(kit, 0);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, SpellId, SpellCastTargets.ForSelf(), triggered: true));
    }

    [Fact]
    public void DuplicateSlotsCannotConsumeAPartialCost()
    {
        using var kit = new SpellTestKit(CostSpell() with { Reagents = [new((int)Ankh, 1), new((int)Ankh, 1)] });
        Player player = AddPlayer(kit, 1);
        Assert.Equal(SpellCastResult.ItemNotReady, Cast(kit, player));
        Assert.Equal(1u, player.Inventory.GetItemCount(Ankh));
    }

    [Fact]
    public void SettlementHoldDefersBothCostAndEffect()
    {
        using var kit = new SpellTestKit(CostSpell());
        Player player = AddPlayer(kit, 1);
        Guid hold = Guid.NewGuid();
        Assert.True(player.BeginQuestSettlement(hold));
        Assert.Equal(SpellCastResult.NotReady, Cast(kit, player));
        Assert.Equal(1u, player.Inventory.GetItemCount(Ankh));
        player.EndQuestSettlement(hold);
    }

    [Fact]
    public void ReagentDefinitionCopiesInputAndEnforcesEightSlots()
    {
        var input = new[] { new SpellReagent((int)Ankh, 1) };
        SpellInfo spell = CostSpell() with { Reagents = input };
        input[0] = new(999, 99);
        Assert.Equal(new SpellReagent((int)Ankh, 1), spell.Reagents[0]);
        Assert.Equal(8, spell.Reagents.Count);
        Assert.Throws<ArgumentException>(() => CostSpell() with { Reagents = new SpellReagent[9] });
    }

    [Theory]
    [InlineData(false, 0u)]
    [InlineData(true, 1u)]
    public void TriggeredChildPaysOnlyWhenItsMasterDidNotCarryReagentSlotZero(bool masterPays, uint remaining)
    {
        using var kit = new SpellTestKit(CostSpell());
        Player player = AddPlayer(kit, 1);
        SpellInfo master = SpellTestKit.Spell(994901) with { Reagents = masterPays ? [new((int)Ankh, 1)] : [] };
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, SpellId, SpellCastTargets.ForSelf(),
            triggered: true, triggeringSpell: master));
        Assert.Equal(remaining, player.Inventory.GetItemCount(Ankh));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TriggerSpellEffectForwardsTheOriginalReagentPolicy(bool masterPays)
    {
        SpellInfo master = SpellTestKit.Spell(994901,
            SpellTestKit.Effect(SpellEffectName.TriggerSpell, 0, trigger: SpellId)) with
        {
            Reagents = masterPays ? [new((int)Ankh, 1)] : [], StartRecoveryCategory = 0, StartRecoveryTime = 0,
        };
        using var kit = new SpellTestKit(CostSpell(), master);
        Player player = AddPlayer(kit, 1);
        player.Health = 10;
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, master.Id, SpellCastTargets.ForSelf(), triggered: false));
        Assert.Equal(0u, player.Inventory.GetItemCount(Ankh));
        Assert.Equal(60u, player.Health);
    }

    [Fact]
    public void TriggeredForeignTradeItemStillRequiresReagents()
    {
        using var kit = new SpellTestKit(CostSpell());
        Player player = AddPlayer(kit, 0);
        Assert.Equal(SpellCastResult.ItemNotReady, kit.System.CastSpell(player, SpellId,
            new SpellCastTargets { Mask = SpellCastTargetFlags.TradeItem }, triggered: true));
    }

    [Fact]
    public void NegativeAndZeroReagentSlotsAreIgnored()
    {
        using var kit = new SpellTestKit(CostSpell() with { Reagents = [new(-1, 10), new(0, 99), new((int)Ankh, 0)] });
        Player player = AddPlayer(kit, 0);
        Assert.Equal(SpellCastResult.CastOk, Cast(kit, player));
    }

    [Fact]
    public void OverflowingCombinedCountFailsWithoutConsumingAnything()
    {
        using var kit = new SpellTestKit(CostSpell() with { Reagents = [new((int)Ankh, uint.MaxValue), new((int)Ankh, 1)] });
        Player player = AddPlayer(kit, 1);
        Assert.Equal(SpellCastResult.ItemNotReady, Cast(kit, player));
        Assert.Equal(1u, player.Inventory.GetItemCount(Ankh));
    }

    [Fact]
    public void DistinctMissingReagentLeavesTheOtherStackUnchanged()
    {
        using var kit = new SpellTestKit(CostSpell() with { Reagents = [new((int)Ankh, 1), new(999, 1)] });
        Player player = AddPlayer(kit, 1);
        Assert.Equal(SpellCastResult.ItemNotReady, Cast(kit, player));
        Assert.Equal(1u, player.Inventory.GetItemCount(Ankh));
    }

    [Fact]
    public void DuplicateReagentCostIsAppliedBeforeObserversSeeIt()
    {
        using var kit = new SpellTestKit(CostSpell() with { Reagents = [new((int)Ankh, 1), new((int)Ankh, 1)] });
        Player player = AddPlayer(kit, 3);
        uint seenCount = uint.MaxValue;
        int notifications = 0;
        player.Inventory.ItemCountChanged += (entry, delta) =>
        {
            Assert.Equal(Ankh, entry);
            Assert.Equal(-2, delta);
            seenCount = player.Inventory.GetItemCount(Ankh);
            notifications++;
        };
        Assert.Equal(SpellCastResult.CastOk, Cast(kit, player));
        Assert.Equal(1u, seenCount);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void PeriodicTriggerUsesItsAuraOriginAndCannotHealWithoutTheNextReagent()
    {
        SpellInfo aura = SpellTestKit.Spell(994902,
            SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.PeriodicTriggerSpell, amplitude: 1000, trigger: SpellId)) with
        {
            Duration = new SpellDuration(3000, 0, 3000), StartRecoveryCategory = 0, StartRecoveryTime = 0,
        };
        using var kit = new SpellTestKit(CostSpell(), aura);
        Player player = AddPlayer(kit, 1);
        player.Health = 10;
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, aura.Id, SpellCastTargets.ForSelf(), triggered: false));
        kit.Advance(1000);
        Assert.Equal(60u, player.Health);
        Assert.Equal(0u, player.Inventory.GetItemCount(Ankh));
        kit.Advance(1000);
        Assert.Equal(60u, player.Health);
    }

    [Fact]
    public void EveryDistinctReagentIsConsumedBeforeAnyCountObserverRuns()
    {
        using var kit = new SpellTestKit(CostSpell() with { Reagents = [new((int)Ankh, 1), new(17031, 1)] });
        Player player = AddPlayer(kit, 1);
        player.Inventory.Templates = new ItemTemplateStore([
            new ItemTemplate { Entry = Ankh, Name = "Synthetic Ankh", Class = 5, Stackable = 20 },
            new ItemTemplate { Entry = 17031, Name = "Synthetic second reagent", Class = 5, Stackable = 20 },
        ]);
        player.Inventory.Load([
            new InventoryItemData(0, InventorySlots.ItemStart, new ItemInstanceData { Guid = 100, Entry = Ankh, Count = 1 }),
            new InventoryItemData(0, InventorySlots.ItemStart + 1, new ItemInstanceData { Guid = 101, Entry = 17031, Count = 1 }),
        ]);
        int notifications = 0;
        player.Inventory.ItemCountChanged += (_, _) =>
        {
            Assert.Equal(0u, player.Inventory.GetItemCount(Ankh));
            Assert.Equal(0u, player.Inventory.GetItemCount(17031));
            notifications++;
        };
        Assert.Equal(SpellCastResult.CastOk, Cast(kit, player));
        Assert.Equal(2, notifications);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LiveEquipmentRemovalRestrictionsCannotBeBypassedByDetachedStaging(bool partialStack)
    {
        using var kit = new SpellTestKit(CostSpell());
        Player player = AddPlayer(kit, 0);
        player.Inventory.Templates = new ItemTemplateStore([new ItemTemplate
        {
            Entry = Ankh, Name = "Synthetic equipped reagent", Class = 4, InventoryType = 5, Stackable = 20,
        }]);
        player.Inventory.Load([new InventoryItemData(0, InventorySlots.Chest,
            new ItemInstanceData { Guid = 100, Entry = Ankh, Count = partialStack ? 2u : 1u })]);
        player.UnitFlags |= UnitFlags.InCombat;
        Assert.Equal(InventoryResult.NotInCombat, player.Inventory.CanUnequipItem(InventorySlots.Bag0, InventorySlots.Chest, swap: false));
        player.Health = 10;
        Assert.Equal(SpellCastResult.ItemNotReady, Cast(kit, player));
        Assert.Equal(partialStack ? 2u : 1u, player.Inventory.GetItemCount(Ankh));
        Assert.Equal(10u, player.Health);
    }

    internal static Player AddPlayer(SpellTestKit kit, uint ankhs, bool bank = false)
    {
        (Player player, _) = kit.AddPlayer(1);
        player.Inventory.Templates = new ItemTemplateStore([new ItemTemplate { Entry = Ankh, Name = "Synthetic Ankh", Class = 5, Stackable = 20 }]);
        player.Inventory.GuidAllocator = new ItemGuidAllocator(100);
        player.Inventory.Load(ankhs == 0 ? [] : [new InventoryItemData(0, bank ? InventorySlots.BankItemStart : InventorySlots.ItemStart,
            new ItemInstanceData { Guid = 100, Entry = Ankh, Count = ankhs })]);
        kit.Spellbook.Teach(player, SpellId);
        return player;
    }

    internal static SpellInfo CostSpell() => SpellTestKit.Spell(SpellId, SpellTestKit.Effect(SpellEffectName.Heal, 50)) with
    {
        Reagents = [new((int)Ankh, 1)], StartRecoveryCategory = 0, StartRecoveryTime = 0,
    };

    private static SpellCastResult Cast(SpellTestKit kit, Player player)
        => kit.System.HandleCastRequest(player, SpellId, SpellCastTargets.ForSelf());
}
