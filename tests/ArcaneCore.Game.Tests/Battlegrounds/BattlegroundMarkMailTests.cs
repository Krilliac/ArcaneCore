using ArcaneCore.Game.Crafting;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Crafting;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Items;
using Xunit;

namespace ArcaneCore.Game.Tests.Battlegrounds;

/// <summary>
/// The CREATE_ITEM effect of a battleground mark spell (24950-24955) after vmangos Spell::DoCreateItem (SpellEffects.cpp:1972-1977): what the
/// bags cannot take is mailed (BattleGround::SendRewardMarkByMail) instead of being lost.
/// </summary>
public sealed class BattlegroundMarkMailTests
{
    private const uint WsWinMark = 24951;
    private const uint Mark = CraftingTestKit.LinenBandage;

    private static CraftingTestKit Rig(uint spellId, List<(uint Spell, uint Item, uint Count)> mailed)
    {
        SpellInfo spell = CraftingTestKit.Craft(spellId, SpellTestKit.Effect(SpellEffectName.CreateItem, 3) with { ItemType = Mark });
        var rig = new CraftingTestKit(spell);
        ReagentRules.Install(rig.System);
        CreateItemSpells.Install(rig.System);
        CreateItemSpells.SetBattlegroundMarkMailer(rig.System, (_, spellId2, item, count) => mailed.Add((spellId2, item, count)));
        return rig;
    }

    private static void FillBags(CraftingTestKit rig)
    {
        while (rig.Inventory.AddItem(CraftingTestKit.Filler, 1, out _) == InventoryResult.Ok)
        {
        }
    }

    private static void Reward(CraftingTestKit rig, uint spellId)
    {
        rig.Kit.System.CastSpell(rig.Player, spellId, SpellCastTargets.ForSelf(), triggered: true);
        if (rig.System.SpellBatchWindowMs != 0)
        {
            rig.Kit.Advance(rig.System.SpellBatchWindowMs);
        }
    }

    [Fact]
    public void MarkWithFullBags_IsMailedInsteadOfLost()
    {
        var mailed = new List<(uint Spell, uint Item, uint Count)>();
        using CraftingTestKit rig = Rig(WsWinMark, mailed);
        FillBags(rig);

        Reward(rig, WsWinMark);

        Assert.Equal(0u, rig.Inventory.GetItemCount(Mark));
        Assert.Equal((WsWinMark, Mark, 3u), Assert.Single(mailed));
    }

    [Fact]
    public void MarkWithRoom_IsStoredAndNothingIsMailed()
    {
        var mailed = new List<(uint Spell, uint Item, uint Count)>();
        using CraftingTestKit rig = Rig(WsWinMark, mailed);

        Reward(rig, WsWinMark);

        Assert.Equal(3u, rig.Inventory.GetItemCount(Mark));
        Assert.Empty(mailed);
    }

    [Fact]
    public void ANonMarkCreateItemSpellWithFullBags_IsNotMailed()
    {
        var mailed = new List<(uint Spell, uint Item, uint Count)>();
        using CraftingTestKit rig = Rig(2657, mailed);
        FillBags(rig);

        Reward(rig, 2657);

        Assert.Empty(mailed);
        Assert.Equal(0u, rig.Inventory.GetItemCount(Mark));
    }
}
