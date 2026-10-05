using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    internal Func<Unit, SpellInfo, SpellCastResult>? ItemEquipFormCheck { get; set; }

    private SpellCastResult CheckItemEquipForm(Player player, SpellInfo spell)
        => ItemEquipFormCheck?.Invoke(player, spell)
            ?? spell.GetErrorAtShapeshiftedCast((uint)ShapeshiftService.GetForm(player), null);

    public void ReconcileItemEquipSpellsAtFormChange(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        foreach ((byte slot, Item item) in player.Inventory.Equipped)
        {
            if (item.MaxDurability > 0 && item.Durability == 0) continue;
            for (byte index = 0; index < item.Template.Spells.Count; index++)
            {
                ItemSpell itemSpell = item.Template.Spells[index];
                if (itemSpell.SpellId == 0 || itemSpell.Trigger != 1 || Store.Get(itemSpell.SpellId) is not { } spell) continue;
                bool compatible = CheckItemEquipForm(player, spell) == SpellCastResult.CastOk;
                bool active = GetAuras(player).Any(h => h.IsItemEquipAura && h.ItemGuid == item.Guid && h.Spell.Id == spell.Id);
                if (compatible && !active) Prepare(player, spell, SpellCastTargets.ForSelf(), triggered: true,
                    castItem: item, itemSpellIndex: index, itemEquipCast: true);
                else if (!compatible && active) RemoveAurasByItem(player, item.Guid, spell.Id);
            }
        }
    }
    internal void RemoveAurasByItem(Player player, ObjectGuid itemGuid, uint spellId)
    {
        if (GetState(player.Guid) is not { } state || !ReferenceEquals(state.Unit, player)) return;
        foreach (SpellAuraHolder holder in state.Auras.Where(h => h.IsItemEquipAura && h.ItemGuid == itemGuid && h.Spell.Id == spellId).ToArray())
            RemoveHolder(state, holder);
    }

    /// <summary>Apply/remove ITEM_SPELLTRIGGER_ON_EQUIP effects for one committed equipment transition.</summary>
    public void ApplyItemEquipSpell(Player player, Item item, byte slot, bool apply)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(item);
        if (item.Container is not null || item.Slot != slot)
        {
            return;
        }

        for (byte index = 0; index < item.Template.Spells.Count; index++)
        {
            ItemSpell itemSpell = item.Template.Spells[index];
            if (itemSpell.SpellId == 0 || itemSpell.Trigger != 1 || Store.Get(itemSpell.SpellId) is not { } spell)
            {
                continue;
            }

            if (apply && (item.MaxDurability == 0 || item.Durability > 0))
            {
                if (CheckItemEquipForm(player, spell) != SpellCastResult.CastOk) continue;
                // ON_EQUIP is a triggered owner cast: it bypasses ON_USE charge,
                // power, and cooldown policy while retaining item provenance.
                Prepare(player, spell, SpellCastTargets.ForSelf(), triggered: true, triggeringSpell: null,
                    castItem: item, itemSpellIndex: index, itemCooldownMs: null, itemCategoryCooldownMs: null,
                    itemCategory: null, itemEquipCast: true);
            }
            else
            {
                RemoveAurasByItem(player, item.Guid, spell.Id);
            }
        }
    }

}
