using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Updates;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

public sealed class TradeItemUseDispatchTests
{
    [Fact]
    public void TradeItemUse_InvokesDeferredHookAfterBindingWithoutOrdinaryDispatch()
    {
        const uint spellId = 99131;
        const uint entry = 99132;
        // Spell.dbc: an on-use spell aimed at a trade-window item carries TARGET_FLAG_ITEM (0x10), which allows TRADE_ITEM.
        using var kit = new SpellTestKit(SpellTestKit.Spell(spellId,
            SpellTestKit.Effect(SpellEffectName.Dummy, 0)) with { Targets = 0x10 });
        (Player player, FakeSession session) = kit.AddPlayer(1);
        player.Inventory.Templates = new ItemTemplateStore([
            new ItemTemplate
            {
                Entry = entry,
                Class = 0,
                Stackable = 1,
                Bonding = (uint)ItemBonding.WhenUse,
                Spells = [new ItemSpell(spellId, 0, 2, 0, 0, 0, 0)],
            }]);
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        player.Inventory.Load([]);
        Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(entry, 1, out Item? item));

        int calls = 0;
        kit.System.TradeItemEnchantmentRequest = (caster, castItem, index, targets) =>
        {
            calls++;
            Assert.Same(player, caster);
            Assert.Same(item, castItem);
            Assert.Equal((byte)0, index);
            Assert.True(targets.IsRawNonTradedTradeTarget);
            Assert.True(castItem.IsSoulBound);
            return SpellCastResult.DontReport;
        };

