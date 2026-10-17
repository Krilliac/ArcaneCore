using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Items.ItemUse;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Skills;

namespace ArcaneCore.World.Playerbots;

/// <summary>How a bot can mount: by casting a spell it knows, or by using an item it carries.</summary>
internal enum PlayerbotMountKind : byte { None, Spell, Item }

/// <summary>The mount a bot would use: the spell to cast, or the bag and slot of the item to use.</summary>
internal readonly record struct PlayerbotMountChoice(PlayerbotMountKind Kind, uint SpellId, byte Bag, byte Slot, uint ItemEntry);

/// <summary>
/// What is a mount, in the world data: a mount spell applies the mounted aura (SPELL_AURA_MOUNTED, 78); a mount item names such a spell
/// as an on-use spell, or requires a riding skill. No price, level or entry is written here: those come from the trainer and vendor rows.
/// </summary>
internal static class PlayerbotMountRules
{
    private static readonly uint[] RidingSkills =
    [
        SkillIds.RidingHorse, SkillIds.RidingWolf, SkillIds.RidingTiger, SkillIds.RidingRam, SkillIds.RidingRaptor,
        SkillIds.RidingMechanostrider, SkillIds.RidingUndeadHorse, SkillIds.RidingKodo, SkillIds.Riding,
    ];

    internal static bool IsRidingSkill(uint skill) => Array.IndexOf(RidingSkills, skill) >= 0;

    /// <summary>The highest riding skill value the player has, whichever riding skill it is (0 when it has none).</summary>
    internal static uint RidingSkillValue(Player player, ISpellLearner spells)
    {
        uint best = 0;
        foreach (uint skill in RidingSkills)
            best = Math.Max(best, spells.GetSkillValueBase(player, skill));
        return best;
    }

    /// <summary>
    /// A mount item belongs to a riding tier: a riding-skill item asks a rank above <paramref name="floor"/> (the previous tier's rank) and
    /// up to <paramref name="cap"/>; an item that asks no riding skill belongs to the first tier only (<paramref name="floor"/> 0).
    /// </summary>
    internal static bool FitsTier(ItemTemplate template, uint floor, uint cap)
        => IsRidingSkill(template.RequiredSkill) ? template.RequiredSkillRank > floor && template.RequiredSkillRank <= cap : floor == 0;

    /// <summary>
    /// The player has a mount of the tier above <paramref name="floor"/>: it knows a mount spell, or carries a usable mount item that
    /// <see cref="FitsTier"/> accepts for the tier of the riding skill it has learned (an old tier's mount does not count for the next).
    /// </summary>
    internal static bool HasMountOfTier(Player player, SpellStore store, ISpellLearner learner, uint floor)
    {
        foreach (uint id in MountSpells(store))
        {
            if (learner.HasSpell(player, id)) return true;
        }

        int scanned = 0;
        foreach (Item item in player.Inventory.AllItems)
        {
            if (++scanned > 128) break;
            if (!InventorySlots.IsInventoryPos(item.BagSlot, item.Slot) || item.Count == 0 || item.Template.Spells.Count == 0
                || !IsMountItem(item.Template, store) || player.Inventory.CanUseItem(item) != ArcaneCore.Game.Items.InventoryResult.Ok) continue;
            if (IsRidingSkill(item.Template.RequiredSkill) ? item.Template.RequiredSkillRank > floor : floor == 0) return true;
        }

        return false;
    }

    internal static bool IsMountSpell(SpellInfo? spell) => spell is { IsPassive: false } && spell.HasAura(AuraType.Mounted);

    /// <summary>A vendor's item is a mount: it requires a riding skill, or one of its on-use spells applies the mounted aura.</summary>
    internal static bool IsMountItem(ItemTemplate template, SpellStore store)
    {
        if (IsRidingSkill(template.RequiredSkill)) return true;
        foreach (ItemSpell spell in template.Spells)
        {
            if (spell.SpellId != 0 && spell.Trigger == ItemSpellTriggers.OnUse && IsMountSpell(store.Get(spell.SpellId))) return true;
        }

        return false;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SpellStore, uint[]> MountSpellsByStore = [];

    /// <summary>Every mount spell of the spell table (ascending), computed once per table: the spells a player could know to ride.</summary>
    internal static uint[] MountSpells(SpellStore store)
        => MountSpellsByStore.GetValue(store, s => [.. s.All.Where(IsMountSpell).Select(spell => spell.Id).Order()]);

    /// <summary>
    /// The mount the player can use now: a known mount spell first (<see cref="ISpellLearner.HasSpell"/> over the table's mount spells,
    /// no spellbook copy), else a carried mount item its inventory lets it use. World thread; no allocation.
    /// </summary>
    internal static PlayerbotMountChoice Find(Player player, SpellStore store, ISpellLearner learner)
    {
        foreach (uint id in MountSpells(store))
        {
            if (learner.HasSpell(player, id)) return new(PlayerbotMountKind.Spell, id, 0, 0, 0);
        }

        int scanned = 0;
        foreach (Item item in player.Inventory.AllItems)
        {
            if (++scanned > 128) break;
            if (!InventorySlots.IsInventoryPos(item.BagSlot, item.Slot) || item.Count == 0 || item.Template.Spells.Count == 0
                || !IsMountItem(item.Template, store) || player.Inventory.CanUseItem(item) != ArcaneCore.Game.Items.InventoryResult.Ok) continue;
            return new(PlayerbotMountKind.Item, 0, item.BagSlot, item.Slot, item.Entry);
        }

        return default;
    }
}
