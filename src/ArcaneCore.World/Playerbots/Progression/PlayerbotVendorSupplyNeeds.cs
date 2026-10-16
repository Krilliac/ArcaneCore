using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Spells;

namespace ArcaneCore.World.Playerbots.Progression;

/// <summary>
/// <see cref="PlayerbotCrafting.NeedsVendorSupply"/> for many items at once: the bot's spellbook is read once (<see cref="Refresh"/>)
/// into the tools its known spells name and, per reagent, the largest count a recipe the bot wants asks for; each item is then one
/// inventory count and two lookups. The answer is the same as <see cref="PlayerbotCrafting.NeedsVendorSupply"/>'s as long as the bot does
/// not change in between (its spells, its inventory, its skills and gear: what a recipe being wanted depends on).
/// <para>
/// Why: the town controller asked <see cref="PlayerbotCrafting.NeedsVendorSupply"/> of every row of every visible vendor, and each call
/// walked every known spell and weighed every recipe (an inventory count of the made item, its upgrade score). Live 2026-10-16 in
/// Ironforge and Kharanos a think spent 28 ms in that scan, twice (the goal policy's snapshot and the town rung): "phases policy
/// snapshot 28.4, town 28.2".
/// </para>
/// </summary>
internal sealed class PlayerbotVendorSupplyNeeds
{
    /// <summary>The tools (<see cref="SpellInfo.Totems"/> slots 0 and 1) of every known spell, wanted or not.</summary>
    private readonly HashSet<uint> _tools = [];

    /// <summary>Per reagent item of a wanted recipe (crafting or enchanting): the largest count one such recipe uses.</summary>
    private readonly Dictionary<uint, uint> _reagents = [];

    private bool _hasBook;

    /// <summary>Read the bot's spellbook as of now (world thread). Without a store or a spellbook only the common tools count.</summary>
    internal void Refresh(Player player, SpellStore? store, SpellbookCache? spellbook)
    {
        _tools.Clear();
        _reagents.Clear();
        _hasBook = store is not null && spellbook is not null;
        if (store is null || spellbook is null)
            return;

        PlayerbotStatWeights weights = PlayerbotTalentBuilds.Choose(player.Class, player.Guid.Low).Weights;
        IReadOnlyList<uint> spells = spellbook.GetSpells(player);
        for (int s = 0; s < spells.Count; s++)
        {
            if (store.Get(spells[s]) is not { } spell)
                continue;
            if (spell.Totems.Count > 0) _tools.Add(spell.Totems[0]);
            if (spell.Totems.Count > 1) _tools.Add(spell.Totems[1]);
            if (!PlayerbotCrafting.WantsToCraftSpell(player, spell, weights, store, spellbook)
                && !PlayerbotCrafting.WantsToEnchantSpell(player, spell))
                continue;
            for (int r = 0; r < spell.Reagents.Count; r++)
            {
                SpellReagent reagent = spell.Reagents[r];
                if (reagent.ItemId == 0 || reagent.Count == 0)
                    continue;
                if (!_reagents.TryGetValue(reagent.ItemId, out uint most) || reagent.Count > most)
                    _reagents[reagent.ItemId] = reagent.Count;
            }
        }
    }

    /// <summary>Whether the bot needs <paramref name="itemId"/> from a vendor, as of the last <see cref="Refresh"/> (its inventory as of now).</summary>
    internal bool Needs(Player player, uint itemId)
    {
        if (itemId == 0)
            return false;
        uint have = player.Inventory.GetItemCount(itemId);
        if (PlayerbotCrafting.CommonToolNeed(player, itemId, have) is { } tool)
            return tool;
        if (!_hasBook)
            return false;
        // A known spell's tool, while none is carried; a wanted recipe's reagent, while fewer are carried than one craft uses.
        return (have == 0 && _tools.Contains(itemId))
            || (_reagents.TryGetValue(itemId, out uint most) && have < most);
    }
}
