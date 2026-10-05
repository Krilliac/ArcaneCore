using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells;

namespace ArcaneCore.World.Items;

public sealed class ItemEnchantmentFeature(SpellFeature spells) : IWorldFeature
{
    public void Attach(ArcaneCore.Game.Maps.WorldRuntime world)
    {
        world.PlayerLoggedIn += player =>
        {
            if (spells.EnchantmentCatalogProvider is null) return;
            player.Inventory.EnchantmentSink = new ItemEnchantmentEffects(spells.EnchantmentCatalogProvider);
            player.Inventory.EnchantmentSpellSink = new ArcaneCore.Game.Spells.SpellSystemEnchantmentEquipSink(spells.System);
            foreach ((byte slot, Item item) in player.Inventory.Equipped)
                for (int enchantmentSlot = 0; enchantmentSlot < Item.EnchantmentValues / 3; enchantmentSlot++)
                    player.Inventory.EnchantmentSink.ApplyEnchantment(player, item, enchantmentSlot, true);
        };
    }
}
