using ArcaneCore.Game.Crafting;
using ArcaneCore.Game.Crafting.Enchanting;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Items;
using Xunit;

namespace ArcaneCore.Game.Tests.Crafting;

/// <summary>
/// Crafting lane, fail-closed enchanting: with no <c>SpellItemEnchantment.dbc</c> loaded the enchant effects have no handler, but the reagent pair is
/// installed, so without a guard a cast would destroy rods and dust and apply nothing (review finding). <see cref="EnchantItemSpells.InstallUnavailable"/>
/// refuses the cast before the reagents are taken. An ArcaneCore guard with no vmangos counterpart (vmangos always loads the DBC).
/// </summary>
public sealed class EnchantUnavailableTests
{
    private const uint EnchantSpell = 95501;
    private const uint Wrist = 95502;
    private const uint Rod = CraftingTestKit.CopperBar;

    private static CraftingTestKit Rig(bool guard)
    {
        var rig = new CraftingTestKit(
            [CraftingTestKit.Craft(EnchantSpell, SpellTestKit.Effect(SpellEffectName.EnchantItem, 1, SpellImplicitTarget.None, misc: 701)) with
            {
                Targets = 0x10, EquippedItemClass = 4, Reagents = [new SpellReagent(Rod, 1)],
            }],
            [new ItemTemplate { Entry = Wrist, Class = 4, SubClass = 1, Name = "Test Bracers", DisplayId = 1, InventoryType = 9, Quality = 2, ItemLevel = 30 }]);
        ReagentRules.Install(rig.System);
        if (guard)
        {
            EnchantItemSpells.InstallUnavailable(rig.System);
        }

        return rig;
    }

    private static SpellCastResult Cast(CraftingTestKit rig, Item item)
    {
        rig.Session.Clear();
        rig.Kit.Spellbook.Teach(rig.Player, EnchantSpell);
        return rig.Kit.System.HandleCastRequest(rig.Player, EnchantSpell, new SpellCastTargets { Mask = SpellCastTargetFlags.Item, Item = item.Guid });
    }

    [Fact]
    public void WithoutACatalog_AnEnchantCast_IsRefused_AndKeepsTheReagents()
    {
        using CraftingTestKit rig = Rig(guard: true);
        Item wrist = rig.Give(Wrist);
        rig.Give(Rod, 3);

        Assert.Equal(SpellCastResult.Unknown, Cast(rig, wrist));

        Assert.Equal(3u, rig.Inventory.GetItemCount(Rod));
    }

    [Fact]
    public void WithoutTheGuard_TheReagentsWouldBeLost_ProvingTheGuardIsLoadBearing()
    {
        using CraftingTestKit rig = Rig(guard: false);
        Item wrist = rig.Give(Wrist);
        rig.Give(Rod, 3);

        Cast(rig, wrist);

        Assert.Equal(2u, rig.Inventory.GetItemCount(Rod));   // the reagent was destroyed although nothing was enchanted
    }
}
