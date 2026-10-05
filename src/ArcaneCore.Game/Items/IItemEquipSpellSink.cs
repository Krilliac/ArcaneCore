using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Items;

/// <summary>World spell bridge for committed equipment transitions; null keeps shadow inventories side-effect free.</summary>
public interface IItemEquipSpellSink
{
    void OnItemEquipped(Player player, Item item, byte slot, bool apply);

    void OnPlayerFormChanged(Player player) { }
}
