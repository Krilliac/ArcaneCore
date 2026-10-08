using ArcaneCore.Game.Crafting;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using Xunit;

namespace ArcaneCore.Game.Tests.Crafting;

/// <summary>
/// Crafting lane, slice reagents-tools: vmangos Spell::CheckItems reagents and totems (Spell.cpp:7249-7306) and Spell::TakeReagents
/// (Spell.cpp:5082-5128), in the cast order TakePower, TakeReagents, TakeAmmo, effects (:3716-3718).
/// </summary>
public sealed class ReagentTests
{
    private const uint NeedsCloth = 91001;
    private const uint NeedsTool = 91002;
    private const uint NeedsMany = 91003;
    private const uint NoRequirements = 91004;
    private const uint SelfReagentItem = 90100;

    private static CraftingTestKit Rig(Func<Player, Item, bool>? isInTrade = null)
    {
        var rig = new CraftingTestKit(
            CraftingTestKit.Craft(NeedsCloth) with { Reagents = [new SpellReagent(CraftingTestKit.LinenCloth, 2)] },
            CraftingTestKit.Craft(NeedsTool) with { Totems = [CraftingTestKit.BlacksmithHammer], Reagents = [new SpellReagent(CraftingTestKit.CopperBar, 1)] },
            CraftingTestKit.Craft(NeedsMany) with
            {
                Reagents = [new SpellReagent(CraftingTestKit.LinenCloth, 3), new SpellReagent(CraftingTestKit.CopperBar, 2)],
            },
            CraftingTestKit.Craft(NoRequirements));
        ReagentRules.Install(rig.System, isInTrade);
        return rig;
    }

    [Fact]
    public void ReagentsOfferedInATrade_DoNotCount_AndAreNotDestroyed()
    {
        // vmangos Player::HasItemCount and DestroyItemCount skip IsInTrade items (Player.cpp:8681-8734).
        Item? offered = null;
        using CraftingTestKit rig = Rig((_, item) => item == offered);
        offered = rig.Give(CraftingTestKit.LinenCloth, 5);

        Assert.Equal(SpellCastResult.ItemNotReady, rig.Cast(NeedsCloth));

        Assert.Equal(5u, offered.Count);
        Assert.NotNull(rig.Inventory.GetItemByGuid(offered.Guid));
    }

    [Fact]
    public void WithAFreeStackBesideTheTradedOne_OnlyTheFreeStackIsConsumed()
    {
        Item? offered = null;
        using CraftingTestKit rig = Rig((_, item) => item == offered);
        offered = rig.Give(CraftingTestKit.LinenCloth, 20);   // a full stack: the next cloth lands in its own stack
        Item freeCloth = rig.Give(CraftingTestKit.LinenCloth, 2);
        Assert.NotEqual(offered.Guid, freeCloth.Guid);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(NeedsCloth));

