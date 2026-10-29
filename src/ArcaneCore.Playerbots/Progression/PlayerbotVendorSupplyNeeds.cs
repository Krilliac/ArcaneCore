using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Bots.Progression;

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

    /// <summary>Per reagent item of a wanted recipe, the largest count one craft uses (as of the last <see cref="Refresh"/>).</summary>
    internal IReadOnlyDictionary<uint, uint> Reagents => _reagents;

    private bool _hasBook;

    /// <summary>The time-to-live backstop of a read: a change no stamp covers (a skill rank, an honor rank) is seen within it.</summary>
    internal const uint CacheTtlMs = 10_000;

    /// <summary>How many times the spellbook was read (<see cref="Refresh"/>): the cache's test seam.</summary>
    internal int Refreshes { get; private set; }

    /// <summary>
    /// <see cref="Refresh"/> when what the answer depends on may have changed since the last read; otherwise keep the last read.
    /// </summary>
    /// <remarks>
    /// The stamps: the spell store and spellbook instances; the bot's known-spell snapshot (<see cref="SpellbookCache.GetSpells"/> hands out
    /// the same instance until the book changes, so a learned or forgotten spell is a new reference); its level; and a hash of every stored
    /// item (position, guid, entry, stack count, permanent enchantment), which covers a buy, a sale, a craft, a loot, an equip, an enchant
    /// and a bank move; a worn item breaking or being repaired is stamped too. Money is not stamped: <see cref="Refresh"/> and what it calls never read it. <see cref="CacheTtlMs"/> is the
    /// backstop for what no stamp covers (a skill rank that changes whether an item can be used).
    /// </remarks>
    internal bool RefreshIfChanged(Player player, SpellStore? store, SpellbookCache? spellbook, uint nowMs)
    {
        IReadOnlyList<uint>? spells = store is not null && spellbook is not null ? spellbook.GetSpells(player) : null;
        ulong inventory = InventoryStamp(player);
        if (_stamped && ReferenceEquals(_stampStore, store) && ReferenceEquals(_stampBook, spellbook) && ReferenceEquals(_stampSpells, spells)
            && _stampLevel == player.Level && _stampInventory == inventory
            && unchecked(nowMs - _stampAt) < CacheTtlMs)
            return false;

        Refresh(player, store, spellbook);
        _stamped = true;
        _stampStore = store;
        _stampBook = spellbook;
        _stampSpells = spells;
        _stampLevel = player.Level;
        _stampInventory = inventory;
        _stampAt = nowMs;
        return true;
    }

    private bool _stamped;
    private SpellStore? _stampStore;
    private SpellbookCache? _stampBook;
    private IReadOnlyList<uint>? _stampSpells;
    private byte _stampLevel;
    private uint _stampAt;
    private ulong _stampInventory;

    /// <summary>
    /// A hash of the bot's stored items (<see cref="PlayerInventory.AllItems"/>, which walks the equipment slots first: position, guid,
    /// entry, stack count and permanent enchantment of each, and whether a worn item is broken). The position is mixed in because a move between two slots (a bank
    /// deposit) can leave the walk order unchanged while changing what counts. One pass over the bags, no allocation: far cheaper than the
    /// spellbook read it guards (every known recipe weighed against the inventory).
    /// </summary>
    internal static ulong InventoryStamp(Player player)
    {
        const ulong Prime = 1099511628211UL;
        ulong hash = 14695981039346656037UL;
        foreach (Item item in player.Inventory.AllItems)
        {
            hash = (hash ^ (((ulong)item.BagSlot << 8) | item.Slot)) * Prime;
            hash = (hash ^ item.Guid.Value) * Prime;
            hash = (hash ^ item.Entry) * Prime;
            hash = (hash ^ item.Count) * Prime;
            hash = (hash ^ item.GetUInt32(UpdateFields.ItemFieldEnchantment)) * Prime;
            // A broken worn item scores 0 (PlayerbotItemScore.Score), which makes a craftable replacement an upgrade: a repair must be seen.
            if (item.BagSlot == InventorySlots.Bag0 && item.Slot < InventorySlots.EquipmentEnd)
                hash = (hash ^ (item.MaxDurability > 0 && item.Durability == 0 ? 1UL : 0UL)) * Prime;
        }

        return hash;
    }

    /// <summary>Read the bot's spellbook as of now (world thread). Without a store or a spellbook only the common tools count.</summary>
    internal void Refresh(Player player, SpellStore? store, SpellbookCache? spellbook)
    {
        Refreshes++;
        _tools.Clear();
        _reagents.Clear();
        _hasBook = store is not null && spellbook is not null;
        if (store is null || spellbook is null)
            return;

        PlayerbotStatWeights weights = PlayerbotTalentBuilds.Choose(player.Class, player.Guid.Low).Weights;
        IReadOnlyList<uint> spells = spellbook.GetSpells(player);
        bool knowsCampfire = false, wantsFireRecipe = false;
        for (int s = 0; s < spells.Count; s++)
        {
            if (store.Get(spells[s]) is not { } spell)
                continue;
            if (spell.Totems.Count > 0) _tools.Add(spell.Totems[0]);
            if (spell.Totems.Count > 1) _tools.Add(spell.Totems[1]);
            if (spells[s] == PlayerbotCrafting.BasicCampfireSpell) knowsCampfire = true;
            if (!PlayerbotCrafting.WantsToCraftSpell(player, spell, weights, store, spellbook)
                && !PlayerbotCrafting.WantsToEnchantSpell(player, spell))
                continue;
            if (spell.RequiresSpellFocus == PlayerbotCrafting.CookingFireFocus) wantsFireRecipe = true;
            for (int r = 0; r < spell.Reagents.Count; r++)
            {
                SpellReagent reagent = spell.Reagents[r];
                if (reagent.ItemId == 0 || reagent.Count == 0)
                    continue;
                if (!_reagents.TryGetValue(reagent.ItemId, out uint most) || reagent.Count > most)
                    _reagents[reagent.ItemId] = reagent.Count;
            }
        }

        // The campfire's wood for a cook who would need a fire (PlayerbotCrafting.CampfireReagentShort).
        if (knowsCampfire && wantsFireRecipe && store.Get(PlayerbotCrafting.BasicCampfireSpell) is { } campfire)
            for (int r = 0; r < campfire.Reagents.Count; r++)
            {
                SpellReagent reagent = campfire.Reagents[r];
                if (reagent.ItemId == 0 || reagent.Count == 0)
                    continue;
                if (!_reagents.TryGetValue(reagent.ItemId, out uint most) || reagent.Count > most)
                    _reagents[reagent.ItemId] = reagent.Count;
            }
    }

    /// <summary>
    /// The reagents of the recipes the bot wants (as of the last <see cref="Refresh"/>) it carries fewer of than one craft uses, with how
    /// many it lacks (its inventory as of now), at most <paramref name="max"/>; the bot voice's WTB lines (Chat/PlayerbotNeedsVoice).
    /// </summary>
    internal List<(uint ItemId, uint Missing)> Shortfalls(Player player, int max)
    {
        var missing = new List<(uint, uint)>();
        foreach ((uint itemId, uint most) in _reagents)
        {
            if (missing.Count >= max) break;
            uint have = player.Inventory.GetItemCount(itemId);
            if (have < most) missing.Add((itemId, most - have));
        }

        return missing;
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
