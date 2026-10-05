using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Items;

public interface IItemEnchantmentSink
{
    void ApplyEnchantment(Player player, Item item, int enchantmentSlot, bool apply);
}

public interface IItemEnchantmentSpellSink
{
    void ApplyEnchantmentSpell(Player player, Item item, uint spellId, bool apply);
}
