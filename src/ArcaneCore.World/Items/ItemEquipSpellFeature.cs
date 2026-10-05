using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells;

namespace ArcaneCore.World.Items;

/// <summary>Bridges committed inventory equipment transitions to ON_EQUIP spell application.</summary>
public sealed class ItemEquipSpellFeature(SpellFeature spells) : IWorldFeature, IItemEquipSpellSink
{
    public void Attach(WorldRuntime world)
    {
        world.PlayerLoggedIn += player =>
        {
            player.Inventory.EquipSpellSink = this;
            foreach ((byte slot, Item item) in player.Inventory.Equipped)
            {
                if (item.MaxDurability == 0 || item.Durability > 0)
                {
                    OnItemEquipped(player, item, slot, apply: true);
                }
            }
        };
    }

    public void OnItemEquipped(Player player, Item item, byte slot, bool apply)
        => spells.System.ApplyItemEquipSpell(player, item, slot, apply);

    public void OnPlayerFormChanged(Player player)
        => spells.System.ReconcileItemEquipSpellsAtFormChange(player);
}
