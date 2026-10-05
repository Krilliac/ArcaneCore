using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;

namespace ArcaneCore.Game.Spells;

public sealed class SpellSystemEnchantmentEquipSink(SpellSystem spells) : IItemEnchantmentSpellSink
{
    public void ApplyEnchantmentSpell(Player player, Item item, uint spellId, bool apply)
    {
        if (spellId == 0) return;
        if (apply) spells.CastEnchantmentEquipSpell(player, item, spellId);
        else spells.RemoveAurasByItem(player, item.Guid, spellId);
    }
}

public sealed partial class SpellSystem
{
    internal SpellCastResult CastEnchantmentEquipSpell(Player player, Item item, uint spellId)
    {
        SpellInfo? spell = Store.Get(spellId);
        return spell is null
            ? SpellCastResult.NotFound
            : Prepare(player, spell, SpellCastTargets.ForSelf(), triggered: true, castItem: item, itemEquipCast: true);
    }
}