        Assert.Equal(20u, offered.Count);
        Assert.Null(rig.Inventory.GetItemByGuid(freeCloth.Guid));
    }

    [Fact]
    public void MissingReagent_AnswersItemNotReady_AndConsumesNothing()
    {
        using CraftingTestKit rig = Rig();
        rig.Give(CraftingTestKit.LinenCloth, 1);

        Assert.Equal(SpellCastResult.ItemNotReady, rig.Cast(NeedsCloth));

        Assert.Equal(1u, rig.Inventory.GetItemCount(CraftingTestKit.LinenCloth));
    }

    [Fact]
    public void Reagents_AreConsumed_AndOtherItemsStay()
    {
        using CraftingTestKit rig = Rig();
        rig.Give(CraftingTestKit.LinenCloth, 2);
        rig.Give(CraftingTestKit.CopperBar, 5);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(NeedsCloth));

        Assert.Equal(0u, rig.Inventory.GetItemCount(CraftingTestKit.LinenCloth));
        Assert.Equal(5u, rig.Inventory.GetItemCount(CraftingTestKit.CopperBar));
    }

    [Fact]
    public void EveryReagentIsConsumed()
    {
        using CraftingTestKit rig = Rig();
        rig.Give(CraftingTestKit.LinenCloth, 5);
        rig.Give(CraftingTestKit.CopperBar, 2);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(NeedsMany));

        Assert.Equal(2u, rig.Inventory.GetItemCount(CraftingTestKit.LinenCloth));
        Assert.Equal(0u, rig.Inventory.GetItemCount(CraftingTestKit.CopperBar));
    }

    [Fact]
    public void OneReagentMissing_OfSeveral_ConsumesNothing()
    {
        using CraftingTestKit rig = Rig();
        rig.Give(CraftingTestKit.LinenCloth, 5);
        rig.Give(CraftingTestKit.CopperBar, 1);

        Assert.Equal(SpellCastResult.ItemNotReady, rig.Cast(NeedsMany));

        Assert.Equal(5u, rig.Inventory.GetItemCount(CraftingTestKit.LinenCloth));
        Assert.Equal(1u, rig.Inventory.GetItemCount(CraftingTestKit.CopperBar));
    }

    [Fact]
    public void MissingTool_AnswersItemGone_AndHavingItConsumesNothing()
    {
        using CraftingTestKit rig = Rig();
        rig.Give(CraftingTestKit.CopperBar, 1);

        Assert.Equal(SpellCastResult.ItemGone, rig.Cast(NeedsTool));
        Assert.Equal(1u, rig.Inventory.GetItemCount(CraftingTestKit.CopperBar));

        rig.Give(CraftingTestKit.BlacksmithHammer);
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(NeedsTool));
        Assert.Equal(1u, rig.Inventory.GetItemCount(CraftingTestKit.BlacksmithHammer));   // tools are not consumed
        Assert.Equal(0u, rig.Inventory.GetItemCount(CraftingTestKit.CopperBar));
    }

    [Fact]
    public void ReagentCheck_RunsBeforeTheToolCheck()
    {
        using CraftingTestKit rig = Rig();   // neither bar nor hammer: vmangos checks reagents (ITEM_NOT_READY) before totems (ITEM_GONE)

        Assert.Equal(SpellCastResult.ItemNotReady, rig.Cast(NeedsTool));
    }

    [Fact]
    public void ASpellWithoutRequirements_IsNotAffected()
    {
        using CraftingTestKit rig = Rig();

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(NoRequirements));
    }

    [Fact]
    public void TriggeredSpell_IgnoresItemRequirements_ProcessedByTheOriginalSpell()
    {
        // vmangos Spell::IgnoreItemRequirements (Spell.cpp:7069-7083): a triggered spell neither checks nor takes reagents.
        using CraftingTestKit rig = Rig();
        rig.Give(CraftingTestKit.LinenCloth, 5);

        Assert.Equal(SpellCastResult.CastOk, rig.System.CastSpell(rig.Player, NeedsMany, SpellCastTargets.ForSelf(), triggered: true));

        Assert.Equal(5u, rig.Inventory.GetItemCount(CraftingTestKit.LinenCloth));
    }

    [Fact]
    public void TriggeredChild_WithoutAMasterReagent_StillNeedsItsToolBeforePayingItsReagent()
    {
        // vmangos Spell::IgnoreItemRequirements (Spell.cpp:7069-7083) returns false when the
        // triggering spell has no first reagent. CheckItems then checks both child reagents and totems.
        using CraftingTestKit rig = Rig();
        rig.Give(CraftingTestKit.CopperBar);
        SpellInfo master = rig.Kit.Store.Get(NoRequirements)!;

        Assert.Equal(SpellCastResult.ItemGone,
            rig.System.CastSpell(rig.Player, NeedsTool, SpellCastTargets.ForSelf(), triggered: true, triggeringSpell: master));
        Assert.Equal(1u, rig.Inventory.GetItemCount(CraftingTestKit.CopperBar));

        rig.Give(CraftingTestKit.BlacksmithHammer);
        Assert.Equal(SpellCastResult.CastOk,
            rig.System.CastSpell(rig.Player, NeedsTool, SpellCastTargets.ForSelf(), triggered: true, triggeringSpell: master));
        Assert.Equal(0u, rig.Inventory.GetItemCount(CraftingTestKit.CopperBar));
    }

    [Fact]
    public void TriggeredChild_WithAMasterReagent_ReusesTheOriginalItemsAndTool()
    {
        using CraftingTestKit rig = Rig();
        rig.Give(CraftingTestKit.CopperBar);
        SpellInfo master = rig.Kit.Store.Get(NeedsCloth)!;

        Assert.Equal(SpellCastResult.CastOk,
            rig.System.CastSpell(rig.Player, NeedsTool, SpellCastTargets.ForSelf(), triggered: true, triggeringSpell: master));
        Assert.Equal(1u, rig.Inventory.GetItemCount(CraftingTestKit.CopperBar));
        Assert.Equal(0u, rig.Inventory.GetItemCount(CraftingTestKit.BlacksmithHammer));
    }

    [Fact]
    public void ReagentsAreDestroyed_BeforeTheEffect_SoACreatedItemCanUseTheFreedSlot()
    {
        using CraftingTestKit rig = Rig();
        rig.Give(CraftingTestKit.LinenCloth, 2);
        uint clothAtEffect = uint.MaxValue;
        rig.System.RegisterEffect(SpellEffectName.Dummy, ctx => clothAtEffect = ((Player)ctx.Caster).Inventory.GetItemCount(CraftingTestKit.LinenCloth));

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(NeedsCloth));

        Assert.Equal(0u, clothAtEffect);
    }

    [Fact]
    public void CastItemThatIsItsOwnReagent_WithNegativeChargesBelowTwo_RaisesTheCountByOne()
    {
        // vmangos Spell.cpp:7266-7280 / :5101-5113: the cast item is used up and does not count as a reagent, so a count above 1 grows by one.
        var template = new ItemTemplate
        {
            Entry = SelfReagentItem, Name = "Self Reagent", DisplayId = 9, Stackable = 20, Quality = 1,
            Spells = [new ItemSpell(91005, 0, -1, 0, 0, 0, 0)],
        }.Normalized();
        var item = new Item(1, template, ObjectGuid.Empty);
        Assert.Equal(-1, item.GetInt32(UpdateFields.ItemFieldSpellCharges));
        var two = new SpellReagent(SelfReagentItem, 2);

        Assert.Equal(3u, ReagentRules.EffectiveCount(item, two));

        item.SetInt32(UpdateFields.ItemFieldSpellCharges, -2);   // abs(charges) is not below 2: unchanged
        Assert.Equal(2u, ReagentRules.EffectiveCount(item, two));

        item.SetInt32(UpdateFields.ItemFieldSpellCharges, -1);
        Assert.Equal(2u, ReagentRules.EffectiveCount(null, two));
        Assert.Equal(1u, ReagentRules.EffectiveCount(item, new SpellReagent(SelfReagentItem, 1)));   // count 1 never grows
        Assert.Equal(2u, ReagentRules.EffectiveCount(item, new SpellReagent(555, 2)));               // another item: unchanged
    }

    [Fact]
    public void InstallingTwice_Throws()
    {
        using CraftingTestKit rig = Rig();

        Assert.Throws<InvalidOperationException>(() => ReagentRules.Install(rig.System));
    }
}