        SpellCastTargets targets = new() { Mask = SpellCastTargetFlags.TradeItem, Item = new ObjectGuid(6) };
        Assert.Equal(SpellCastResult.DontReport,
            kit.System.HandleItemUse(player, item!.BagSlot, item.Slot, 0, targets));
        Assert.Equal(1, calls);
        Assert.Equal(2, item.GetInt32(UpdateFields.ItemFieldSpellCharges));
        Assert.Empty(SpellTestKit.Packets(session, WorldOpcode.SmsgSpellStart));
        Assert.Empty(SpellTestKit.Packets(session, WorldOpcode.SmsgSpellGo));
    }

    [Fact]
    public void TradeItemPublication_UsesSourceGuidTargetGuidAndTemplateCooldowns()
    {
        const uint spellId = 99141;
        const uint sourceEntry = 99142;
        using var kit = new SpellTestKit(SpellTestKit.Spell(spellId,
            SpellTestKit.Effect(SpellEffectName.Dummy, 0)) with
        {
            RecoveryTime = 9_000,
            Category = 4,
            CategoryRecoveryTime = 8_000,
        });
        (Player caster, FakeSession session) = kit.AddPlayer(1);
        (Player recipient, _) = kit.AddPlayer(2, 2, 0);
        var sourceSpell = new ItemSpell(spellId, 0, 2, 0, 1_200, 77, 2_400);
        var store = new ItemTemplateStore([
            new ItemTemplate { Entry = sourceEntry, Class = 0, Stackable = 1, Spells = [sourceSpell] },
            ItemTestData.Templates.Single(t => t.Entry == ItemTestData.WornShortsword),
        ]);
        caster.Inventory.Templates = store;
        recipient.Inventory.Templates = store;
        caster.Inventory.GuidAllocator = new ItemGuidAllocator();
        recipient.Inventory.GuidAllocator = new ItemGuidAllocator();
        caster.Inventory.Load([]);
        recipient.Inventory.Load([]);
        Assert.Equal(InventoryResult.Ok, caster.Inventory.AddItem(sourceEntry, 1, out Item? source));
        Assert.Equal(InventoryResult.Ok, recipient.Inventory.AddItem(ItemTestData.WornShortsword, 1, out Item? target));

        SpellCastTargets expectedTargets = new() { Mask = SpellCastTargetFlags.TradeItem, Item = target!.Guid };
        var plan = new TradeEnchantmentPlan(target.ToData(), [], SpellSnapshot: kit.System.Store.Get(spellId),
            ItemCast: new TradeItemCastContext(source!.Guid, sourceEntry, 0, sourceSpell,
                source.BagSlot, source.Slot, false, ItemUsePaymentPlan.Create(source)));

        Assert.True(kit.System.PublishCommittedTradeEnchantment(Guid.NewGuid(), caster, target, plan));
        Assert.Equal(SpellPackets.BuildSpellGo(source.Guid, caster.Guid, spellId, SpellCastFlags.Unknown9,
            [], [], expectedTargets), Assert.Single(SpellTestKit.Packets(session, WorldOpcode.SmsgSpellGo)));
        Assert.Contains(kit.System.GetActiveCooldowns(caster), cooldown =>
            cooldown.SpellId == spellId && cooldown.ItemId == sourceEntry
            && cooldown.Category == 77 && cooldown.CooldownMs == 1_200 && cooldown.CategoryCooldownMs == 2_400);

        var waivedPlan = new TradeEnchantmentPlan(target.ToData(), [], SpellSnapshot: kit.System.Store.Get(spellId),
            ItemCast: new TradeItemCastContext(source.Guid, sourceEntry, 0, sourceSpell,
                source.BagSlot, source.Slot, true, null));
        Assert.True(kit.System.PublishCommittedTradeEnchantment(Guid.NewGuid(), caster, target, waivedPlan));
        Assert.Equal(SpellPackets.BuildSpellGo(caster.Guid, caster.Guid, spellId, SpellCastFlags.Unknown9,
            [], [], expectedTargets), Assert.Single(SpellTestKit.Packets(session, WorldOpcode.SmsgSpellGo).Skip(1)));
    }

    // vmangos TradeHandler.cpp:362, :399: a trade spell is triggered, so Spell::IsNeedSendToClient shows it only for a visual, a channel or a missile speed.
    [Theory]
    [InlineData(0u, 0f, false, false, false)]
    [InlineData(0u, 0f, false, true, false)]
    [InlineData(1u, 0f, false, false, true)]
    [InlineData(1u, 0f, false, true, true)]
    [InlineData(0u, 20f, false, false, true)]
    [InlineData(0u, 20f, false, true, true)]
    [InlineData(0u, 0f, true, false, true)]
    [InlineData(0u, 0f, true, true, true)]
    public void TradePublication_SendsSpellGoOnlyForAVisibleTriggeredSpell_AndAlwaysStartsTheCooldown(uint visual, float speed, bool channeled, bool itemCast, bool shown)
    {
        const uint spellId = 99161;
        const uint sourceEntry = 99162;
        using var kit = new SpellTestKit(SpellTestKit.Spell(spellId, SpellTestKit.Effect(SpellEffectName.Dummy, 0)) with
        {
            SpellVisual = visual,
            Speed = speed,
            AttributesEx = channeled ? SpellAttributesEx.IsChanneled : default,
            RecoveryTime = 9_000,
        });
        (Player caster, FakeSession session) = kit.AddPlayer(1);
        (Player recipient, _) = kit.AddPlayer(2, 2, 0);
        var sourceSpell = new ItemSpell(spellId, 0, 2, 0, 1_200, 77, 2_400);
        var store = new ItemTemplateStore([
            new ItemTemplate { Entry = sourceEntry, Class = 0, Stackable = 1, Spells = [sourceSpell] },
            ItemTestData.Templates.Single(t => t.Entry == ItemTestData.WornShortsword),
        ]);
        caster.Inventory.Templates = store;
        recipient.Inventory.Templates = store;
        caster.Inventory.GuidAllocator = new ItemGuidAllocator();
        recipient.Inventory.GuidAllocator = new ItemGuidAllocator();
        caster.Inventory.Load([]);
        recipient.Inventory.Load([]);
        Assert.Equal(InventoryResult.Ok, caster.Inventory.AddItem(sourceEntry, 1, out Item? source));
        Assert.Equal(InventoryResult.Ok, recipient.Inventory.AddItem(ItemTestData.WornShortsword, 1, out Item? target));
        var plan = new TradeEnchantmentPlan(target!.ToData(), [], SpellSnapshot: kit.System.Store.Get(spellId),
            ItemCast: itemCast ? new TradeItemCastContext(source!.Guid, sourceEntry, 0, sourceSpell, source.BagSlot, source.Slot, false, ItemUsePaymentPlan.Create(source)) : null);

        Assert.True(kit.System.PublishCommittedTradeEnchantment(Guid.NewGuid(), caster, target, plan));

        Assert.Equal(shown ? 1 : 0, SpellTestKit.Packets(session, WorldOpcode.SmsgSpellGo).Count);
        Assert.Contains(kit.System.GetActiveCooldowns(caster), cooldown => cooldown.SpellId == spellId);
    }

    [Fact]
    public void TradeItemUse_RefusesSecondPassiveTemplateSpell()
    {
        const uint useSpellId = 99151;
        const uint passiveSpellId = 99152;
        const uint enchantId = 99153;
        const uint sourceEntry = 99154;
        using var kit = new SpellTestKit(
            SpellTestKit.Spell(useSpellId, SpellTestKit.Effect(SpellEffectName.EnchantItem, 0, misc: (int)enchantId)),
            SpellTestKit.Spell(passiveSpellId, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
            { Attributes = SpellAttributes.Passive });
        (Player caster, _) = kit.AddPlayer(1);
        (Player recipient, _) = kit.AddPlayer(2, 2, 0);
        var sourceSpell = new ItemSpell(useSpellId, 0, 1, 0, 0, 0, 0);
        var store = new ItemTemplateStore([
            new ItemTemplate { Entry = sourceEntry, Class = 0, Stackable = 1,
                Spells = [sourceSpell, new ItemSpell(passiveSpellId, 0, 0, 0, 0, 0, 0)] },
            ItemTestData.Templates.Single(t => t.Entry == ItemTestData.WornShortsword),
        ]);
        caster.Inventory.Templates = store;
        recipient.Inventory.Templates = store;
        caster.Inventory.GuidAllocator = new ItemGuidAllocator();
        caster.Inventory.Load([]);
        recipient.Inventory.GuidAllocator = new ItemGuidAllocator();
        recipient.Inventory.Load([]);
        Assert.Equal(InventoryResult.Ok, caster.Inventory.AddItem(sourceEntry, 1, out Item? source));
        Assert.Equal(InventoryResult.Ok, recipient.Inventory.AddItem(ItemTestData.WornShortsword, 1, out Item? target));
        kit.System.ItemEnchantments = new ArcaneCore.Kernel.WorldData.Items.ItemEnchantmentCatalog([
            new ArcaneCore.Kernel.WorldData.Items.ItemEnchantmentDefinition(enchantId, [])]);

        Assert.Equal(SpellCastResult.ItemNotReady,
            kit.System.TryPlanTradeItemEnchantment(caster, recipient, target!, source!, 0, out TradeEnchantmentPlan? plan));
        Assert.Null(plan);
    }
}
